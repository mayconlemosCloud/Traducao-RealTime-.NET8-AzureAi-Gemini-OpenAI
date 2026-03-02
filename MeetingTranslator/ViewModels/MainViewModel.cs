using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using MeetingTranslator.Models;
using MeetingTranslator.Services;
using dotenv.net;

namespace MeetingTranslator.ViewModels;

public class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private RealtimeService? _service;
    private readonly Dispatcher _dispatcher;

    // ─── BINDABLE PROPERTIES ───────────────────────────────
    private string _subtitleText = "Aguardando conexão...";
    public string SubtitleText
    {
        get => _subtitleText;
        set { _subtitleText = value; OnPropertyChanged(); }
    }

    private string _statusText = "Desconectado";
    public string StatusText
    {
        get => _statusText;
        set { _statusText = value; OnPropertyChanged(); }
    }

    private bool _isConnected;
    public bool IsConnected
    {
        get => _isConnected;
        set { _isConnected = value; OnPropertyChanged(); OnPropertyChanged(nameof(ConnectButtonText)); }
    }

    public string ConnectButtonText => IsConnected ? "Desconectar" : "Conectar";

    private bool _isHistoryVisible;
    public bool IsHistoryVisible
    {
        get => _isHistoryVisible;
        set { _isHistoryVisible = value; OnPropertyChanged(); }
    }

    private bool _isMuted;
    public bool IsMuted
    {
        get => _isMuted;
        set { _isMuted = value; OnPropertyChanged(); OnPropertyChanged(nameof(MuteIcon)); }
    }
    public string MuteIcon => IsMuted ? "🔇" : "🎤";

    private bool _useMic = true;
    public bool UseMic
    {
        get => _useMic;
        set { _useMic = value; OnPropertyChanged(); }
    }

    private bool _useLoopback = true;
    public bool UseLoopback
    {
        get => _useLoopback;
        set { _useLoopback = value; OnPropertyChanged(); }
    }

    private AudioDeviceInfo? _selectedMicDevice;
    public AudioDeviceInfo? SelectedMicDevice
    {
        get => _selectedMicDevice;
        set { _selectedMicDevice = value; OnPropertyChanged(); }
    }

    private AudioDeviceInfo? _selectedLoopbackDevice;
    public AudioDeviceInfo? SelectedLoopbackDevice
    {
        get => _selectedLoopbackDevice;
        set { _selectedLoopbackDevice = value; OnPropertyChanged(); }
    }

    private bool _isSettingsVisible;
    public bool IsSettingsVisible
    {
        get => _isSettingsVisible;
        set { _isSettingsVisible = value; OnPropertyChanged(); }
    }

    private bool _isAssistantTyping;
    public bool IsAssistantTyping
    {
        get => _isAssistantTyping;
        set { _isAssistantTyping = value; OnPropertyChanged(); }
    }

    private string _inputText = "";
    public string InputText
    {
        get => _inputText;
        set
        {
            _inputText = value;
            OnPropertyChanged();
            if (SendMessageCommand is DelegateCommand cmd)
            {
                cmd.RaiseCanExecuteChanged();
            }
        }
    }

    // ─── COLLECTIONS ──────────────────────────────────────
    public ObservableCollection<AudioDeviceInfo> MicDevices { get; } = new();
    public ObservableCollection<AudioDeviceInfo> LoopbackDevices { get; } = new();
    public ObservableCollection<ConversationEntry> History { get; } = new();

    // partial transcript accumulator (texto completo atual da fala)
    private string _partialTranscript = "";

    public ICommand SendMessageCommand { get; }

    public MainViewModel()
    {
        _dispatcher = Application.Current.Dispatcher;
        LoadDevices();
        SendMessageCommand = new DelegateCommand(_ => SendMessage(), _ => !string.IsNullOrWhiteSpace(InputText));
    }

    private void LoadDevices()
    {
        MicDevices.Clear();
        foreach (var d in RealtimeService.GetInputDevices())
            MicDevices.Add(d);

        LoopbackDevices.Clear();
        foreach (var d in RealtimeService.GetLoopbackDevices())
            LoopbackDevices.Add(d);

        if (MicDevices.Count > 0) SelectedMicDevice = MicDevices[0];
        if (LoopbackDevices.Count > 0) SelectedLoopbackDevice = LoopbackDevices[0];
    }

    // ─── COMMANDS ──────────────────────────────────────────
    public async Task ToggleConnectionAsync()
    {
        if (IsConnected)
        {
            await DisconnectAsync();
        }
        else
        {
            await ConnectAsync();
        }
    }

    private async Task ConnectAsync()
    {
        // Procura .env na pasta do executável e na pasta de trabalho atual
        var exeDir = AppDomain.CurrentDomain.BaseDirectory;
        DotEnv.Load(new DotEnvOptions(envFilePaths: new[] {
            Path.Combine(exeDir, ".env"),
            Path.Combine(Directory.GetCurrentDirectory(), ".env"),
            ".env"
        }, ignoreExceptions: true));

        var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            SubtitleText = "⚠ OPENAI_API_KEY não encontrada";
            return;
        }

        _service = new RealtimeService(apiKey);

        _service.TranscriptReceived += OnTranscriptReceived;
        _service.StatusChanged += OnStatusChanged;
        _service.ErrorOccurred += OnError;

        try
        {
            await _service.StartAsync(
                SelectedMicDevice?.DeviceIndex ?? 0,
                SelectedLoopbackDevice?.DeviceIndex ?? 0,
                UseMic,
                UseLoopback
            );
            IsConnected = true;
            SubtitleText = "Pronto — ouvindo...";
        }
        catch (Exception ex)
        {
            SubtitleText = $"⚠ Erro: {ex.Message}";
        }
    }

    private async Task DisconnectAsync()
    {
        if (_service != null)
        {
            await _service.StopAsync();
            _service.Dispose();
            _service = null;
        }
        IsConnected = false;
        SubtitleText = "Desconectado";
    }

    public void ToggleHistory()
    {
        IsHistoryVisible = !IsHistoryVisible;
    }

    public void ToggleSettings()
    {
        IsSettingsVisible = !IsSettingsVisible;
    }

    public void ToggleMute()
    {
        IsMuted = !IsMuted;
        // TODO: actually mute the mic in the service
    }

    public void RefreshDevices()
    {
        LoadDevices();
    }

    private void SendMessage()
    {
        var text = InputText.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        History.Add(new ConversationEntry
        {
            Speaker = Speaker.You,
            TranslatedText = text
        });

        InputText = string.Empty;
    }

    // ─── EVENT HANDLERS ────────────────────────────────────
    private void OnTranscriptReceived(object? sender, TranscriptEventArgs e)
    {
        try
        {
            var logLine =
                $"[{DateTime.Now:HH:mm:ss}] IsPartial={e.IsPartial}, Speaker={e.Speaker}, Text=\"{e.TranslatedText}\"{Environment.NewLine}";
            var logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "transcripts.log");
            File.AppendAllText(logPath, logLine);
            Console.WriteLine(logLine);
        }
        catch
        {
            // logging best-effort, não pode quebrar a UI
        }

        _dispatcher.Invoke(() =>
        {
            if (e.IsPartial)
            {
                IsAssistantTyping = true;
                // Serviço já envia o texto completo acumulado; apenas espelhamos no subtítulo
                _partialTranscript = e.TranslatedText;
                SubtitleText = e.TranslatedText;
            }
            else
            {
                // Final transcript
                IsAssistantTyping = false;

                // Texto final já vem completo do serviço
                var finalText = string.IsNullOrEmpty(e.TranslatedText) ? _partialTranscript : e.TranslatedText;

                SubtitleText = finalText;

                // Apenas mensagens finais vão para o histórico (bolha única completa)
                History.Add(new ConversationEntry
                {
                    Speaker = e.Speaker,
                    TranslatedText = finalText
                });

                _partialTranscript = "";
            }
        });
    }

    private void OnStatusChanged(object? sender, StatusEventArgs e)
    {
        _dispatcher.Invoke(() => StatusText = e.Message);
    }

    private void OnError(object? sender, StatusEventArgs e)
    {
        _dispatcher.Invoke(() => SubtitleText = $"⚠ {e.Message}");
    }

    // ─── INotifyPropertyChanged ────────────────────────────
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public void Dispose()
    {
        _service?.Dispose();
    }
}
