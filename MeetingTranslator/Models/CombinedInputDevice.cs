namespace MeetingTranslator.Models;

public class CombinedInputDevice
{
    public int DeviceIndex { get; set; }
    public string Name { get; set; } = "";
    public bool IsMic { get; set; }
    public bool IsLoopback { get; set; }

    public override string ToString() => Name;
}
