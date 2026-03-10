using System.IO;
using MeetingTranslator.Models;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace MeetingTranslator.Services.Common;

/// <summary>
/// Utilitários de áudio compartilhados entre os serviços de tradução e interpretação.
/// </summary>
public static class AudioHelper
{
    public const int DefaultSampleRate = 24000;
    public const int DefaultChannels = 1;
    public const int DefaultBitsPerSample = 16;

    [ThreadStatic] private static byte[]? _resampleBuffer;
    [ThreadStatic] private static MemoryStream? _resampleMs;

    /// <summary>
    /// Calcula RMS (Root Mean Square) de um buffer PCM16 para detectar energia de voz.
    /// </summary>
    public static float CalculateRms(byte[] buffer, int bytesRecorded)
    {
        if (bytesRecorded < 2) return 0f;

        long sumSquares = 0;
        int sampleCount = bytesRecorded / 2;

        for (int i = 0; i < bytesRecorded - 1; i += 2)
        {
            short sample = (short)(buffer[i] | (buffer[i + 1] << 8));
            sumSquares += (long)sample * sample;
        }

        return (float)Math.Sqrt((double)sumSquares / sampleCount);
    }

    /// <summary>
    /// Converte áudio de um formato para outro (ex: loopback 48kHz stereo → 24kHz mono PCM16).
    /// Reutiliza buffers por thread para evitar alocações.
    /// </summary>
    public static byte[] ConvertAudioFormat(byte[] sourceBuffer, int bytesRecorded, WaveFormat sourceFormat, WaveFormat targetFormat)
    {
        using var sourceStream = new RawSourceWaveStream(sourceBuffer, 0, bytesRecorded, sourceFormat);
        using var resampler = new MediaFoundationResampler(sourceStream, targetFormat);
        resampler.ResamplerQuality = 60;

        _resampleBuffer ??= new byte[4096];
        var ms = _resampleMs ??= new MemoryStream(16384);
        ms.SetLength(0);

        int read;
        while ((read = resampler.Read(_resampleBuffer, 0, _resampleBuffer.Length)) > 0)
        {
            ms.Write(_resampleBuffer, 0, read);
        }
        return ms.ToArray();
    }

    /// <summary>
    /// Lista dispositivos de entrada (microfones) via WaveIn.
    /// </summary>
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

    /// <summary>
    /// Lista dispositivos de loopback (áudio do sistema) via WASAPI.
    /// </summary>
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

    /// <summary>
    /// Lista dispositivos de saída de áudio via WaveOut.
    /// </summary>
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
