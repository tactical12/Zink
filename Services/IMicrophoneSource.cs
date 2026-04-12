using System;
using System.Threading.Tasks;

namespace Zink.Services
{
    public interface IMicrophoneSource : IAsyncDisposable
    {
        event Action<byte[], int, int>? PcmFrameReady;

        Task StartAsync();
        Task StopAsync();
        Task ToggleMuteAsync();
    }
}