using MeetingTranslator.Models;

namespace MeetingTranslator.Services;

public interface IInterpreterService : IDisposable
{
    event EventHandler<StatusEventArgs>? StatusChanged;
    event EventHandler<StatusEventArgs>? ErrorOccurred;
    event EventHandler<bool>? SpeakingChanged;

    bool IsConnected { get; }
    bool IsMuted { get; set; }

    void SetSharedAudioState(SharedAudioState state);
    Task StartAsync(int micDeviceIndex, int outputDeviceIndex);
    Task StopAsync();
    void ClearPendingAudio();
}
