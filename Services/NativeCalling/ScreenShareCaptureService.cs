using System.Threading.Tasks;

namespace Zink.Services.NativeCalling
{
    public sealed class ScreenShareCaptureService
    {
        public bool IsRunning { get; private set; }

        public Task StartAsync()
        {
            IsRunning = true;
            return Task.CompletedTask;
        }

        public Task StopAsync()
        {
            IsRunning = false;
            return Task.CompletedTask;
        }
    }
}