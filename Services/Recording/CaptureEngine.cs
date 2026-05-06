using SharpDX;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics.Capture;
using Zink.Models;

using D3D11Device = SharpDX.Direct3D11.Device;
using D3D11Texture2D = SharpDX.Direct3D11.Texture2D;
using D3D11MapMode = SharpDX.Direct3D11.MapMode;
using D3D11MapFlags = SharpDX.Direct3D11.MapFlags;
using D3D11CpuAccessFlags = SharpDX.Direct3D11.CpuAccessFlags;
using D3D11BindFlags = SharpDX.Direct3D11.BindFlags;
using D3D11Usage = SharpDX.Direct3D11.ResourceUsage;
using D3D11ResourceOptionFlags = SharpDX.Direct3D11.ResourceOptionFlags;

using DxgiFactory1 = SharpDX.DXGI.Factory1;
using DxgiAdapter1 = SharpDX.DXGI.Adapter1;
using DxgiOutput = SharpDX.DXGI.Output;
using DxgiOutput1 = SharpDX.DXGI.Output1;
using DxgiOutputDuplication = SharpDX.DXGI.OutputDuplication;
using DxgiResource = SharpDX.DXGI.Resource;
using DxgiFormat = SharpDX.DXGI.Format;
using DxgiSampleDescription = SharpDX.DXGI.SampleDescription;
using DxgiOutputDuplicateFrameInformation = SharpDX.DXGI.OutputDuplicateFrameInformation;
using DxgiResultCode = SharpDX.DXGI.ResultCode;

namespace Zink.Services.Recording
{
    public sealed class CaptureEngine : IAsyncDisposable
    {
        private D3D11Device? _device;
        private DxgiOutputDuplication? _duplication;
        private D3D11Texture2D? _stagingTexture;
        private CancellationTokenSource? _cts;
        private Task? _captureTask;
        private Stopwatch? _stopwatch;

        private bool _isRunning;
        private long _frameCount;
        private int _width;
        private int _height;

        // Fixed pacing is much steadier for replay segments than "capture whenever + Task.Delay jitter".
        private uint _targetFps = 60;
        private TimeSpan _targetFrameInterval = TimeSpan.FromMilliseconds(1000.0 / 60);

        public event EventHandler<VideoFramePacket>? VideoFrameArrived;

        private const int MONITORINFOF_PRIMARY = 0x00000001;
        private const int MONITOR_DEFAULTTOPRIMARY = 1;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct MONITORINFOEX
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public int dwFlags;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string szDevice;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int left;
            public int top;
            public int right;
            public int bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromPoint(POINT pt, int dwFlags);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

        public bool IsRunning => _isRunning;

        public async Task StartAsync(GraphicsCaptureItem? item = null, RecordingOptions? options = null)
        {
            if (_isRunning)
                return;

            try
            {
                _targetFps = Math.Clamp(options?.FrameRate ?? 60, 1u, 240u);
                _targetFrameInterval = TimeSpan.FromMilliseconds(1000.0 / _targetFps);
                _frameCount = 0;
                InitializeDuplication();

                _cts = new CancellationTokenSource();
                _stopwatch = Stopwatch.StartNew();
                _captureTask = Task.Run(() => CaptureLoop(_cts.Token), _cts.Token);

                _isRunning = true;

                await RecorderLog.InfoAsync(nameof(CaptureEngine),
                    $"DXGI duplication capture started. Size={_width}x{_height}, TargetFps={_targetFps}");
            }
            catch (Exception ex)
            {
                await RecorderLog.ErrorAsync(nameof(CaptureEngine), ex, "StartAsync failed");
                throw;
            }
        }

        private void InitializeDuplication()
        {
            using var factory = new DxgiFactory1();

            string? primaryDeviceName = GetPrimaryMonitorDeviceName();
            if (string.IsNullOrWhiteSpace(primaryDeviceName))
                throw new InvalidOperationException("Could not determine the primary monitor device name.");

            DxgiAdapter1? chosenAdapter = null;
            DxgiOutput? chosenOutput = null;

            for (int adapterIndex = 0; ; adapterIndex++)
            {
                DxgiAdapter1 adapter;
                try
                {
                    adapter = factory.GetAdapter1(adapterIndex);
                }
                catch
                {
                    break;
                }

                bool found = false;

                for (int outputIndex = 0; ; outputIndex++)
                {
                    DxgiOutput output;
                    try
                    {
                        output = adapter.GetOutput(outputIndex);
                    }
                    catch
                    {
                        break;
                    }

                    var desc = output.Description;

                    if (!desc.IsAttachedToDesktop)
                    {
                        output.Dispose();
                        continue;
                    }

                    if (string.Equals(desc.DeviceName?.Trim(), primaryDeviceName.Trim(), StringComparison.OrdinalIgnoreCase))
                    {
                        chosenAdapter = adapter;
                        chosenOutput = output;
                        found = true;
                        break;
                    }

                    output.Dispose();
                }

                if (found)
                    break;

                adapter.Dispose();
            }

            if (chosenAdapter == null || chosenOutput == null)
                throw new InvalidOperationException("Primary monitor output was not found for DXGI duplication.");

            _device = new D3D11Device(chosenAdapter);
            using var output1 = chosenOutput.QueryInterface<DxgiOutput1>();

            _duplication = output1.DuplicateOutput(_device);

            var outputDesc = chosenOutput.Description;
            _width = outputDesc.DesktopBounds.Right - outputDesc.DesktopBounds.Left;
            _height = outputDesc.DesktopBounds.Bottom - outputDesc.DesktopBounds.Top;

            _stagingTexture = new D3D11Texture2D(_device, new Texture2DDescription
            {
                Width = _width,
                Height = _height,
                MipLevels = 1,
                ArraySize = 1,
                Format = DxgiFormat.B8G8R8A8_UNorm,
                SampleDescription = new DxgiSampleDescription(1, 0),
                Usage = D3D11Usage.Staging,
                BindFlags = D3D11BindFlags.None,
                CpuAccessFlags = D3D11CpuAccessFlags.Read,
                OptionFlags = D3D11ResourceOptionFlags.None
            });

            chosenOutput.Dispose();
            chosenAdapter.Dispose();
        }

