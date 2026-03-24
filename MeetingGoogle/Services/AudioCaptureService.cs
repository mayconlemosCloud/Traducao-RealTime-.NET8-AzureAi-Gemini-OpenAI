using System;
using System.Linq;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using MeetingGoogle.Models;

namespace MeetingGoogle.Services
{
    public class AudioCaptureService : IDisposable
    {
        private IWaveIn? _capture;
        public event EventHandler<byte[]>? DataAvailable;
        public event EventHandler<float>? PeakLevelChanged;
        private bool _isRecording;

        public WaveFormat WaveFormat => _capture?.WaveFormat ?? new WaveFormat(16000, 16, 1);
        private readonly WaveFormat _targetFormat = new WaveFormat(16000, 16, 1);

        public void Start(CombinedInputDevice? device)
        {
            if (_isRecording || device == null) return;

            if (device.IsMic)
            {
                var waveIn = new WaveInEvent { DeviceNumber = device.DeviceIndex, WaveFormat = _targetFormat };
                waveIn.DataAvailable += (s, e) =>
                {
                    if (e.BytesRecorded == 0) return;
                    var copiedBuffer = new byte[e.BytesRecorded];
                    Array.Copy(e.Buffer, copiedBuffer, e.BytesRecorded);

                    float peak = CalculatePeak(copiedBuffer, e.BytesRecorded);
                    PeakLevelChanged?.Invoke(this, peak);
                    DataAvailable?.Invoke(this, copiedBuffer);
                };
                waveIn.RecordingStopped += Capture_RecordingStopped;
                _capture = waveIn;
            }
            else
            {
                var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
                var endpoint = enumerator.EnumerateAudioEndPoints(NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.DeviceState.Active).ElementAtOrDefault(device.DeviceIndex);
                var loopback = endpoint != null ? new WasapiLoopbackCapture(endpoint) : new WasapiLoopbackCapture();
                
                loopback.DataAvailable += (s, e) =>
                {
                    if (e.BytesRecorded == 0) return;
                    var converted = ConvertAudio(e.Buffer, e.BytesRecorded, loopback.WaveFormat, _targetFormat);
                    float peak = CalculatePeak(converted, converted.Length);
                    PeakLevelChanged?.Invoke(this, peak);
                    DataAvailable?.Invoke(this, converted);
                };
                loopback.RecordingStopped += Capture_RecordingStopped;
                _capture = loopback;
            }

            _capture.StartRecording();
            _isRecording = true;
        }

        private void Capture_RecordingStopped(object? sender, StoppedEventArgs e)
        {
            _isRecording = false;
        }

        public void Stop()
        {
            if (!_isRecording) return;
            _capture?.StopRecording();
            _isRecording = false;
        }

        private byte[] ConvertAudio(byte[] buffer, int length, WaveFormat source, WaveFormat target)
        {
            using var ms = new System.IO.MemoryStream();
            using (var raw = new RawSourceWaveStream(buffer, 0, length, source))
            using (var resampler = new MediaFoundationResampler(raw, target))
            {
                resampler.ResamplerQuality = 60;
                byte[] temp = new byte[4096];
                int read;
                while ((read = resampler.Read(temp, 0, temp.Length)) > 0)
                {
                    ms.Write(temp, 0, read);
                }
            }
            return ms.ToArray();
        }

        private float CalculatePeak(byte[] buffer, int bytesRecorded)
        {
            float max = 0;
            for (int i = 0; i < bytesRecorded; i += 2)
            {
                if (i + 1 >= buffer.Length) break;
                short sample = BitConverter.ToInt16(buffer, i);
                float fSample = Math.Abs(sample / 32768f);
                if (fSample > max) max = fSample;
            }
            return max;
        }

        public void Dispose()
        {
            Stop();
            _capture?.Dispose();
        }
    }
}
