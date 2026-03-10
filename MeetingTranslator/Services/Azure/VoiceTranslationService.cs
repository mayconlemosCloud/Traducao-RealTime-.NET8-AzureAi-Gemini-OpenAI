using System.Text;
using System.Threading.Channels;
using System.Timers;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using MeetingTranslator.Models;
using MeetingTranslator.Services.Common;
using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;
using Microsoft.CognitiveServices.Speech.Translation;

namespace MeetingTranslator.Services.Azure;

/// <summary>
/// Tradução de voz em tempo real via Azure Speech Translation.
/// Captura mic e/ou loopback, traduz PT↔EN e sintetiza a saída.
/// Estratégia: dois reconhecedores independentes para mic (PT→EN) e loopback (EN→PT),
/// com playback único e gating para evitar feedback.
/// </summary>
public sealed class VoiceTranslationService : IDisposable
{
    private const int InputSampleRate = 24000;
    private const int InputBits = 16;
    private const int InputChannels = 1;
    private static readonly WaveFormat InputWaveFormat = new(InputSampleRate, InputBits, InputChannels);

    private const int SynthSampleRate = 16000; // Azure Raw PCM output
    private const int SynthBits = 16;
    private const int SynthChannels = 1;
    private static readonly WaveFormat SynthWaveFormat = new(SynthSampleRate, SynthBits, SynthChannels);

    // Idiomas e vozes por direção
    private string _micSourceLang = "en-US";
    private string _micTargetLang = "pt-BR";
    private string _micVoice = "pt-BR-FranciscaNeural";

    private string _loopSourceLang = "en-US";
    private string _loopTargetLang = "pt-BR";
    private string _loopVoice = "pt-BR-FranciscaNeural";

    private readonly string _speechKey;
    private readonly string _speechRegion;

    // Captura
    private WaveInEvent? _waveIn;
    private WasapiLoopbackCapture? _loopback;

    // Azure streams/configs
    private PushAudioInputStream? _micPush;
    private PushAudioInputStream? _loopPush;
    private TranslationRecognizer? _micRecognizer;
    private TranslationRecognizer? _loopRecognizer;

    // Playback
    private BufferedWaveProvider? _buffer;
    private IWavePlayer? _audioOut;
    private volatile bool _isPlaying;
    private DateTime _playbackCooldownUntil = DateTime.MinValue;
    private DateTime _lastSynthAudioAt = DateTime.MinValue;
    private System.Timers.Timer? _playbackWatchdog;

    // Estado
    private CancellationTokenSource? _cts;
    private readonly StringBuilder _transcriptBuilder = new(256);
    public volatile bool IsMuted;

    private SharedAudioState? _sharedAudioState;

    // Eventos (mesmos do serviço OpenAI)
    public event EventHandler<TranscriptEventArgs>? TranscriptReceived;
    public event EventHandler<StatusEventArgs>? StatusChanged;
    public event EventHandler<StatusEventArgs>? ErrorOccurred;
    public event EventHandler<bool>? AnalyzingChanged;

    public VoiceTranslationService(string speechKey, string speechRegion, SharedAudioState? sharedAudioState = null)
    {
        _speechKey = speechKey;
        _speechRegion = speechRegion;
        _sharedAudioState = sharedAudioState;

        // Direções/vozes configuráveis por ambiente
        var micDir = (Environment.GetEnvironmentVariable("AZURE_VOICE_MIC_DIRECTION") ?? "en-pt").ToLowerInvariant();
        if (micDir == "pt-en")
        {
            _micSourceLang = "pt-BR";
            _micTargetLang = "en";
            _micVoice = "en-US-JennyNeural";
        }
        else // en-pt (default desejado)
        {
            _micSourceLang = "en-US";
            _micTargetLang = "pt-BR";
            _micVoice = "pt-BR-FranciscaNeural";
        }

        var loopDir = (Environment.GetEnvironmentVariable("AZURE_VOICE_LOOP_DIRECTION") ?? "en-pt").ToLowerInvariant();
        if (loopDir == "pt-en")
        {
            _loopSourceLang = "pt-BR";
            _loopTargetLang = "en";
            _loopVoice = "en-US-JennyNeural";
        }
        else
        {
            _loopSourceLang = "en-US";
            _loopTargetLang = "pt-BR";
            _loopVoice = "pt-BR-FranciscaNeural";
        }

        _micVoice = Environment.GetEnvironmentVariable("AZURE_VOICE_MIC_VOICE") ?? _micVoice;
        _loopVoice = Environment.GetEnvironmentVariable("AZURE_VOICE_LOOP_VOICE") ?? _loopVoice;
    }

