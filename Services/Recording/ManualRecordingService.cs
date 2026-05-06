using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Windows.Graphics.Capture;
using Windows.Storage;
using Zink.Models;

namespace Zink.Services.Recording
{
    public sealed class ManualRecordingService : IAsyncDisposable
    {
        public static ManualRecordingService Instance { get; } = new();

        private readonly object _gate = new();

        private readonly TimeSpan _replayBufferDuration = TimeSpan.FromSeconds(45);

        // Keep active raw segment short so high-resolution replay stays off the managed heap.
        private readonly TimeSpan _segmentDuration = TimeSpan.FromMilliseconds(250);

        private CaptureEngine? _captureEngine;
        private IAudioCaptureService? _systemAudioCapture;
        private IAudioCaptureService? _microphoneCapture;

        private RollingClipBuffer? _activeVideoBuffer;
        private RollingAudioBuffer? _activeSystemAudioBuffer;
        private RollingAudioBuffer? _activeMicrophoneBuffer;

        private string? _currentOutputPath;
        private GraphicsCaptureItem? _captureItem;
        private RecordingOptions _options = new();

        private bool _isSavingReplay;
        private bool _resumeReplayAfterManual;
        private RecordingOptions? _lastReplayOptions;

        private string? _sessionFolderPath;
        private readonly List<string> _segmentPaths = new();
        private DateTimeOffset _currentSegmentStartedUtc;
        private int _nextSegmentIndex;

        private Task? _segmentWriteTask;
        private long _droppedVideoFrames;

        private readonly object _directWriterGate = new();
        private bool _useDirectManualWriter;
        private bool _directManualWriterStarted;
        private bool _directManualWriterFailed;
        private TimeSpan? _directManualOrigin;
        private int _directManualWidth;
        private int _directManualHeight;
        private int _directManualFps;
        private long _directManualVideoFrames;
        private long _directManualAudioPackets;

        public bool IsRecording { get; private set; }
        public bool IsReplayMode { get; private set; }

        public bool IsManualRecording => IsRecording && !IsReplayMode;
        public bool IsReplayBufferRunning => IsRecording && IsReplayMode;

        public event EventHandler<string>? StatusChanged;

        private ManualRecordingService()
        {
        }

        public void SetCaptureItem(GraphicsCaptureItem item, RecordingOptions options)
        {
            _captureItem = item;
            _options = options.Clone();
            StatusChanged?.Invoke(this, "Recorder target updated.");
        }

        public TimeSpan GetReplayBufferedDuration()
        {
            lock (_gate)
            {
                if (!IsReplayMode || !IsRecording)
                    return TimeSpan.Zero;

                double finalizedSeconds = _segmentPaths.Count * _segmentDuration.TotalSeconds;
                double activeSeconds = 0;

                if (_activeVideoBuffer != null)
                {
                    activeSeconds = Math.Max(0, (DateTimeOffset.UtcNow - _currentSegmentStartedUtc).TotalSeconds);
                }

                return TimeSpan.FromSeconds(finalizedSeconds + activeSeconds);
            }
        }

        public async Task<string> StartAsync()
        {
            bool replayWasRunning;
            RecordingOptions? replayOptionsToResume;

            lock (_gate)
            {
                if (IsRecording && !IsReplayMode)
                    return _currentOutputPath ?? string.Empty;

                replayWasRunning = IsRecording && IsReplayMode;
                replayOptionsToResume = replayWasRunning
                    ? (_lastReplayOptions?.Clone() ?? _options.Clone())
                    : null;

                if (replayWasRunning)
                {
                    _resumeReplayAfterManual = true;
                    _lastReplayOptions = replayOptionsToResume?.Clone();
                }
            }

            if (replayWasRunning)
            {
                await StopCoreAsync(allowReplayResume: false);
            }

            return await StartCoreAsync(replayMode: false);
        }

        public async Task<string> StartReplayAsync(GraphicsCaptureItem item, RecordingOptions options)
        {
            SetCaptureItem(item, options);

            lock (_gate)
            {
                _lastReplayOptions = options.Clone();
                _resumeReplayAfterManual = false;
            }

            return await StartCoreAsync(replayMode: true);
        }

        public async Task<string> StartReplayAsync(RecordingOptions options)
        {
            _captureItem = null;
            _options = options.Clone();

            lock (_gate)
            {
                _lastReplayOptions = options.Clone();
                _resumeReplayAfterManual = false;
            }

            return await StartCoreAsync(replayMode: true);
        }

