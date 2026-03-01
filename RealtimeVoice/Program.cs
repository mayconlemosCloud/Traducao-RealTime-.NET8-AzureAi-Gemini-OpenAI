using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using NAudio.Wave;
using dotenv.net;

// ─── CONFIG ────────────────────────────────────────────────────────────────────
DotEnv.Load();
var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY")
    ?? throw new Exception("Defina a variável OPENAI_API_KEY");

const int SampleRate = 24000;   // exigido pela API
const int Channels = 1;
const int BitsPerSample = 16;
const int BytesPerSecond = SampleRate * (BitsPerSample / 8) * Channels; // 48000 B/s
const string WsUrl = "wss://api.openai.com/v1/realtime?model=gpt-4o-realtime-preview";

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

// ─── WEBSOCKET ─────────────────────────────────────────────────────────────────
using var ws = new ClientWebSocket();
ws.Options.SetRequestHeader("Authorization", $"Bearer {apiKey}");
ws.Options.SetRequestHeader("OpenAI-Beta", "realtime=v1");

Console.WriteLine("Conectando ao OpenAI Realtime...");
await ws.ConnectAsync(new Uri(WsUrl), cts.Token);
Console.WriteLine("Conectado!");

// ─── THREAD-SAFE SEND QUEUE ───────────────────────────────────────────────────
// ClientWebSocket.SendAsync NÃO é thread-safe. Usamos um Channel para serializar
// todos os envios em uma única task, evitando corrupção e travamentos.
var sendChannel = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions
{
    SingleReader = true,
    SingleWriter = false
});

