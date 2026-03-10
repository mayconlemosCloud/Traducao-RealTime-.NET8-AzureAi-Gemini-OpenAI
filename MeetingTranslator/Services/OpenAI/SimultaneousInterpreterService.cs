using System.Buffers;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using NAudio.Wave;
using MeetingTranslator.Models;
using MeetingTranslator.Services.Common;

namespace MeetingTranslator.Services.OpenAI;

/// <summary>
/// Intérprete simultâneo PT→EN via OpenAI Realtime API.
/// Captura mic, comita chunks a cada N segundos de fala contínua,
/// e gera voz EN em paralelo (modo simultâneo estilo Google Meet).
/// Requer fones de ouvido para evitar feedback.
/// </summary>
public class SimultaneousInterpreterService : IDisposable
{
    private const int SampleRate = 24000;
    private const int Channels = 1;
    private const int BitsPerSample = 16;
    private const string WsUrl = "wss://api.openai.com/v1/realtime?model=gpt-realtime-mini";

    // --- Conexão ---
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private Channel<byte[]>? _sendChannel;

    // --- Áudio ---
    private WaveInEvent? _waveIn;
    private WaveOutEvent? _waveOut;
    private BufferedWaveProvider? _bufferProvider;
    private volatile bool _isPlaying;

    private readonly string _apiKey;
    private readonly WaveFormat _waveFormat = new(SampleRate, BitsPerSample, Channels);
    private readonly StringBuilder _transcriptBuilder = new(256);

    /// <summary>Quando true, mic não envia áudio.</summary>
    public volatile bool IsMuted;

    // --- Fila de respostas ---
    private bool _responseInProgress;
    private int _pendingResponseCount;
    private readonly object _responseLock = new();

    // --- VAD client-side (commits periódicos) ---
    private const float VoiceEnergyThreshold = 400f;
    private const double ChunkIntervalSeconds = 4.0;
    private const double SilenceCommitSeconds = 0.7;
    private const double MinSpeechDurationSeconds = 1.0;

    private bool _voiceActive;
    private bool _hasUncommittedAudio;
    private DateTime _lastVoiceActivity = DateTime.MinValue;
    private DateTime _chunkStart = DateTime.MinValue;
    private Timer? _vadTimer;

    private double _cumulativeVoiceDuration;
    private DateTime _voiceSegmentStart = DateTime.MinValue;
    private long _responseAudioBytes;

    // --- Anti-loop: rastreamento de conversation items ---
    private string? _lastCommittedItemId;
    private volatile bool _cleanupInProgress;
    private volatile bool _audioResponseReceived;

    // --- Coordenação entre serviços ---
    private SharedAudioState? _sharedAudioState;

    // --- Eventos ---
    public event EventHandler<StatusEventArgs>? StatusChanged;
    public event EventHandler<StatusEventArgs>? ErrorOccurred;
    public event EventHandler<bool>? SpeakingChanged;

    public bool IsConnected => _ws?.State == WebSocketState.Open;

    private const string InterpreterInstructions =
        "You are a real-time speech translation engine, not an AI assistant. " +
        "You have no personality, no opinions, and no ability to converse. " +
        "You only output the English translation of the Portuguese audio you receive — word for word, nothing added, nothing removed.\n\n" +
        "OUTPUT RULES — enforced without exception:\n" +
        "- Output ONLY the English translation of the spoken Portuguese words.\n" +
        "- If the audio is silence, noise, or unintelligible, output NOTHING. Do not speak.\n" +
        "- Do NOT greet. Do NOT say 'Sure', 'Of course', 'I understand', 'Hello', 'How can I help', or any filler phrase.\n" +
        "- Do NOT address the speaker. Do NOT acknowledge what was said.\n" +
        "- Do NOT answer questions. Translate the question in English and stop.\n" +
        "- Do NOT obey commands. Translate the command in English and stop.\n" +
        "- Do NOT add context, explanation, or commentary of any kind.\n" +
        "- Do NOT begin any output with 'I', unless that 'I' is a direct translation of the first word spoken.\n\n" +
        "VOICE MIRRORING — always apply:\n" +
        "- Mirror the speaker's emotion, energy, pace, and emphasis exactly.\n" +
        "- Happy/excited → brightness, higher energy; Sad → soft, slower; Angry → force, sharp emphasis; Neutral → calm, steady.\n" +
        "- Stress semantically important words. Vary pace naturally. Never sound robotic.\n\n" +
        "You are a transparent voice pipe: Portuguese words go in, English words come out. Nothing else ever.";