        private async Task<string> StartCoreAsync(bool replayMode)
        {
            if (!replayMode && _captureItem is null)
                throw new InvalidOperationException("Choose a capture source first.");

            lock (_gate)
            {
                if (IsRecording)
                    return _currentOutputPath ?? string.Empty;

                IsRecording = true;
                IsReplayMode = replayMode;
                _isSavingReplay = false;
                _droppedVideoFrames = 0;
            }

            try
            {
                _currentOutputPath = replayMode
                    ? null
                    : await RecordingOutputService.CreateNewOutputPathAsync("Zink Recording");

                await CreateNewSessionFolderAsync();

                lock (_gate)
                {
                    _useDirectManualWriter = !replayMode && ShouldUseDirectManualWriter(_options);
                    ResetDirectManualWriterStateNoLock();
                }

                if (replayMode || !_useDirectManualWriter)
                {
                    InitializeActiveSegmentBuffers();
                }

                _captureEngine = new CaptureEngine();
                _captureEngine.VideoFrameArrived += CaptureEngine_VideoFrameArrived;
                await _captureEngine.StartAsync(_captureItem, _options);

                if (_options.IncludeSystemAudio)
                {
                    _systemAudioCapture = new SystemLoopbackCaptureService();
                    _systemAudioCapture.AudioPacketArrived += SystemAudioCapture_AudioPacketArrived;
                    await _systemAudioCapture.StartAsync(_options.SelectedRenderDeviceId);
                }

                if (_options.IncludeMicrophone)
                {
                    _microphoneCapture = new MicrophoneCaptureService();
                    _microphoneCapture.AudioPacketArrived += MicrophoneCapture_AudioPacketArrived;
                    await _microphoneCapture.StartAsync(_options.SelectedMicDeviceId);
                }

                await RecorderLog.InfoAsync(nameof(ManualRecordingService),
                    $"Recording started. ReplayMode={replayMode}, DirectManualWriter={_useDirectManualWriter}, SystemAudio={_options.IncludeSystemAudio}, Mic={_options.IncludeMicrophone}, SegmentDuration={_segmentDuration}");

                StatusChanged?.Invoke(this,
                    replayMode
                        ? "Instant replay started."
                        : $"Manual recording started: {_currentOutputPath}");

                return _currentOutputPath ?? string.Empty;
            }
            catch (Exception ex)
            {
                await RecorderLog.ErrorAsync(nameof(ManualRecordingService), ex, "StartCoreAsync failed");

                lock (_gate)
                {
                    IsRecording = false;
                    IsReplayMode = false;
                    _isSavingReplay = false;
                }

                await SafeStopInternalAsync();
                throw;
            }
        }

        private void CaptureEngine_VideoFrameArrived(object? sender, VideoFramePacket packet)
        {
            if (TryWriteDirectManualVideo(packet))
                return;

            SegmentBatch? batchToFinalize = null;
            Task? startedWriteTask = null;

            lock (_gate)
            {
                if (_useDirectManualWriter && _directManualWriterFailed && _activeVideoBuffer is null)
                {
                    InitializeActiveSegmentBuffersNoLock();
                }

                long beforeBytes = _activeVideoBuffer?.TotalBufferedBytes ?? 0;
                _activeVideoBuffer?.Add(packet);
                long afterBytes = _activeVideoBuffer?.TotalBufferedBytes ?? 0;

                if (afterBytes < beforeBytes)
                {
                    _droppedVideoFrames++;
                }

                bool writeBusy = _segmentWriteTask != null && !_segmentWriteTask.IsCompleted;

                if (ShouldRotateSegmentNoLock() && !writeBusy)
                {
                    batchToFinalize = CreateSegmentBatchNoLock();
                    InitializeActiveSegmentBuffersNoLock();

                    if (batchToFinalize is not null)
                    {
                        startedWriteTask = FinalizeSegmentAsync(batchToFinalize);
                        _segmentWriteTask = startedWriteTask;
                    }
                }
            }

            if (startedWriteTask != null)
            {
                _ = startedWriteTask.ContinueWith(_ =>
                {
                    lock (_gate)
                    {
                        if (ReferenceEquals(_segmentWriteTask, startedWriteTask))
                            _segmentWriteTask = null;
                    }
                });
            }
        }