    public void SetSharedAudioState(SharedAudioState state) => _sharedAudioState = state;

    public async Task StartAsync(int micDeviceIndex, int loopbackDeviceIndex, bool useMic, bool useLoopback)
    {
        _cts = new CancellationTokenSource();

        // Buffer + saída
        _buffer = new BufferedWaveProvider(SynthWaveFormat)
        {
            BufferLength = SynthSampleRate * 2 * 30,
            DiscardOnBufferOverflow = true
        };
        _audioOut = CreateAudioOut(_buffer);

        // Reconhecedor MIC (direção configurável)
        if (useMic)
        {
            try
            {
                var micConfig = CreateTranslationConfig(_micSourceLang, _micTargetLang, _micVoice);
                _micPush = AudioInputStream.CreatePushStream(AudioStreamFormat.GetWaveFormatPCM(SynthSampleRate, SynthBits, SynthChannels));
                var micAudio = AudioConfig.FromStreamInput(_micPush);
                _micRecognizer = new TranslationRecognizer(micConfig, micAudio);
                AttachHandlers(_micRecognizer, _micTargetLang);
                StartMicCapture(micDeviceIndex);
                await _micRecognizer.StartContinuousRecognitionAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke(this, new StatusEventArgs { Message = $"Azure(MIC) erro: {ex.Message}" });
            }
        }

        // Reconhecedor LOOPBACK (direção configurável)
        if (useLoopback)
        {
            try
            {
                var loopConfig = CreateTranslationConfig(_loopSourceLang, _loopTargetLang, _loopVoice);
                _loopPush = AudioInputStream.CreatePushStream(AudioStreamFormat.GetWaveFormatPCM(SynthSampleRate, SynthBits, SynthChannels));
                var loopAudio = AudioConfig.FromStreamInput(_loopPush);
                _loopRecognizer = new TranslationRecognizer(loopConfig, loopAudio);
                AttachHandlers(_loopRecognizer, _loopTargetLang);
                StartLoopbackCapture(loopbackDeviceIndex);
                await _loopRecognizer.StartContinuousRecognitionAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke(this, new StatusEventArgs { Message = $"Azure(LOOP) erro: {ex.Message}" });
            }
        }

        StatusChanged?.Invoke(this, new StatusEventArgs { Message = "Ouvindo..." });

        // Watchdog para encerrar playback travado (faltou chunk final vazio)
        _playbackWatchdog = new System.Timers.Timer(300);
        _playbackWatchdog.Elapsed += (_, __) =>
        {
            if (!_isPlaying) return;
            if (_buffer == null) return;
            var ageMs = (DateTime.UtcNow - _lastSynthAudioAt).TotalMilliseconds;
            if (_buffer.BufferedBytes < 512 && ageMs > 500)
            {
                _isPlaying = false;
                if (_sharedAudioState != null)
                    _sharedAudioState.RealtimePlaybackActive = false;
                _playbackCooldownUntil = DateTime.UtcNow.AddMilliseconds(350);
            }
        };
        _playbackWatchdog.AutoReset = true;
        _playbackWatchdog.Start();
    }

    private SpeechTranslationConfig CreateTranslationConfig(string srcLang, string tgtLang, string voice)
    {
        var cfg = SpeechTranslationConfig.FromSubscription(_speechKey, _speechRegion);
        cfg.SpeechRecognitionLanguage = srcLang;
        cfg.AddTargetLanguage(tgtLang);
        cfg.VoiceName = voice;
        cfg.SetSpeechSynthesisOutputFormat(SpeechSynthesisOutputFormat.Raw16Khz16BitMonoPcm);
        cfg.SetProperty(PropertyId.Speech_SegmentationSilenceTimeoutMs, "1500");
        return cfg;
    }

