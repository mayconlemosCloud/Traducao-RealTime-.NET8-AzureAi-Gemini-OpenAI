namespace MeetingTranslator.Models;

public enum Speaker
{
    Them,  // pessoa da reunião falando em inglês
    You    // usuário falando em português
}

public class ConversationEntry
{
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public Speaker Speaker { get; set; }
    public string OriginalText { get; set; } = "";
    public string TranslatedText { get; set; } = "";

    public string TimeLabel => Timestamp.ToString("HH:mm");
}