        private void SystemAudioCapture_AudioPacketArrived(object? sender, AudioPacket packet)
        {
            if (TryWriteDirectManualAudio(packet))
                return;

            lock (_gate)
            {
                _activeSystemAudioBuffer?.Add(packet);
            }
        }

        private void MicrophoneCapture_AudioPacketArrived(object? sender, AudioPacket packet)
        {
            lock (_gate)
            {
                _activeMicrophoneBuffer?.Add(packet);
            }
        }

        public async Task<string> SaveReplayAsync()
        {
            if (!IsReplayBufferRunning)
                throw new InvalidOperationException("Replay buffer is not running yet.");

            lock (_gate)
            {
                if (_isSavingReplay)
                    throw new InvalidOperationException("Replay save is already in progress.");

                _isSavingReplay = true;
            }

            try
            {
                await AwaitCurrentSegmentWriteAsync();

                SegmentBatch? finalBatch;

                lock (_gate)
                {
                    finalBatch = CreateSegmentBatchNoLock();
                    InitializeActiveSegmentBuffersNoLock();
                }

                if (finalBatch is not null)
                {
                    await FinalizeSegmentAsync(finalBatch);
                }

                List<string> segments;
                lock (_gate)
                {
                    segments = _segmentPaths.ToList();
                }

                if (segments.Count == 0)
                    throw new InvalidOperationException("No buffered replay segments are available yet.");

                string outputPath = await RecordingOutputService.CreateNewOutputPathAsync("Zink Replay");

                if (segments.Count == 1)
                {
                    File.Copy(segments[0], outputPath, true);
                }
                else
                {
                    var mux = new MediaMuxService();
                    await mux.ConcatVideosAsync(segments, outputPath);
                }

                if (!File.Exists(outputPath))
                    throw new InvalidOperationException("Replay save finished but the output file was not created.");

                var outputInfo = new FileInfo(outputPath);
                if (!outputInfo.Exists || outputInfo.Length == 0)
                    throw new InvalidOperationException("Replay save finished but the output file is empty.");

                await RecorderLog.InfoAsync(nameof(ManualRecordingService),
                    $"Replay clip saved successfully. Output='{outputPath}', Bytes={outputInfo.Length}");

                StatusChanged?.Invoke(this, $"Replay clip saved: {outputPath}");
                return outputPath;
            }
            catch (Exception ex)
            {
                await RecorderLog.ErrorAsync(nameof(ManualRecordingService), ex, "SaveReplayAsync failed");
                StatusChanged?.Invoke(this, $"Replay save failed: {ex.Message}");
                throw;
            }
            finally
            {
                lock (_gate)
                {
                    _isSavingReplay = false;
                }
            }
        }

        public async Task StopAsync()
        {
            await StopCoreAsync(allowReplayResume: true);
        }

