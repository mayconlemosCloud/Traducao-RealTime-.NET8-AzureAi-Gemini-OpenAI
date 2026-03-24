using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Collections.ObjectModel;
using System.Linq;
using dotenv.net;
using MeetingGoogle.Models;
using MeetingGoogle.Services;

namespace MeetingGoogle.ViewModels
{
    public class MainViewModel : INotifyPropertyChanged, IDisposable
    {
        private string _subtitleText = "Bem-vindo ao MeetingGoogle";
        private string _translatedSubtitleText = "";
        private string _statusText = "Pronto";
        private bool _isConnected;
        private bool _isAnalyzing;

        // --- Novas Propriedades de UI Portadas ---
        private bool _isSettingsVisible;
        private bool _isHistoryVisible;
        private int _selectedSettingsTab;
        private TranslationMode _selectedMode = TranslationMode.Voice;
        private string _speakStatusText = "Intérprete desativado";
        private bool _isSpeakConnected;
        private bool _isSpeaking;
        private bool _isSpeakFeatureEnabled;
        private string _speakButtonTooltip = "Falar";
        private bool _isMuted;

        private readonly AudioCaptureService _audioCapture;
        private readonly GeminiLiveService _geminiService;

        public ObservableCollection<CombinedInputDevice> AllInputDevices { get; } = new ObservableCollection<CombinedInputDevice>();
        public ObservableCollection<AudioDeviceInfo> OutputDevices { get; } = new ObservableCollection<AudioDeviceInfo>();
        public ObservableCollection<ConversationEntry> History { get; } = new ObservableCollection<ConversationEntry>();

        private CombinedInputDevice? _selectedInputDevice;
        public CombinedInputDevice? SelectedInputDevice
        {
            get => _selectedInputDevice;
            set { _selectedInputDevice = value; OnPropertyChanged(); }
        }

        private AudioDeviceInfo? _selectedOutputDevice;
        public AudioDeviceInfo? SelectedOutputDevice
        {
            get => _selectedOutputDevice;
            set { _selectedOutputDevice = value; OnPropertyChanged(); }
        }

        public MainViewModel()
        {
            DotEnv.Load(options: new DotEnvOptions(probeForEnv: true, probeLevelsToSearch: 6));
            var geminiKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY") ?? "";

            _audioCapture = new AudioCaptureService();
            _geminiService = new GeminiLiveService();

            LoadDevices();

            if (!string.IsNullOrEmpty(geminiKey))
            {
                _geminiService.Initialize(geminiKey);
                // Validação de Modelos na inicialização (Fogo-e-Desquecimento)
                _ = Task.Run(async () =>
                {
                    var result = await _geminiService.ValidateModelsAsync();
                    Application.Current.Dispatcher.Invoke(() => {
                        SubtitleText = result;
                    });
                });
            }
            else
            {
                StatusText = "Falta GEMINI_API_KEY no .env";
            }

            // Bind events
            _audioCapture.DataAvailable += async (s, e) =>
            {
                if (IsConnected) 
                {
                    // Se o Gemini estiver falando e usamos Loopback, não mandamos o áudio (evitar eco/barge-in)
                    if (_geminiService.IsPlayingAudio && SelectedInputDevice?.IsLoopback == true) return;

                    await _geminiService.SendAudioAsync(e, e.Length);
                }
            };

            _geminiService.LogMessageReceived += (s, text) =>
            {
                Application.Current.Dispatcher.Invoke(() => {
                    SubtitleText = text;
                });
            };

            _geminiService.PartialModelResponseReceived += (s, text) =>
            {
                Application.Current.Dispatcher.Invoke(() => {
                    SubtitleText = text; // Atualiza a legenda temporária com o pedaço atual
                });
            };

            _geminiService.FinalModelResponseCompleted += (s, text) =>
            {
                Application.Current.Dispatcher.Invoke(() => {
                    SubtitleText = text; 
                    History.Add(new ConversationEntry 
                    { 
                        Speaker = Speaker.AI, 
                        TranslatedText = text, 
                        SpeakerId = "AI" 
                    });
                });
            };

            _geminiService.PartialUserInputReceived += (s, text) =>
            {
                // Ignorado intencionalmente: O usuário não quer ver a transcrição em inglês, apenas o resultado em PT-BR
            };

            _geminiService.FinalUserInputCompleted += (s, text) =>
            {
                // Ignorado intencionalmente: não polui histórico com original
            };
        }

        // --- Propriedades UI Portadas ---

        public bool IsSettingsVisible
        {
            get => _isSettingsVisible;
            set { _isSettingsVisible = value; OnPropertyChanged(); }
        }

        public bool IsHistoryVisible
        {
            get => _isHistoryVisible;
            set { _isHistoryVisible = value; OnPropertyChanged(); }
        }

        public int SelectedSettingsTab
        {
            get => _selectedSettingsTab;
            set { _selectedSettingsTab = value; OnPropertyChanged(); }
        }

