using System;
using System.Threading.Tasks;
using NAudio.Wave;

namespace Zink.Services.NativeCalling
{
    public sealed class MicCaptureService
    {
        public static MicCaptureService Instance { get; } = new MicCaptureService();

        private readonly object _gate = new();
        private WaveInEvent? _capture;

        public bool IsRunning { get; private set; }

        public event Action<byte[]>? AudioCaptured;

        private MicCaptureService()
        {
        }

        public Task StartAsync()
        {
            lock (_gate)
            {
                if (IsRunning)
                    return Task.CompletedTask;

                _capture = new WaveInEvent
                {
                    DeviceNumber = 0,
                    WaveFormat = new WaveFormat(16000, 16, 1),
                    BufferMilliseconds = 40,
                    NumberOfBuffers = 3
                };

                _capture.DataAvailable += OnDataAvailable;
                _capture.RecordingStopped += OnRecordingStopped;
                _capture.StartRecording();

                IsRunning = true;
            }

            return Task.CompletedTask;
        }

        public Task StopAsync()
        {
            lock (_gate)
            {
                if (!IsRunning)
                    return Task.CompletedTask;

                try
                {
                    if (_capture != null)
                    {
                        _capture.DataAvailable -= OnDataAvailable;
                        _capture.RecordingStopped -= OnRecordingStopped;
                        _capture.StopRecording();
                        _capture.Dispose();
                        _capture = null;
                    }
                }
                finally
                {
                    IsRunning = false;
                    AudioActivityService.Instance.UpdateLocalLevel(0);
                }
            }

            return Task.CompletedTask;
        }

        private void OnRecordingStopped(object? sender, StoppedEventArgs e)
        {
            lock (_gate)
            {
                IsRunning = false;
            }

            AudioActivityService.Instance.UpdateLocalLevel(0);
        }

        private void OnDataAvailable(object? sender, WaveInEventArgs e)
        {
            if (e.BytesRecorded <= 0)
            {
                AudioActivityService.Instance.UpdateLocalLevel(0);
                return;
            }

            var buffer = new byte[e.BytesRecorded];
            Array.Copy(e.Buffer, buffer, e.BytesRecorded);

            AudioCaptured?.Invoke(buffer);

            double level = 0;
            int sampleCount = e.BytesRecorded / 2;

            if (sampleCount <= 0)
            {
                AudioActivityService.Instance.UpdateLocalLevel(0);
                return;
            }

            for (int i = 0; i < e.BytesRecorded - 1; i += 2)
            {
                short sample = BitConverter.ToInt16(e.Buffer, i);
                level += Math.Abs(sample);
            }

            level /= sampleCount;
            level /= short.MaxValue;

            AudioActivityService.Instance.UpdateLocalLevel(level);
        }
    }
}