        private async Task StopCoreAsync(bool allowReplayResume)
        {
            string? outputPath;
            CaptureEngine? localEngine;
            IAudioCaptureService? localSystemCapture;
            IAudioCaptureService? localMicCapture;
            bool localReplayMode;
            bool restartReplayAfterManual = false;
            RecordingOptions? replayOptionsToResume = null;

            lock (_gate)
            {
                if (!IsRecording)
                    return;

                localReplayMode = IsReplayMode;

                if (!localReplayMode && allowReplayResume && _resumeReplayAfterManual)
                {
                    restartReplayAfterManual = true;
                    replayOptionsToResume = _lastReplayOptions?.Clone() ?? _options.Clone();
                }

                IsRecording = false;
                IsReplayMode = false;
                _isSavingReplay = false;
                _resumeReplayAfterManual = false;

                outputPath = _currentOutputPath;
                localEngine = _captureEngine;
                localSystemCapture = _systemAudioCapture;
                localMicCapture = _microphoneCapture;

                _captureEngine = null;
                _systemAudioCapture = null;
                _microphoneCapture = null;
                _currentOutputPath = null;
            }

            try
            {
                if (localEngine is not null)
                {
                    localEngine.VideoFrameArrived -= CaptureEngine_VideoFrameArrived;
                    await localEngine.StopAsync();
                    await localEngine.DisposeAsync();
                }

                if (localSystemCapture is not null)
                {
                    localSystemCapture.AudioPacketArrived -= SystemAudioCapture_AudioPacketArrived;
                    await localSystemCapture.StopAsync();
                    await localSystemCapture.DisposeAsync();
                }

                if (localMicCapture is not null)
                {
                    localMicCapture.AudioPacketArrived -= MicrophoneCapture_AudioPacketArrived;
                    await localMicCapture.StopAsync();
                    await localMicCapture.DisposeAsync();
                }

                await AwaitCurrentSegmentWriteAsync();

                bool usedDirectWriter;
                bool directWriterFailed;
                lock (_gate)
                {
                    usedDirectWriter = _useDirectManualWriter;
                    directWriterFailed = _directManualWriterFailed;
                }

                if (!localReplayMode && usedDirectWriter && !directWriterFailed)
                {
                    if (string.IsNullOrWhiteSpace(outputPath))
                        throw new InvalidOperationException("Manual recording output path was not created.");

                    await FinalizeDirectManualWriterAsync(outputPath);

                    if (!File.Exists(outputPath))
                        throw new InvalidOperationException("Manual recording finished but the output file was not created.");

                    var directOutputInfo = new FileInfo(outputPath);
                    if (!directOutputInfo.Exists || directOutputInfo.Length == 0)
                        throw new InvalidOperationException("Manual recording finished but the output file is empty.");

                    await CleanupSessionFolderAsync();
                    ReleaseRecorderMemory();
                    StatusChanged?.Invoke(this, $"Manual recording saved: {outputPath}");
                    StatusChanged?.Invoke(this, "Manual recording stopped.");
                    return;
                }

                SegmentBatch? finalBatch;
                lock (_gate)
                {
                    finalBatch = CreateSegmentBatchNoLock();
                    _activeVideoBuffer = null;
                    _activeSystemAudioBuffer = null;
                    _activeMicrophoneBuffer = null;
                }

                if (finalBatch is not null)
                {
                    await FinalizeSegmentAsync(finalBatch);
                }

                List<string> segments;
                lock (_gate)
                {
                    segments = _segmentPaths.ToList();
                }

                if (localReplayMode)
                {
                    await CleanupSessionFolderAsync();

                    await RecorderLog.InfoAsync(nameof(ManualRecordingService), "Replay buffering stopped.");
                    StatusChanged?.Invoke(this, "Replay buffering stopped.");
                    return;
                }

                if (!string.IsNullOrWhiteSpace(outputPath))
                {
                    if (segments.Count == 0)
                        throw new InvalidOperationException("No recording segments were created, so no recording can be saved.");

                    if (segments.Count == 1)
                    {
                        File.Copy(segments[0], outputPath, true);
                    }
                    else
                    {
                        var mux = new MediaMuxService();
                        await mux.ConcatVideosAsync(segments, outputPath);
                    }

                    if (!File.Exists(outputPath))
                        throw new InvalidOperationException("Manual recording finished but the output file was not created.");

                    var outputInfo = new FileInfo(outputPath);
                    if (!outputInfo.Exists || outputInfo.Length == 0)
                        throw new InvalidOperationException("Manual recording finished but the output file is empty.");

                    StatusChanged?.Invoke(this, $"Manual recording saved: {outputPath}");
                }

                await CleanupSessionFolderAsync();
                ReleaseRecorderMemory();
                StatusChanged?.Invoke(this, "Manual recording stopped.");
            }
            catch (Exception ex)
            {
                await RecorderLog.ErrorAsync(nameof(ManualRecordingService), ex, "StopAsync failed");
                StatusChanged?.Invoke(this, $"Recording stop failed: {ex.Message}");
                throw;
            }
            finally
            {
                if (restartReplayAfterManual &&
                    replayOptionsToResume != null &&
                    RecordingPreferences.IsGamingBackgroundReplayEnabled)
                {
                    try
                    {
                        if (_captureItem is not null)
                        {
                            await StartReplayAsync(_captureItem, replayOptionsToResume);
                        }
                        else
                        {
                            await StartReplayAsync(replayOptionsToResume);
                        }

                        await RecorderLog.InfoAsync(nameof(ManualRecordingService),
                            "Replay buffering restarted automatically after manual recording stop.");
                        StatusChanged?.Invoke(this, "Replay buffering restarted.");
                    }
                    catch (Exception replayEx)
                    {
                        await RecorderLog.ErrorAsync(nameof(ManualRecordingService), replayEx,
                            "Failed to restart replay buffering after manual recording stop.");
                    }
                }
            }
        }