        public TranslationMode SelectedMode
        {
            get => _selectedMode;
            set { _selectedMode = value; OnPropertyChanged(); OnPropertyChanged(nameof(ModeDescription)); }
        }

        public string ModeDescription => SelectedMode switch
        {
            TranslationMode.Voice => "Modo de voz natural (Gemini Live).",
            TranslationMode.Transcription => "Modo focado apenas em transcrição de texto.",
            _ => ""
        };

        public string SpeakStatusText
        {
            get => _speakStatusText;
            set { _speakStatusText = value; OnPropertyChanged(); }
        }

        public bool IsSpeakConnected
        {
            get => _isSpeakConnected;
            set { _isSpeakConnected = value; OnPropertyChanged(); }
        }

        public bool IsSpeaking
        {
            get => _isSpeaking;
            set { _isSpeaking = value; OnPropertyChanged(); }
        }

        public bool IsSpeakFeatureEnabled
        {
            get => _isSpeakFeatureEnabled;
            set { _isSpeakFeatureEnabled = value; OnPropertyChanged(); }
        }

        public string SpeakButtonTooltip
        {
            get => _speakButtonTooltip;
            set { _speakButtonTooltip = value; OnPropertyChanged(); }
        }

        public string MuteIcon => _isMuted ? "🔇" : "🔊";

        // --- Originais ---

        public string SubtitleText
        {
            get => _subtitleText;
            set { _subtitleText = value; OnPropertyChanged(); }
        }

        public string TranslatedSubtitleText
        {
            get => _translatedSubtitleText;
            set { _translatedSubtitleText = value; OnPropertyChanged(); }
        }

        public string StatusText
        {
            get => _statusText;
            set { _statusText = value; OnPropertyChanged(); }
        }

        public bool IsConnected
        {
            get => _isConnected;
            set { _isConnected = value; OnPropertyChanged(); }
        }

        public bool IsAnalyzing
        {
            get => _isAnalyzing;
            set { _isAnalyzing = value; OnPropertyChanged(); }
        }

        public string ConnectButtonText => IsConnected ? "Desconectar" : "Conectar";

        public async Task ToggleConnectionAsync()
        {
            try
            {
                if (IsConnected)
                {
                    _audioCapture.Stop();
                    await _geminiService.StopStreamingAsync();
                    IsConnected = false;
                    StatusText = "Desconectado";
                }
                else
                {
                    StatusText = "Iniciando...";
                    await _geminiService.StartStreamingAsync(SelectedOutputDevice, IsSpeakFeatureEnabled);
                    _audioCapture.Start(SelectedInputDevice);
                    IsConnected = true;
                    StatusText = "Conectado ao Gemini Live";
                }
                OnPropertyChanged(nameof(ConnectButtonText));
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Erro: {ex.Message}", "Erro de Conexão", MessageBoxButton.OK, MessageBoxImage.Error);
                IsConnected = false;
                StatusText = "Erro";
            }
        }

        // --- Métodos Portados de UI ---

        public void ToggleSettings()
        {
            IsSettingsVisible = !IsSettingsVisible;
            if (IsSettingsVisible) IsHistoryVisible = false;
        }

        public void ToggleHistory()
        {
            IsHistoryVisible = !IsHistoryVisible;
            if (IsHistoryVisible) IsSettingsVisible = false;
        }

        public void ToggleMute()
        {
            _isMuted = !_isMuted;
            OnPropertyChanged(nameof(MuteIcon));
            // Placeholder: Implement Gemini muting logic
        }

        public void ToggleSpeakConnection()
        {
            IsSpeakConnected = !IsSpeakConnected;
            SpeakStatusText = IsSpeakConnected ? "Intérprete Ativo" : "Intérprete Desativado";
        }

        public void RefreshDevices()
        {
            LoadDevices();
        }

        public void ClearHistory()
        {
            History.Clear();
        }

        private void LoadDevices()
        {
            AllInputDevices.Clear();
            foreach (var m in AudioHelper.GetInputDevices())
            {
                AllInputDevices.Add(new CombinedInputDevice { DeviceIndex = m.DeviceIndex, Name = $"🎤 {m.Name}", IsMic = true });
            }
            foreach (var l in AudioHelper.GetLoopbackDevices())
            {
                AllInputDevices.Add(new CombinedInputDevice { DeviceIndex = l.DeviceIndex, Name = $"🔊 {l.Name}", IsLoopback = true });
            }
            if (AllInputDevices.Count > 0) SelectedInputDevice = AllInputDevices.FirstOrDefault(x => x.IsLoopback) ?? AllInputDevices[0];

            OutputDevices.Clear();
            foreach (var o in AudioHelper.GetOutputDevices()) OutputDevices.Add(o);
            if (OutputDevices.Count > 0) SelectedOutputDevice = OutputDevices[0];
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        public void Dispose()
        {
            _audioCapture.Dispose();
            _geminiService.Dispose();
        }
    }
}