    private IWavePlayer CreateAudioOut(BufferedWaveProvider provider)
    {
        try
        {
            var wo = new WaveOutEvent { DesiredLatency = 200 };
            wo.Init(provider);
            wo.PlaybackStopped += (_, __) =>
            {
                _isPlaying = false;
                if (_sharedAudioState != null)
                    _sharedAudioState.RealtimePlaybackActive = false;
                _playbackCooldownUntil = DateTime.UtcNow.AddMilliseconds(350);
            };
            return wo;
        }
        catch
        {
            using var enumerator = new MMDeviceEnumerator();
            var def = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var wasapi = new WasapiOut(def, AudioClientShareMode.Shared, true, 200);
            wasapi.Init(provider);
            wasapi.PlaybackStopped += (_, __) =>
            {
                _isPlaying = false;
                if (_sharedAudioState != null)
                    _sharedAudioState.RealtimePlaybackActive = false;
                _playbackCooldownUntil = DateTime.UtcNow.AddMilliseconds(350);
            };
            return wasapi;
        }
    }

    private void StartMicCapture(int deviceIndex)
    {
        _waveIn = new WaveInEvent
        {
            DeviceNumber = deviceIndex,
            WaveFormat = InputWaveFormat,
            BufferMilliseconds = 100
        };

        _waveIn.DataAvailable += (_, e) =>
        {
            if (IsMuted) return;
            // Evita loops: bloqueia enquanto qualquer playback externo/atual estiver ativo ou em cooldown
            if (_isPlaying) return;
            if (_sharedAudioState?.IsAnyExternalPlaybackActive == true) return;
            if (DateTime.UtcNow < _playbackCooldownUntil) return;
            if (_sharedAudioState?.SpeakServiceActive == true) return; // intérprete ativo assume o mic

            var converted = AudioHelper.ConvertAudioFormat(e.Buffer, e.BytesRecorded, InputWaveFormat, SynthWaveFormat);
            if (converted.Length == 0) return;

            _micPush?.Write(converted);
        };

        _waveIn.StartRecording();
    }