        private async Task AwaitCurrentSegmentWriteAsync()
        {
            Task? task;
            lock (_gate)
            {
                task = _segmentWriteTask;
            }

            if (task != null)
            {
                await task;
            }

            lock (_gate)
            {
                _segmentWriteTask = null;
            }
        }

        private async Task SafeStopInternalAsync()
        {
            CaptureEngine? localEngine = _captureEngine;
            IAudioCaptureService? localSystemCapture = _systemAudioCapture;
            IAudioCaptureService? localMicCapture = _microphoneCapture;

            _captureEngine = null;
            _systemAudioCapture = null;
            _microphoneCapture = null;
            _isSavingReplay = false;
            IsReplayMode = false;
            IsRecording = false;
            _resumeReplayAfterManual = false;
            _useDirectManualWriter = false;
            ResetDirectManualWriterStateNoLock();

            if (localEngine is not null)
            {
                localEngine.VideoFrameArrived -= CaptureEngine_VideoFrameArrived;
                await localEngine.StopAsync();
                await localEngine.DisposeAsync();
            }

            if (localSystemCapture is not null)
            {
                localSystemCapture.AudioPacketArrived -= SystemAudioCapture_AudioPacketArrived;
                await localSystemCapture.StopAsync();
                await localSystemCapture.DisposeAsync();
            }

            if (localMicCapture is not null)
            {
                localMicCapture.AudioPacketArrived -= MicrophoneCapture_AudioPacketArrived;
                await localMicCapture.StopAsync();
                await localMicCapture.DisposeAsync();
            }

            _activeVideoBuffer?.Dispose();
            _activeVideoBuffer = null;

            _activeSystemAudioBuffer?.Clear();
            _activeSystemAudioBuffer = null;

            _activeMicrophoneBuffer?.Clear();
            _activeMicrophoneBuffer = null;

            await CleanupSessionFolderAsync();
            ReleaseRecorderMemory();
        }

        private static bool ShouldUseDirectManualWriter(RecordingOptions options)
        {
            // The legacy native mux writer exposes one audio stream. Keep mic mixing on the
            // existing segment path until the native writer can mix two live PCM sources.
            return !options.IncludeMicrophone;
        }

        private void ResetDirectManualWriterStateNoLock()
        {
            lock (_directWriterGate)
            {
                if (_directManualWriterStarted)
                {
                    try
                    {
                        NativeMuxWriter.ZrmShutdownWriter();
                    }
                    catch
                    {
                    }
                }

                _directManualWriterStarted = false;
                _directManualWriterFailed = false;
                _directManualOrigin = null;
                _directManualWidth = 0;
                _directManualHeight = 0;
                _directManualFps = 0;
                _directManualVideoFrames = 0;
                _directManualAudioPackets = 0;
            }
        }

        private bool TryWriteDirectManualVideo(VideoFramePacket packet)
        {
            bool shouldUseDirect;
            string? outputPath;

            lock (_gate)
            {
                shouldUseDirect = IsManualRecording && _useDirectManualWriter && !_directManualWriterFailed;
                outputPath = _currentOutputPath;
            }

            if (!shouldUseDirect || string.IsNullOrWhiteSpace(outputPath))
                return false;

            if (packet.Bgra32Bytes is null ||
                packet.Width <= 0 ||
                packet.Height <= 0 ||
                packet.Bgra32Bytes.Length != packet.Width * packet.Height * 4)
            {
                packet.Dispose();
                return true;
            }

            try
            {
                lock (_directWriterGate)
                {
                    if (_directManualWriterFailed)
                        return false;

                    if (!_directManualWriterStarted)
                    {
                        _directManualOrigin = packet.Timestamp;
                        _directManualWidth = packet.Width;
                        _directManualHeight = packet.Height;
                        _directManualFps = CalculateDirectManualFps(packet.Width, packet.Height, _options.FrameRate);

                        int hr = NativeMuxWriter.ZrmCreateWriter(
                            outputPath,
                            (uint)_directManualWidth,
                            (uint)_directManualHeight,
                            (uint)_directManualFps,
                            1,
                            CalculateDirectManualBitrate((uint)_directManualWidth, (uint)_directManualHeight, (uint)_directManualFps),
                            48000,
                            2,
                            16);

                        NativeMuxWriter.ThrowIfFailed(hr, nameof(NativeMuxWriter.ZrmCreateWriter));
                        _directManualWriterStarted = true;

                        _ = RecorderLog.InfoAsync(nameof(ManualRecordingService),
                            $"Direct manual writer started. Output='{outputPath}', Size={_directManualWidth}x{_directManualHeight}, Fps={_directManualFps}");
                    }

                    long timestamp100ns = Math.Max(0, (packet.Timestamp - (_directManualOrigin ?? packet.Timestamp)).Ticks);
                    long duration100ns = TimeSpan.FromMilliseconds(1000.0 / Math.Max(1, _directManualFps)).Ticks;

                    int hrWrite = NativeMuxWriter.ZrmWriteVideoFrame(
                        timestamp100ns,
                        duration100ns,
                        packet.Bgra32Bytes,
                        (uint)packet.Bgra32Bytes.Length);

                    NativeMuxWriter.ThrowIfFailed(hrWrite, nameof(NativeMuxWriter.ZrmWriteVideoFrame));
                    _directManualVideoFrames++;
                }

                packet.Dispose();
                return true;
            }
            catch (Exception ex)
            {
                MarkDirectManualWriterFailed(ex, "Direct manual video writer failed; falling back to buffered segment writing.");
                return false;
            }
        }

