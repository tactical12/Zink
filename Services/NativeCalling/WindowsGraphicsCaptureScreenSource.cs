using SharpDX.Direct3D11;
using SharpDX.DXGI;
using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Zink.Services;
using Zink.Services.Recording;

namespace Zink.Services.NativeCalling
{
    internal sealed class WindowsGraphicsCaptureScreenSource : IDisposable
    {
        private readonly object _syncRoot = new();
        private readonly object _disposeSync = new();

        private IDirect3DDevice? _winRtDevice;
        private SharpDX.Direct3D11.Device? _sharpDxDevice;
        private Direct3D11CaptureFramePool? _framePool;
        private GraphicsCaptureSession? _session;
        private Texture2D? _stagingTexture;
        private Bitmap? _latestFrame;
        private CapturedGpuFrame? _latestGpuFrame;
        private long _frameArrivedCount;
        private int _frameArrivedBreadcrumbs;
        private uint _lastFrameFingerprint;
        private int _sameFrameCount;
        private DateTimeOffset _lastFrameLogUtc = DateTimeOffset.MinValue;
        private bool _started;
        private bool _disabled;
        private bool _disposed = true;

        public bool IsAvailable => !_disabled;

        public SharpDX.Direct3D11.Device? CaptureDevice => _sharpDxDevice;

        private static bool IsArm64Process =>
            RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ||
            RuntimeInformation.OSArchitecture == Architecture.Arm64;

        public async Task<bool> StartAsync()
        {
            if (_started)
                return true;

            if (_disabled)
                return false;

            try
            {
                if (!GraphicsCaptureSession.IsSupported())
                {
                    _disabled = true;
                    return false;
                }

                DiagnosticLogService.WriteLine("[ScreenShare:WGC] StartAsync entered.");
                DiagnosticLogService.Flush();
                ScreenShareCrashBreadcrumb.Mark("WGC StartAsync entered");

                var hwnd = App.MainWindow?.GetWindowHandle() ?? IntPtr.Zero;
                var item = await CaptureSourceHelper.GetPrimaryScreenOrPromptAsync(hwnd);
                if (item == null)
                {
                    Debug.WriteLine("[ScreenShare:WGC] No Windows Graphics Capture item was created.");
                    DiagnosticLogService.WriteLine("[ScreenShare:WGC] No Windows Graphics Capture item was created.");
                    _disabled = true;
                    return false;
                }

                _ = TryRequestBorderlessCaptureAccessAsync();

                DiagnosticLogService.WriteLine($"[ScreenShare:WGC] Capture item ready {item.Size.Width}x{item.Size.Height}; arm64={IsArm64Process}.");
                DiagnosticLogService.Flush();
                ScreenShareCrashBreadcrumb.Mark($"WGC capture item ready {item.Size.Width}x{item.Size.Height}; arm64={IsArm64Process}");

                _sharpDxDevice = CreateCaptureDevice();
                EnableMultithreadProtection(_sharpDxDevice);

                DiagnosticLogService.WriteLine("[ScreenShare:WGC] D3D11 capture device created.");
                DiagnosticLogService.Flush();
                ScreenShareCrashBreadcrumb.Mark("WGC D3D11 capture device created");

                _winRtDevice = Direct3D11Helpers.CreateD3DDevice(_sharpDxDevice);

                DiagnosticLogService.WriteLine("[ScreenShare:WGC] WinRT Direct3D device created.");
                DiagnosticLogService.Flush();
                ScreenShareCrashBreadcrumb.Mark("WGC WinRT Direct3D device created");

                _framePool = IsArm64Process
                    ? Direct3D11CaptureFramePool.Create(
                        _winRtDevice,
                        DirectXPixelFormat.B8G8R8A8UIntNormalized,
                        2,
                        item.Size)
                    : Direct3D11CaptureFramePool.CreateFreeThreaded(
                        _winRtDevice,
                        DirectXPixelFormat.B8G8R8A8UIntNormalized,
                        2,
                        item.Size);

                DiagnosticLogService.WriteLine(IsArm64Process
                    ? "[ScreenShare:WGC] Dispatcher-backed frame pool created for ARM64 automatic capture."
                    : "[ScreenShare:WGC] Free-threaded frame pool created.");
                DiagnosticLogService.Flush();
                ScreenShareCrashBreadcrumb.Mark(IsArm64Process
                    ? "WGC dispatcher-backed frame pool created"
                    : "WGC free-threaded frame pool created");

                _session = _framePool.CreateCaptureSession(item);
                TryDisableCaptureBorder(_session);
                TryEnableCursorCapture(_session);
                lock (_disposeSync)
                {
                    _disposed = false;
                }

                _framePool.FrameArrived += FramePool_FrameArrived;
                ScreenShareCrashBreadcrumb.Mark("WGC FrameArrived handler attached");
                _session.StartCapture();
                _started = true;
                ScreenShareCrashBreadcrumb.Mark("WGC StartCapture returned");

                Debug.WriteLine($"[ScreenShare:WGC] Windows Graphics Capture started {item.Size.Width}x{item.Size.Height} via native D3D11 GPU capture device.");
                DiagnosticLogService.WriteLine($"[ScreenShare:WGC] Windows Graphics Capture started {item.Size.Width}x{item.Size.Height} via native D3D11 GPU capture device.");
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ScreenShare:WGC] Failed to start Windows Graphics Capture: {ex}");
                DiagnosticLogService.WriteLine($"[ScreenShare:WGC] Failed to start Windows Graphics Capture: {ex}");
                DiagnosticLogService.Flush();
                _disabled = true;
                Dispose();
                return false;
            }
        }

