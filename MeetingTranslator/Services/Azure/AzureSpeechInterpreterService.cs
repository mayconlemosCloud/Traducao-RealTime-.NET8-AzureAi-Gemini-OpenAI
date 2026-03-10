using System.Text;
using System.Linq;
using MeetingTranslator.Models;
using MeetingTranslator.Services.Common;
using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;
using Microsoft.CognitiveServices.Speech.Translation;
using NAudio.Wave;
using NAudio.CoreAudioApi;

namespace MeetingTranslator.Services.Azure;

/// <summary>
/// Backend alternativo do interprete PT->EN usando Azure Speech Translation.
/// Mantem a implementacao OpenAI separada e intacta.
/// </summary>
public sealed class AzureSpeechInterpreterService : IInterpreterService
{
    private const int OutputSampleRate = 16000;
    private const int Channels = 1;
    private const int BitsPerSample = 16;
    private const string SourceLanguage = "pt-BR";
    private const string TargetLanguage = "en";
    private const string DefaultVoice = "en-US-JennyNeural";

    private readonly string _speechKey;
    private readonly string _speechRegion;
    private readonly string _voiceName;
    private SharedAudioState? _sharedAudioState;
    private readonly WaveFormat _outputWaveFormat = new(OutputSampleRate, BitsPerSample, Channels);
    private readonly StringBuilder _transcriptBuilder = new(256);
    private readonly object _stateLock = new();

    private TranslationRecognizer? _recognizer;
    private AudioConfig? _audioInputConfig;
    private SpeechTranslationConfig? _translationConfig;
    private WaveOutEvent? _waveOut;
    private BufferedWaveProvider? _bufferProvider;
    private CancellationTokenSource? _cts;
    private bool _isConnected;
    private volatile bool _isPlaying;
    private volatile bool _isMuted;

    public AzureSpeechInterpreterService(string speechKey, string speechRegion, SharedAudioState? sharedAudioState = null, string? voiceName = null)
    {
        _speechKey = speechKey;
        _speechRegion = speechRegion;
        _sharedAudioState = sharedAudioState;
        _voiceName = string.IsNullOrWhiteSpace(voiceName) ? DefaultVoice : voiceName;
    }

    public event EventHandler<StatusEventArgs>? StatusChanged;
    public event EventHandler<StatusEventArgs>? ErrorOccurred;
    public event EventHandler<bool>? SpeakingChanged;

    public bool IsConnected => _isConnected;

    public bool IsMuted
    {
        get => _isMuted;
        set => _isMuted = value;
    }

    public void SetSharedAudioState(SharedAudioState state) => _sharedAudioState = state;

    public async Task StartAsync(int micDeviceIndex, int outputDeviceIndex)
    {
        if (_isConnected)
            return;

        // Log detalhado dos dispositivos de entrada
        var devices = AudioHelper.GetInputDevices();
        var logBuilder = new StringBuilder();
        logBuilder.AppendLine("Dispositivos de entrada detectados:");
        foreach (var dev in devices)
            logBuilder.AppendLine($"Índice: {dev.DeviceIndex} | Nome: {dev.Name}");
        StatusChanged?.Invoke(this, new StatusEventArgs { Message = logBuilder.ToString() });

        // Validação do índice
        var selectedDevice = devices.FirstOrDefault(d => d.DeviceIndex == micDeviceIndex);
        if (selectedDevice == null)
        {
            StatusChanged?.Invoke(this, new StatusEventArgs { Message = $"Índice de microfone inválido: {micDeviceIndex}. Usando dispositivo padrão." });
        }
        else
        {
            StatusChanged?.Invoke(this, new StatusEventArgs { Message = $"Microfone selecionado: {selectedDevice.Name} (Índice: {selectedDevice.DeviceIndex})" });
        }

        _cts = new CancellationTokenSource();

        _translationConfig = SpeechTranslationConfig.FromSubscription(_speechKey, _speechRegion);
        _translationConfig.SpeechRecognitionLanguage = SourceLanguage;
        _translationConfig.AddTargetLanguage(TargetLanguage);
        _translationConfig.VoiceName = _voiceName;
        _translationConfig.SetSpeechSynthesisOutputFormat(SpeechSynthesisOutputFormat.Raw16Khz16BitMonoPcm);
        _translationConfig.SetProperty(PropertyId.Speech_SegmentationSilenceTimeoutMs, "250");
        _translationConfig.SetProperty(PropertyId.SpeechServiceConnection_EndSilenceTimeoutMs, "300");
        _translationConfig.SetProperty(PropertyId.SpeechServiceResponse_StablePartialResultThreshold, "3");

        _audioInputConfig = CreateMicrophoneAudioConfig(micDeviceIndex);
        _recognizer = new TranslationRecognizer(_translationConfig, _audioInputConfig);

        _bufferProvider = new BufferedWaveProvider(_outputWaveFormat)
        {
            BufferLength = OutputSampleRate * 2 * 30,
            DiscardOnBufferOverflow = true
        };
        _waveOut = new WaveOutEvent
        {
            DeviceNumber = outputDeviceIndex,
            DesiredLatency = 150
        };
        _waveOut.Init(_bufferProvider);

        AttachRecognizerHandlers(_recognizer);

        StatusChanged?.Invoke(this, new StatusEventArgs { Message = "Conectando interprete Azure..." });

        await _recognizer.StartContinuousRecognitionAsync().ConfigureAwait(false);

        if (_sharedAudioState != null)
            _sharedAudioState.SpeakServiceActive = true;

        _isConnected = true;
        StatusChanged?.Invoke(this, new StatusEventArgs { Message = "Fale em portugues..." });
    }

