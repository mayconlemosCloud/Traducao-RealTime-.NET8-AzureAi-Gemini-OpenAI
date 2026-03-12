using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Data;
using MeetingTranslator.Models;

namespace MeetingTranslator.ViewModels;

public partial class MainViewModel
{
    public async Task LoadAzureVoicesAsync(string? localeFilter = null)
    {
        try
        {
            IsAzureBusy = true;

            var speechKey = string.IsNullOrWhiteSpace(AzureSpeechKey)
                ? Environment.GetEnvironmentVariable("AZURE_SPEECH_KEY")
                : AzureSpeechKey;
            var speechRegion = string.IsNullOrWhiteSpace(AzureSpeechRegion)
                ? Environment.GetEnvironmentVariable("AZURE_SPEECH_REGION")
                : AzureSpeechRegion;

            if (string.IsNullOrWhiteSpace(speechKey) || string.IsNullOrWhiteSpace(speechRegion))
            {
                SpeakStatusText = "⚠ Configure AZURE_SPEECH_KEY e AZURE_SPEECH_REGION";
                return;
            }

            var list = await Services.Azure.AzureVoiceCatalogService
                .GetVoicesAsync(speechKey!, speechRegion!, localeFilter ?? string.Empty)
                .ConfigureAwait(false);

            await _dispatcher.InvokeAsync(() =>
            {
                AzureVoices.Clear();
                foreach (var v in list)
                    AzureVoices.Add(v);

                // Atualiza view para aplicar filtro atual
                AzureVoicesView.Refresh();

                // Auto-select if current voice matches
                if (!string.IsNullOrWhiteSpace(AzureSpeechVoice))
                {
                    var match = AzureVoices.FirstOrDefault(v => v.ShortName.Equals(AzureSpeechVoice, StringComparison.OrdinalIgnoreCase));
                    if (match != null)
                        SelectedAzureVoice = match;
                }

                StatusText = $"Vozes Azure: {AzureVoices.Count}";
            });
        }
        catch (Exception ex)
        {
            SpeakStatusText = $"⚠ Erro ao listar vozes: {ex.Message}";
        }
        finally
        {
            IsAzureBusy = false;
        }
    }

    public async Task PreviewSelectedAzureVoiceAsync()
    {
        var voice = SelectedAzureVoice?.ShortName ?? AzureSpeechVoice;
        if (string.IsNullOrWhiteSpace(voice))
        {
            SpeakStatusText = "⚠ Selecione uma voz";
            return;
        }

        var speechKey = string.IsNullOrWhiteSpace(AzureSpeechKey)
            ? Environment.GetEnvironmentVariable("AZURE_SPEECH_KEY")
            : AzureSpeechKey;
        var speechRegion = string.IsNullOrWhiteSpace(AzureSpeechRegion)
            ? Environment.GetEnvironmentVariable("AZURE_SPEECH_REGION")
            : AzureSpeechRegion;

        if (string.IsNullOrWhiteSpace(speechKey) || string.IsNullOrWhiteSpace(speechRegion))
        {
            SpeakStatusText = "⚠ Configure AZURE_SPEECH_KEY e AZURE_SPEECH_REGION";
            return;
        }

        try
        {
            IsAzureBusy = true;
            await Services.Azure.AzureVoiceCatalogService
                .PlayPreviewAsync(speechKey!, speechRegion!, voice!)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            SpeakStatusText = $"⚠ Erro na previa: {ex.Message}";
        }
        finally
        {
            IsAzureBusy = false;
        }
    }

    private async Task ApplySelectedVoiceToActiveServicesAsync()
    {
        try
        {
            if (_azureVoiceService != null && !string.IsNullOrWhiteSpace(AzureSpeechVoice))
            {
                // Aplica para ambos para manter consistência
                _azureVoiceService.SetBothVoices(AzureSpeechVoice);

                if (IsConnected && _useAzureProvider && SelectedMode == TranslationMode.Voice)
                {
                    await _azureVoiceService.StopAsync().ConfigureAwait(false);
                    await _azureVoiceService.StartAsync(
                        SelectedMicDevice?.DeviceIndex ?? 0,
                        SelectedLoopbackDevice?.DeviceIndex ?? 0,
                        UseMic,
                        UseLoopback
                    ).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex)
        {
            StatusText = $"⚠ Erro ao aplicar voz: {ex.Message}";
        }
    }
}