        private static string? GetPrimaryMonitorDeviceName()
        {
            IntPtr primaryMonitor = MonitorFromPoint(new POINT { X = 0, Y = 0 }, MONITOR_DEFAULTTOPRIMARY);
            if (primaryMonitor == IntPtr.Zero)
                return null;

            var info = new MONITORINFOEX();
            info.cbSize = Marshal.SizeOf<MONITORINFOEX>();

            if (!GetMonitorInfo(primaryMonitor, ref info))
                return null;

            if ((info.dwFlags & MONITORINFOF_PRIMARY) == 0)
                return null;

            return info.szDevice;
        }

        private async Task CaptureLoop(CancellationToken token)
        {
            if (_stopwatch == null)
                return;

            TimeSpan nextFrameDue = TimeSpan.Zero;

            while (!token.IsCancellationRequested)
            {
                DxgiResource? desktopResource = null;
                bool frameAcquired = false;

                try
                {
                    if (_duplication == null || _device == null || _stagingTexture == null || _stopwatch == null)
                        return;

                    TimeSpan now = _stopwatch.Elapsed;
                    TimeSpan wait = nextFrameDue - now;
                    if (wait > TimeSpan.Zero)
                    {
                        await Task.Delay(wait, token);
                    }

                    // Timestamp from the scheduled cadence, not the wall-clock jitter after delays.
                    TimeSpan packetTimestamp = nextFrameDue;
                    nextFrameDue += _targetFrameInterval;

                    DxgiOutputDuplicateFrameInformation frameInfo;
                    var result = _duplication.TryAcquireNextFrame(
                        16,
                        out frameInfo,
                        out desktopResource);

                    if (result == DxgiResultCode.WaitTimeout || desktopResource == null)
                    {
                        // If no fresh frame is available, skip emitting one for this tick.
                        continue;
                    }

                    result.CheckError();
                    frameAcquired = true;

                    using var desktopImage = desktopResource.QueryInterface<D3D11Texture2D>();
                    _device.ImmediateContext.CopyResource(desktopImage, _stagingTexture);

                    var dataBox = _device.ImmediateContext.MapSubresource(
                        _stagingTexture,
                        0,
                        D3D11MapMode.Read,
                        D3D11MapFlags.None);

                    byte[] bgraBytes = FrameBufferPool.Rent(_width * _height * 4);

                    try
                    {
                        int rowSize = _width * 4;

                        for (int y = 0; y < _height; y++)
                        {
                            IntPtr sourcePtr = IntPtr.Add(dataBox.DataPointer, y * dataBox.RowPitch);
                            Marshal.Copy(sourcePtr, bgraBytes, y * rowSize, rowSize);
                        }
                    }
                    finally
                    {
                        _device.ImmediateContext.UnmapSubresource(_stagingTexture, 0);
                    }

                    var packet = new VideoFramePacket(
                        packetTimestamp,
                        _width,
                        _height,
                        bgraBytes);

                    _frameCount++;

                    if (_frameCount % 120 == 0)
                    {
                        await RecorderLog.InfoAsync(nameof(CaptureEngine),
                            $"DXGI frames captured: {_frameCount}, Timestamp={packet.Timestamp}");
                    }

                    VideoFrameArrived?.Invoke(this, packet);
                }
                catch (SharpDXException ex) when (ex.ResultCode == DxgiResultCode.WaitTimeout)
                {
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    await RecorderLog.ErrorAsync(nameof(CaptureEngine), ex, "CaptureLoop failed");

                    try
                    {
                        await Task.Delay(50, token);
                    }
                    catch
                    {
                    }
                }
                finally
                {
                    desktopResource?.Dispose();

                    if (frameAcquired && _duplication != null)
                    {
                        try
                        {
                            _duplication.ReleaseFrame();
                        }
                        catch
                        {
                        }
                    }
                }
            }
        }

        public async Task StopAsync()
        {
            try
            {
                _isRunning = false;

                if (_cts != null)
                {
                    _cts.Cancel();
                }

                if (_captureTask != null)
                {
                    try
                    {
                        await _captureTask;
                    }
                    catch
                    {
                    }
                }

                _duplication?.Dispose();
                _duplication = null;

                _stagingTexture?.Dispose();
                _stagingTexture = null;

                _device?.Dispose();
                _device = null;

                _cts?.Dispose();
                _cts = null;

                _captureTask = null;
                _stopwatch = null;

                await RecorderLog.InfoAsync(nameof(CaptureEngine),
                    $"DXGI capture stopped. Total frames={_frameCount}");
            }
            catch (Exception ex)
            {
                await RecorderLog.ErrorAsync(nameof(CaptureEngine), ex, "StopAsync failed");
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync();
        }
    }
}
