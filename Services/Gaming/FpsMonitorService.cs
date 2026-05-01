using Microsoft.UI.Dispatching;
using SharpDX.Direct3D11;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Zink.Services.Recording;

namespace Zink.Services.Gaming
{
    public sealed class FpsMonitorService : IDisposable
    {
        public static FpsMonitorService Instance { get; } = new();

        private readonly object _syncRoot = new();
        private readonly Queue<FrameSample> _recentSamples = new();
        private readonly Stopwatch _clock = new();
        private readonly DispatcherQueueTimer _publishTimer;

        private IDirect3DDevice? _winRtDevice;
        private Device? _d3dDevice;
        private Direct3D11CaptureFramePool? _framePool;
        private GraphicsCaptureSession? _session;
        private GraphicsCaptureItem? _captureItem;
        private StreamWriter? _recordWriter;
        private FpsMonitorSettings _settings = new();
        private DateTimeOffset _sessionStartedAtUtc;
        private long _frameCount;
        private long _lastFrameTicks;
        private double _lastFrameTimeMs;
        private bool _isMonitoring;
        private bool _isRecording;
        private string _targetName = "No game selected";
        private string _status = "Idle";
        private string? _recordingPath;

        public event EventHandler<FpsMonitorSnapshot>? SnapshotChanged;

        private FpsMonitorService()
        {
            var dispatcher = App.MainWindow?.DispatcherQueue ?? DispatcherQueue.GetForCurrentThread();
            _publishTimer = dispatcher.CreateTimer();
            _publishTimer.Interval = TimeSpan.FromMilliseconds(250);
            _publishTimer.Tick += (_, _) => PublishSnapshot();
        }

        public FpsMonitorSnapshot CurrentSnapshot => BuildSnapshot();

        public async Task PickAndStartAsync(IntPtr hwnd, FpsMonitorSettings settings)
        {
            Stop();

            if (!GraphicsCaptureSession.IsSupported())
            {
                _status = "Windows Graphics Capture is not supported on this device.";
                PublishSnapshot();
                return;
            }

            _settings = settings;
            _captureItem = await CapturePickerHelper.PickCaptureItemAsync(hwnd);
            if (_captureItem == null)
            {
                _status = "No game selected.";
                PublishSnapshot();
                return;
            }

            _targetName = string.IsNullOrWhiteSpace(_captureItem.DisplayName)
                ? "Selected game"
                : _captureItem.DisplayName;
            _captureItem.Closed += CaptureItem_Closed;

            StartCapture();
        }

        public void StartRecording()
        {
            lock (_syncRoot)
            {
                if (!_isMonitoring || _isRecording)
                    return;

                try
                {
                    var folder = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
                        "Zink FPS Recorder");
                    Directory.CreateDirectory(folder);

                    var safeTarget = string.Concat(_targetName.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
                    var fileName = $"zink-fps-{safeTarget}-{DateTime.Now:yyyyMMdd-HHmmss}.csv";
                    _recordingPath = Path.Combine(folder, fileName);
                    _recordWriter = new StreamWriter(_recordingPath, append: false);
                    _recordWriter.WriteLine("elapsedMs,fps,averageFps,onePercentLowFps,pointOnePercentLowFps,frameTimeMs,frameCount,target");
                    _isRecording = true;
                    _status = "Recording FPS.";
                }
                catch (Exception ex)
                {
                    _status = $"FPS recording failed: {ex.Message}";
                    Debug.WriteLine($"[FPS] Recording start failed: {ex}");
                }
            }

            PublishSnapshot();
        }

        public void StopRecording()
        {
            lock (_syncRoot)
            {
                StopRecordingCore();
                if (_isMonitoring)
                    _status = "Monitoring FPS.";
            }

            PublishSnapshot();
        }

        public void Stop()
        {
            lock (_syncRoot)
            {
                StopRecordingCore();
                _isMonitoring = false;
                _status = "Stopped.";

                try { _publishTimer.Stop(); } catch { }
                try
                {
                    if (_captureItem != null)
                        _captureItem.Closed -= CaptureItem_Closed;
                }
                catch { }
                try { _session?.Dispose(); } catch { }
                try { _framePool?.Dispose(); } catch { }
                try { _d3dDevice?.Dispose(); } catch { }

                _session = null;
                _framePool = null;
                _d3dDevice = null;
                _winRtDevice = null;
                _captureItem = null;
            }

            PublishSnapshot();
        }

        public void Dispose()
        {
            Stop();
        }

        private void StartCapture()
        {
            lock (_syncRoot)
            {
                if (_captureItem == null)
                    return;

                _d3dDevice = new Device(
                    SharpDX.Direct3D.DriverType.Hardware,
                    DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport);
                _winRtDevice = Direct3D11Helpers.CreateD3DDevice(_d3dDevice);
                _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                    _winRtDevice,
                    DirectXPixelFormat.B8G8R8A8UIntNormalized,
                    2,
                    _captureItem.Size);
                _framePool.FrameArrived += FramePool_FrameArrived;

                _session = _framePool.CreateCaptureSession(_captureItem);
                TryApplySessionSettings(_session);

                _recentSamples.Clear();
                _frameCount = 0;
                _lastFrameTicks = 0;
                _lastFrameTimeMs = 0;
                _sessionStartedAtUtc = DateTimeOffset.UtcNow;
                _clock.Restart();
                _isMonitoring = true;
                _status = "Monitoring FPS.";

                _session.StartCapture();
                _publishTimer.Start();
            }