    public async Task StopAsync()
    {
        _cts?.Cancel();

        try
        {
            if (_recognizer != null)
                await _recognizer.StopContinuousRecognitionAsync().ConfigureAwait(false);
        }
        catch { }

        _waveOut?.Stop();

        if (_sharedAudioState != null)
        {
            _sharedAudioState.SpeakPlaybackActive = false;
            _sharedAudioState.SpeakServiceActive = false;
        }

        _isConnected = false;
        _isPlaying = false;
        SpeakingChanged?.Invoke(this, false);
        StatusChanged?.Invoke(this, new StatusEventArgs { Message = "Desconectado" });
    }

    public void ClearPendingAudio()
    {
        _bufferProvider?.ClearBuffer();
    }

    public void Dispose()
    {
        if (_recognizer != null)
            DetachRecognizerHandlers(_recognizer);

        _waveOut?.Dispose();
        _recognizer?.Dispose();
        _audioInputConfig?.Dispose();
        _cts?.Dispose();

        if (_sharedAudioState != null)
        {
            _sharedAudioState.SpeakPlaybackActive = false;
            _sharedAudioState.SpeakServiceActive = false;
        }
    }

    private AudioConfig CreateMicrophoneAudioConfig(int micDeviceIndex)
    {
        // 1) Pega seleção da UI (WaveIn)
        var uiDevices = AudioHelper.GetInputDevices();
        var selectedUiDevice = uiDevices.FirstOrDefault(d => d.DeviceIndex == micDeviceIndex);

        // 2) Enumera dispositivos de captura via WASAPI (compatível com Azure SDK)
        var enumerator = new MMDeviceEnumerator();
        var wasapiCapture = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active).ToList();

        // 3) Resolve o dispositivo WASAPI correspondente
        MMDevice? chosen = null;
        if (selectedUiDevice != null && !string.IsNullOrWhiteSpace(selectedUiDevice.Name))
        {
            // Tenta por nome (match flexível)
            chosen = wasapiCapture
                .FirstOrDefault(d => d.FriendlyName.Equals(selectedUiDevice.Name, StringComparison.OrdinalIgnoreCase))
                ?? wasapiCapture.FirstOrDefault(d => d.FriendlyName.Contains(selectedUiDevice.Name, StringComparison.OrdinalIgnoreCase));
        }

        // Se não achou por nome, tenta por índice (fallback aproximado)
        if (chosen == null && micDeviceIndex >= 0 && micDeviceIndex < wasapiCapture.Count)
        {
            chosen = wasapiCapture[micDeviceIndex];
        }

        // Se ainda não achou, usa padrão do sistema
        if (chosen == null)
        {
            StatusChanged?.Invoke(this, new StatusEventArgs { Message = "Microfone Azure: usando dispositivo padrão (não foi possível mapear seleção)" });
            return AudioConfig.FromDefaultMicrophoneInput();
        }

