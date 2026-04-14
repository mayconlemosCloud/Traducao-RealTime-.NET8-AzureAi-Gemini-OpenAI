using MeetingTranslator.Models;
using MeetingTranslator.Services.Common;
using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;
using Microsoft.CognitiveServices.Speech.Transcription;
using Microsoft.CognitiveServices.Speech.Translation;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using System.Linq;

namespace MeetingTranslator.Services.Azure;

/// <summary>
/// Transcrição em tempo real (Azure Speech SDK).
/// Usa ConversationTranscriber para mic e TranslationRecognizer para loopback (Live Captions reais).
/// Auto-reconecta em caso de erros de sessão.
/// </summary>
public sealed class AzureTranscriptionService : IDisposable
{
    private readonly string _speechKey;
    private readonly string _speechRegion;
    private readonly AzureTranslatorClient _translator;

    // ── Mic (Usa ConversationTranscriber para manter diarização se for multiuso presencial) ──
    private ConversationTranscriber? _micTranscriber;
    private AudioConfig? _micAudioConfig;

    // ── Loopback (Live Captions Streaming) ──
    private TranslationRecognizer? _loopbackRecognizer;
    private AudioConfig? _loopbackAudioConfig;
    private WasapiLoopbackCapture? _loopbackCapture;
    private PushAudioInputStream? _loopbackPushStream;

    // ── Estado ──
    private CancellationTokenSource _cts = new();
    private bool _isConnected;
    private bool _isDisposed;

    // Parâmetros do último Start para poder reconectar
    private int _lastMicIndex;
    private int _lastLoopbackIndex;
    private bool _lastUseMic;
    private bool _lastUseLoopback;

    // Controle de reconnect por canal
    private int _micReconnectAttempts;
    private int _loopbackReconnectAttempts;
    private const int MaxReconnectAttempts = 5;

    // ── Eventos públicos ──
    public event EventHandler<TranscriptEventArgs>? TranscriptReceived;
    public event EventHandler<StatusEventArgs>? StatusChanged;
    public event EventHandler<StatusEventArgs>? ErrorOccurred;
    public event EventHandler<bool>? AnalyzingChanged;

    public bool IsConnected => _isConnected;

    public AzureTranscriptionService(string speechKey, string speechRegion)
    {
        _speechKey = speechKey;
        _speechRegion = speechRegion;
        _translator = new AzureTranslatorClient(speechKey, speechRegion);
    }

    // ─────────────────────────────────────────────────────────────────
    //  Start / Stop
    // ─────────────────────────────────────────────────────────────────

    public async Task StartAsync(int micDeviceIndex, int loopbackDeviceIndex, bool useMic, bool useLoopback)
    {
        _cts = new CancellationTokenSource();
        _lastMicIndex = micDeviceIndex;
        _lastLoopbackIndex = loopbackDeviceIndex;
        _lastUseMic = useMic;
        _lastUseLoopback = useLoopback;
        _micReconnectAttempts = 0;
        _loopbackReconnectAttempts = 0;

        StatusChanged?.Invoke(this, new StatusEventArgs { Message = "Conectando Azure..." });

        try
        {
            if (useMic)
                await StartMicTranscriberAsync(micDeviceIndex).ConfigureAwait(false);

            if (useLoopback)
                await StartLoopbackRecognizerAsync(loopbackDeviceIndex).ConfigureAwait(false);

            _isConnected = true;
            StatusChanged?.Invoke(this, new StatusEventArgs { Message = "Pronto — ouvindo..." });
        }
        catch (Exception ex)
        {
            Log($"Erro em StartAsync: {ex.Message}");
            ErrorOccurred?.Invoke(this, new StatusEventArgs { Message = $"Erro ao iniciar: {ex.Message}" });
        }
    }

    public async Task StopAsync()
    {
        _cts.Cancel();

        try { if (_micTranscriber != null) await _micTranscriber.StopTranscribingAsync().ConfigureAwait(false); } catch { }
        try { if (_loopbackRecognizer != null) await _loopbackRecognizer.StopContinuousRecognitionAsync().ConfigureAwait(false); } catch { }
        try { _loopbackCapture?.StopRecording(); } catch { }

        DisposeTranscribers();
        _isConnected = false;
        StatusChanged?.Invoke(this, new StatusEventArgs { Message = "Desconectado" });
    }