    private void StartLoopbackCapture(int loopIndex)
    {
        try
        {
            var enumerator = new MMDeviceEnumerator();
            var renders = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).ToList();

            WasapiLoopbackCapture CreateDefaultLoopback()
            {
                // Captura do dispositivo de reprodução padrão
                return new WasapiLoopbackCapture();
            }

            // Resolve dispositivo escolhido; fallback para padrão quando inválido
            MMDevice? device = null;
            if (loopIndex >= 0 && loopIndex < renders.Count)
                device = renders[loopIndex];

            try
            {
                _loopback = device != null ? new WasapiLoopbackCapture(device) : CreateDefaultLoopback();
            }
            catch
            {
                // Alguns endpoints (ex.: Bluetooth) não suportam loopback → usa padrão
                _loopback = CreateDefaultLoopback();
            }

            var srcFmt = _loopback.WaveFormat;

            // Feedback rápido para UI/diagnóstico
            StatusChanged?.Invoke(this, new StatusEventArgs
            {
                Message = device != null
                    ? $"Capturando sistema: {device.FriendlyName}"
                    : "Capturando sistema: dispositivo padrão"
            });

            _loopback.DataAvailable += (_, e) =>
            {
                // Evita loops: bloqueia durante playback próprio e durante atividade do intérprete
                if (_isPlaying) return;
                if (_sharedAudioState?.SpeakPlaybackActive == true) return;
                if (_sharedAudioState?.SpeakCooldownActive == true) return;
                if (DateTime.UtcNow < _playbackCooldownUntil) return;

                if (e.BytesRecorded == 0) return;
                var converted = AudioHelper.ConvertAudioFormat(e.Buffer, e.BytesRecorded, srcFmt, SynthWaveFormat);
                if (converted.Length == 0) return;
                _loopPush?.Write(converted);
            };

            _loopback.StartRecording();
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(this, new StatusEventArgs { Message = $"Erro ao iniciar loopback: {ex.Message}" });
        }
    }

    private void AttachHandlers(TranslationRecognizer recognizer, string targetLang)
    {
        recognizer.Recognizing += (_, e) =>
        {
            if (e.Result.Reason != ResultReason.TranslatingSpeech) return;
            if (e.Result.Translations.TryGetValue(targetLang, out var partial) && !string.IsNullOrWhiteSpace(partial))
            {
                AnalyzingChanged?.Invoke(this, false);
                TranscriptReceived?.Invoke(this, new TranscriptEventArgs
                {
                    Speaker = Speaker.Them,
                    OriginalText = e.Result.Text ?? string.Empty,
                    TranslatedText = partial,
                    IsPartial = true
                });
                StatusChanged?.Invoke(this, new StatusEventArgs { Message = "..." });
            }
        };

        recognizer.Recognized += (_, e) =>
        {
            switch (e.Result.Reason)
            {
                case ResultReason.TranslatedSpeech:
                    if (e.Result.Translations.TryGetValue(targetLang, out var translated) && !string.IsNullOrWhiteSpace(translated))
                    {
                        TranscriptReceived?.Invoke(this, new TranscriptEventArgs
                        {
                            Speaker = Speaker.Them,
                            OriginalText = e.Result.Text ?? string.Empty,
                            TranslatedText = translated,
                            IsPartial = false
                        });
                        AnalyzingChanged?.Invoke(this, false);
                        StatusChanged?.Invoke(this, new StatusEventArgs { Message = "Gerando áudio..." });
                    }
                    break;
                case ResultReason.RecognizedSpeech:
                    StatusChanged?.Invoke(this, new StatusEventArgs { Message = "Reconhecido (sem tradução)" });
                    break;
                case ResultReason.NoMatch:
                    StatusChanged?.Invoke(this, new StatusEventArgs { Message = "Sem correspondência" });
                    break;
            }
        };

        recognizer.Synthesizing += (_, e) =>
        {
            var audio = e.Result.GetAudio();
            if (audio == null || audio.Length == 0)
            {
                // flush do buffer antes de encerrar playback
                _ = Task.Run(async () =>
                {
                    try
                    {
                        while ((_buffer?.BufferedBytes ?? 0) > 0 && !(_cts?.Token.IsCancellationRequested ?? true))
                            await Task.Delay(60, _cts!.Token).ConfigureAwait(false);
                    }
                    catch { }
                    finally
                    {
                        _isPlaying = false;
                        if (_sharedAudioState != null)
                            _sharedAudioState.RealtimePlaybackActive = false;
                        // aplica pequeno cooldown para evitar eco imediato do fim do áudio
                        _playbackCooldownUntil = DateTime.UtcNow.AddMilliseconds(350);
                    }
                });
                return;
            }

            _buffer?.AddSamples(audio, 0, audio.Length);
            _lastSynthAudioAt = DateTime.UtcNow;
            if (!_isPlaying)
            {
                _isPlaying = true;
                if (_sharedAudioState != null)
                    _sharedAudioState.RealtimePlaybackActive = true;
                _audioOut?.Play();
            }
        };

        recognizer.Canceled += (_, e) =>
        {
            var msg = e.Reason == CancellationReason.Error
                ? $"Azure cancelado: {e.ErrorCode} - {e.ErrorDetails}"
                : $"Azure cancelado: {e.Reason}";
            ErrorOccurred?.Invoke(this, new StatusEventArgs { Message = msg });
        };

        recognizer.SessionStarted += (_, __) => StatusChanged?.Invoke(this, new StatusEventArgs { Message = "Sessão Azure iniciada" });
        recognizer.SessionStopped += (_, __) => StatusChanged?.Invoke(this, new StatusEventArgs { Message = "Sessão Azure finalizada" });
        recognizer.SpeechStartDetected += (_, __) => StatusChanged?.Invoke(this, new StatusEventArgs { Message = "Ouvindo fala..." });
        recognizer.SpeechEndDetected += (_, __) => StatusChanged?.Invoke(this, new StatusEventArgs { Message = "Processando..." });
    }

    public void ClearPendingAudio()
    {
        _buffer?.ClearBuffer();
    }

    public async Task StopAsync()
    {
        _cts?.Cancel();

        try { if (_micRecognizer != null) await _micRecognizer.StopContinuousRecognitionAsync().ConfigureAwait(false); } catch { }
        try { if (_loopRecognizer != null) await _loopRecognizer.StopContinuousRecognitionAsync().ConfigureAwait(false); } catch { }

        _waveIn?.StopRecording();
        _loopback?.StopRecording();
        _audioOut?.Stop();
        _playbackWatchdog?.Stop();
        _playbackWatchdog?.Dispose();
        _playbackWatchdog = null;

        if (_sharedAudioState != null)
            _sharedAudioState.RealtimePlaybackActive = false;

        StatusChanged?.Invoke(this, new StatusEventArgs { Message = "Desconectado" });
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _waveIn?.Dispose();
        _loopback?.Dispose();
        _audioOut?.Dispose();
        _micRecognizer?.Dispose();
        _loopRecognizer?.Dispose();
        _micPush?.Dispose();
        _loopPush?.Dispose();
        _playbackWatchdog?.Dispose();
        _cts?.Dispose();
    }
}
