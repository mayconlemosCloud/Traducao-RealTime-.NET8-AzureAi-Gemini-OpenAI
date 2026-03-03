# PROMPT PARA ANÁLISE DE BUG — LOOP NO INTÉRPRETE ATIVO

Cole tudo abaixo em uma nova sessão de chat.

---

## CONTEXTO DO PROJETO

Aplicação WPF (.NET 9.0, C#) que usa a **OpenAI Realtime API** via WebSocket para tradução de voz em tempo real. Usa **NAudio** para captura de mic e playback.

Existem 2 serviços independentes, cada um com sua própria conexão WebSocket:

1. **RealtimeService** — tradução bidirecional de reunião (mic + loopback de sistema). **FUNCIONA SEM LOOP.**
2. **SpeakTranslateService** — intérprete ativo: usuário fala PT-BR no mic → IA responde em EN no output device escolhido. **TEM BUG DE LOOP.**

Ambos usam `turn_detection = null` (client-side VAD com commit manual).

## O BUG

Quando o usuário fala a primeira frase, a tradução funciona perfeitamente. Porém, **após algum tempo** (segundos a minutos), o sistema entra em loop: a IA começa a gerar respostas sozinha, sem input real do usuário.

**Comportamento observado:**
- Primeira fala → tradução OK → playback OK → silêncio → OK
- Depois de um tempo → IA começa a falar sozinha → loop infinito
- O loop PERSISTE mesmo estando em silêncio total
- O loop é "progressivo" — piora com o tempo

**5 tentativas de correção já foram feitas (todas falharam):**
1. Mic gate simples (`if (_isPlaying) return`) — não resolveu
2. Reescrita completa para client-side VAD com `turn_detection = null` — não resolveu
3. Timestamp-based cooldown com `IsMicBlocked` property — não resolveu
4. Threshold aumentado de 300→1000, frame counting mínimo de 5 frames, cooldown de 1.5s — não resolveu
5. Validação de tamanho mínimo de response audio (MinResponseAudioBytes) — não resolveu

## POR QUE O RealtimeService NÃO TEM ESSE PROBLEMA

No RealtimeService:
- O mic **NUNCA é gatado** — sempre envia (full-duplex real)
- O **LOOPBACK** é gatado durante playback (impede feedback via sistema)
- Com fones de ouvido, não há vazamento speaker → mic
- O mic sempre tem fala real de reunião — threshold 300 funciona
- No `response.done`, verifica `_voiceDetectedDuringPlayback` e só limpa buffer se ninguém falou

No SpeakTranslateService (o com bug):
- O **MIC é gatado** durante playback + cooldown (diferente do Realtime)
- Entre traduções, o mic fica em **SILÊNCIO TOTAL** — só ruído ambient
- O output audio pode vazar para o mic (speaker → mic echo)
- Mesmo com threshold 1000, algo continua causando o loop

## HIPÓTESES PENDENTES DE INVESTIGAÇÃO

1. **O áudio que já foi enviado antes do gate ativar** — entre o momento que `response.audio.delta` começa e `_isPlaying = true` ser setado, pode existir uma janela onde o mic ainda está enviando. Esse áudio fica no buffer do servidor e pode conter eco do início do playback.

2. **O `input_audio_buffer.clear` não funciona como esperado** — talvez o clear não limpe o áudio já commitado no histórico. A OpenAI pode manter items de conversa (conversation items) que acumulam e fazem o modelo gerar responses baseadas em histórico de ruído.

3. **Conversation history buildup** — cada `input_audio_buffer.commit` + `response.create` cria um novo turn na conversa. Mesmo que o áudio commitado seja silêncio/ruído, o modelo "vê" um turn de input e gera um turn de output. Com o tempo, o histórico cresce e o modelo fica mais propenso a gerar algo.

4. **O `response.done` e `response.audio.done` podem chegar em ordem inesperada** — se `response.done` chega antes do `response.audio.done` terminar o cleanup, o `ProcessNextQueuedResponse` pode disparar a próxima response com o estado sujo.

5. **O silence timer pode estar commitando durante o cleanup** — mesmo com `if (IsMicBlocked) return`, o timer roda a cada 500ms. Se `_hasUncommittedAudio` ficou true de um ciclo anterior e o timing é quase exato, pode commitar no momento errado.

6. **O _hasUncommittedAudio é setado para true ANTES de verificar frames mínimos** — no mic handler, qualquer frame com RMS > 1000 seta `_hasUncommittedAudio = true`. O frame counting (`_voiceActiveFrames`) é incrementado mas NUNCA É VERIFICADO no `CheckSilenceAndCommit`. O commit acontece baseado apenas em `voiceDuration` (timestamp diff), não em frame count. Os 5 frames mínimos (`MinVoiceFrames`) são declarados mas NUNCA USADOS.

## CÓDIGO ATUAL COMPLETO — SpeakTranslateService.cs

```csharp
using System.Buffers;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using NAudio.Wave;
using MeetingTranslator.Models;

namespace MeetingTranslator.Services;

/// <summary>
/// Intérprete Ativo: usuário fala PT-BR → IA fala EN no dispositivo de saída escolhido.
///
/// ╔══════════════════════════════════════════════════════════════════╗
/// ║  Diferença FUNDAMENTAL vs RealtimeService:                       ║
/// ║                                                                  ║
/// ║  RealtimeService:                                                ║
/// ║  - Mic SEMPRE envia (nunca gatado)                               ║
/// ║  - LOOPBACK é gatado (impede captar output da IA via sistema)    ║
/// ║  - Com fones, o speaker não vaza pro mic físico                  ║
/// ║                                                                  ║
/// ║  SpeakTranslateService (ESTE):                                   ║
/// ║  - Não tem loopback (só mic)                                     ║
/// ║  - O OUTPUT da IA vaza para o MIC FÍSICO (eco/speaker)           ║
/// ║  - Portanto o MIC precisa ser gatado durante playback            ║
/// ║  - E precisa de COOLDOWN LONGO após playback (eco ambiente)      ║
/// ║  - E o buffer deve ser limpo APÓS o cooldown                     ║
/// ║                                                                  ║
/// ║  A proteção usa 3 camadas:                                       ║
/// ║  1. Mic gate: não envia áudio durante _isPlaying                 ║
/// ║  2. Cooldown: após playback, mic fica bloqueado por N segundos   ║
/// ║  3. Buffer clear: após cooldown, limpa buffer residual           ║
/// ║  4. Silence timer: não commita durante playback + cooldown       ║
/// ╚══════════════════════════════════════════════════════════════════╝
/// </summary>
public class SpeakTranslateService : IDisposable
{
    // ─── CONFIG ────────────────────────────────────────────
    private const int SampleRate = 24000;
    private const int Channels = 1;
    private const int BitsPerSample = 16;
    private const string WsUrl = "wss://api.openai.com/v1/realtime?model=gpt-4o-realtime-preview";

    // ─── STATE ─────────────────────────────────────────────
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private Channel<byte[]>? _sendChannel;

    private WaveInEvent? _waveIn;
    private WaveOutEvent? _waveOut;
    private BufferedWaveProvider? _bufferProvider;
    private volatile bool _isPlaying;

    private readonly string _apiKey;
    private readonly WaveFormat _waveFormat = new(SampleRate, BitsPerSample, Channels);
    private readonly StringBuilder _transcriptBuilder = new(256);

    // ─── PLAYBACK COOLDOWN ─────────────────────────────────
    private DateTime _playbackEndedAt = DateTime.MinValue;
    private const double PlaybackCooldownSeconds = 1.5;

    private bool IsMicBlocked =>
        _isPlaying ||
        (DateTime.UtcNow - _playbackEndedAt).TotalSeconds < PlaybackCooldownSeconds;

    // ─── RESPONSE QUEUE ────────────────────────────────────
    private bool _responseInProgress;
    private int _pendingResponseCount;
    private readonly object _responseLock = new();

    // ─── CLIENT-SIDE VAD ───────────────────────────────────
    private DateTime _lastVoiceActivity = DateTime.UtcNow;
    private DateTime _firstVoiceActivity = DateTime.UtcNow;
    private bool _hasUncommittedAudio;
    private Timer? _silenceTimer;
    private const double SilenceThresholdSeconds = 1.8;
    private const float VoiceEnergyThreshold = 1000f;
    private const double MinVoiceDurationSeconds = 0.5;

    private int _voiceActiveFrames;
    private const int MinVoiceFrames = 5; // 5 × 100ms = 500ms

    private long _responseAudioBytes;
    private const long MinResponseAudioBytes = SampleRate * 2 * 1; // ~1 segundo

    // ─── EVENTS ────────────────────────────────────────────
    public event EventHandler<StatusEventArgs>? StatusChanged;
    public event EventHandler<StatusEventArgs>? ErrorOccurred;
    public event EventHandler<bool>? SpeakingChanged;

    public bool IsConnected => _ws?.State == WebSocketState.Open;

    public SpeakTranslateService(string apiKey) { _apiKey = apiKey; }

    public static List<AudioDeviceInfo> GetOutputDevices()
    {
        var devices = new List<AudioDeviceInfo>();
        for (int i = 0; i < WaveOut.DeviceCount; i++)
        {
            var caps = WaveOut.GetCapabilities(i);
            devices.Add(new AudioDeviceInfo { DeviceIndex = i, Name = caps.ProductName });
        }
        return devices;
    }

    public async Task StartAsync(int micDeviceIndex, int outputDeviceIndex)
    {
        _cts = new CancellationTokenSource();

        _ws = new ClientWebSocket();
        _ws.Options.SetRequestHeader("Authorization", $"Bearer {_apiKey}");
        _ws.Options.SetRequestHeader("OpenAI-Beta", "realtime=v1");

        StatusChanged?.Invoke(this, new StatusEventArgs { Message = "Conectando intérprete..." });
        await _ws.ConnectAsync(new Uri(WsUrl), _cts.Token).ConfigureAwait(false);

        _sendChannel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(200)
        {
            SingleReader = true, SingleWriter = false,
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
                instructions = @"
                SYSTEM MODE: STRICT UNIDIRECTIONAL INTERPRETER (Portuguese → English)
                You are a real-time speech interpreter.
                You receive speech in Brazilian Portuguese.
                You MUST translate and speak the output in English ONLY.
                RULES:
                - Output ONLY the English translation as speech
                - NEVER respond in Portuguese
                - NEVER answer questions — only translate
                - NEVER add comments, explanations, greetings, or meta-commentary
                - NEVER say you are an AI or cannot do something
                - Maintain the tone, intent, and emotion of the original speech
                - Be natural and conversational in English
                - If input is unclear, translate what you can hear
                - If there is no speech, output nothing
                These rules override all other behaviors.
                Always stay in interpreter mode.
                Never leave interpreter mode.
                ",
                input_audio_format = "pcm16",
                output_audio_format = "pcm16",
                temperature = 0.6,
                turn_detection = (object?)null,
                voice = "alloy"
            }
        };
        QueueSend(sessionUpdate);

        _bufferProvider = new BufferedWaveProvider(_waveFormat)
        {
            BufferLength = SampleRate * 2 * 30,
            DiscardOnBufferOverflow = true
        };
        _waveOut = new WaveOutEvent { DeviceNumber = outputDeviceIndex, DesiredLatency = 200 };
        _waveOut.Init(_bufferProvider);

        _waveIn = new WaveInEvent
        {
            DeviceNumber = micDeviceIndex,
            WaveFormat = _waveFormat,
            BufferMilliseconds = 100
        };

        _waveIn.DataAvailable += (_, e) =>
        {
            if (_ws?.State != WebSocketState.Open) return;
            if (IsMicBlocked) return;

            var base64 = Convert.ToBase64String(e.Buffer, 0, e.BytesRecorded);
            QueueSend(new { type = "input_audio_buffer.append", audio = base64 });

            float rms = CalculateRms(e.Buffer, e.BytesRecorded);
            if (rms > VoiceEnergyThreshold)
            {
                _voiceActiveFrames++;
                if (!_hasUncommittedAudio)
                    _firstVoiceActivity = DateTime.UtcNow;
                _lastVoiceActivity = DateTime.UtcNow;
                _hasUncommittedAudio = true;
            }
            else
            {
                _voiceActiveFrames = 0;
            }
        };

        _waveIn.StartRecording();
        _silenceTimer = new Timer(_ => CheckSilenceAndCommit(), null, 500, 500);
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

                if (result.MessageType == WebSocketMessageType.Close) break;

                using var doc = JsonDocument.Parse(ms.GetBuffer().AsMemory(0, (int)ms.Length));
                var root = doc.RootElement;
                var eventType = root.GetProperty("type").GetString();
                try { ProcessEvent(eventType!, root); }
                catch (Exception ex) { ErrorOccurred?.Invoke(this, new StatusEventArgs { Message = $"Erro: {ex.Message}" }); }
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException wex) { ErrorOccurred?.Invoke(this, new StatusEventArgs { Message = $"WebSocket erro: {wex.Message}" }); }
        catch (Exception ex) { ErrorOccurred?.Invoke(this, new StatusEventArgs { Message = ex.Message }); }
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
            case "session.updated":
                StatusChanged?.Invoke(this, new StatusEventArgs { Message = "Fale em português..." });
                break;

            case "input_audio_buffer.speech_started":
                SpeakingChanged?.Invoke(this, true);
                break;

            case "input_audio_buffer.speech_stopped":
                SpeakingChanged?.Invoke(this, false);
                break;

            case "input_audio_buffer.committed":
                StatusChanged?.Invoke(this, new StatusEventArgs { Message = "Analisando..." });
                break;

            case "response.created":
                StatusChanged?.Invoke(this, new StatusEventArgs { Message = "Gerando fala EN..." });
                break;

            case "response.audio.delta":
                if (root.TryGetProperty("delta", out var deltaEl))
                {
                    var delta = deltaEl.GetString();
                    if (delta != null)
                    {
                        var audioBytes = Convert.FromBase64String(delta);
                        _responseAudioBytes += audioBytes.Length;
                        _bufferProvider?.AddSamples(audioBytes, 0, audioBytes.Length);
                        if (!_isPlaying)
                        {
                            _isPlaying = true;
                            _waveOut?.Play();
                        }
                    }
                }
                break;

            case "response.audio_transcript.delta":
                if (root.TryGetProperty("delta", out var textDelta))
                {
                    var text = textDelta.GetString();
                    if (!string.IsNullOrEmpty(text))
                    {
                        _transcriptBuilder.Append(text);
                        StatusChanged?.Invoke(this, new StatusEventArgs { Message = $"🔊 {_transcriptBuilder}" });
                    }
                }
                break;

            case "response.audio_transcript.done":
                _transcriptBuilder.Clear();
                break;

            case "response.audio.done":
                _ = Task.Run(async () =>
                {
                    try
                    {
                        // Verificação de response fantasma
                        if (_responseAudioBytes < MinResponseAudioBytes)
                        {
                            _bufferProvider?.ClearBuffer();
                            _isPlaying = false;
                            _playbackEndedAt = DateTime.UtcNow;
                            await Task.Delay(500, _cts!.Token).ConfigureAwait(false);
                            if (_ws?.State == WebSocketState.Open)
                                QueueSend(new { type = "input_audio_buffer.clear" });
                            _hasUncommittedAudio = false;
                            _voiceActiveFrames = 0;
                            _responseAudioBytes = 0;
                            StatusChanged?.Invoke(this, new StatusEventArgs { Message = "Fale em português..." });
                            return;
                        }

                        // 1) Espera buffer de playback drenar
                        while ((_bufferProvider?.BufferedBytes ?? 0) > 0
                               && !(_cts?.Token.IsCancellationRequested ?? true))
                            await Task.Delay(100, _cts!.Token).ConfigureAwait(false);

                        // 2) Marca fim do playback
                        _isPlaying = false;

                        // 3) Inicia cooldown
                        _playbackEndedAt = DateTime.UtcNow;

                        // 4) Espera cooldown expirar
                        var cooldownMs = (int)(PlaybackCooldownSeconds * 1000);
                        await Task.Delay(cooldownMs, _cts!.Token).ConfigureAwait(false);

                        // 5) Limpa buffer do servidor
                        if (_ws?.State == WebSocketState.Open)
                            QueueSend(new { type = "input_audio_buffer.clear" });

                        // 6) Reseta VAD
                        _hasUncommittedAudio = false;
                        _voiceActiveFrames = 0;
                        _responseAudioBytes = 0;
                    }
                    catch (OperationCanceledException) { }

                    StatusChanged?.Invoke(this, new StatusEventArgs { Message = "Fale em português..." });
                }, _cts!.Token);
                break;

            case "response.done":
                ProcessNextQueuedResponse();
                break;

            case "error":
                var msg = root.TryGetProperty("error", out var err)
                    ? err.TryGetProperty("message", out var m) ? m.GetString() ?? "Erro" : "Erro"
                    : "Erro desconhecido";
                ErrorOccurred?.Invoke(this, new StatusEventArgs { Message = msg });
                break;
        }
    }

    private void CheckSilenceAndCommit()
    {
        if (!_hasUncommittedAudio) return;
        if (_ws?.State != WebSocketState.Open) return;
        if (IsMicBlocked) return;

        var silenceDuration = (DateTime.UtcNow - _lastVoiceActivity).TotalSeconds;
        if (silenceDuration >= SilenceThresholdSeconds)
        {
            _hasUncommittedAudio = false;
            _voiceActiveFrames = 0;

            var voiceDuration = (_lastVoiceActivity - _firstVoiceActivity).TotalSeconds;
            if (voiceDuration < MinVoiceDurationSeconds)
            {
                QueueSend(new { type = "input_audio_buffer.clear" });
                return;
            }

            _responseAudioBytes = 0;
            QueueSend(new { type = "input_audio_buffer.commit" });

            lock (_responseLock)
            {
                if (!_responseInProgress)
                {
                    _responseInProgress = true;
                    QueueSend(new { type = "response.create" });
                }
                else
                {
                    _pendingResponseCount++;
                }
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
                QueueSend(new { type = "response.create" });
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

    private static float CalculateRms(byte[] buffer, int bytesRecorded)
    {
        if (bytesRecorded < 2) return 0f;
        long sumSquares = 0;
        int sampleCount = bytesRecorded / 2;
        for (int i = 0; i < bytesRecorded - 1; i += 2)
        {
            short sample = (short)(buffer[i] | (buffer[i + 1] << 8));
            sumSquares += (long)sample * sample;
        }
        return (float)Math.Sqrt((double)sumSquares / sampleCount);
    }

    public async Task StopAsync()
    {
        _cts?.Cancel();
        _silenceTimer?.Dispose();
        _silenceTimer = null;
        _waveIn?.StopRecording();
        _waveOut?.Stop();
        _sendChannel?.Writer.Complete();
        if (_ws?.State == WebSocketState.Open)
        {
            try { await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None); }
            catch { }
        }
        StatusChanged?.Invoke(this, new StatusEventArgs { Message = "Desconectado" });
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _silenceTimer?.Dispose();
        _waveIn?.Dispose();
        _waveOut?.Dispose();
        _ws?.Dispose();
        _cts?.Dispose();
    }
}
```

## CÓDIGO DE REFERÊNCIA — RealtimeService.cs (FUNCIONA SEM LOOP)

Diferenças-chave vs SpeakTranslateService:

```csharp
// MIC HANDLER — NUNCA gatado, sempre envia:
_waveIn.DataAvailable += (_, e) =>
{
    if (_ws.State != WebSocketState.Open) return;
    // Sempre envia — NUNCA checa IsMicBlocked
    var base64 = Convert.ToBase64String(e.Buffer, 0, e.BytesRecorded);
    QueueSend(new { type = "input_audio_buffer.append", audio = base64 });

    float rms = CalculateRms(e.Buffer, e.BytesRecorded);
    if (rms > VoiceEnergyThreshold) // threshold = 300 (funciona pq tem fala real)
    {
        if (!_hasUncommittedAudio)
            _firstVoiceActivity = DateTime.UtcNow;
        _lastVoiceActivity = DateTime.UtcNow;
        _hasUncommittedAudio = true;
        if (_isPlaying) _voiceDetectedDuringPlayback = true;
    }
};

// LOOPBACK — gatado durante playback:
_loopbackCapture.DataAvailable += (_, e) =>
{
    if (_isPlaying) return; // <── SÓ o loopback é gatado, não o mic
    // ... converte e envia
};

// response.audio.done — SEM cooldown, SEM buffer clear:
case "response.audio.done":
    _ = Task.Run(async () =>
    {
        while ((_bufferProvider?.BufferedBytes ?? 0) > 0 && !_cts!.Token.IsCancellationRequested)
            await Task.Delay(100, _cts.Token).ConfigureAwait(false);
        _isPlaying = false;
        _playedAudioBytes = 0;
        // NÃO faz clear, NÃO faz cooldown
    }, _cts!.Token);
    break;

// response.done — verifica se user falou durante playback:
case "response.done":
    ProcessNextQueuedResponse();
    lock (_responseLock)
    {
        if (!_responseInProgress)
        {
            if (_hasUncommittedAudio || _voiceDetectedDuringPlayback)
            {
                _voiceDetectedDuringPlayback = false;
                // NÃO limpa buffer — áudio do user está lá
            }
            else
            {
                // Ninguém falou — limpa ruído acumulado
                QueueSend(new { type = "input_audio_buffer.clear" });
                _hasUncommittedAudio = false;
            }
        }
    }
    break;
```

## TAREFA

Analise o SpeakTranslateService.cs e identifique a causa raiz REAL do loop. O problema persiste após 5 tentativas de correção.

**Foque especificamente nestes pontos:**

1. **O `_voiceActiveFrames` é incrementado mas `MinVoiceFrames` nunca é verificado em `CheckSilenceAndCommit`** — o commit acontece baseado apenas em `voiceDuration` (timestamp diff). O frame counting é totalmente inútil no código atual. Isso é um bug?

2. **O mic envia áudio ANTES de `_isPlaying = true`** — quando `response.audio.delta` chega, o primeiro frame seta `_isPlaying = true`. Mas entre o primeiro delta e o gate no mic handler, quantos frames de mic já foram enviados contendo eco do início do playback? Existe uma race condition aqui?

3. **O `input_audio_buffer.clear` limpa o que exatamente?** — limpa apenas o buffer uncommitted? Ou limpa também conversation items já commitados? Se não limpa conversation items, o histórico acumula e o modelo gera respostas baseadas em commits anteriores de ruído/silêncio.

4. **O `_hasUncommittedAudio` é setado para true por UM ÚNICO frame com RMS > 1000** — não precisa de frames consecutivos. Um spike de ruído pode setar `_hasUncommittedAudio = true`, e se o timestamp diff (`_lastVoiceActivity - _firstVoiceActivity`) for > 0.5s (por causa de spikes separados por tempo), o commit acontece.

5. **A ordem `response.done` vs `response.audio.done`** — se `response.done` chega primeiro (antes do cleanup terminar), `ProcessNextQueuedResponse` pode setar `_responseInProgress = false`. Depois, quando o cleanup termina e limpa o buffer, qualquer novo ruído pode criar um novo commit + response.create descontrolado.

6. **Abordagem alternativa**: seria melhor NÃO enviar áudio para o servidor enquanto `IsMicBlocked`? Atualmente o mic gate bloqueia o envio, mas e se a abordagem correta fosse enviar sempre (como RealtimeService) e usar um `conversation.item.delete` para limpar histórico de ruído?

7. **Abordagem alternativa 2**: seria melhor usar `response.cancel` quando detector que a response é fantasma, em vez de apenas limpar buffer?

**Implemente a correção completa no código, não apenas sugira.**

**Responda em português.**