            PublishSnapshot();
        }

        private void FramePool_FrameArrived(Direct3D11CaptureFramePool sender, object args)
        {
            try
            {
                using var frame = sender.TryGetNextFrame();
                if (frame == null)
                    return;

                var nowTicks = Stopwatch.GetTimestamp();
                FpsMonitorSnapshot? snapshotForRecord = null;

                lock (_syncRoot)
                {
                    if (!_isMonitoring)
                        return;

                    if (_lastFrameTicks != 0)
                    {
                        _lastFrameTimeMs = (nowTicks - _lastFrameTicks) * 1000.0 / Stopwatch.Frequency;
                    }

                    _lastFrameTicks = nowTicks;
                    _frameCount++;
                    _recentSamples.Enqueue(new FrameSample(nowTicks, _lastFrameTimeMs));

                    var cutoffTicks = nowTicks - (long)(_settings.SampleWindowSeconds * Stopwatch.Frequency);
                    while (_recentSamples.Count > 0 && _recentSamples.Peek().Ticks < cutoffTicks)
                        _recentSamples.Dequeue();

                    if (_isRecording && _recordWriter != null)
                        snapshotForRecord = BuildSnapshotLocked();
                }

                if (snapshotForRecord != null)
                    WriteRecordingSample(snapshotForRecord);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[FPS] Frame timing failed: {ex.Message}");
            }
        }

        private void WriteRecordingSample(FpsMonitorSnapshot snapshot)
        {
            try
            {
                _recordWriter?.WriteLine(string.Join(",",
                    snapshot.Duration.TotalMilliseconds.ToString("F0", CultureInfo.InvariantCulture),
                    snapshot.CurrentFps.ToString("F2", CultureInfo.InvariantCulture),
                    snapshot.AverageFps.ToString("F2", CultureInfo.InvariantCulture),
                    snapshot.OnePercentLowFps.ToString("F2", CultureInfo.InvariantCulture),
                    snapshot.PointOnePercentLowFps.ToString("F2", CultureInfo.InvariantCulture),
                    snapshot.FrameTimeMs.ToString("F2", CultureInfo.InvariantCulture),
                    snapshot.FrameCount.ToString(CultureInfo.InvariantCulture),
                    QuoteCsv(snapshot.TargetName)));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[FPS] Failed to write FPS sample: {ex.Message}");
            }
        }

        private void PublishSnapshot()
        {
            SnapshotChanged?.Invoke(this, BuildSnapshot());
        }

        private FpsMonitorSnapshot BuildSnapshot()
        {
            lock (_syncRoot)
            {
                return BuildSnapshotLocked();
            }
        }

        private FpsMonitorSnapshot BuildSnapshotLocked()
        {
            var samples = _recentSamples.ToArray();
            var currentFps = CalculateCurrentFps(samples);
            var averageFps = _clock.Elapsed.TotalSeconds > 0
                ? _frameCount / _clock.Elapsed.TotalSeconds
                : 0;

            return new FpsMonitorSnapshot
            {
                CurrentFps = currentFps,
                AverageFps = averageFps,
                OnePercentLowFps = CalculateLowFps(samples, 0.01),
                PointOnePercentLowFps = CalculateLowFps(samples, 0.001),
                FrameTimeMs = _lastFrameTimeMs,
                FrameCount = _frameCount,
                IsMonitoring = _isMonitoring,
                IsRecording = _isRecording,
                TargetName = _targetName,
                Status = _status,
                Duration = _isMonitoring ? DateTimeOffset.UtcNow - _sessionStartedAtUtc : TimeSpan.Zero,
                RecordingPath = _recordingPath
            };
        }

        private static double CalculateCurrentFps(FrameSample[] samples)
        {
            if (samples.Length < 2)
                return 0;

            var elapsedSeconds = (samples[^1].Ticks - samples[0].Ticks) / (double)Stopwatch.Frequency;
            return elapsedSeconds > 0 ? (samples.Length - 1) / elapsedSeconds : 0;
        }

        private static double CalculateLowFps(FrameSample[] samples, double percentile)
        {
            var frameTimes = samples
                .Select(sample => sample.FrameTimeMs)
                .Where(ms => ms > 0)
                .OrderByDescending(ms => ms)
                .ToArray();

            if (frameTimes.Length == 0)
                return 0;

            var sampleCount = Math.Max(1, (int)Math.Ceiling(frameTimes.Length * percentile));
            var averageWorstFrameMs = frameTimes.Take(sampleCount).Average();
            return averageWorstFrameMs > 0 ? 1000.0 / averageWorstFrameMs : 0;
        }

        private void TryApplySessionSettings(GraphicsCaptureSession session)
        {
            try { session.IsCursorCaptureEnabled = _settings.IncludeCursor; } catch { }
            try { session.IsBorderRequired = !_settings.HideCaptureBorder; } catch { }
        }

        private void CaptureItem_Closed(GraphicsCaptureItem sender, object args)
        {
            Stop();
        }

        private void StopRecordingCore()
        {
            _isRecording = false;
            try
            {
                _recordWriter?.Flush();
                _recordWriter?.Dispose();
            }
            catch { }
            finally
            {
                _recordWriter = null;
            }
        }

        private static string QuoteCsv(string value)
        {
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }

        private readonly record struct FrameSample(long Ticks, double FrameTimeMs);
    }
}
