using SharpDX;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Device = SharpDX.Direct3D11.Device;
using Resource = SharpDX.DXGI.Resource;

namespace Zink.Services.NativeCalling
{
    internal sealed class DxgiScreenCaptureService : IDisposable
    {
        private Factory1? _factory;
        private Adapter1? _adapter;
        private Output? _output;
        private Output1? _output1;
        private Device? _device;
        private OutputDuplication? _duplication;
        private Texture2D? _stagingTexture;
        private Bitmap? _lastFrame;
        private bool _disabled;

        public bool IsAvailable => !_disabled;

        public Bitmap? TryCapture(ScreenShareQualityProfile quality)
        {
            if (_disabled)
                return null;

            try
            {
                EnsureStarted();

                if (_duplication == null || _device == null)
                    return null;

                var result = _duplication.TryAcquireNextFrame(0, out _, out Resource? desktopResource);
                if (result == SharpDX.DXGI.ResultCode.WaitTimeout)
                    return _lastFrame == null ? null : new Bitmap(_lastFrame);

                if (result == SharpDX.DXGI.ResultCode.AccessLost)
                {
                    ResetDuplication();
                    return _lastFrame == null ? null : new Bitmap(_lastFrame);
                }

                result.CheckError();

                try
                {
                    using var desktopTexture = desktopResource.QueryInterface<Texture2D>();
                    var desktopDescription = desktopTexture.Description;
                    EnsureStagingTexture(desktopDescription);

                    if (_stagingTexture == null)
                        return null;

                    _device.ImmediateContext.CopyResource(desktopTexture, _stagingTexture);
                    var dataBox = _device.ImmediateContext.MapSubresource(_stagingTexture, 0, MapMode.Read, SharpDX.Direct3D11.MapFlags.None);
                    try
                    {
                        var captured = CreateScaledBitmapFromBgra(
                            dataBox.DataPointer,
                            dataBox.RowPitch,
                            desktopDescription.Width,
                            desktopDescription.Height,
                            quality.Width,
                            quality.Height);

                        _lastFrame?.Dispose();
                        _lastFrame = new Bitmap(captured);
                        return captured;
                    }
                    finally
                    {
                        _device.ImmediateContext.UnmapSubresource(_stagingTexture, 0);
                    }
                }
                finally
                {
                    desktopResource.Dispose();
                    _duplication.ReleaseFrame();
                }
            }
            catch (SharpDXException ex) when (ex.ResultCode == SharpDX.DXGI.ResultCode.WaitTimeout)
            {
                return _lastFrame == null ? null : new Bitmap(_lastFrame);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ScreenShare:DXGI] Disabled desktop duplication capture: {ex.Message}");
                _disabled = true;
                Dispose();
                return null;
            }
        }

        private void EnsureStarted()
        {
            if (_duplication != null)
                return;

            _factory = new Factory1();
            for (var adapterIndex = 0; ; adapterIndex++)
            {
                Adapter1? adapter = null;
                try
                {
                    adapter = _factory.GetAdapter1(adapterIndex);
                }
                catch (SharpDXException ex) when (ex.ResultCode == SharpDX.DXGI.ResultCode.NotFound)
                {
                    break;
                }

                for (var outputIndex = 0; ; outputIndex++)
                {
                    Output? output = null;
                    Output1? output1 = null;
                    Device? device = null;

                    try
                    {
                        output = adapter.GetOutput(outputIndex);
                        var description = output.Description;
                        if (!description.IsAttachedToDesktop)
                        {
                            output.Dispose();
                            continue;
                        }

                        output1 = output.QueryInterface<Output1>();
                        device = new Device(adapter, DeviceCreationFlags.BgraSupport, FeatureLevel.Level_11_0);
                        var duplication = output1.DuplicateOutput(device);

                        _adapter = adapter;
                        _output = output;
                        _output1 = output1;
                        _device = device;
                        _duplication = duplication;

                        System.Diagnostics.Debug.WriteLine($"[ScreenShare:DXGI] Desktop duplication capture started on adapter={adapterIndex} output={outputIndex} bounds={description.DesktopBounds}.");
                        return;
                    }
                    catch (SharpDXException ex) when (ex.ResultCode == SharpDX.DXGI.ResultCode.NotFound)
                    {
                        output?.Dispose();
                        break;
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[ScreenShare:DXGI] Adapter {adapterIndex} output {outputIndex} failed: {ex.Message}");
                        device?.Dispose();
                        output1?.Dispose();
                        output?.Dispose();
                    }
                }

                adapter.Dispose();
            }

            throw new InvalidOperationException("DXGI desktop duplication is not available for any attached display.");
        }

        private void EnsureStagingTexture(Texture2DDescription desktopDescription)
        {
            if (_stagingTexture != null)
            {
                var current = _stagingTexture.Description;
                if (current.Width == desktopDescription.Width && current.Height == desktopDescription.Height)
                    return;

                _stagingTexture.Dispose();
                _stagingTexture = null;
            }

            _stagingTexture = new Texture2D(_device, new Texture2DDescription
            {
                CpuAccessFlags = CpuAccessFlags.Read,
                BindFlags = BindFlags.None,
                Format = desktopDescription.Format,
                Width = desktopDescription.Width,
                Height = desktopDescription.Height,
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
            var bitmap = new Bitmap(targetWidth, targetHeight, PixelFormat.Format32bppArgb);
            var targetData = bitmap.LockBits(
                new Rectangle(0, 0, targetWidth, targetHeight),
                ImageLockMode.WriteOnly,
                PixelFormat.Format32bppArgb);

            try
            {
                var sourceBase = (byte*)sourcePtr;
                var targetBase = (byte*)targetData.Scan0;

                for (var y = 0; y < targetHeight; y++)
                {
                    var sourceY = Math.Min(sourceHeight - 1, (int)((long)y * sourceHeight / targetHeight));
                    var sourceRow = sourceBase + sourceY * sourceStride;
                    var targetRow = targetBase + y * targetData.Stride;

                    for (var x = 0; x < targetWidth; x++)
                    {
                        var sourceX = Math.Min(sourceWidth - 1, (int)((long)x * sourceWidth / targetWidth));
                        var sourcePixel = sourceRow + sourceX * 4;
                        var targetPixel = targetRow + x * 4;

                        targetPixel[0] = sourcePixel[0];
                        targetPixel[1] = sourcePixel[1];
                        targetPixel[2] = sourcePixel[2];
                        targetPixel[3] = 255;
                    }
                }
            }
            finally
            {
                bitmap.UnlockBits(targetData);
            }

            return bitmap;
        }

        private void ResetDuplication()
        {
            _duplication?.Dispose();
            _duplication = null;
            _stagingTexture?.Dispose();
            _stagingTexture = null;
        }

        public void Dispose()
        {
            _lastFrame?.Dispose();
            _lastFrame = null;
            _stagingTexture?.Dispose();
            _stagingTexture = null;
            _duplication?.Dispose();
            _duplication = null;
            _device?.Dispose();
            _device = null;
            _output1?.Dispose();
            _output1 = null;
            _output?.Dispose();
            _output = null;
            _adapter?.Dispose();
            _adapter = null;
            _factory?.Dispose();
            _factory = null;
        }
    }
}