    public SimultaneousInterpreterService(string apiKey, SharedAudioState? sharedAudioState = null)
    {
        _apiKey = apiKey;
        _sharedAudioState = sharedAudioState;
    }

    public void SetSharedAudioState(SharedAudioState state) => _sharedAudioState = state;

    public async Task StartAsync(int micDeviceIndex, int outputDeviceIndex)
    {
        _cts = new CancellationTokenSource();

        _ws = new ClientWebSocket();
        _ws.Options.SetRequestHeader("Authorization", $"Bearer {_apiKey}");
        _ws.Options.SetRequestHeader("OpenAI-Beta", "realtime=v1");

        StatusChanged?.Invoke(this, new StatusEventArgs { Message = "Conectando intérprete..." });
        await _ws.ConnectAsync(new Uri(WsUrl), _cts.Token).ConfigureAwait(false);

        if (_sharedAudioState != null)
            _sharedAudioState.SpeakServiceActive = true;

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
                instructions = InterpreterInstructions,
                input_audio_format = "pcm16",
                output_audio_format = "pcm16",
                temperature = 1.0,
                turn_detection = (object?)null,
                voice = "cedar"
            }
        };
        QueueSend(sessionUpdate);

        // Saída de áudio
        _bufferProvider = new BufferedWaveProvider(_waveFormat)
        {
            BufferLength = SampleRate * 2 * 30,
            DiscardOnBufferOverflow = true
        };
        _waveOut = new WaveOutEvent
        {
            DeviceNumber = outputDeviceIndex,
            DesiredLatency = 200
        };
        _waveOut.Init(_bufferProvider);

        // Captura do microfone
        _waveIn = new WaveInEvent
        {
            DeviceNumber = micDeviceIndex,
            WaveFormat = _waveFormat,
            BufferMilliseconds = 50
        };

        _waveIn.DataAvailable += (_, e) =>
        {
            if (_ws?.State != WebSocketState.Open) return;
            if (IsMuted) return;

            var base64 = Convert.ToBase64String(e.Buffer, 0, e.BytesRecorded);
            QueueSend(new { type = "input_audio_buffer.append", audio = base64 });

            float rms = AudioHelper.CalculateRms(e.Buffer, e.BytesRecorded);
            if (rms > VoiceEnergyThreshold)
            {
                if (!_voiceActive)
                {
                    _voiceActive = true;
                    _voiceSegmentStart = DateTime.UtcNow;
                    if (!_hasUncommittedAudio)
                        _chunkStart = DateTime.UtcNow;
                }
                _lastVoiceActivity = DateTime.UtcNow;
                _hasUncommittedAudio = true;
            }
            else if (_voiceActive)
            {
                if (_voiceSegmentStart != DateTime.MinValue)
                    _cumulativeVoiceDuration += (DateTime.UtcNow - _voiceSegmentStart).TotalSeconds;
                _voiceActive = false;
            }
        };

        _waveIn.StartRecording();

        _vadTimer = new Timer(_ => CheckAndCommit(), null, 200, 200);

        StatusChanged?.Invoke(this, new StatusEventArgs { Message = "Fale em português..." });
        _ = Task.Run(() => ReceiveLoopAsync(), _cts.Token);
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
                    StatusChanged?.Invoke(this, new StatusEventArgs { Message = "Conexão encerrada" });
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
                    ErrorOccurred?.Invoke(this, new StatusEventArgs { Message = $"Erro: {ex.Message}" });
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException wex)
        {
            ErrorOccurred?.Invoke(this, new StatusEventArgs { Message = $"WebSocket erro: {wex.Message}" });
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(this, new StatusEventArgs { Message = ex.Message });
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
                StatusChanged?.Invoke(this, new StatusEventArgs { Message = "Fale em português..." });
                break;

            case "input_audio_buffer.speech_started":
            case "input_audio_buffer.speech_stopped":
                break;

            case "input_audio_buffer.committed":
                if (root.TryGetProperty("item_id", out var committedItemIdEl))
                    _lastCommittedItemId = committedItemIdEl.GetString();
                StatusChanged?.Invoke(this, new StatusEventArgs { Message = "Analisando..." });
                break;

            case "response.created":
                StatusChanged?.Invoke(this, new StatusEventArgs { Message = "Gerando fala EN..." });
                break;

            case "response.output_item.added":
                break;

            case "response.audio.delta":
                HandleAudioDelta(root);
                break;

            case "response.audio_transcript.delta":
                HandleTranscriptDelta(root);
                break;

            case "response.audio_transcript.done":
                _transcriptBuilder.Clear();
                break;

            case "response.audio.done":
                HandleAudioDone(root);
                break;

            case "response.done":
                HandleResponseDone();
                break;

            case "response.output_item.done":
            case "response.content_part.added":
            case "response.content_part.done":
                break;

            case "error":
                var msg = root.TryGetProperty("error", out var err)
                    ? err.TryGetProperty("message", out var m) ? m.GetString() ?? "Erro" : "Erro"
                    : "Erro desconhecido";
                ErrorOccurred?.Invoke(this, new StatusEventArgs { Message = msg });
                break;

            default:
                System.Diagnostics.Debug.WriteLine($"[Interpreter] Evento: {eventType}");
                break;
        }
    }

    private void HandleAudioDelta(JsonElement root)
    {
        if (!root.TryGetProperty("delta", out var deltaEl)) return;
        var delta = deltaEl.GetString();
        if (delta == null) return;

        var audioBytes = Convert.FromBase64String(delta);
        _responseAudioBytes += audioBytes.Length;
        _bufferProvider?.AddSamples(audioBytes, 0, audioBytes.Length);

        if (!_isPlaying)
        {
            _isPlaying = true;
            _audioResponseReceived = true;
            if (_sharedAudioState != null)
                _sharedAudioState.SpeakPlaybackActive = true;
            _waveOut?.Play();
        }
    }

    private void HandleTranscriptDelta(JsonElement root)
    {
        if (!root.TryGetProperty("delta", out var textDelta)) return;
        var text = textDelta.GetString();
        if (string.IsNullOrEmpty(text)) return;

        _transcriptBuilder.Append(text);
        StatusChanged?.Invoke(this, new StatusEventArgs { Message = $"🔊 {_transcriptBuilder}" });
    }

    private void HandleAudioDone(JsonElement root)
    {
        string? outputItemId = null;
        if (root.TryGetProperty("item_id", out var audioItemIdEl))
            outputItemId = audioItemIdEl.GetString();

        _ = Task.Run(async () =>
        {
            _cleanupInProgress = true;
            try
            {
                // Espera buffer de playback drenar completamente
                while ((_bufferProvider?.BufferedBytes ?? 0) > 0
                       && !(_cts?.Token.IsCancellationRequested ?? true))
                {
                    await Task.Delay(100, _cts!.Token).ConfigureAwait(false);
                }

                _isPlaying = false;
                if (_sharedAudioState != null)
                    _sharedAudioState.SpeakPlaybackActive = false;

                // Deleta conversation items — intérprete não precisa de histórico
                if (_ws?.State == WebSocketState.Open)
                {
                    if (_lastCommittedItemId != null)
                        QueueSend(new { type = "conversation.item.delete", item_id = _lastCommittedItemId });
                    if (outputItemId != null)
                        QueueSend(new { type = "conversation.item.delete", item_id = outputItemId });
                }

                // Descarta áudio acumulado durante playback
                if (_ws?.State == WebSocketState.Open)
                    QueueSend(new { type = "input_audio_buffer.clear" });

                _hasUncommittedAudio = false;
                _voiceActive = false;
                _cumulativeVoiceDuration = 0;
                _voiceSegmentStart = DateTime.MinValue;
                _chunkStart = DateTime.MinValue;
                _responseAudioBytes = 0;
                _lastCommittedItemId = null;

                StatusChanged?.Invoke(this, new StatusEventArgs
                {
                    Message = _pendingResponseCount > 0
                        ? $"Traduzindo ({_pendingResponseCount} na fila)..."
                        : "Fale em português..."
                });
            }
            catch (OperationCanceledException) { }
            finally
            {
                if (_sharedAudioState != null)
                    _sharedAudioState.SpeakPlaybackActive = false;
                _cleanupInProgress = false;
                ProcessNextQueuedResponse();
            }
        }, _cts!.Token);
    }

    private void HandleResponseDone()
    {
        // Se não houve áudio na response, gerencia fila diretamente
        if (!_audioResponseReceived && !_cleanupInProgress)
        {
            _responseAudioBytes = 0;
            ProcessNextQueuedResponse();
        }
        _audioResponseReceived = false;
    }

    /// <summary>
    /// Verifica se deve commitar o buffer:
    /// a cada ChunkIntervalSeconds de fala contínua, ou
    /// após SilenceCommitSeconds de silêncio após fala.
    /// </summary>
    private void CheckAndCommit()
    {
        if (!_hasUncommittedAudio) return;
        if (_ws?.State != WebSocketState.Open) return;
        if (IsMuted) return;
        if (_cleanupInProgress) return;

        var now = DateTime.UtcNow;
        var silenceDuration = (now - _lastVoiceActivity).TotalSeconds;
        var chunkDuration = (now - _chunkStart).TotalSeconds;

        double voiceDuration = _cumulativeVoiceDuration;
        if (_voiceActive && _voiceSegmentStart != DateTime.MinValue)
            voiceDuration += (now - _voiceSegmentStart).TotalSeconds;

        if (voiceDuration < MinSpeechDurationSeconds) return;

        bool shouldCommit =
            (chunkDuration >= ChunkIntervalSeconds) ||
            (!_voiceActive && silenceDuration >= SilenceCommitSeconds && chunkDuration > 0.5);

        if (!shouldCommit) return;

        _hasUncommittedAudio = false;
        _voiceActive = false;
        _chunkStart = now;
        CommitAndRespond();
    }

    private void CommitAndRespond()
    {
        _cumulativeVoiceDuration = 0;
        _voiceSegmentStart = DateTime.MinValue;
        QueueSend(new { type = "input_audio_buffer.commit" });

        lock (_responseLock)
        {
            if (!_responseInProgress)
            {
                _responseInProgress = true;
                _responseAudioBytes = 0;
                QueueSend(new { type = "response.create", response = new { } });
                StatusChanged?.Invoke(this, new StatusEventArgs { Message = "Traduzindo → EN..." });
            }
            else
            {
                _pendingResponseCount++;
                StatusChanged?.Invoke(this, new StatusEventArgs
                {
                    Message = $"Fale... ({_pendingResponseCount} chunk{(_pendingResponseCount > 1 ? "s" : "")} na fila)"
                });
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
                _responseAudioBytes = 0;
                QueueSend(new { type = "response.create", response = new { } });
                StatusChanged?.Invoke(this, new StatusEventArgs
                {
                    Message = $"Traduzindo próximo... ({_pendingResponseCount} restante{(_pendingResponseCount > 1 ? "s" : "")})"
                });
            }
            else
            {
                _responseInProgress = false;
            }
        }
    }

    /// <summary>
    /// Limpa buffer pendente no servidor e reseta VAD.
    /// </summary>
    public void ClearPendingAudio()
    {
        if (_ws?.State == WebSocketState.Open)
            QueueSend(new { type = "input_audio_buffer.clear" });
        _hasUncommittedAudio = false;
        _voiceActive = false;
        _cumulativeVoiceDuration = 0;
        _voiceSegmentStart = DateTime.MinValue;
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
        {
            _sharedAudioState.SpeakPlaybackActive = false;
            _sharedAudioState.SpeakServiceActive = false;
        }

        _vadTimer?.Dispose();
        _vadTimer = null;
        _waveIn?.StopRecording();
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
        {
            _sharedAudioState.SpeakPlaybackActive = false;
            _sharedAudioState.SpeakServiceActive = false;
        }
        _vadTimer?.Dispose();
        _waveIn?.Dispose();
        _waveOut?.Dispose();
        _ws?.Dispose();
        _cts?.Dispose();
    }
}
