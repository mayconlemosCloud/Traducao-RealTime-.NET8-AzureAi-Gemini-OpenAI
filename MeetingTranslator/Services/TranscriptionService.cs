using System.Buffers;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using NAudio.Wave;
using NAudio.CoreAudioApi;
using MeetingTranslator.Models;

namespace MeetingTranslator.Services;

/// <summary>
/// Engine de transcrição em tempo real via OpenAI Realtime Transcription API.
///
/// Ref: https://developers.openai.com/api/docs/guides/realtime-transcription
///
/// Diferenças chave vs RealtimeService (Voice mode):
/// - Server-side VAD — detecção de turnos feita pelo servidor
/// - Texto aparece ENQUANTO a pessoa fala (streaming deltas)
/// - Sem saída de áudio — texto puro legendado
/// - Tradução via Chat Completions API após cada frase completa
/// - Sem problemas de feedback loop (não toca áudio)
/// - Não precisa de silence detection client-side
///
/// Pontos-chave da implementação:
/// 1. WebSocket URL usa intent=transcription para criar uma transcription session
/// 2. session.update envia type="transcription" + config em session.audio.input.*
/// 3. Eventos de sessão: transcription_session.created / .updated
/// 4. Transcrição: conversation.item.input_audio_transcription.delta / .completed
/// </summary>
public class TranscriptionService : IDisposable
{
    // ─── CONFIG ────────────────────────────────────────────
    private const int SampleRate = 24000;
    private const int Channels = 1;
    private const int BitsPerSample = 16;

    // intent=transcription → cria uma transcription session (não realtime/conversation)
    // Ref: https://developers.openai.com/api/docs/guides/realtime-transcription
    // Para transcription sessions, NÃO se passa model na URL nem no session.update.
    private const string WsUrl =
        "wss://api.openai.com/v1/realtime?intent=transcription";

    private const string ChatApiUrl = "https://api.openai.com/v1/chat/completions";

    // ─── STATE ─────────────────────────────────────────────
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private Channel<byte[]>? _sendChannel;

    private WaveInEvent? _waveIn;
    private WasapiLoopbackCapture? _loopbackCapture;

    private readonly string _apiKey;
    private readonly WaveFormat _waveFormat = new(SampleRate, BitsPerSample, Channels);
    private readonly HttpClient _httpClient;

    // ─── TRANSCRIPT STATE ──────────────────────────────────
    // Acumula deltas de transcrição por item_id
    private readonly Dictionary<string, StringBuilder> _transcriptBuffers = new();
    private string _currentStreamingItemId = "";

    // ─── EVENTS ────────────────────────────────────────────
    public event EventHandler<TranscriptEventArgs>? TranscriptReceived;
    public event EventHandler<StatusEventArgs>? StatusChanged;
    public event EventHandler<StatusEventArgs>? ErrorOccurred;
    public event EventHandler<bool>? AnalyzingChanged;

    public bool IsConnected => _ws?.State == WebSocketState.Open;

    public TranscriptionService(string apiKey)
    {
        _apiKey = apiKey;
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
    }

    // ─── START ─────────────────────────────────────────────
    public async Task StartAsync(int micDeviceIndex, int loopbackDeviceIndex, bool useMic, bool useLoopback)
    {
        _cts = new CancellationTokenSource();

        // ── WebSocket ──
        // GA API: sem header OpenAI-Beta. O intent=transcription na URL
        // já cria a sessão como transcription session.
        _ws = new ClientWebSocket();
        _ws.Options.SetRequestHeader("Authorization", $"Bearer {_apiKey}");

        StatusChanged?.Invoke(this, new StatusEventArgs { Message = "Conectando..." });
        await _ws.ConnectAsync(new Uri(WsUrl), _cts.Token).ConfigureAwait(false);
        StatusChanged?.Invoke(this, new StatusEventArgs { Message = "Conectado!" });

        // ── Send channel (thread-safe, bounded) ──
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

        // ── Session update (transcription config) ──
        // Ref: https://developers.openai.com/api/docs/guides/realtime-transcription
        // Transcription sessions usam o formato audio.input.* aninhado.
        var sessionUpdate = new
        {
            type = "session.update",
            session = new
            {
                type = "transcription",
                audio = new
                {
                    input = new
                    {
                        format = new { type = "audio/pcm", rate = 24000 },
                        noise_reduction = new { type = "near_field" },
                        transcription = new
                        {
                            model = "gpt-4o-mini-transcribe",
                            prompt = "",
                            language = "en"
                        },
                        turn_detection = new
                        {
                            type = "server_vad",
                            threshold = 0.5,
                            prefix_padding_ms = 300,
                            silence_duration_ms = 500
                        }
                    }
                }
            }
        };
        QueueSend(sessionUpdate);

        // ── Mic capture ──
        if (useMic)
        {
            _waveIn = new WaveInEvent
            {
                DeviceNumber = micDeviceIndex,
                WaveFormat = _waveFormat,
                BufferMilliseconds = 100
            };

            _waveIn.DataAvailable += (_, e) =>
            {
                if (_ws.State != WebSocketState.Open) return;
                var base64 = Convert.ToBase64String(e.Buffer, 0, e.BytesRecorded);
                QueueSend(new { type = "input_audio_buffer.append", audio = base64 });
            };

            _waveIn.StartRecording();
        }

        // ── Loopback capture ──
        if (useLoopback)
        {
            StartLoopbackCapture(loopbackDeviceIndex);
        }

        StatusChanged?.Invoke(this, new StatusEventArgs { Message = "Ouvindo..." });

        // ── Receive loop ──
        _ = Task.Run(() => ReceiveLoopAsync(), _cts.Token);
    }

