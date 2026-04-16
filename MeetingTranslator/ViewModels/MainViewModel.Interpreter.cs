using System;
using System.Threading.Tasks;
using MeetingTranslator.Models;
using MeetingTranslator.Services.Azure;
using MeetingTranslator.Services.Common;
using MeetingTranslator.Services.OpenAI;

namespace MeetingTranslator.ViewModels;

public partial class MainViewModel
{
    public async Task ToggleSpeakConnectionAsync()
    {
        if (IsSpeakConnected)
            await DisconnectSpeakAsync();
        else
            await ConnectSpeakAsync();
    }

    private async Task ConnectSpeakAsync()
    {
        LoadEnvironmentVariables();

        try
        {
            _speakService = CreateInterpreterService();
            if (_speakService == null)
                return;

            _speakService.StatusChanged += OnSpeakStatusChanged;
            _speakService.ErrorOccurred += OnSpeakError;
            _speakService.SpeakingChanged += OnSpeakingChanged;

            await _speakService.StartAsync(
                SelectedSpeakMicDevice?.DeviceIndex ?? 0,
                SelectedSpeakOutputDevice?.DeviceIndex ?? 0
            );

            IsSpeakConnected = true;
        }
        catch (Exception ex)
        {
            var detailedMessage = ex.InnerException == null
                ? ex.Message
                : $"{ex.Message} | Inner: {ex.InnerException.Message}";

            _logChannel.Writer.TryWrite(("error.log", $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [SpeakStart] {ex}"));
            SpeakStatusText = $"⚠ Erro ao iniciar intérprete: {detailedMessage}";
        }
    }

    private IInterpreterService? CreateInterpreterService()
    {
        switch (SelectedInterpreterProvider)
        {
            case InterpreterProvider.OpenAI:
                var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
                if (string.IsNullOrWhiteSpace(apiKey))
                {
                    SpeakStatusText = "⚠ OPENAI_API_KEY nao encontrada";
                    return null;
                }
                return new OpenAiInterpreterAdapter(apiKey, _sharedAudioState);

            case InterpreterProvider.AzureSpeech:
                var speechKey = string.IsNullOrWhiteSpace(AzureSpeechKey)
                    ? Environment.GetEnvironmentVariable("AZURE_SPEECH_KEY")
                    : AzureSpeechKey;
                var speechRegion = string.IsNullOrWhiteSpace(AzureSpeechRegion)
                    ? Environment.GetEnvironmentVariable("AZURE_SPEECH_REGION")
                    : AzureSpeechRegion;
                var interpreterVoice = string.IsNullOrWhiteSpace(InterpreterVoiceCode)
                    ? "en-US-JennyNeural"
                    : InterpreterVoiceCode;

                if (string.IsNullOrWhiteSpace(speechKey) || string.IsNullOrWhiteSpace(speechRegion))
                {
                    SpeakStatusText = "⚠ Configure AZURE_SPEECH_KEY e AZURE_SPEECH_REGION no .env para usar o interprete Azure";
                    return null;
                }
                return new AzureSpeechInterpreterService(speechKey, speechRegion, _sharedAudioState, interpreterVoice);

            default:
                SpeakStatusText = "⚠ Provedor de interprete invalido";
                return null;
        }
    }

    private async Task DisconnectSpeakAsync()
    {
        if (_speakService != null)
        {
            _speakService.StatusChanged -= OnSpeakStatusChanged;
            _speakService.ErrorOccurred -= OnSpeakError;
            _speakService.SpeakingChanged -= OnSpeakingChanged;

            await _speakService.StopAsync();
            _speakService.Dispose();
            _speakService = null;
        }

        IsSpeakConnected = false;
        SpeakStatusText = "";
    }

    private void OnSpeakStatusChanged(object? sender, StatusEventArgs e)
    {
        _dispatcher.BeginInvoke(() => SpeakStatusText = e.Message);
    }

    private void OnSpeakError(object? sender, StatusEventArgs e)
    {
        _logChannel.Writer.TryWrite(("error.log", $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [Speak] {e.Message}"));
        _dispatcher.BeginInvoke(() => SpeakStatusText = $"⚠ {e.Message}");
    }

    private void OnSpeakingChanged(object? sender, bool isSpeaking)
    {
        _dispatcher.BeginInvoke(() => IsSpeaking = isSpeaking);
    }
}
