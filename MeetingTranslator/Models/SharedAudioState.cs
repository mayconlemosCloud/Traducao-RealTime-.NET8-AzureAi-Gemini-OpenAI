namespace MeetingTranslator.Models;

/// <summary>
/// Estado compartilhado entre os serviços de tradução e interpretação.
/// Cada serviço sinaliza quando está tocando áudio para evitar feedback loop.
/// </summary>
public class SharedAudioState
{
    /// <summary>True quando o tradutor de voz está tocando áudio.</summary>
    public volatile bool RealtimePlaybackActive;

    /// <summary>True quando o intérprete está tocando áudio.</summary>
    public volatile bool SpeakPlaybackActive;

    /// <summary>True durante cooldown pós-playback do intérprete.</summary>
    public volatile bool SpeakCooldownActive;

    /// <summary>True quando o intérprete está conectado e ativo.</summary>
    public volatile bool SpeakServiceActive;

    /// <summary>True se qualquer serviço está tocando/em cooldown.</summary>
    public bool IsAnyExternalPlaybackActive =>
        RealtimePlaybackActive || SpeakPlaybackActive || SpeakCooldownActive;
}
