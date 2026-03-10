using MeetingTranslator.Models;
using MeetingTranslator.Services.Common;
using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;
using Microsoft.CognitiveServices.Speech.Translation;
using NAudio.CoreAudioApi;
using System.Linq;

namespace MeetingTranslator.Services.Azure;

/// <summary>
/// Transcrição e tradução em tempo real via Azure Speech Translation SDK.
/// Captura mic (PT-BR → EN) e/ou loopback (EN → PT-BR) e emite eventos
/// de transcrição parcial/final compatíveis com o fluxo do MainViewModel.
/// </summary>
public sealed class AzureTranscriptionService : IDisposable
{
    private readonly string _speechKey;
    private readonly string _speechRegion;

    private TranslationRecognizer? _micRecognizer;
    private TranslationRecognizer? _loopbackRecognizer;
    private AudioConfig? _micAudioConfig;
    private AudioConfig? _loopbackAudioConfig;
    private CancellationTokenSource? _cts;
    private bool _isConnected;

    public event EventHandler<TranscriptEventArgs>? TranscriptReceived;
    public event EventHandler<StatusEventArgs>? StatusChanged;
    public event EventHandler<StatusEventArgs>? ErrorOccurred;
    public event EventHandler<bool>? AnalyzingChanged;

    public bool IsConnected => _isConnected;

    public AzureTranscriptionService(string speechKey, string speechRegion)
    {
        _speechKey = speechKey;
        _speechRegion = speechRegion;
    }

    public async Task StartAsync(int micDeviceIndex, int loopbackDeviceIndex, bool useMic, bool useLoopback)
    {
        _cts = new CancellationTokenSource();

        StatusChanged?.Invoke(this, new StatusEventArgs { Message = "Conectando Azure..." });

        if (useMic)
        {
            var config = SpeechTranslationConfig.FromSubscription(_speechKey, _speechRegion);
            config.SpeechRecognitionLanguage = "pt-BR";
            config.AddTargetLanguage("en");

            _micAudioConfig = ResolveMicrophoneAudioConfig(micDeviceIndex);
            _micRecognizer = new TranslationRecognizer(config, _micAudioConfig);

            _micRecognizer.Recognizing += (s, e) => OnRecognizing(e, Speaker.You, "en");
            _micRecognizer.Recognized += (s, e) => OnRecognized(e, Speaker.You, "en", "pt-BR");
            _micRecognizer.Canceled += OnCanceled;

            await _micRecognizer.StartContinuousRecognitionAsync().ConfigureAwait(false);
        }

        if (useLoopback)
        {
            var config = SpeechTranslationConfig.FromSubscription(_speechKey, _speechRegion);
            config.SpeechRecognitionLanguage = "en-US";
            config.AddTargetLanguage("pt");

            // Loopback usa mic padrão — o áudio do sistema é roteado via virtual cable
            _loopbackAudioConfig = AudioConfig.FromDefaultMicrophoneInput();
            _loopbackRecognizer = new TranslationRecognizer(config, _loopbackAudioConfig);

            _loopbackRecognizer.Recognizing += (s, e) => OnRecognizing(e, Speaker.Them, "pt");
            _loopbackRecognizer.Recognized += (s, e) => OnRecognized(e, Speaker.Them, "pt", "en-US");
            _loopbackRecognizer.Canceled += OnCanceled;

            await _loopbackRecognizer.StartContinuousRecognitionAsync().ConfigureAwait(false);
        }

        _isConnected = true;
        StatusChanged?.Invoke(this, new StatusEventArgs { Message = "Pronto — ouvindo..." });
    }

    public async Task StopAsync()
    {
        _cts?.Cancel();

        try
        {
            if (_micRecognizer != null)
                await _micRecognizer.StopContinuousRecognitionAsync().ConfigureAwait(false);
        }
        catch { }

        try
        {
            if (_loopbackRecognizer != null)
                await _loopbackRecognizer.StopContinuousRecognitionAsync().ConfigureAwait(false);
        }
        catch { }

        _isConnected = false;
        StatusChanged?.Invoke(this, new StatusEventArgs { Message = "Desconectado" });
    }

    private void OnRecognizing(TranslationRecognitionEventArgs e, Speaker speaker, string targetLang)
    {
        if (e.Result.Reason != ResultReason.TranslatingSpeech)
            return;

        if (e.Result.Translations.TryGetValue(targetLang, out var partial) && !string.IsNullOrWhiteSpace(partial))
        {
            AnalyzingChanged?.Invoke(this, false);
            TranscriptReceived?.Invoke(this, new TranscriptEventArgs
            {
                Speaker = speaker,
                OriginalText = e.Result.Text,
                TranslatedText = partial,
                IsPartial = true
            });
        }
    }

    private void OnRecognized(TranslationRecognitionEventArgs e, Speaker speaker, string targetLang, string sourceLang)
    {
        switch (e.Result.Reason)
        {
            case ResultReason.TranslatedSpeech:
                if (e.Result.Translations.TryGetValue(targetLang, out var translated) && !string.IsNullOrWhiteSpace(translated))
                {
                    TranscriptReceived?.Invoke(this, new TranscriptEventArgs
                    {
                        Speaker = speaker,
                        OriginalText = e.Result.Text,
                        TranslatedText = translated,
                        IsPartial = false
                    });
                }
                break;

            case ResultReason.NoMatch:
                AnalyzingChanged?.Invoke(this, false);
                break;
        }
    }

    private void OnCanceled(object? sender, TranslationRecognitionCanceledEventArgs e)
    {
        if (e.Reason == CancellationReason.Error)
        {
            ErrorOccurred?.Invoke(this, new StatusEventArgs
            {
                Message = $"Azure erro: {e.ErrorCode} — {e.ErrorDetails}"
            });
        }
    }

    private AudioConfig ResolveMicrophoneAudioConfig(int micDeviceIndex)
    {
        var uiDevices = AudioHelper.GetInputDevices();
        var selected = uiDevices.FirstOrDefault(d => d.DeviceIndex == micDeviceIndex);

        var enumerator = new MMDeviceEnumerator();
        var wasapiDevices = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active).ToList();

        MMDevice? chosen = null;
        if (selected != null && !string.IsNullOrWhiteSpace(selected.Name))
        {
            chosen = wasapiDevices
                .FirstOrDefault(d => d.FriendlyName.Equals(selected.Name, StringComparison.OrdinalIgnoreCase))
                ?? wasapiDevices.FirstOrDefault(d => d.FriendlyName.Contains(selected.Name, StringComparison.OrdinalIgnoreCase));
        }

        if (chosen == null && micDeviceIndex >= 0 && micDeviceIndex < wasapiDevices.Count)
            chosen = wasapiDevices[micDeviceIndex];

        if (chosen == null)
            return AudioConfig.FromDefaultMicrophoneInput();

        try { return AudioConfig.FromMicrophoneInput(chosen.ID); }
        catch
        {
            try { return AudioConfig.FromMicrophoneInput(chosen.FriendlyName); }
            catch { return AudioConfig.FromDefaultMicrophoneInput(); }
        }
    }

    public void Dispose()
    {
        _micRecognizer?.Dispose();
        _loopbackRecognizer?.Dispose();
        _micAudioConfig?.Dispose();
        _loopbackAudioConfig?.Dispose();
        _cts?.Dispose();
    }
}