    // ─────────────────────────────────────────────────────────────────
    //  Start: Microfone
    // ─────────────────────────────────────────────────────────────────

    private async Task StartMicTranscriberAsync(int micDeviceIndex)
    {
        var config = BuildSpeechConfig("pt-BR");
        _micAudioConfig = ResolveMicrophoneAudioConfig(micDeviceIndex);
        _micTranscriber = new ConversationTranscriber(config, _micAudioConfig);

        WireEventsMic(_micTranscriber, Speaker.You, "pt-BR", "en", "Mic");

        await _micTranscriber.StartTranscribingAsync().ConfigureAwait(false);
        Log("Mic: StartTranscribingAsync OK");
    }

    // ─────────────────────────────────────────────────────────────────
    //  Start: Loopback (Live Captions via TranslationRecognizer)
    // ─────────────────────────────────────────────────────────────────

    private async Task StartLoopbackRecognizerAsync(int loopbackDeviceIndex)
    {
        // 1. Encontrar dispositivo de renderização selecionado
        var enumerator = new MMDeviceEnumerator();
        var renderDevices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).ToList();

        MMDevice? chosen = null;
        var uiDevices = AudioHelper.GetLoopbackDevices();
        var selected = uiDevices.FirstOrDefault(d => d.DeviceIndex == loopbackDeviceIndex);

        if (selected != null && !string.IsNullOrWhiteSpace(selected.Name))
        {
            chosen = renderDevices.FirstOrDefault(d => d.FriendlyName.Equals(selected.Name, StringComparison.OrdinalIgnoreCase))
                  ?? renderDevices.FirstOrDefault(d => d.FriendlyName.Contains(selected.Name, StringComparison.OrdinalIgnoreCase));
        }

        if (chosen == null && loopbackDeviceIndex >= 0 && loopbackDeviceIndex < renderDevices.Count)
            chosen = renderDevices[loopbackDeviceIndex];

        Log($"Loopback: dispositivo='{chosen?.FriendlyName ?? "padrão do sistema"}'");

        // 2. WasapiLoopbackCapture captura áudio de renderização
        _loopbackCapture = chosen != null
            ? new WasapiLoopbackCapture(chosen)
            : new WasapiLoopbackCapture();

        // 3. Formato para Azure = 16kHz mono PCM16
        var pushFormat = AudioStreamFormat.GetWaveFormatPCM(16000, 16, 1);
        _loopbackPushStream = AudioInputStream.CreatePushStream(pushFormat);
        var targetWaveFormat = new WaveFormat(16000, 16, 1);

        _loopbackCapture.DataAvailable += (s, e) =>
        {
            if (e.BytesRecorded == 0 || _cts.IsCancellationRequested) return;
            try
            {
                var converted = AudioHelper.ConvertAudioFormat(
                    e.Buffer, e.BytesRecorded,
                    _loopbackCapture.WaveFormat, targetWaveFormat);

                if (converted.Length > 0)
                    _loopbackPushStream.Write(converted, converted.Length);
            }
            catch (Exception ex) { Log($"Loopback DataAvailable erro: {ex.Message}"); }
        };

        _loopbackCapture.RecordingStopped += (s, e) =>
        {
            Log($"Loopback parou{(e.Exception != null ? $" erro: {e.Exception.Message}" : "")}");
        };

        _loopbackCapture.StartRecording();
        Log("Loopback: StartRecording() OK");

        // 4. Utilizar TranslationRecognizer para Live Captions Reais
        var config = BuildSpeechTranslationConfig("en-US", "pt-BR");
        _loopbackAudioConfig = AudioConfig.FromStreamInput(_loopbackPushStream);
        _loopbackRecognizer = new TranslationRecognizer(config, _loopbackAudioConfig);

        WireTranslationEvents(_loopbackRecognizer, Speaker.Them, "en-US", "pt-BR", "Loopback");

