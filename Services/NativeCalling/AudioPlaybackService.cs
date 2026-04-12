using System;
using NAudio.Wave;

namespace Zink.Services.NativeCalling
{
    public sealed class AudioPlaybackService
    {
        public static AudioPlaybackService Instance { get; } = new AudioPlaybackService();

        private readonly object _gate = new();
        private BufferedWaveProvider? _buffer;
        private WaveOutEvent? _output;

        private AudioPlaybackService()
        {
        }

        public void Start()
        {
            lock (_gate)
            {
                if (_output != null)
                    return;

                var format = new WaveFormat(16000, 16, 1);

                _buffer = new BufferedWaveProvider(format)
                {
                    DiscardOnBufferOverflow = true,
                    BufferDuration = TimeSpan.FromSeconds(2)
                };

                _output = new WaveOutEvent
                {
                    DesiredLatency = 120
                };

                _output.Init(_buffer);
                _output.Play();
            }
        }

        public void Stop()
        {
            lock (_gate)
            {
                try
                {
                    _output?.Stop();
                    _output?.Dispose();
                    _output = null;
                    _buffer = null;
                }
                finally
                {
                    AudioActivityService.Instance.UpdateRemoteLevel(0);
                }
            }
        }

        public void Play(byte[] data)
        {
            lock (_gate)
            {
                _buffer?.AddSamples(data, 0, data.Length);
            }

            double level = 0;
            int sampleCount = data.Length / 2;

            if (sampleCount <= 0)
            {
                AudioActivityService.Instance.UpdateRemoteLevel(0);
                return;
            }

            for (int i = 0; i < data.Length - 1; i += 2)
            {
                short sample = BitConverter.ToInt16(data, i);
                level += Math.Abs(sample);
            }

            level /= sampleCount;
            level /= short.MaxValue;

            AudioActivityService.Instance.UpdateRemoteLevel(level);
        }
    }
}