        private bool TryWriteDirectManualAudio(AudioPacket packet)
        {
            bool shouldUseDirect;

            lock (_gate)
            {
                shouldUseDirect = IsManualRecording && _useDirectManualWriter && !_directManualWriterFailed;
            }

            if (!shouldUseDirect)
                return false;

            try
            {
                lock (_directWriterGate)
                {
                    if (_directManualWriterFailed)
                        return false;

                    if (!_directManualWriterStarted || _directManualOrigin is null)
                        return true;

                    if (packet.PcmData.Length == 0)
                        return true;

                    long timestamp100ns = (packet.Timestamp - _directManualOrigin.Value).Ticks;
                    if (timestamp100ns < 0)
                        return true;

                    int blockAlign = packet.Channels * (packet.BitsPerSample / 8);
                    long duration100ns = blockAlign > 0 && packet.SampleRate > 0
                        ? (10_000_000L * packet.PcmData.Length) / ((long)packet.SampleRate * blockAlign)
                        : 0;

                    int hr = NativeMuxWriter.ZrmWriteAudioPacket(
                        timestamp100ns,
                        duration100ns,
                        packet.PcmData,
                        (uint)packet.PcmData.Length);

                    NativeMuxWriter.ThrowIfFailed(hr, nameof(NativeMuxWriter.ZrmWriteAudioPacket));
                    _directManualAudioPackets++;
                    return true;
                }
            }
            catch (Exception ex)
            {
                MarkDirectManualWriterFailed(ex, "Direct manual audio writer failed; falling back to buffered segment writing.");
                return false;
            }
        }

        private void MarkDirectManualWriterFailed(Exception ex, string message)
        {
            lock (_directWriterGate)
            {
                _directManualWriterFailed = true;

                try
                {
                    NativeMuxWriter.ZrmShutdownWriter();
                }
                catch
                {
                }

                _directManualWriterStarted = false;
            }

            lock (_gate)
            {
                _directManualWriterFailed = true;
            }

            _ = RecorderLog.ErrorAsync(nameof(ManualRecordingService), ex, message);
        }

        private async Task FinalizeDirectManualWriterAsync(string outputPath)
        {
            var finalizeStartedUtc = DateTimeOffset.UtcNow;
            long videoFrames;
            long audioPackets;

            lock (_directWriterGate)
            {
                if (!_directManualWriterStarted || _directManualVideoFrames == 0)
                    throw new InvalidOperationException("No direct manual frames were written.");

                NativeMuxWriter.ThrowIfFailed(
                    NativeMuxWriter.ZrmFinalizeWriter(),
                    nameof(NativeMuxWriter.ZrmFinalizeWriter));

                NativeMuxWriter.ZrmShutdownWriter();
                videoFrames = _directManualVideoFrames;
                audioPackets = _directManualAudioPackets;
                _directManualWriterStarted = false;
            }

            await RecorderLog.InfoAsync(nameof(ManualRecordingService),
                $"Direct manual writer finalized. Output='{outputPath}', VideoFrames={videoFrames}, AudioPackets={audioPackets}, ElapsedMs={(DateTimeOffset.UtcNow - finalizeStartedUtc).TotalMilliseconds:F0}");
        }

