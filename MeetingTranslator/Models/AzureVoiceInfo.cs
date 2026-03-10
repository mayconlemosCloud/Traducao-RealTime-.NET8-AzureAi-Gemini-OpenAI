namespace MeetingTranslator.Models;

public sealed class AzureVoiceInfo
{
    public string ShortName { get; init; } = string.Empty;
    public string LocalName { get; init; } = string.Empty;
    public string Locale { get; init; } = string.Empty;
    public string Gender { get; init; } = string.Empty;
    public string? DisplayName => string.IsNullOrWhiteSpace(LocalName)
        ? ShortName
        : $"{LocalName} ({ShortName}, {Locale})";
}
