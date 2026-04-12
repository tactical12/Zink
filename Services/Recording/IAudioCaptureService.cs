using System;
using System.Threading.Tasks;

namespace Zink.Services.Recording
{
    public interface IAudioCaptureService : IAsyncDisposable
    {
        event EventHandler<AudioPacket>? AudioPacketArrived;

        bool IsRunning { get; }

        string? DeviceId { get; }

        Task StartAsync(string? deviceId = null);
        Task StopAsync();
    }
}