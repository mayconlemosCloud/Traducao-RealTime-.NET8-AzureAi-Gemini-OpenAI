namespace MeetingGoogle.Models
{
    public class CombinedInputDevice
    {
        public int DeviceIndex { get; set; }
        public string Name { get; set; } = string.Empty;
        public bool IsMic { get; set; }
        public bool IsLoopback { get; set; }
    }
}
