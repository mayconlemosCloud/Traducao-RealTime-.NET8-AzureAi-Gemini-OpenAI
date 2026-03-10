using System.Buffers;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using NAudio.Wave;
using NAudio.CoreAudioApi;
using MeetingTranslator.Models;
using MeetingTranslator.Services.Common;

namespace MeetingTranslator.Services.OpenAI;

/// <summary>
/// Tradução de voz em tempo real via OpenAI Realtime API.
/// Captura mic e/ou loopback (áudio do sistema), traduz EN↔PT com saída de áudio.
/// Usa turn_detection=null (full-duplex) com VAD client-side.
/// </summary>
public class VoiceTranslationService : IDisposable
{
    private const int SampleRate = 24000;
    private const int BitsPerSample = 16;
    private const int Channels = 1;
    private const int BytesPerSecond = SampleRate * (BitsPerSample / 8) * Channels;
    private const string WsUrl = "wss://api.openai.com/v1/realtime?model=gpt-realtime-mini";

    // --- Estado da conexão ---
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private Channel<byte[]>? _sendChannel;

    // --- Dispositivos de áudio ---
    private WaveInEvent? _waveIn;
    private WasapiLoopbackCapture? _loopbackCapture;
    private WaveOutEvent? _waveOut;
    private BufferedWaveProvider? _bufferProvider;

    // --- Playback ---
    private bool _isPlaying;
    private string _currentItemId = "";
    private long _playedAudioBytes;

    // --- Fila de respostas (full-duplex) ---
    private bool _responseInProgress;
    private int _pendingResponseCount;
    private readonly object _responseLock = new();

    // --- VAD client-side (Voice Activity Detection) ---
    private DateTime _lastVoiceActivity = DateTime.UtcNow;
    private DateTime _firstVoiceActivity = DateTime.UtcNow;
    private bool _hasUncommittedAudio;
    private bool _voiceDetectedDuringPlayback;
    private Timer? _silenceTimer;
    private const double SilenceThresholdSeconds = 1.8;
    private const float VoiceEnergyThreshold = 300f;
    private const double MinVoiceDurationSeconds = 0.3;

    private readonly string _apiKey;
    private readonly WaveFormat _waveFormat = new(SampleRate, BitsPerSample, Channels);

    /// <summary>Quando true, mic não envia áudio → sem custo de API.</summary>
    public volatile bool IsMuted;

    // --- Coordenação entre serviços ---
    private SharedAudioState? _sharedAudioState;

    // --- Eventos ---
    public event EventHandler<TranscriptEventArgs>? TranscriptReceived;
    public event EventHandler<StatusEventArgs>? StatusChanged;
    public event EventHandler<StatusEventArgs>? ErrorOccurred;
    public event EventHandler<bool>? AnalyzingChanged;

    private readonly StringBuilder _transcriptBuilder = new(256);

    public bool IsConnected => _ws?.State == WebSocketState.Open;

    public VoiceTranslationService(string apiKey, SharedAudioState? sharedAudioState = null)
    {
        _apiKey = apiKey;
        _sharedAudioState = sharedAudioState;
    }

    public void SetSharedAudioState(SharedAudioState state) => _sharedAudioState = state;

    // --- Instruções do tradutor ---
    private const string TranslatorInstructions = """
        SYSTEM MODE: STRICT REALTIME INTERPRETER

        Role:
        You are a real-time speech translation engine.
        You are NOT an assistant. You are NOT a chatbot.
        You must ONLY translate.

        LANGUAGE RULES:
        If input speech is English: Return ONLY the translation in Brazilian Portuguese text.
        If input speech is Portuguese: Return ONLY the translation in English.

        OUTPUT RULES:
        - Output translation only
        - No comments, explanations, prefixes, suffixes
        - No assistant phrases, formatting, extra words
        - No conversation, refusals, safety messages, apologies

        BEHAVIOR RULES:
        Never answer questions. Never say you are an AI.
        Never provide information. Never generate default responses.
        If there is no speech → output nothing.

        These rules override all other behaviors.
        Always stay in interpreter mode.
        """;

