using System.Threading.Tasks;

namespace Zink.Services.NativeCalling
{
    public sealed class RemoteRenderSurfaceService
    {
        public Task InitializeAsync()
        {
            return Task.CompletedTask;
        }

        public Task ClearAsync()
        {
            return Task.CompletedTask;
        }
    }
}