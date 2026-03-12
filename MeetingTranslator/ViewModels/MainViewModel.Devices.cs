using System.Linq;
using MeetingTranslator.Models;
using MeetingTranslator.Services.Common;

namespace MeetingTranslator.ViewModels;

public partial class MainViewModel
{
    private void LoadDevices()
    {
        MicDevices.Clear();
        foreach (var d in AudioHelper.GetInputDevices())
            MicDevices.Add(d);

        OnPropertyChanged(nameof(ShowOpenAiInterpreterSettings));
        OnPropertyChanged(nameof(ShowAzureInterpreterSettings));

        LoopbackDevices.Clear();
        foreach (var d in AudioHelper.GetLoopbackDevices())
            LoopbackDevices.Add(d);

        SpeakOutputDevices.Clear();
        foreach (var d in AudioHelper.GetOutputDevices())
            SpeakOutputDevices.Add(d);

        if (MicDevices.Count > 0) SelectedMicDevice = MicDevices[0];
        if (LoopbackDevices.Count > 0) SelectedLoopbackDevice = LoopbackDevices[0];
        if (MicDevices.Count > 0 && SelectedSpeakMicDevice == null) SelectedSpeakMicDevice = MicDevices[0];
        if (SpeakOutputDevices.Count > 0) SelectedSpeakOutputDevice = SpeakOutputDevices[0];

        // Preenche lista combinada de entrada (mic + loopback)
        AllInputDevices.Clear();
        foreach (var m in MicDevices)
        {
            AllInputDevices.Add(new CombinedInputDevice
            {
                DeviceIndex = m.DeviceIndex,
                Name = $"🎤 {m.Name}",
                IsMic = true,
                IsLoopback = false
            });
        }
        foreach (var l in LoopbackDevices)
        {
            AllInputDevices.Add(new CombinedInputDevice
            {
                DeviceIndex = l.DeviceIndex,
                Name = $"🔊 {l.Name}",
                IsMic = false,
                IsLoopback = true
            });
        }

        // Define padrão: prioriza microfone, senão loopback
        if (MicDevices.Count > 0)
        {
            SelectedInputDevice = AllInputDevices.FirstOrDefault(x => x.IsMic);
        }
        else if (LoopbackDevices.Count > 0)
        {
            SelectedInputDevice = AllInputDevices.FirstOrDefault(x => x.IsLoopback);
        }
    }

    public void RefreshDevices()
    {
        LoadDevices();
    }
}
