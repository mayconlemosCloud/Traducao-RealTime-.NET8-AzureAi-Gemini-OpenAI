namespace MeetingTranslator.Models;

/// <summary>
/// Modo de operação do tradutor.
/// </summary>
public enum TranslationMode
{
    /// <summary>
    /// IA ouve, traduz e fala. Resultado após silêncio detectado.
    /// Ideal para conversas presenciais com tradução falada.
    /// </summary>
    Voice,

    /// <summary>
    /// Texto em tempo real enquanto a pessoa fala. Tradução após cada frase.
    /// Sem saída de áudio. Ideal para reuniões online / legendas.
    /// </summary>
    Transcription
}