        private static SharpDX.Direct3D11.Device CreateCaptureDevice()
        {
            var flags = DeviceCreationFlags.BgraSupport;
            if (!IsArm64Process)
                flags |= DeviceCreationFlags.VideoSupport;

            try
            {
                return new SharpDX.Direct3D11.Device(
                    SharpDX.Direct3D.DriverType.Hardware,
                    flags);
            }
            catch (Exception ex) when ((flags & DeviceCreationFlags.VideoSupport) != 0)
            {
                Debug.WriteLine($"[ScreenShare:WGC] D3D11 capture device with VideoSupport failed; retrying BGRA-only: {ex.Message}");
                DiagnosticLogService.WriteLine($"[ScreenShare:WGC] D3D11 capture device with VideoSupport failed; retrying BGRA-only: {ex.Message}");
                ScreenShareCrashBreadcrumb.Mark("WGC capture hardware device retrying BGRA-only");
                try
                {
                    return new SharpDX.Direct3D11.Device(
                        SharpDX.Direct3D.DriverType.Hardware,
                        DeviceCreationFlags.BgraSupport);
                }
                catch (Exception retryEx)
                {
                    Debug.WriteLine($"[ScreenShare:WGC] D3D11 hardware capture device failed; retrying WARP BGRA-only: {retryEx.Message}");
                    DiagnosticLogService.WriteLine($"[ScreenShare:WGC] D3D11 hardware capture device failed; retrying WARP BGRA-only: {retryEx.Message}");
                    ScreenShareCrashBreadcrumb.Mark("WGC capture hardware device failed; retrying WARP");
                    return new SharpDX.Direct3D11.Device(
                        SharpDX.Direct3D.DriverType.Warp,
                        DeviceCreationFlags.BgraSupport);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ScreenShare:WGC] D3D11 capture device failed; retrying WARP BGRA-only: {ex.Message}");
                DiagnosticLogService.WriteLine($"[ScreenShare:WGC] D3D11 capture device failed; retrying WARP BGRA-only: {ex.Message}");
                ScreenShareCrashBreadcrumb.Mark("WGC capture device failed; retrying WARP");
                return new SharpDX.Direct3D11.Device(
                    SharpDX.Direct3D.DriverType.Warp,
                    DeviceCreationFlags.BgraSupport);
            }
        }