        await _loopbackRecognizer.StartContinuousRecognitionAsync().ConfigureAwait(false);
        Log("Loopback: TranslationRecognizer OK");
    }

    // ─────────────────────────────────────────────────────────────────
    //  Configs
    // ─────────────────────────────────────────────────────────────────

    private SpeechConfig BuildSpeechConfig(string recognitionLanguage)
    {
        var config = SpeechConfig.FromSubscription(_speechKey, _speechRegion);
        config.SpeechRecognitionLanguage = recognitionLanguage;
        config.SetProperty(PropertyId.Speech_SegmentationSilenceTimeoutMs, "500");
        config.SetProperty(PropertyId.SpeechServiceConnection_EndSilenceTimeoutMs, "2000");
        config.SetProperty(PropertyId.SpeechServiceResponse_StablePartialResultThreshold, "1");
        config.SetProperty(PropertyId.SpeechServiceResponse_DiarizeIntermediateResults, "true");
        return config;
    }

    private SpeechTranslationConfig BuildSpeechTranslationConfig(string fromLang, string toLang)
    {
        var config = SpeechTranslationConfig.FromSubscription(_speechKey, _speechRegion);
        config.SpeechRecognitionLanguage = fromLang;
        config.AddTargetLanguage(toLang);
        config.SetProperty(PropertyId.Speech_SegmentationSilenceTimeoutMs, "500");
        config.SetProperty(PropertyId.SpeechServiceConnection_EndSilenceTimeoutMs, "2000");
        config.SetProperty(PropertyId.SpeechServiceResponse_StablePartialResultThreshold, "1");
        return config;
    }

    // ─────────────────────────────────────────────────────────────────
    //  Eventos Mic (O antigo ConversationTranscriber com REST)
    // ─────────────────────────────────────────────────────────────────

    private void WireEventsMic(ConversationTranscriber t, Speaker speaker, string fromLang, string toLang, string source)
    {
        t.Transcribing += (s, e) => OnTranscribing(e, speaker);
        t.Transcribed += (s, e) => _ = OnTranscribedAsync(e, speaker, fromLang, toLang);
        t.Canceled += (s, e) => _ = OnCanceledAsync(e, source, isMic: true);
        t.SessionStarted += (s, e) =>
        {
            Log($"{source}: sessão iniciada");
            _micReconnectAttempts = 0;
        };
        t.SessionStopped += (s, e) => _ = OnSessionStoppedAsync(source, isMic: true);
    }

    private void OnTranscribing(ConversationTranscriptionEventArgs e, Speaker speaker)
    {
        try
        {
            var text = e.Result.Text;
            if (string.IsNullOrWhiteSpace(text)) return;

            AnalyzingChanged?.Invoke(this, false);
            TranscriptReceived?.Invoke(this, new TranscriptEventArgs
            {
                Speaker = speaker,
                OriginalText = text,
                TranslatedText = text,        // parcial: exibe original do mic
                IsPartial = true,
                SpeakerId = NormalizeSpeakerId(e.Result.SpeakerId)
            });
        }
        catch (Exception ex) { Log($"Erro OnTranscribing: {ex.Message}"); }
    }

    private async Task OnTranscribedAsync(ConversationTranscriptionEventArgs e, Speaker speaker, string fromLang, string toLang)
    {
        try
        {
            if (e.Result.Reason == ResultReason.NoMatch || e.Result.Reason != ResultReason.RecognizedSpeech) 
                return;

            var original = e.Result.Text;
            if (string.IsNullOrWhiteSpace(original)) return;

            var translated = await _translator.TranslateAsync(original, fromLang, toLang, _cts.Token).ConfigureAwait(false);

            TranscriptReceived?.Invoke(this, new TranscriptEventArgs
            {
                Speaker = speaker,
                OriginalText = original,
                TranslatedText = translated,
                IsPartial = false,
                SpeakerId = NormalizeSpeakerId(e.Result.SpeakerId)
            });
        }
        catch (Exception ex) { Log($"Erro OnTranscribedAsync: {ex.Message}"); }
    }

    private async Task OnCanceledAsync(ConversationTranscriptionCanceledEventArgs e, string source, bool isMic)
    {
        Log($"{source} Cancelado: {e.Reason}");
        if (e.Reason == CancellationReason.Error)
        {
            Log($"{source} Erro: {e.ErrorCode} — {e.ErrorDetails}");
            ErrorOccurred?.Invoke(this, new StatusEventArgs { Message = $"{source}: {e.ErrorCode}" });
            await TryReconnectAsync(source, isMic).ConfigureAwait(false);
        }
    }

    // ─────────────────────────────────────────────────────────────────
    //  Eventos Loopback (Novo TranslationRecognizer Nativo)
    // ─────────────────────────────────────────────────────────────────

    private void WireTranslationEvents(TranslationRecognizer t, Speaker speaker, string fromLang, string toLang, string source)
    {
        t.Recognizing += (s, e) => OnTranslationRecognizing(e, speaker, toLang);
        t.Recognized += (s, e) => OnTranslationRecognized(e, speaker, toLang);
        t.Canceled += (s, e) => _ = OnTranslationCanceledAsync(e, source, isMic: false);
        t.SessionStarted += (s, e) =>
        {
            Log($"{source}: sessão iniciada (Tradutor Nativo)");
            _loopbackReconnectAttempts = 0;
        };
        t.SessionStopped += (s, e) => _ = OnSessionStoppedAsync(source, isMic: false);
    }

    private void OnTranslationRecognizing(TranslationRecognitionEventArgs e, Speaker speaker, string toLang)
    {
        try
        {
            var translations = e.Result.Translations;
            if (translations == null || translations.Count == 0) return;

            string translated = "";
            if (translations.TryGetValue(toLang, out var t)) translated = t;
            else if (translations.TryGetValue("pt", out var t2)) translated = t2;
            else translated = translations.First().Value; // Fallback extremo para a única tradução pedida

            if (!string.IsNullOrWhiteSpace(translated))
            {
                AnalyzingChanged?.Invoke(this, false);
                TranscriptReceived?.Invoke(this, new TranscriptEventArgs
                {
                    Speaker = speaker,
                    OriginalText = e.Result.Text,
                    TranslatedText = translated,
                    IsPartial = true,
                    SpeakerId = null
                });
            }
        }
        catch (Exception ex) { Log($"Erro OnTranslationRecognizing: {ex.Message}"); }
    }

    private void OnTranslationRecognized(TranslationRecognitionEventArgs e, Speaker speaker, string toLang)
    {
        try
        {
            if (e.Result.Reason == ResultReason.TranslatedSpeech)
            {
                var translations = e.Result.Translations;
                if (translations == null || translations.Count == 0) return;

                string translated = "";
                if (translations.TryGetValue(toLang, out var t)) translated = t;
                else if (translations.TryGetValue("pt", out var t2)) translated = t2;
                else translated = translations.First().Value;

                var original = e.Result.Text;
                if (string.IsNullOrWhiteSpace(original)) return;

                TranscriptReceived?.Invoke(this, new TranscriptEventArgs
                {
                    Speaker = speaker,
                    OriginalText = original,
                    TranslatedText = translated,
                    IsPartial = false,
                    SpeakerId = null
                });
            }
        }
        catch (Exception ex) { Log($"Erro OnTranslationRecognized: {ex.Message}"); }
    }

    private async Task OnTranslationCanceledAsync(TranslationRecognitionCanceledEventArgs e, string source, bool isMic)
    {
        Log($"{source} Cancelado: {e.Reason}");
        if (e.Reason == CancellationReason.Error)
        {
            Log($"{source} Erro: {e.ErrorCode} — {e.ErrorDetails}");
            ErrorOccurred?.Invoke(this, new StatusEventArgs { Message = $"{source}: {e.ErrorCode}" });
            await TryReconnectAsync(source, isMic).ConfigureAwait(false);
        }
    }

    // ─────────────────────────────────────────────────────────────────
    //  Resiliência 
    // ─────────────────────────────────────────────────────────────────

    private async Task OnSessionStoppedAsync(string source, bool isMic)
    {
        if (_cts.IsCancellationRequested || _isDisposed) return;
        Log($"{source}: sessão parou");
        await TryReconnectAsync(source, isMic).ConfigureAwait(false);
    }

    private async Task TryReconnectAsync(string source, bool isMic)
    {
        if (_cts.IsCancellationRequested || _isDisposed) return;

        ref int attempts = ref isMic ? ref _micReconnectAttempts : ref _loopbackReconnectAttempts;

        if (attempts >= MaxReconnectAttempts)
        {
            StatusChanged?.Invoke(this, new StatusEventArgs { Message = $"⚠ {source}: falha na reconexão" });
            return;
        }

        attempts++;
        int delaySec = (int)Math.Pow(2, attempts);
        StatusChanged?.Invoke(this, new StatusEventArgs { Message = $"Reconectando {source}... ({attempts}/{MaxReconnectAttempts})" });

        try { await Task.Delay(TimeSpan.FromSeconds(delaySec), _cts.Token).ConfigureAwait(false); }
        catch { return; }

        if (_cts.IsCancellationRequested || _isDisposed) return;

        try
        {
            if (isMic)
            {
                DisposeMicTranscriber();
                await StartMicTranscriberAsync(_lastMicIndex).ConfigureAwait(false);
            }
            else
            {
                DisposeLoopbackRecognizer();
                await StartLoopbackRecognizerAsync(_lastLoopbackIndex).ConfigureAwait(false);
            }

            StatusChanged?.Invoke(this, new StatusEventArgs { Message = "Pronto — reconectado" });
        }
        catch (Exception ex)
        {
            Log($"[Reconnect] {source} falha: {ex.Message}");
            await TryReconnectAsync(source, isMic).ConfigureAwait(false);
        }
    }

    // ─────────────────────────────────────────────────────────────────
    //  Audio Config & Dispose
    // ─────────────────────────────────────────────────────────────────

    private AudioConfig ResolveMicrophoneAudioConfig(int micDeviceIndex)
    {
        var uiDevices = AudioHelper.GetInputDevices();
        var selected = uiDevices.FirstOrDefault(d => d.DeviceIndex == micDeviceIndex);
        var enumerator = new MMDeviceEnumerator();
        var captureDevices = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active).ToList();

        MMDevice? chosen = null;
        if (selected != null && !string.IsNullOrWhiteSpace(selected.Name))
            chosen = captureDevices.FirstOrDefault(d => d.FriendlyName.Contains(selected.Name, StringComparison.OrdinalIgnoreCase));

        if (chosen == null && micDeviceIndex >= 0 && micDeviceIndex < captureDevices.Count)
            chosen = captureDevices[micDeviceIndex];

        if (chosen == null) return AudioConfig.FromDefaultMicrophoneInput();

        try { return AudioConfig.FromMicrophoneInput(chosen.ID); }
        catch { return AudioConfig.FromDefaultMicrophoneInput(); }
    }

    private static string? NormalizeSpeakerId(string? id) =>
        (string.IsNullOrWhiteSpace(id) || id.Equals("Unknown", StringComparison.OrdinalIgnoreCase)) ? null : id;

    private static void Log(string msg) => System.Diagnostics.Debug.WriteLine($"[AzureLiveCaptions] {msg}");

    private void DisposeMicTranscriber()
    {
        _micTranscriber?.Dispose();
        _micTranscriber = null;
        _micAudioConfig?.Dispose();
        _micAudioConfig = null;
    }

    private void DisposeLoopbackRecognizer()
    {
        try { _loopbackCapture?.StopRecording(); } catch { }
        _loopbackCapture?.Dispose();
        _loopbackCapture = null;
        _loopbackRecognizer?.Dispose();
        _loopbackRecognizer = null;
        _loopbackPushStream?.Dispose();
        _loopbackPushStream = null;
        _loopbackAudioConfig?.Dispose();
        _loopbackAudioConfig = null;
    }

    private void DisposeTranscribers()
    {
        DisposeMicTranscriber();
        DisposeLoopbackRecognizer();
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        _cts.Cancel();
        DisposeTranscribers();
        _translator.Dispose();
        _cts.Dispose();
    }
}
