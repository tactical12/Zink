using System;
using System.Threading.Tasks;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Zink.Models;

namespace Zink.Services
{
    public sealed class WindowsGraphicsCaptureShareSource : INativeScreenShareSource
    {
        private GraphicsCaptureItem? _item;
        private Direct3D11CaptureFramePool? _framePool;
        private GraphicsCaptureSession? _session;

        public event Action<byte[], int, int, long>? FrameReady;

        public async Task<ShareStats> StartAsync(bool require4k)
        {
            _item = await GraphicsCapturePickerHelper.PickAsync();
            if (_item == null)
                throw new InvalidOperationException("No display or window was selected.");

            int width = _item.Size.Width;
            int height = _item.Size.Height;

            if (require4k && (width < 3840 || height < 2160))
                throw new InvalidOperationException($"Selected source is {width} x {height}, not 4K.");

            var d3dDevice = Direct3DDeviceHelper.CreateDevice();

            _framePool = Direct3D11CaptureFramePool.Create(
                d3dDevice,
                DirectXPixelFormat.B8G8R8A8UIntNormalized,
                2,
                _item.Size);

            _session = _framePool.CreateCaptureSession(_item);
            _framePool.FrameArrived += FramePool_FrameArrived;
            _session.StartCapture();

            return new ShareStats
            {
                SourceWidth = width,
                SourceHeight = height,
                FrameRate = 30
            };
        }

        private void FramePool_FrameArrived(Direct3D11CaptureFramePool sender, object args)
        {
            using var frame = sender.TryGetNextFrame();
            if (frame == null)
                return;

            int width = frame.ContentSize.Width;
            int height = frame.ContentSize.Height;

            byte[] rawPlaceholder = Array.Empty<byte>();
            FrameReady?.Invoke(rawPlaceholder, width, height, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        }

        public Task StopAsync()
        {
            if (_framePool != null)
            {
                _framePool.FrameArrived -= FramePool_FrameArrived;
                _framePool.Dispose();
                _framePool = null;
            }

            _session?.Dispose();
            _session = null;
            _item = null;

            return Task.CompletedTask;
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync();
        }
    }
}