    // ─── LOOPBACK ──────────────────────────────────────────
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

            byte[] converted = ConvertAudioFormat(e.Buffer, e.BytesRecorded, loopbackFormat, _waveFormat);
            if (converted.Length == 0) return;

            var base64 = Convert.ToBase64String(converted);
            QueueSend(new { type = "input_audio_buffer.append", audio = base64 });
        };

        _loopbackCapture.StartRecording();
    }

    [ThreadStatic] private static byte[]? _resampleBuffer;
    [ThreadStatic] private static MemoryStream? _resampleMs;

    private static byte[] ConvertAudioFormat(byte[] sourceBuffer, int bytesRecorded, WaveFormat sourceFormat, WaveFormat targetFormat)
    {
        using var sourceStream = new RawSourceWaveStream(sourceBuffer, 0, bytesRecorded, sourceFormat);
        using var resampler = new MediaFoundationResampler(sourceStream, targetFormat);
        resampler.ResamplerQuality = 60;

        _resampleBuffer ??= new byte[4096];
        var ms = _resampleMs ??= new MemoryStream(16384);
        ms.SetLength(0);

        int read;
        while ((read = resampler.Read(_resampleBuffer, 0, _resampleBuffer.Length)) > 0)
        {
            ms.Write(_resampleBuffer, 0, read);
        }
        return ms.ToArray();
    }

    // ─── RECEIVE LOOP ──────────────────────────────────────
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
                    break;
                }

                using var doc = JsonDocument.Parse(ms.GetBuffer().AsMemory(0, (int)ms.Length));
                var root = doc.RootElement;
                var eventType = root.GetProperty("type").GetString();

                try
                {
                    await ProcessEventAsync(eventType!, root);
                }
                catch (Exception ex)
                {
                    ErrorOccurred?.Invoke(this, new StatusEventArgs { Message = $"Erro '{eventType}': {ex.Message}" });
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

    // ─── EVENT PROCESSING ──────────────────────────────────
    private async Task ProcessEventAsync(string eventType, JsonElement root)
    {
        switch (eventType)
        {
            // ── SESSION LIFECYCLE ──
            case "session.created":
            case "transcription_session.created":
                StatusChanged?.Invoke(this, new StatusEventArgs { Message = "Sessão criada" });
                break;

            case "session.updated":
            case "transcription_session.updated":
                StatusChanged?.Invoke(this, new StatusEventArgs { Message = "Pronto — ouvindo..." });
                break;

            // ── VAD EVENTS ──
            case "input_audio_buffer.speech_started":
                StatusChanged?.Invoke(this, new StatusEventArgs { Message = "Ouvindo fala..." });
                break;

            case "input_audio_buffer.speech_stopped":
                StatusChanged?.Invoke(this, new StatusEventArgs { Message = "Processando..." });
                AnalyzingChanged?.Invoke(this, true);
                break;

            case "input_audio_buffer.committed":
                StatusChanged?.Invoke(this, new StatusEventArgs { Message = "Transcrevendo..." });
                break;

            case "input_audio_buffer.cleared":
                break;

            // ── STREAMING TRANSCRIPTION DELTAS ──
            // Texto aparece ENQUANTO a pessoa fala (com gpt-4o-transcribe)
            case "conversation.item.input_audio_transcription.delta":
                {
                    var itemId = root.TryGetProperty("item_id", out var id) ? id.GetString() ?? "" : "";
                    var delta = root.TryGetProperty("delta", out var d) ? d.GetString() ?? "" : "";

                    if (string.IsNullOrEmpty(delta)) break;

                    if (!_transcriptBuffers.TryGetValue(itemId, out var sb))
                    {
                        sb = new StringBuilder(256);
                        _transcriptBuffers[itemId] = sb;
                    }
                    sb.Append(delta);

                    _currentStreamingItemId = itemId;
                    AnalyzingChanged?.Invoke(this, false);

                    // Envia transcrição parcial (texto original em tempo real)
                    TranscriptReceived?.Invoke(this, new TranscriptEventArgs
                    {
                        Speaker = Speaker.Them,
                        OriginalText = sb.ToString(),
                        TranslatedText = sb.ToString(), // durante streaming, mostra original
                        IsPartial = true
                    });
                    break;
                }

            // ── TRANSCRIPTION COMPLETE ──
            // Frase finalizada → traduz via Chat API
            case "conversation.item.input_audio_transcription.completed":
                {
                    var itemId = root.TryGetProperty("item_id", out var id) ? id.GetString() ?? "" : "";
                    var transcript = root.TryGetProperty("transcript", out var t) ? t.GetString() ?? "" : "";

                    // Usa transcript do evento, ou do buffer acumulado
                    if (string.IsNullOrEmpty(transcript) && _transcriptBuffers.TryGetValue(itemId, out var sb))
                    {
                        transcript = sb.ToString();
                    }

                    // Cleanup buffer
                    _transcriptBuffers.Remove(itemId);

                    if (string.IsNullOrWhiteSpace(transcript))
                    {
                        AnalyzingChanged?.Invoke(this, false);
                        StatusChanged?.Invoke(this, new StatusEventArgs { Message = "Ouvindo..." });
                        break;
                    }

                    // Traduz via Chat API (background)
                    AnalyzingChanged?.Invoke(this, true);
                    StatusChanged?.Invoke(this, new StatusEventArgs { Message = "Traduzindo..." });

                    var translatedText = await TranslateTextAsync(transcript);
                    AnalyzingChanged?.Invoke(this, false);

                    // Envia transcription final com tradução
                    TranscriptReceived?.Invoke(this, new TranscriptEventArgs
                    {
                        Speaker = Speaker.Them,
                        OriginalText = transcript,
                        TranslatedText = string.IsNullOrEmpty(translatedText) ? transcript : translatedText,
                        IsPartial = false
                    });

                    StatusChanged?.Invoke(this, new StatusEventArgs { Message = "Ouvindo..." });
                    break;
                }

            // ── CONVERSATION ITEM EVENTS ──
            case "conversation.item.created":
                break;

            // ── ERROR ──
            case "error":
                {
                    var msg = root.TryGetProperty("error", out var err)
                        ? err.TryGetProperty("message", out var m) ? m.GetString() ?? "Erro" : "Erro"
                        : "Erro desconhecido";
                    ErrorOccurred?.Invoke(this, new StatusEventArgs { Message = msg });
                    break;
                }

            // ── UNKNOWN ──
            default:
                // Log eventos desconhecidos para debug
                System.Diagnostics.Debug.WriteLine($"[TranscriptionService] Evento não tratado: {eventType}");
                ErrorOccurred?.Invoke(this, new StatusEventArgs { Message = $"Evento: {eventType}" });
                break;
        }
    }

    // ─── TRANSLATION VIA CHAT API ──────────────────────────
    /// <summary>
    /// Traduz texto usando OpenAI Chat Completions API.
    /// EN → PT-BR ou PT → EN, detectado automaticamente.
    /// </summary>
    private async Task<string> TranslateTextAsync(string text)
    {
        try
        {
            var requestBody = new
            {
                model = "gpt-4o-mini",
                messages = new[]
                {
                    new
                    {
                        role = "system",
                        content = @"You are a strict translation engine. 
If the input is in English, translate to Brazilian Portuguese.
If the input is in Portuguese, translate to English.
Output ONLY the translation. No explanations, no prefixes, no extra text."
                    },
                    new
                    {
                        role = "user",
                        content = text
                    }
                },
                temperature = 0.3,
                max_tokens = 1000
            };

            var json = JsonSerializer.Serialize(requestBody);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync(ChatApiUrl, content, _cts?.Token ?? CancellationToken.None)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                ErrorOccurred?.Invoke(this, new StatusEventArgs
                {
                    Message = $"Tradução falhou: {response.StatusCode}"
                });
                return "";
            }

            var responseJson = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            using var doc = JsonDocument.Parse(responseJson);
            var translated = doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString() ?? "";

            return translated.Trim();
        }
        catch (OperationCanceledException)
        {
            return "";
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(this, new StatusEventArgs { Message = $"Erro tradução: {ex.Message}" });
            return "";
        }
    }

    // ─── HELPERS ───────────────────────────────────────────
    private void QueueSend(object evt)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(evt);
        _sendChannel?.Writer.TryWrite(bytes);
    }

    // ─── STOP / DISPOSE ────────────────────────────────────
    public async Task StopAsync()
    {
        _cts?.Cancel();

        _waveIn?.StopRecording();
        _loopbackCapture?.StopRecording();
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
        _waveIn?.Dispose();
        _loopbackCapture?.Dispose();
        _ws?.Dispose();
        _cts?.Dispose();
        _httpClient.Dispose();
    }
}
