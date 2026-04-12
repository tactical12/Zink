using System;
using System.Threading.Tasks;

namespace Zink.Services
{
    public sealed class WasapiMicrophoneSource : IMicrophoneSource
    {
        private bool _muted;
        private bool _started;

        public event Action<byte[], int, int>? PcmFrameReady;

        public Task StartAsync()
        {
            _started = true;
            return Task.CompletedTask;
        }

        public Task StopAsync()
        {
            _started = false;
            return Task.CompletedTask;
        }

        public Task ToggleMuteAsync()
        {
            if (!_started)
            {
                _muted = false;
                return Task.CompletedTask;
            }

            _muted = !_muted;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            _started = false;
            _muted = false;
            return ValueTask.CompletedTask;
        }
    }
}