        private static int CalculateDirectManualFps(int width, int height, uint requestedFps)
        {
            int fps = (int)Math.Clamp(requestedFps == 0 ? 60 : requestedFps, 1u, 60u);
            long pixels = (long)width * height;

            return pixels > 3840L * 2160L
                ? Math.Min(fps, 30)
                : fps;
        }

        private static uint CalculateDirectManualBitrate(uint width, uint height, uint fps)
        {
            double bitsPerSecond = width * (double)height * Math.Max(1, fps) * 0.16;
            return (uint)Math.Clamp(bitsPerSecond, 12_000_000, 100_000_000);
        }

        private static void ReleaseRecorderMemory()
        {
            FrameBufferPool.Clear();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        private async Task CreateNewSessionFolderAsync()
        {
            string root = Path.Combine(ApplicationData.Current.LocalCacheFolder.Path, "ZinkSegments");
            Directory.CreateDirectory(root);

            string sessionFolder = Path.Combine(root, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(sessionFolder);

            lock (_gate)
            {
                _sessionFolderPath = sessionFolder;
                _segmentPaths.Clear();
                _nextSegmentIndex = 0;
                _segmentWriteTask = null;
            }

            await Task.CompletedTask;
        }

        private async Task CleanupSessionFolderAsync()
        {
            string? folder;
            lock (_gate)
            {
                folder = _sessionFolderPath;
                _sessionFolderPath = null;
                _segmentPaths.Clear();
                _nextSegmentIndex = 0;
                _segmentWriteTask = null;
            }

            if (!string.IsNullOrWhiteSpace(folder))
            {
                try
                {
                    if (Directory.Exists(folder))
                        Directory.Delete(folder, true);
                }
                catch
                {
                }
            }

            await Task.CompletedTask;
        }

        private void InitializeActiveSegmentBuffers()
        {
            lock (_gate)
            {
                InitializeActiveSegmentBuffersNoLock();
            }
        }

        private void InitializeActiveSegmentBuffersNoLock()
        {
            _activeVideoBuffer = new RollingClipBuffer(_segmentDuration);

            _activeSystemAudioBuffer = _options.IncludeSystemAudio
                ? new RollingAudioBuffer(_segmentDuration)
                : null;

            _activeMicrophoneBuffer = _options.IncludeMicrophone
                ? new RollingAudioBuffer(_segmentDuration)
                : null;

            _currentSegmentStartedUtc = DateTimeOffset.UtcNow;
        }

        private bool ShouldRotateSegmentNoLock()
        {
            return (DateTimeOffset.UtcNow - _currentSegmentStartedUtc) >= _segmentDuration;
        }

        private SegmentBatch? CreateSegmentBatchNoLock()
        {
            if (_activeVideoBuffer is null || _activeVideoBuffer.Count == 0 || string.IsNullOrWhiteSpace(_sessionFolderPath))
                return null;

            string segmentPath = Path.Combine(_sessionFolderPath, $"segment_{_nextSegmentIndex++:000000}.mp4");

            return new SegmentBatch(
                segmentPath,
                _activeVideoBuffer,
                _activeSystemAudioBuffer,
                _activeMicrophoneBuffer,
                IsReplayMode);
        }

        private async Task FinalizeSegmentAsync(SegmentBatch batch)
        {
            var finalizeStartedUtc = DateTimeOffset.UtcNow;

            List<VideoFramePacket> videoFrames = batch.VideoBuffer.Snapshot()
                .Where(f => f.Bgra32Bytes is not null)
                .OrderBy(f => f.Timestamp)
                .ToList();

            List<AudioPacket>? systemPackets = batch.SystemAudioBuffer?.Snapshot()
                .OrderBy(p => p.Timestamp)
                .ToList();

            List<AudioPacket>? micPackets = batch.MicrophoneAudioBuffer?.Snapshot()
                .OrderBy(p => p.Timestamp)
                .ToList();

            if (videoFrames.Count == 0)
            {
                batch.VideoBuffer.Dispose();
                batch.SystemAudioBuffer?.Clear();
                batch.MicrophoneAudioBuffer?.Clear();
                return;
            }

            string tempVideo = Path.ChangeExtension(batch.OutputPath, null) + "_video.mp4";
            string tempAudio = Path.ChangeExtension(batch.OutputPath, null) + "_audio.wav";

            List<VideoFramePacket>? syncedVideo = null;

            try
            {
                await RecorderLog.InfoAsync(nameof(ManualRecordingService),
                    $"Finalizing segment. Replay={batch.IsReplaySegment}, VideoFrames={videoFrames.Count}, SystemPackets={systemPackets?.Count ?? 0}, MicPackets={micPackets?.Count ?? 0}, ManagedMemoryMB={GC.GetTotalMemory(false) / 1024 / 1024}, DroppedFrames={_droppedVideoFrames}");

                var origin = AvSyncHelpers.ComputeCommonOrigin(videoFrames, systemPackets, micPackets);

                syncedVideo = AvSyncHelpers.ShiftVideo(videoFrames, origin);
                var syncedSystem = AvSyncHelpers.ShiftAudio(systemPackets, origin);
                var syncedMic = AvSyncHelpers.ShiftAudio(micPackets, origin);

                if (syncedVideo.Count == 0)
                    throw new InvalidOperationException("No valid video frames remained after A/V sync alignment.");

                var videoWriter = new Mp4VideoWriter();
                await videoWriter.WriteAsync(syncedVideo, tempVideo, _options);

                var mixedAudio = AudioMixHelpers.MixPcm16(syncedSystem, syncedMic);

                if (mixedAudio.Count > 0 && mixedAudio.Any(p => p.PcmData.Length > 0))
                {
                    WaveFileWriter.WritePcm16Wave(tempAudio, mixedAudio);

                    var mux = new MediaMuxService();
                    await mux.MuxAsync(tempVideo, tempAudio, batch.OutputPath);
                }
                else
                {
                    if (File.Exists(batch.OutputPath))
                        File.Delete(batch.OutputPath);

                    File.Move(tempVideo, batch.OutputPath);
                }

                lock (_gate)
                {
                    _segmentPaths.Add(batch.OutputPath);

                    if (batch.IsReplaySegment)
                    {
                        int maxSegments = Math.Max(1, (int)Math.Ceiling(_replayBufferDuration.TotalSeconds / _segmentDuration.TotalSeconds));
                        while (_segmentPaths.Count > maxSegments)
                        {
                            string oldSegment = _segmentPaths[0];
                            _segmentPaths.RemoveAt(0);

                            try
                            {
                                if (File.Exists(oldSegment))
                                    File.Delete(oldSegment);
                            }
                            catch
                            {
                            }
                        }
                    }
                }

                var elapsed = DateTimeOffset.UtcNow - finalizeStartedUtc;
                await RecorderLog.InfoAsync(nameof(ManualRecordingService),
                    $"Segment finalized. Replay={batch.IsReplaySegment}, ElapsedMs={elapsed.TotalMilliseconds:F0}, Output='{batch.OutputPath}'");
            }
            finally
            {
                if (syncedVideo is not null)
                {
                    foreach (var frame in syncedVideo)
                    {
                        frame.Dispose();
                    }
                }

                foreach (var frame in videoFrames)
                {
                    frame.Dispose();
                }

                batch.VideoBuffer.Dispose();
                batch.SystemAudioBuffer?.Clear();
                batch.MicrophoneAudioBuffer?.Clear();

                try
                {
                    if (File.Exists(tempVideo))
                        File.Delete(tempVideo);
                }
                catch
                {
                }

                try
                {
                    if (File.Exists(tempAudio))
                        File.Delete(tempAudio);
                }
                catch
                {
                }
            }
        }

        private sealed class SegmentBatch
        {
            public string OutputPath { get; }
            public RollingClipBuffer VideoBuffer { get; }
            public RollingAudioBuffer? SystemAudioBuffer { get; }
            public RollingAudioBuffer? MicrophoneAudioBuffer { get; }
            public bool IsReplaySegment { get; }

            public SegmentBatch(
                string outputPath,
                RollingClipBuffer videoBuffer,
                RollingAudioBuffer? systemAudioBuffer,
                RollingAudioBuffer? microphoneAudioBuffer,
                bool isReplaySegment)
            {
                OutputPath = outputPath;
                VideoBuffer = videoBuffer;
                SystemAudioBuffer = systemAudioBuffer;
                MicrophoneAudioBuffer = microphoneAudioBuffer;
                IsReplaySegment = isReplaySegment;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync();
        }
    }
}
