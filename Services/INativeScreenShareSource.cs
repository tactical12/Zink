using System;
using System.Threading.Tasks;
using Zink.Models;

namespace Zink.Services
{
    public interface INativeScreenShareSource : IAsyncDisposable
    {
        event Action<byte[], int, int, long>? FrameReady;

        Task<ShareStats> StartAsync(bool require4k);
        Task StopAsync();
    }
}