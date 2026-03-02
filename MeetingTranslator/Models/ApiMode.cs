namespace MeetingTranslator.Models;

/// <summary>
/// Modo de operação do tradutor.
/// </summary>
public enum ApiMode
{
    /// <summary>
    /// OpenAI Realtime (voz): envia áudio, recebe áudio + texto traduzido.
    /// Resultado só aparece após silêncio detectado.
    /// Ideal para conversas presenciais com tradução falada.
    /// </summary>
    Voice,

    /// <summary>
    /// OpenAI Realtime Transcription: envia áudio, recebe texto em tempo real
    /// enquanto a pessoa fala. Tradução via Chat API após cada frase.
    /// Sem saída de áudio. Ideal para reuniões online / legendas.
    /// </summary>
    Transcription
}
