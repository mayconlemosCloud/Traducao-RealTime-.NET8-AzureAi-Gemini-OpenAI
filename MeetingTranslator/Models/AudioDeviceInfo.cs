namespace MeetingTranslator.Models;

public class AudioDeviceInfo
{
    public int DeviceIndex { get; set; }
    public string Name { get; set; } = "";

    public override string ToString() => Name;
}