        public Bitmap? TryGetLatestFrame()
        {
            lock (_syncRoot)
            {
                var frame = _latestFrame;
                _latestFrame = null;
                return frame;
            }
        }

        public CapturedGpuFrame? TryGetLatestGpuFrame()
        {
            lock (_syncRoot)
            {
                var frame = _latestGpuFrame;
                _latestGpuFrame = null;
                return frame;
            }
        }

        private void FramePool_FrameArrived(Direct3D11CaptureFramePool sender, object args)
        {
            var breadcrumbId = Interlocked.Increment(ref _frameArrivedBreadcrumbs);
            var writeBreadcrumb = IsArm64Process && breadcrumbId <= 4;
            if (writeBreadcrumb)
                ScreenShareCrashBreadcrumb.Mark($"WGC FrameArrived entry #{breadcrumbId}");

            lock (_disposeSync)
            {
                if (_disposed || _sharpDxDevice == null)
                    return;

                try
                {
                    if (writeBreadcrumb)
                        ScreenShareCrashBreadcrumb.Mark($"WGC FrameArrived #{breadcrumbId} before TryGetNextFrame");

                    using var frame = sender.TryGetNextFrame();
                    if (frame == null)
                        return;

                    if (writeBreadcrumb)
                        ScreenShareCrashBreadcrumb.Mark($"WGC FrameArrived #{breadcrumbId} got frame");

                    var quality = NativeScreenShareStreamingService.Instance.CurrentQuality;
                    if (writeBreadcrumb)
                        ScreenShareCrashBreadcrumb.Mark($"WGC FrameArrived #{breadcrumbId} before surface texture");

                    using var sourceTexture = Direct3D11Helpers.CreateSharpDXTexture2D(frame.Surface);
                    var description = sourceTexture.Description;
                    if (writeBreadcrumb)
                        ScreenShareCrashBreadcrumb.Mark($"WGC FrameArrived #{breadcrumbId} texture {description.Width}x{description.Height}");

                    var gpuFrame = CaptureGpuFrame(sourceTexture, description);
                    if (gpuFrame != null)
                    {
                        lock (_syncRoot)
                        {
                            _latestGpuFrame?.Dispose();
                            _latestFrame?.Dispose();
                            _latestGpuFrame = gpuFrame;
                            _latestFrame = null;
                            gpuFrame = null;
                        }

                        LogFrameArrival(description.Width, description.Height, quality.Width, quality.Height, 0);
                        if (writeBreadcrumb)
                            ScreenShareCrashBreadcrumb.Mark($"WGC FrameArrived #{breadcrumbId} stored GPU frame");
                        return;
                    }

                    if (writeBreadcrumb)
                        ScreenShareCrashBreadcrumb.Mark($"WGC FrameArrived #{breadcrumbId} before staging readback");

                    EnsureStagingTexture(description);
                    if (_stagingTexture == null || _sharpDxDevice == null)
                        return;

                    _sharpDxDevice.ImmediateContext.CopyResource(sourceTexture, _stagingTexture);
                    _sharpDxDevice.ImmediateContext.Flush();
                    if (writeBreadcrumb)
                        ScreenShareCrashBreadcrumb.Mark($"WGC FrameArrived #{breadcrumbId} before MapSubresource");

                    var dataBox = _sharpDxDevice.ImmediateContext.MapSubresource(_stagingTexture, 0, MapMode.Read, SharpDX.Direct3D11.MapFlags.None);
                    try
                    {
                        if (writeBreadcrumb)
                            ScreenShareCrashBreadcrumb.Mark($"WGC FrameArrived #{breadcrumbId} mapped frame");

                        var fingerprint = SampleFrameFingerprint(
                            dataBox.DataPointer,
                            dataBox.RowPitch,
                            description.Width,
                            description.Height);
                        var captured = CreateScaledBitmapFromBgra(
                            dataBox.DataPointer,
                            dataBox.RowPitch,
                            description.Width,
                            description.Height,
                            quality.Width,
                            quality.Height);

                        lock (_syncRoot)
                        {
                            _latestFrame?.Dispose();
                            _latestGpuFrame?.Dispose();
                            _latestFrame = captured;
                            _latestGpuFrame = gpuFrame;
                            gpuFrame = null;
                        }

                        LogFrameArrival(description.Width, description.Height, quality.Width, quality.Height, fingerprint);
                        if (writeBreadcrumb)
                            ScreenShareCrashBreadcrumb.Mark($"WGC FrameArrived #{breadcrumbId} stored bitmap frame");
                    }
                    finally
                    {
                        _sharpDxDevice?.ImmediateContext.UnmapSubresource(_stagingTexture, 0);
                        gpuFrame?.Dispose();
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[ScreenShare:WGC] Frame readback failed: {ex.Message}");
                }
            }
        }

        private CapturedGpuFrame? CaptureGpuFrame(Texture2D sourceTexture, Texture2DDescription sourceDescription)
        {
            if (!NativeScreenShareStreamingService.EnableDirectGpuTexturePath)
                return null;

            if (_sharpDxDevice == null)
                return null;

            try
            {
                var gpuTexture = new Texture2D(_sharpDxDevice, new Texture2DDescription
                {
                    CpuAccessFlags = CpuAccessFlags.None,
                    BindFlags = BindFlags.ShaderResource | BindFlags.Decoder,
                    Format = sourceDescription.Format,
                    Width = sourceDescription.Width,
                    Height = sourceDescription.Height,
                    OptionFlags = ResourceOptionFlags.None,
                    MipLevels = 1,
                    ArraySize = 1,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Default
                });

                _sharpDxDevice.ImmediateContext.CopyResource(sourceTexture, gpuTexture);
                return new CapturedGpuFrame(gpuTexture, sourceDescription.Width, sourceDescription.Height);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ScreenShare:WGC] GPU frame copy failed; falling back to bitmap path: {ex.Message}");
                return null;
            }
        }