        // 4) Tenta criar o AudioConfig usando ID (mais estável), depois FriendlyName
        try
        {
            StatusChanged?.Invoke(this, new StatusEventArgs { Message = $"Microfone Azure: {chosen.FriendlyName} (ID)" });
            return AudioConfig.FromMicrophoneInput(chosen.ID);
        }
        catch
        {
            try
            {
                StatusChanged?.Invoke(this, new StatusEventArgs { Message = $"Microfone Azure: {chosen.FriendlyName} (Nome amigável)" });
                return AudioConfig.FromMicrophoneInput(chosen.FriendlyName);
            }
            catch
            {
                StatusChanged?.Invoke(this, new StatusEventArgs { Message = "Microfone Azure: fallback para dispositivo padrão" });
                return AudioConfig.FromDefaultMicrophoneInput();
            }
        }
    }

    private void AttachRecognizerHandlers(TranslationRecognizer recognizer)
    {
        recognizer.SessionStarted += OnSessionStarted;
        recognizer.SessionStopped += OnSessionStopped;
        recognizer.SpeechStartDetected += OnSpeechStartDetected;
        recognizer.SpeechEndDetected += OnSpeechEndDetected;
        recognizer.Recognizing += OnRecognizing;
        recognizer.Recognized += OnRecognized;
        recognizer.Synthesizing += OnSynthesizing;
        recognizer.Canceled += OnCanceled;
    }

    private void DetachRecognizerHandlers(TranslationRecognizer recognizer)
    {
        recognizer.SessionStarted -= OnSessionStarted;
        recognizer.SessionStopped -= OnSessionStopped;
        recognizer.SpeechStartDetected -= OnSpeechStartDetected;
        recognizer.SpeechEndDetected -= OnSpeechEndDetected;
        recognizer.Recognizing -= OnRecognizing;
        recognizer.Recognized -= OnRecognized;
        recognizer.Synthesizing -= OnSynthesizing;
        recognizer.Canceled -= OnCanceled;
    }

    private void OnSessionStarted(object? sender, SessionEventArgs e)
    {
        StatusChanged?.Invoke(this, new StatusEventArgs { Message = "Sessao Azure iniciada" });
    }

    private void OnSessionStopped(object? sender, SessionEventArgs e)
    {
        if (_isConnected)
            StatusChanged?.Invoke(this, new StatusEventArgs { Message = "Sessao Azure finalizada" });
    }

    private void OnSpeechStartDetected(object? sender, RecognitionEventArgs e)
    {
        StatusChanged?.Invoke(this, new StatusEventArgs { Message = "Ouvindo..." });
    }

    private void OnSpeechEndDetected(object? sender, RecognitionEventArgs e)
    {
        StatusChanged?.Invoke(this, new StatusEventArgs { Message = "Processando..." });
    }

    private void OnRecognizing(object? sender, TranslationRecognitionEventArgs e)
    {
        if (IsMuted)
            return;

        if (e.Result.Reason != ResultReason.TranslatingSpeech)
            return;

        if (e.Result.Translations.TryGetValue(TargetLanguage, out var partial) && !string.IsNullOrWhiteSpace(partial))
        {
            StatusChanged?.Invoke(this, new StatusEventArgs { Message = $"... {partial}" });
        }
    }

    private void OnRecognized(object? sender, TranslationRecognitionEventArgs e)
    {
        if (IsMuted)
            return;

        switch (e.Result.Reason)
        {
            case ResultReason.TranslatedSpeech:
                if (e.Result.Translations.TryGetValue(TargetLanguage, out var translated) && !string.IsNullOrWhiteSpace(translated))
                {
                    _transcriptBuilder.Clear();
                    _transcriptBuilder.Append(translated);
                    StatusChanged?.Invoke(this, new StatusEventArgs { Message = $"Traduzindo: {translated}" });
                }
                break;

            case ResultReason.RecognizedSpeech:
                StatusChanged?.Invoke(this, new StatusEventArgs { Message = "Fala reconhecida sem traducao" });
                break;

            case ResultReason.NoMatch:
                StatusChanged?.Invoke(this, new StatusEventArgs { Message = "Sem correspondencia de fala" });
                break;
        }
    }

    private void OnSynthesizing(object? sender, TranslationSynthesisEventArgs e)
    {
        if (IsMuted)
            return;

        var audio = e.Result.GetAudio();
        if (audio == null || audio.Length == 0)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    while ((_bufferProvider?.BufferedBytes ?? 0) > 0 && !(_cts?.Token.IsCancellationRequested ?? true))
                    {
                        await Task.Delay(40, _cts!.Token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) { }
                finally
                {
                    EndPlayback();
                    StatusChanged?.Invoke(this, new StatusEventArgs { Message = "Fale em portugues..." });
                }
            });
            return;
        }

        _bufferProvider?.AddSamples(audio, 0, audio.Length);

        lock (_stateLock)
        {
            if (_isPlaying)
                return;

            _isPlaying = true;
            if (_sharedAudioState != null)
                _sharedAudioState.SpeakPlaybackActive = true;
            SpeakingChanged?.Invoke(this, true);
            _waveOut?.Play();
        }
    }

    private void OnCanceled(object? sender, TranslationRecognitionCanceledEventArgs e)
    {
        var msg = e.Reason == CancellationReason.Error
            ? $"Azure erro: {e.ErrorCode} - {e.ErrorDetails}"
            : $"Azure cancelado: {e.Reason}";
        ErrorOccurred?.Invoke(this, new StatusEventArgs { Message = msg });
    }

    private void EndPlayback()
    {
        lock (_stateLock)
        {
            _isPlaying = false;
            if (_sharedAudioState != null)
                _sharedAudioState.SpeakPlaybackActive = false;
            SpeakingChanged?.Invoke(this, false);
        }
    }
}