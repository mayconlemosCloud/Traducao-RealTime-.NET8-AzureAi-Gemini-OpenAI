using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;
using MeetingTranslator.Models;

namespace MeetingTranslator.Services.Azure;

public static class AzureVoiceCatalogService
{
    public static async Task<List<AzureVoiceInfo>> GetVoicesAsync(string speechKey, string region, string? localeFilter = null, CancellationToken ct = default)
    {
        var config = SpeechConfig.FromSubscription(speechKey, region);
        using var synthesizer = new SpeechSynthesizer(config, (AudioConfig?)null);

        // Try full catalog first
        var aggregated = new Dictionary<string, AzureVoiceInfo>(StringComparer.OrdinalIgnoreCase);

        async Task AddFromAsync(string? locale)
        {
            var res = string.IsNullOrWhiteSpace(locale)
                ? await synthesizer.GetVoicesAsync().ConfigureAwait(false)
                : await synthesizer.GetVoicesAsync(locale).ConfigureAwait(false);
            if (res?.Voices == null) return;
            foreach (var v in res.Voices)
            {
                ct.ThrowIfCancellationRequested();
                var shortName = v.ShortName ?? v.Name ?? string.Empty;
                if (string.IsNullOrWhiteSpace(shortName)) continue;
                aggregated[shortName] = new AzureVoiceInfo
                {
                    ShortName = shortName,
                    LocalName = v.LocalName ?? string.Empty,
                    Locale = v.Locale ?? string.Empty,
                    Gender = v.Gender.ToString()
                };
            }
        }

        await AddFromAsync(localeFilter).ConfigureAwait(false);

        // If suspiciously small, try common locales as a fallback (some regions restrict all-voices listing)
        if (aggregated.Count <= 1 && string.IsNullOrWhiteSpace(localeFilter))
        {
            var locales = new[] { "en-US", "en-GB", "pt-BR", "es-ES", "fr-FR", "de-DE" };
            foreach (var loc in locales)
            {
                await AddFromAsync(loc).ConfigureAwait(false);
            }
        }

        return aggregated.Values
            .OrderBy(v => v.Locale)
            .ThenBy(v => v.LocalName)
            .ThenBy(v => v.ShortName)
            .ToList();
    }

    public static async Task PlayPreviewAsync(string speechKey, string region, string voiceShortName, string? text = null, CancellationToken ct = default)
    {
        var config = SpeechConfig.FromSubscription(speechKey, region);
        config.SpeechSynthesisVoiceName = voiceShortName;

        // Use default speaker for quick preview
        using var audioConfig = AudioConfig.FromDefaultSpeakerOutput();
        using var synthesizer = new SpeechSynthesizer(config, audioConfig);

        var sample = text;
        if (string.IsNullOrWhiteSpace(sample))
        {
            // Simple locale-aware sample phrase
            sample = voiceShortName.Contains("pt-", StringComparison.OrdinalIgnoreCase)
                ? "Olá! Esta é uma prévia da voz."
                : voiceShortName.Contains("es-", StringComparison.OrdinalIgnoreCase)
                    ? "¡Hola! Esta es una vista previa de la voz."
                    : "Hello! This is a quick voice preview.";
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var speakTask = synthesizer.SpeakTextAsync(sample);
        using (cts.Token.Register(() => speakTask?.Dispose())) { }
        var result = await speakTask.ConfigureAwait(false);
        if (result.Reason == ResultReason.Canceled)
        {
            var details = SpeechSynthesisCancellationDetails.FromResult(result);
            throw new InvalidOperationException($"Synthesis canceled: {details.Reason} - {details.ErrorDetails}");
        }
    }
}