        private void LogFrameArrival(int sourceWidth, int sourceHeight, int targetWidth, int targetHeight, uint fingerprint)
        {
            var frameCount = ++_frameArrivedCount;
            if (fingerprint != 0 && fingerprint == _lastFrameFingerprint)
                _sameFrameCount++;
            else
                _sameFrameCount = 0;

            if (fingerprint != 0)
                _lastFrameFingerprint = fingerprint;

            var now = DateTimeOffset.UtcNow;
            if (frameCount == 1 || now - _lastFrameLogUtc >= TimeSpan.FromSeconds(2) || _sameFrameCount == 120)
            {
                _lastFrameLogUtc = now;
                Debug.WriteLine(
                    $"[ScreenShare:WGC] frame={frameCount}; source={sourceWidth}x{sourceHeight}; target={targetWidth}x{targetHeight}; hash=0x{fingerprint:X8}; sameFrame={_sameFrameCount}.");
            }
        }

        private static void EnableMultithreadProtection(SharpDX.Direct3D11.Device device)
        {
            try
            {
                using var multithread = device.QueryInterface<Multithread>();
                var wasProtected = multithread.SetMultithreadProtected(true);
                Debug.WriteLine($"[ScreenShare:WGC] Native D3D11 multithread protection enabled; previously protected={wasProtected}.");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ScreenShare:WGC] Native D3D11 multithread protection unavailable: {ex.Message}");
            }
        }

        private void EnsureStagingTexture(Texture2DDescription sourceDescription)
        {
            if (_sharpDxDevice == null)
                return;

            if (_stagingTexture != null)
            {
                var current = _stagingTexture.Description;
                if (current.Width == sourceDescription.Width && current.Height == sourceDescription.Height)
                    return;

                _stagingTexture.Dispose();
                _stagingTexture = null;
            }

            _stagingTexture = new Texture2D(_sharpDxDevice, new Texture2DDescription
            {
                CpuAccessFlags = CpuAccessFlags.Read,
                BindFlags = BindFlags.None,
                Format = sourceDescription.Format,
                Width = sourceDescription.Width,
                Height = sourceDescription.Height,
                OptionFlags = ResourceOptionFlags.None,
                MipLevels = 1,
                ArraySize = 1,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging
            });
        }

        private static unsafe Bitmap CreateScaledBitmapFromBgra(
            IntPtr sourcePtr,
            int sourceStride,
            int sourceWidth,
            int sourceHeight,
            int targetWidth,
            int targetHeight)
        {
            var target = new Bitmap(targetWidth, targetHeight, PixelFormat.Format32bppArgb);
            var destination = GetAspectFitRectangle(sourceWidth, sourceHeight, targetWidth, targetHeight);
            var targetData = target.LockBits(
                new Rectangle(0, 0, targetWidth, targetHeight),
                ImageLockMode.WriteOnly,
                PixelFormat.Format32bppArgb);

            try
            {
                var targetBase = (byte*)targetData.Scan0;
                for (var y = 0; y < targetHeight; y++)
                {
                    var targetRow = targetBase + y * targetData.Stride;
                    for (var x = 0; x < targetWidth * 4; x++)
                        targetRow[x] = 0;
                }

                var sourceBase = (byte*)sourcePtr;
                const int weightScale = 256;
                var x0Map = new int[destination.Width];
                var x1Map = new int[destination.Width];
                var xWeightMap = new int[destination.Width];
                var scaleX = (double)sourceWidth / destination.Width;
                var scaleY = (double)sourceHeight / destination.Height;

                for (var x = 0; x < destination.Width; x++)
                {
                    var sourceX = ((x + 0.5) * scaleX) - 0.5;
                    if (sourceX < 0)
                        sourceX = 0;

                    var x0 = (int)sourceX;
                    var xWeight = (int)Math.Round((sourceX - x0) * weightScale);
                    var x1 = x0 + 1;
                    if (x1 >= sourceWidth)
                    {
                        x1 = x0;
                        xWeight = 0;
                    }

                    x0Map[x] = x0;
                    x1Map[x] = x1;
                    xWeightMap[x] = Math.Clamp(xWeight, 0, weightScale);
                }

                for (var y = 0; y < destination.Height; y++)
                {
                    var sourceY = ((y + 0.5) * scaleY) - 0.5;
                    if (sourceY < 0)
                        sourceY = 0;

                    var y0 = (int)sourceY;
                    var yWeight = (int)Math.Round((sourceY - y0) * weightScale);
                    var y1 = y0 + 1;
                    if (y1 >= sourceHeight)
                    {
                        y1 = y0;
                        yWeight = 0;
                    }
                    yWeight = Math.Clamp(yWeight, 0, weightScale);

                    var sourceRow0 = sourceBase + y0 * sourceStride;
                    var sourceRow1 = sourceBase + y1 * sourceStride;
                    var targetRow = targetBase + (destination.Y + y) * targetData.Stride + destination.X * 4;

                    for (var x = 0; x < destination.Width; x++)
                    {
                        var x0 = x0Map[x];
                        var x1 = x1Map[x];
                        var xWeight = xWeightMap[x];
                        var inverseXWeight = weightScale - xWeight;
                        var inverseYWeight = weightScale - yWeight;

                        var pixel00 = sourceRow0 + x0 * 4;
                        var pixel10 = sourceRow0 + x1 * 4;
                        var pixel01 = sourceRow1 + x0 * 4;
                        var pixel11 = sourceRow1 + x1 * 4;
                        var targetPixel = targetRow + x * 4;

                        for (var channel = 0; channel < 4; channel++)
                        {
                            var top = (pixel00[channel] * inverseXWeight) + (pixel10[channel] * xWeight);
                            var bottom = (pixel01[channel] * inverseXWeight) + (pixel11[channel] * xWeight);
                            targetPixel[channel] = (byte)(((top * inverseYWeight) + (bottom * yWeight) + 32768) >> 16);
                        }
                    }
                }
            }
            finally
            {
                target.UnlockBits(targetData);
            }

            return target;
        }

        private static Rectangle GetAspectFitRectangle(
            int sourceWidth,
            int sourceHeight,
            int targetWidth,
            int targetHeight)
        {
            var scale = Math.Min((double)targetWidth / sourceWidth, (double)targetHeight / sourceHeight);
            var width = Math.Max(1, (int)Math.Round(sourceWidth * scale));
            var height = Math.Max(1, (int)Math.Round(sourceHeight * scale));
            return new Rectangle(
                (targetWidth - width) / 2,
                (targetHeight - height) / 2,
                width,
                height);
        }

        private static unsafe uint SampleFrameFingerprint(IntPtr sourcePtr, int sourceStride, int width, int height)
        {
            var hash = 2166136261u;
            var sourceBase = (byte*)sourcePtr;
            var sampleRows = Math.Min(12, Math.Max(1, height));
            var sampleColumns = Math.Min(16, Math.Max(1, width));

            for (var y = 0; y < sampleRows; y++)
            {
                var sourceY = Math.Min(height - 1, (int)((long)y * height / sampleRows));
                var sourceRow = sourceBase + sourceY * sourceStride;
                for (var x = 0; x < sampleColumns; x++)
                {
                    var sourceX = Math.Min(width - 1, (int)((long)x * width / sampleColumns));
                    var pixel = sourceRow + sourceX * 4;
                    hash = (hash ^ pixel[0]) * 16777619u;
                    hash = (hash ^ pixel[1]) * 16777619u;
                    hash = (hash ^ pixel[2]) * 16777619u;
                }
            }

            return hash;
        }

        private static void TryEnableCursorCapture(GraphicsCaptureSession session)
        {
            try
            {
                session.IsCursorCaptureEnabled = true;
            }
            catch
            {
            }
        }

        private static void TryDisableCaptureBorder(GraphicsCaptureSession session)
        {
            try
            {
                session.IsBorderRequired = false;
                return;
            }
            catch
            {
            }

            try
            {
                var borderProperty = session.GetType().GetProperty("IsBorderRequired");
                if (borderProperty?.CanWrite == true)
                    borderProperty.SetValue(session, false);
            }
            catch
            {
            }
        }

        private static async Task TryRequestBorderlessCaptureAccessAsync()
        {
            try
            {
                await GraphicsCaptureAccess.RequestAccessAsync(GraphicsCaptureAccessKind.Borderless);
            }
            catch
            {
            }
        }

        public void Dispose()
        {
            lock (_disposeSync)
            {
                _disposed = true;
                _started = false;
                if (_framePool != null)
                    _framePool.FrameArrived -= FramePool_FrameArrived;

                _framePool?.Dispose();
                _framePool = null;
                _session?.Dispose();
                _session = null;
                _stagingTexture?.Dispose();
                _stagingTexture = null;
                _sharpDxDevice?.Dispose();
                _sharpDxDevice = null;
                _winRtDevice = null;
            }

            lock (_syncRoot)
            {
                _latestFrame?.Dispose();
                _latestGpuFrame?.Dispose();
                _latestFrame = null;
                _latestGpuFrame = null;
            }
        }
    }

    public sealed class CapturedGpuFrame : IDisposable
    {
        public CapturedGpuFrame(Texture2D texture, int width, int height)
        {
            Texture = texture;
            Width = width;
            Height = height;
        }

        public Texture2D Texture { get; }
        public int Width { get; }
        public int Height { get; }

        public void Dispose()
        {
            Texture.Dispose();
        }
    }
}
