using System.Collections.Generic;
using MeetingGoogle.Models;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace MeetingGoogle.Services
{
    public static class AudioHelper
    {
        public static List<AudioDeviceInfo> GetInputDevices()
        {
            var devices = new List<AudioDeviceInfo>();
            for (int i = 0; i < WaveInEvent.DeviceCount; i++)
            {
                var caps = WaveInEvent.GetCapabilities(i);
                devices.Add(new AudioDeviceInfo { DeviceIndex = i, Name = caps.ProductName });
            }
            return devices;
        }

        public static List<AudioDeviceInfo> GetLoopbackDevices()
        {
            var devices = new List<AudioDeviceInfo>();
            var enumerator = new MMDeviceEnumerator();
            int idx = 0;
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                devices.Add(new AudioDeviceInfo { DeviceIndex = idx++, Name = device.FriendlyName });
            }
            return devices;
        }

        public static List<AudioDeviceInfo> GetOutputDevices()
        {
            var devices = new List<AudioDeviceInfo>();
            for (int i = 0; i < WaveOut.DeviceCount; i++)
            {
                var caps = WaveOut.GetCapabilities(i);
                devices.Add(new AudioDeviceInfo { DeviceIndex = i, Name = caps.ProductName });
            }
            return devices;
        }
    }
}