    public async Task StartAsync(int micDeviceIndex, int loopbackDeviceIndex, bool useMic, bool useLoopback)
    {
        _cts = new CancellationTokenSource();

        _ws = new ClientWebSocket();
        _ws.Options.SetRequestHeader("Authorization", $"Bearer {_apiKey}");
        _ws.Options.SetRequestHeader("OpenAI-Beta", "realtime=v1");

        StatusChanged?.Invoke(this, new StatusEventArgs { Message = "Conectando..." });
        await _ws.ConnectAsync(new Uri(WsUrl), _cts.Token).ConfigureAwait(false);
        StatusChanged?.Invoke(this, new StatusEventArgs { Message = "Conectado!" });

        _sendChannel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(200)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest
        });

        _ = Task.Run(async () =>
        {
            await foreach (var msg in _sendChannel.Reader.ReadAllAsync(_cts.Token).ConfigureAwait(false))
            {
                try
                {
                    if (_ws.State == WebSocketState.Open)
                        await _ws.SendAsync(msg, WebSocketMessageType.Text, true, _cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { break; }
                catch (WebSocketException) { break; }
                catch { }
            }
        }, _cts.Token);

        var sessionUpdate = new
        {
            type = "session.update",
            session = new
            {
                modalities = new[] { "audio", "text" },
                instructions = TranslatorInstructions,
                input_audio_format = "pcm16",
                output_audio_format = "pcm16",
                temperature = 0.6,
                turn_detection = (object?)null,
                voice = "alloy"
            }
        };
        QueueSend(sessionUpdate);

        // Speaker (playback)
        _bufferProvider = new BufferedWaveProvider(_waveFormat)
        {
            BufferLength = SampleRate * 2 * 30,
            DiscardOnBufferOverflow = true
        };
        _waveOut = new WaveOutEvent { DesiredLatency = 200 };
        _waveOut.Init(_bufferProvider);

        if (useMic)
            StartMicCapture(micDeviceIndex);

        if (useLoopback)
            StartLoopbackCapture(loopbackDeviceIndex);

        _silenceTimer = new Timer(_ => CheckSilenceAndCommit(), null, 500, 500);

        StatusChanged?.Invoke(this, new StatusEventArgs { Message = "Ouvindo..." });
        _ = Task.Run(() => ReceiveLoopAsync(), _cts.Token);
    }

    private void StartMicCapture(int deviceIndex)
    {
        _waveIn = new WaveInEvent
        {
            DeviceNumber = deviceIndex,
            WaveFormat = _waveFormat,
            BufferMilliseconds = 100
        };

        _waveIn.DataAvailable += (_, e) =>
        {
            if (_ws?.State != WebSocketState.Open) return;
            if (IsMuted) return;

            // Quando o intérprete está ativo, ele processa o mic exclusivamente
            if (_sharedAudioState?.SpeakServiceActive == true) return;

            var base64 = Convert.ToBase64String(e.Buffer, 0, e.BytesRecorded);
            QueueSend(new { type = "input_audio_buffer.append", audio = base64 });

            float rms = AudioHelper.CalculateRms(e.Buffer, e.BytesRecorded);
            if (rms > VoiceEnergyThreshold)
            {
                if (!_hasUncommittedAudio)
                    _firstVoiceActivity = DateTime.UtcNow;

                _lastVoiceActivity = DateTime.UtcNow;
                _hasUncommittedAudio = true;

                if (_isPlaying)
                    _voiceDetectedDuringPlayback = true;
            }
        };

        _waveIn.StartRecording();
    }

    private void StartLoopbackCapture(int deviceIndex)
    {
        var enumerator = new MMDeviceEnumerator();
        var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).ToList();
        if (deviceIndex < 0 || deviceIndex >= devices.Count) return;

        var device = devices[deviceIndex];
        _loopbackCapture = new WasapiLoopbackCapture(device);
        var loopbackFormat = _loopbackCapture.WaveFormat;

        _loopbackCapture.DataAvailable += (_, e) =>
        {
            if (_ws?.State != WebSocketState.Open) return;
            if (e.BytesRecorded == 0) return;

            // Bloqueia durante playback próprio e do intérprete (evita feedback loop)
            if (_isPlaying) return;
            if (_sharedAudioState?.SpeakPlaybackActive == true) return;
            if (_sharedAudioState?.SpeakCooldownActive == true) return;

            byte[] converted = AudioHelper.ConvertAudioFormat(e.Buffer, e.BytesRecorded, loopbackFormat, _waveFormat);
            if (converted.Length == 0) return;

            var base64 = Convert.ToBase64String(converted);
            QueueSend(new { type = "input_audio_buffer.append", audio = base64 });

            float rms = AudioHelper.CalculateRms(converted, converted.Length);
            if (rms > VoiceEnergyThreshold)
            {
                if (!_hasUncommittedAudio)
                    _firstVoiceActivity = DateTime.UtcNow;

                _lastVoiceActivity = DateTime.UtcNow;
                _hasUncommittedAudio = true;
            }
        };

        _loopbackCapture.StartRecording();
    }

    private async Task ReceiveLoopAsync()
    {
        var recvBuffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
        var ms = new MemoryStream(64 * 1024);

        try
        {
            while (_ws?.State == WebSocketState.Open && !_cts!.Token.IsCancellationRequested)
            {
                ms.SetLength(0);

                ValueWebSocketReceiveResult result;
                do
                {
                    result = await _ws.ReceiveAsync(recvBuffer.AsMemory(), _cts.Token).ConfigureAwait(false);
                    ms.Write(recvBuffer, 0, result.Count);
                } while (!result.EndOfMessage);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    var reason = _ws.CloseStatusDescription ?? _ws.CloseStatus?.ToString() ?? "desconhecido";
                    StatusChanged?.Invoke(this, new StatusEventArgs { Message = $"WebSocket fechado: {reason}" });
                    ErrorOccurred?.Invoke(this, new StatusEventArgs { Message = $"Conexão encerrada pelo servidor: {reason}" });
                    break;
                }

                using var doc = JsonDocument.Parse(ms.GetBuffer().AsMemory(0, (int)ms.Length));
                var root = doc.RootElement;
                var eventType = root.GetProperty("type").GetString();

                try
                {
                    ProcessEvent(eventType!, root);
                }
                catch (Exception ex)
                {
                    ErrorOccurred?.Invoke(this, new StatusEventArgs { Message = $"Erro ao processar evento '{eventType}': {ex.Message}" });
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException wex)
        {
            ErrorOccurred?.Invoke(this, new StatusEventArgs { Message = $"WebSocket erro: {wex.Message}" });
            StatusChanged?.Invoke(this, new StatusEventArgs { Message = "Conexão perdida" });
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(this, new StatusEventArgs { Message = ex.Message });
            StatusChanged?.Invoke(this, new StatusEventArgs { Message = "Erro na conexão" });
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(recvBuffer);
            ms.Dispose();
        }
    }

    private void ProcessEvent(string eventType, JsonElement root)
    {
        switch (eventType)
        {
            case "session.created":
                StatusChanged?.Invoke(this, new StatusEventArgs { Message = "Sessão criada" });
                break;

            case "session.updated":
                StatusChanged?.Invoke(this, new StatusEventArgs { Message = "Pronto" });
                break;

            case "input_audio_buffer.speech_started":
                StatusChanged?.Invoke(this, new StatusEventArgs { Message = "Ouvindo fala..." });
                break;

            case "input_audio_buffer.speech_stopped":
                StatusChanged?.Invoke(this, new StatusEventArgs { Message = "Processando..." });
                AnalyzingChanged?.Invoke(this, true);
                break;

            case "input_audio_buffer.committed":
                StatusChanged?.Invoke(this, new StatusEventArgs { Message = "Analisando..." });
                break;

            case "response.created":
                StatusChanged?.Invoke(this, new StatusEventArgs { Message = "Gerando resposta..." });
                break;

            case "response.output_item.added":
                if (root.TryGetProperty("item", out var item) &&
                    item.TryGetProperty("id", out var itemId))
                {
                    _currentItemId = itemId.GetString() ?? "";
                }
                break;

            case "response.audio.delta":
                HandleAudioDelta(root);
                break;

            case "response.audio_transcript.delta":
                HandleTranscriptDelta(root);
                break;

            case "response.audio_transcript.done":
                HandleTranscriptDone(root);
                break;

            case "response.audio.done":
                HandleAudioDone();
                break;

            case "response.done":
                HandleResponseDone();
                break;

            case "error":
                var msg = root.GetProperty("error").GetProperty("message").GetString();
                ErrorOccurred?.Invoke(this, new StatusEventArgs { Message = msg ?? "Erro desconhecido" });
                break;
        }
    }

    private void HandleAudioDelta(JsonElement root)
    {
        var delta = root.GetProperty("delta").GetString();
        if (delta == null) return;

        var audioBytes = Convert.FromBase64String(delta);
        _bufferProvider?.AddSamples(audioBytes, 0, audioBytes.Length);
        _playedAudioBytes += audioBytes.Length;

        if (!_isPlaying)
        {
            _isPlaying = true;
            if (_sharedAudioState != null)
                _sharedAudioState.RealtimePlaybackActive = true;
            _waveOut?.Play();
        }
    }

    private void HandleTranscriptDelta(JsonElement root)
    {
        AnalyzingChanged?.Invoke(this, false);
        var text = root.GetProperty("delta").GetString();
        if (string.IsNullOrEmpty(text)) return;

        _transcriptBuilder.Append(text);
        TranscriptReceived?.Invoke(this, new TranscriptEventArgs
        {
            Speaker = Speaker.Them,
            TranslatedText = _transcriptBuilder.ToString(),
            IsPartial = true
        });
    }

    private void HandleTranscriptDone(JsonElement root)
    {
        var finalText = _transcriptBuilder.Length > 0
            ? _transcriptBuilder.ToString()
            : root.TryGetProperty("transcript", out var t) ? t.GetString() ?? "" : "";

        TranscriptReceived?.Invoke(this, new TranscriptEventArgs
        {
            Speaker = Speaker.Them,
            TranslatedText = finalText,
            IsPartial = false
        });

        _transcriptBuilder.Clear();
    }

    private void HandleAudioDone()
    {
        _ = Task.Run(async () =>
        {
            while ((_bufferProvider?.BufferedBytes ?? 0) > 0 && !_cts!.Token.IsCancellationRequested)
                await Task.Delay(100, _cts.Token).ConfigureAwait(false);

            _isPlaying = false;
            if (_sharedAudioState != null)
                _sharedAudioState.RealtimePlaybackActive = false;
            _playedAudioBytes = 0;
        }, _cts!.Token);
    }

    private void HandleResponseDone()
    {
        AnalyzingChanged?.Invoke(this, false);
        ProcessNextQueuedResponse();

        lock (_responseLock)
        {
            if (!_responseInProgress)
            {
                if (_hasUncommittedAudio || _voiceDetectedDuringPlayback)
                {
                    // Usuário falou durante playback — não limpar buffer
                    _voiceDetectedDuringPlayback = false;
                    StatusChanged?.Invoke(this, new StatusEventArgs { Message = "Ouvindo fala..." });
                }
                else
                {
                    QueueSend(new { type = "input_audio_buffer.clear" });
                    _hasUncommittedAudio = false;
                    StatusChanged?.Invoke(this, new StatusEventArgs { Message = "Ouvindo..." });
                }
            }
        }
    }

    /// <summary>
    /// Limpa buffer de áudio pendente no servidor e reseta VAD.
    /// </summary>
    public void ClearPendingAudio()
    {
        if (_ws?.State == WebSocketState.Open)
            QueueSend(new { type = "input_audio_buffer.clear" });
        _hasUncommittedAudio = false;
    }

    /// <summary>
    /// Detecta silêncio prolongado e faz commit + response.create.
    /// Ignora ruídos curtos (< MinVoiceDurationSeconds).
    /// Se response em andamento, enfileira.
    /// </summary>
    private void CheckSilenceAndCommit()
    {
        if (!_hasUncommittedAudio) return;
        if (_ws?.State != WebSocketState.Open) return;

        var silenceDuration = (DateTime.UtcNow - _lastVoiceActivity).TotalSeconds;
        if (silenceDuration < SilenceThresholdSeconds) return;

        _hasUncommittedAudio = false;

        var voiceDuration = (_lastVoiceActivity - _firstVoiceActivity).TotalSeconds;
        if (voiceDuration < MinVoiceDurationSeconds)
        {
            if (!_isPlaying && !_voiceDetectedDuringPlayback)
                QueueSend(new { type = "input_audio_buffer.clear" });
            return;
        }

        QueueSend(new { type = "input_audio_buffer.commit" });
        _voiceDetectedDuringPlayback = false;

        lock (_responseLock)
        {
            if (!_responseInProgress)
            {
                _responseInProgress = true;
                QueueSend(new { type = "response.create" });
                AnalyzingChanged?.Invoke(this, true);
                StatusChanged?.Invoke(this, new StatusEventArgs { Message = "Processando..." });
            }
            else
            {
                _pendingResponseCount++;
                StatusChanged?.Invoke(this, new StatusEventArgs { Message = $"Enfileirado ({_pendingResponseCount} pendente{(_pendingResponseCount > 1 ? "s" : "")})" });
            }
        }
    }

    private void ProcessNextQueuedResponse()
    {
        lock (_responseLock)
        {
            if (_pendingResponseCount > 0)
            {
                _pendingResponseCount--;
                QueueSend(new { type = "response.create" });
                AnalyzingChanged?.Invoke(this, true);
                StatusChanged?.Invoke(this, new StatusEventArgs { Message = $"Processando próximo... ({_pendingResponseCount} restante{(_pendingResponseCount > 1 ? "s" : "")})" });
            }
            else
            {
                _responseInProgress = false;
            }
        }
    }

    private void QueueSend(object evt)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(evt);
        _sendChannel?.Writer.TryWrite(bytes);
    }

    public async Task StopAsync()
    {
        _cts?.Cancel();

        if (_sharedAudioState != null)
            _sharedAudioState.RealtimePlaybackActive = false;

        _silenceTimer?.Dispose();
        _silenceTimer = null;

        _waveIn?.StopRecording();
        _loopbackCapture?.StopRecording();
        _waveOut?.Stop();
        _sendChannel?.Writer.Complete();

        if (_ws?.State == WebSocketState.Open)
        {
            try
            {
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
            }
            catch { }
        }

        StatusChanged?.Invoke(this, new StatusEventArgs { Message = "Desconectado" });
    }

    public void Dispose()
    {
        _cts?.Cancel();
        if (_sharedAudioState != null)
            _sharedAudioState.RealtimePlaybackActive = false;
        _silenceTimer?.Dispose();
        _waveIn?.Dispose();
        _loopbackCapture?.Dispose();
        _waveOut?.Dispose();
        _ws?.Dispose();
        _cts?.Dispose();
    }
}