_ = Task.Run(async () =>
{
    await foreach (var msg in sendChannel.Reader.ReadAllAsync(cts.Token))
    {
        try
        {
            if (ws.State == WebSocketState.Open)
                await ws.SendAsync(msg, WebSocketMessageType.Text, true, cts.Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Ignorar erros de envio se o websocket fechou
        }
    }
}, cts.Token);

void QueueSend(object evt)
{
    var json = JsonSerializer.Serialize(evt);
    var bytes = Encoding.UTF8.GetBytes(json);
    sendChannel.Writer.TryWrite(bytes);
}

// ─── SESSION UPDATE ────────────────────────────────────────────────────────────
// Documentação recomenda semantic_vad: usa classificador semântico para detectar
// fim de fala, reduzindo drasticamente falsos positivos (como eco do alto-falante).
// eagerness = "low" → deixa o usuário terminar de falar antes de responder.
var sessionUpdate = new
{
    type = "session.update",
    session = new
    {
        modalities = new[] { "audio", "text" },
        instructions = "Você é um assistente amigável. Responda em português do Brasil de forma breve.",
        input_audio_format = "pcm16",
        output_audio_format = "pcm16",
        turn_detection = new
        {
            type = "semantic_vad",
            eagerness = "low"
        },
        voice = "alloy"
    }
};
QueueSend(sessionUpdate);

// ─── SPEAKER (playback) ───────────────────────────────────────────────────────
var waveFormat = new WaveFormat(SampleRate, BitsPerSample, Channels);
using var waveOut = new WaveOutEvent { DesiredLatency = 200 };
var bufferProvider = new BufferedWaveProvider(waveFormat)
{
    BufferLength = SampleRate * 2 * 30,  // 30 segundos de buffer
    DiscardOnBufferOverflow = true
};
waveOut.Init(bufferProvider);
// NÃO chamar waveOut.Play() aqui — iniciaremos quando receber o primeiro chunk

// ─── ESTADO DE PLAYBACK ───────────────────────────────────────────────────────
// Rastrear o item_id da resposta atual para enviar conversation.item.truncate
// quando houver interrupção (conforme documentação de WebSocket).
var isPlaying = false;          // se estamos reproduzindo áudio da IA
var currentItemId = "";         // item_id da resposta em andamento
var playedAudioBytes = 0L;      // bytes de áudio já enviados ao buffer de playback
var micMuted = false;           // mutar mic enquanto IA fala (evita echo/feedback)

// ─── MIC (capture) ─────────────────────────────────────────────────────────────
using var waveIn = new WaveInEvent
{
    WaveFormat = waveFormat,
    BufferMilliseconds = 100   // chunks de 100ms
};

waveIn.DataAvailable += (_, e) =>
{
    if (ws.State != WebSocketState.Open) return;

    // Mutar mic enquanto IA está falando para evitar que o som do speaker
    // seja captado pelo mic → evita falsos "speech_started" do VAD
    if (micMuted) return;

    var base64 = Convert.ToBase64String(e.Buffer, 0, e.BytesRecorded);
    QueueSend(new { type = "input_audio_buffer.append", audio = base64 });
};

waveIn.StartRecording();
Console.WriteLine("Microfone ativo. Fale algo! (Ctrl+C para sair)");

// ─── RECEIVE LOOP ──────────────────────────────────────────────────────────────
var recvBuffer = new byte[1024 * 128];  // buffer maior para receber mensagens grandes

try
{
    while (ws.State == WebSocketState.Open && !cts.Token.IsCancellationRequested)
    {
        using var ms = new MemoryStream();
        WebSocketReceiveResult result;

        do
        {
            result = await ws.ReceiveAsync(recvBuffer, cts.Token);
            ms.Write(recvBuffer, 0, result.Count);
        }
        while (!result.EndOfMessage);

        if (result.MessageType == WebSocketMessageType.Close) break;

        var json = Encoding.UTF8.GetString(ms.ToArray());
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var eventType = root.GetProperty("type").GetString();

        switch (eventType)
        {
            case "session.created":
                Console.WriteLine("[Sessão criada]");
                break;

            case "session.updated":
                Console.WriteLine("[Sessão configurada]");
                break;

            case "input_audio_buffer.speech_started":
                Console.WriteLine("[Você está falando...]");

                // ── INTERRUPTION HANDLING (documentação: "Interruption and Truncation") ──
                // 1. Parar playback imediatamente
                if (isPlaying)
                {
                    waveOut.Pause();

                    // 2. Calcular quantos ms de áudio foram reproduzidos
                    //    BufferedWaveProvider.BufferedBytes = bytes NÃO reproduzidos ainda
                    var bufferedNotPlayed = bufferProvider.BufferedBytes;
                    var totalPlayedBytes = playedAudioBytes - bufferedNotPlayed;
                    if (totalPlayedBytes < 0) totalPlayedBytes = 0;
                    var playedMs = (int)(totalPlayedBytes * 1000L / BytesPerSecond);

                    // 3. Limpar buffer de playback
                    bufferProvider.ClearBuffer();

                    // 4. Enviar conversation.item.truncate conforme documentação
                    if (!string.IsNullOrEmpty(currentItemId))
                    {
                        QueueSend(new
                        {
                            type = "conversation.item.truncate",
                            item_id = currentItemId,
                            content_index = 0,
                            audio_end_ms = playedMs
                        });
                    }

                    isPlaying = false;
                    playedAudioBytes = 0;
                    currentItemId = "";
                }
                // Desmutar mic para capturar a fala do usuário
                micMuted = false;
                break;

            case "input_audio_buffer.speech_stopped":
                Console.WriteLine("[Fala detectada, processando...]");
                break;

            case "response.output_item.added":
                // Capturar o item_id do item de resposta (necessário para truncation)
                if (root.TryGetProperty("item", out var item) &&
                    item.TryGetProperty("id", out var itemId))
                {
                    currentItemId = itemId.GetString() ?? "";
                }
                break;

            case "response.audio.delta":
                // Áudio de resposta em base64 PCM16
                var delta = root.GetProperty("delta").GetString();
                if (delta != null)
                {
                    var audioBytes = Convert.FromBase64String(delta);
                    bufferProvider.AddSamples(audioBytes, 0, audioBytes.Length);
                    playedAudioBytes += audioBytes.Length;

                    // Mutar mic enquanto IA fala (evita feedback/echo)
                    micMuted = true;

                    // Iniciar playback no primeiro chunk de áudio
                    if (!isPlaying)
                    {
                        isPlaying = true;
                        waveOut.Play();
                    }
                }
                break;

            case "response.audio_transcript.delta":
                // Transcrição parcial da resposta do modelo
                var text = root.GetProperty("delta").GetString();
                Console.Write(text);
                break;

            case "response.audio_transcript.done":
                Console.WriteLine();
                break;

            case "response.audio.done":
                // Todo o áudio da resposta foi recebido — o player vai terminar
                // de reproduzir o que ainda resta no buffer.
                // Agendar desmutar o mic quando o buffer esvaziar.
                _ = Task.Run(async () =>
                {
                    // Esperar o buffer de playback esvaziar
                    while (bufferProvider.BufferedBytes > 0 && !cts.Token.IsCancellationRequested)
                        await Task.Delay(100, cts.Token).ConfigureAwait(false);

                    micMuted = false;
                    isPlaying = false;
                    playedAudioBytes = 0;
                }, cts.Token);
                break;

            case "response.done":
                Console.WriteLine("[Resposta completa]");
                break;

            case "error":
                var msg = root.GetProperty("error").GetProperty("message").GetString();
                Console.WriteLine($"[ERRO] {msg}");
                break;
        }
    }
}
catch (OperationCanceledException) { }
finally
{
    waveIn.StopRecording();
    waveOut.Stop();
    sendChannel.Writer.Complete();

    if (ws.State == WebSocketState.Open)
        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);

    Console.WriteLine("Desconectado.");
}
