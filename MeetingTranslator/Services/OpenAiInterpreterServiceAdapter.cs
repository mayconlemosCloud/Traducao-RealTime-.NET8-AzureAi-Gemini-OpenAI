using MeetingTranslator.Models;

namespace MeetingTranslator.Services;

/// <summary>
/// Adaptador para encaixar o SpeakTranslateService atual no seletor de provedores
/// sem alterar a implementacao existente do interprete OpenAI.
/// </summary>
public sealed class OpenAiInterpreterServiceAdapter : IInterpreterService
{
    private readonly SpeakTranslateService _inner;

    public OpenAiInterpreterServiceAdapter(string apiKey, SharedAudioState? sharedAudioState = null)
    {
        _inner = new SpeakTranslateService(apiKey, sharedAudioState);
        _inner.StatusChanged += ForwardStatusChanged;
        _inner.ErrorOccurred += ForwardErrorOccurred;
        _inner.SpeakingChanged += ForwardSpeakingChanged;
    }

    public event EventHandler<StatusEventArgs>? StatusChanged;
    public event EventHandler<StatusEventArgs>? ErrorOccurred;
    public event EventHandler<bool>? SpeakingChanged;

    public bool IsConnected => _inner.IsConnected;

    public bool IsMuted
    {
        get => _inner.IsMuted;
        set => _inner.IsMuted = value;
    }

    public void SetSharedAudioState(SharedAudioState state) => _inner.SetSharedAudioState(state);

    public Task StartAsync(int micDeviceIndex, int outputDeviceIndex) => _inner.StartAsync(micDeviceIndex, outputDeviceIndex);

    public Task StopAsync() => _inner.StopAsync();

    public void ClearPendingAudio() => _inner.ClearPendingAudio();

    public void Dispose()
    {
        _inner.StatusChanged -= ForwardStatusChanged;
        _inner.ErrorOccurred -= ForwardErrorOccurred;
        _inner.SpeakingChanged -= ForwardSpeakingChanged;
        _inner.Dispose();
    }

    private void ForwardStatusChanged(object? sender, StatusEventArgs e) => StatusChanged?.Invoke(this, e);

    private void ForwardErrorOccurred(object? sender, StatusEventArgs e) => ErrorOccurred?.Invoke(this, e);

    private void ForwardSpeakingChanged(object? sender, bool speaking) => SpeakingChanged?.Invoke(this, speaking);
}