using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Zink.Services;

namespace Zink.Services.NativeCalling
{
    public sealed class NativeScreenShareStreamingService : IAsyncDisposable
    {
        public static NativeScreenShareStreamingService Instance { get; } = new NativeScreenShareStreamingService();

        public const int TargetFps = 60;
        private const int LivePreviewFps = 30;
        public const long JpegQuality = 88L;
        private const int ReceiverSafe1080pFps = 24;
        internal const bool EnableDirectGpuTexturePath = true;
        private static readonly TimeSpan AdaptationWarmup = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan AdaptationCooldown = TimeSpan.FromSeconds(1);
        private static readonly TimeSpan ReceiverPressurePacingWindow = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan StartupRecoveryKeyFrameThrottle = TimeSpan.FromMilliseconds(2200);
        private static readonly TimeSpan RecoveryKeyFrameThrottle = TimeSpan.FromMilliseconds(900);
        private static readonly TimeSpan EncoderStarvationRefreshThrottle = TimeSpan.FromSeconds(3);
        private static readonly TimeSpan FreshCaptureStallThreshold = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan FreshCaptureRestartCooldown = TimeSpan.FromSeconds(6);
        private static readonly TimeSpan LowFreshCaptureWarmup = TimeSpan.FromSeconds(10);
        private const double LowFreshCaptureFpsThresholdScale = 0.80;
        private const double LowFreshCaptureEncodedFpsThresholdScale = 0.90;
        private const int LowFreshCaptureWindowsBeforeRestart = 3;
        private const double AdaptiveFpsPressureThreshold = 0.90;
        private const double AdaptiveSevereFpsPressureThreshold = 0.82;
        private const int ReceiverPressureSignalsBeforeResolutionDrop = 2;

        private readonly object _qualitySync = new();
        private CancellationTokenSource? _cts;
        private Task? _captureTask;
        private ScreenShareQualityPreset _qualityPreset = ScreenShareQualityPreset.Hd720p;
        private ScreenShareQualityPreset _effectiveQualityPreset = ScreenShareQualityPreset.Hd720p;
        private int _bitrateScalePercent = 100;
        private int? _bitrateOverride;
        private int? _targetFpsOverride;
        private int _emptyEncodeCount;
        private WindowsGraphicsCaptureScreenSource? _wgcCapture;
        private DxgiScreenCaptureService? _dxgiCapture;
        private DateTimeOffset _streamStartedAtUtc;
        private DateTimeOffset _lastAdaptedAtUtc = DateTimeOffset.MinValue;
        private DateTimeOffset _lastEmptyEncodeLogUtc = DateTimeOffset.MinValue;
        private DateTimeOffset _receiverPressurePacingUntilUtc = DateTimeOffset.MinValue;
        private DateTimeOffset _lastReceiverPacingLogUtc = DateTimeOffset.MinValue;
        private DateTimeOffset _lastReceiverPressureKeyFrameQueuedUtc = DateTimeOffset.MinValue;
        private DateTimeOffset _lastRecoveryKeyFrameQueuedUtc = DateTimeOffset.MinValue;
        private DateTimeOffset _lastEncoderStarvationRefreshUtc = DateTimeOffset.MinValue;
        private int _healthyWindows;
        private int _pendingRecoveryKeyFrame;
        private int _pendingEncoderRefresh;
        private int _receiverPressureSignals;

        public bool IsRunning { get; private set; }
        public ScreenShareQualityPreset QualityPreset => _qualityPreset;
        public ScreenShareQualityProfile RequestedQuality => ScreenShareQualityProfile.FromPreset(_qualityPreset);
        public ScreenShareQualityProfile CurrentQuality
        {
            get
            {
                lock (_qualitySync)
                {
                    return ScreenShareQualityProfile.FromPreset(_effectiveQualityPreset);
                }
            }
        }

        public bool IsAdaptiveLatencyModeEnabled { get; private set; } = false;
        public int CurrentBitrate { get; private set; } = ScreenShareQualityProfile.FromPreset(ScreenShareQualityPreset.Hd720p).Bitrate;
        public int AutoDowngradeCount { get; private set; }
        public int CongestionSignals { get; private set; }
        public string AdaptiveState { get; private set; } = "Locked realtime mode ready";
        public int CurrentTargetFps
        {
            get
            {
                lock (_qualitySync)
                {
                    return Math.Clamp(_targetFpsOverride ?? TargetFps, 1, TargetFps);
                }
            }
        }

        public double CaptureFps { get; private set; }
        public double EncodedFps { get; private set; }
        public double LastCaptureMilliseconds { get; private set; }
        public double LastEncodeMilliseconds { get; private set; }
        public double LastLoopMilliseconds { get; private set; }
        public double LastPreviewMilliseconds { get; private set; }
        public string TransportPipeline { get; private set; } = "WebRTC RTP H.264 media track";
        public string EncoderMode { get; private set; } = "Not started";
        public string EncoderInputFormat { get; private set; } = "Unknown";
        public string EncoderGpuDeviceMode { get; private set; } = "Not attached";
        public bool RequireHardwareEncoder { get; set; } = true;
        public bool RequireDirectX12CapturePath { get; set; } = true;
        public bool EnablePreviewFrames { get; set; } = true;
        public bool PrioritizeStreamingPerformance { get; set; }
        public bool DropLateDuplicateFrames { get; set; }
        public NativeCaptureSourceMode PreferredCaptureSourceMode { get; set; } = NativeCaptureSourceMode.Desktop;
        public int RecoveryKeyFrameInterval { get; private set; }
        public bool EncoderRealtimeModeEnabled { get; private set; }
        public bool EncoderLowLatencyOutputEnabled { get; private set; }
        public int RecoveryKeyFrameRequests { get; private set; }
        public int HardwareEncoderFallbackCount { get; private set; }

        public event EventHandler<NativeScreenFrameEventArgs>? FrameReady;
        public event EventHandler<string>? StreamingFailed;

        private NativeScreenShareStreamingService()
        {
        }

        public async Task StartAsync()
        {
            if (IsRunning)
                return;

            _cts = new CancellationTokenSource();
            IsRunning = true;
            _streamStartedAtUtc = DateTimeOffset.UtcNow;
            lock (_qualitySync)
            {
                _effectiveQualityPreset = _qualityPreset;
                _bitrateScalePercent = 100;
                CurrentBitrate = GetConfiguredBitrate(ScreenShareQualityProfile.FromPreset(_effectiveQualityPreset));
                AdaptiveState = $"Locked {ScreenShareQualityProfile.FromPreset(_effectiveQualityPreset).Name} @ {CurrentTargetFps} FPS";
                AutoDowngradeCount = 0;
                CongestionSignals = 0;
                _receiverPressureSignals = 0;
                _healthyWindows = 0;
                _lastAdaptedAtUtc = DateTimeOffset.MinValue;
                _receiverPressurePacingUntilUtc = DateTimeOffset.MinValue;
                _lastReceiverPacingLogUtc = DateTimeOffset.MinValue;
                _lastReceiverPressureKeyFrameQueuedUtc = DateTimeOffset.MinValue;
                _lastRecoveryKeyFrameQueuedUtc = _streamStartedAtUtc;
                EncoderMode = "Starting";
                EncoderInputFormat = "Unknown";
                EncoderGpuDeviceMode = RequireHardwareEncoder
                    ? "DirectX 12 GPU hardware required; no software fallback"
                    : "GPU preferred with software fallback";
                RecoveryKeyFrameInterval = 0;
                EncoderRealtimeModeEnabled = false;
                EncoderLowLatencyOutputEnabled = false;
                RecoveryKeyFrameRequests = 0;
                HardwareEncoderFallbackCount = 0;
                _lastEmptyEncodeLogUtc = DateTimeOffset.MinValue;
                _lastEncoderStarvationRefreshUtc = DateTimeOffset.MinValue;
                _emptyEncodeCount = 0;
                Interlocked.Exchange(ref _pendingRecoveryKeyFrame, 0);
                Interlocked.Exchange(ref _pendingEncoderRefresh, 0);
            }

            DiagnosticLogService.WriteLine("[ScreenShare:UI] Starting Windows Graphics Capture source.");
            DiagnosticLogService.Flush();
            _wgcCapture = new WindowsGraphicsCaptureScreenSource();
            var wgcStarted = await _wgcCapture.StartAsync();
            DiagnosticLogService.WriteLine($"[ScreenShare:UI] Windows Graphics Capture source start result: {wgcStarted}; available={_wgcCapture.IsAvailable}.");

            WriteGpuStreamDiagnostics("start");
            DiagnosticLogService.Flush();
            _captureTask = Task.Factory
                .StartNew(
                    () => CaptureLoopAsync(_cts.Token),
                    _cts.Token,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default)
                .Unwrap();
        }

        public void SetQuality(ScreenShareQualityPreset preset)
        {
            lock (_qualitySync)
            {
                _qualityPreset = preset;
                _effectiveQualityPreset = preset;
                _bitrateScalePercent = 100;
                CurrentBitrate = GetConfiguredBitrate(ScreenShareQualityProfile.FromPreset(_effectiveQualityPreset));
                AdaptiveState = $"Locked {ScreenShareQualityProfile.FromPreset(preset).Name} @ {CurrentTargetFps} FPS";
                _healthyWindows = 0;
                _receiverPressureSignals = 0;
                if (IsRunning)
                    RequestRecoveryKeyFrame($"screen-share quality changed to {ScreenShareQualityProfile.FromPreset(preset).Name}");
            }
        }

        public void SetBitrateOverride(int? bitrate)
        {
            lock (_qualitySync)
            {
                _bitrateOverride = bitrate is > 0 ? bitrate : null;
                CurrentBitrate = GetConfiguredBitrate(ScreenShareQualityProfile.FromPreset(_effectiveQualityPreset));
                _bitrateScalePercent = 100;
                if (IsRunning)
                    RequestRecoveryKeyFrame("streaming bitrate changed");
            }
        }

        public void SetTargetFpsOverride(int? fps)
        {
            lock (_qualitySync)
            {
                _targetFpsOverride = fps.HasValue
                    ? Math.Clamp(fps.Value, 1, TargetFps)
                    : null;
                _healthyWindows = 0;
                _receiverPressureSignals = 0;
                _lastAdaptedAtUtc = DateTimeOffset.MinValue;
                AdaptiveState = IsAdaptiveLatencyModeEnabled
                    ? $"Adaptive realtime {CurrentQuality.Name} @ {CurrentTargetFps} FPS"
                    : $"Locked {CurrentQuality.Name} @ {CurrentTargetFps} FPS";
                if (IsRunning)
                    RequestEncoderRefresh($"target FPS changed to {CurrentTargetFps}");
            }
        }

        public void SetAdaptiveLatencyMode(bool enabled)
        {
            lock (_qualitySync)
            {
                IsAdaptiveLatencyModeEnabled = enabled;
                _healthyWindows = 0;
                _receiverPressureSignals = 0;
                _lastAdaptedAtUtc = DateTimeOffset.MinValue;
                _receiverPressurePacingUntilUtc = DateTimeOffset.MinValue;
                AdaptiveState = enabled
                    ? $"Adaptive realtime {CurrentQuality.Name} @ {CurrentTargetFps} FPS"
                    : $"Locked {CurrentQuality.Name} @ {CurrentTargetFps} FPS";
            }
        }

        public void ReportSendCongestion(string reason, int droppedReceiveFrames = 0, int renderBacklog = 0)
        {
            CongestionSignals++;
            var recoveryReason = IsReceiverRecoveryReason(reason);
            var startupKeyFrameRequest = IsStartupKeyFrameRequest(reason);
            var receiverPressure =
                (!startupKeyFrameRequest && recoveryReason) ||
                droppedReceiveFrames >= TargetFps ||
                renderBacklog > 0;
            var receiverPressureSignals = receiverPressure
                ? Interlocked.Increment(ref _receiverPressureSignals)
                : Volatile.Read(ref _receiverPressureSignals);

            if (recoveryReason)
                RequestRecoveryKeyFrame(reason);

            if (receiverPressure && CurrentQuality.Height >= 1080 && IsAdaptiveLatencyModeEnabled)
            {
                _receiverPressurePacingUntilUtc = DateTimeOffset.UtcNow + ReceiverPressurePacingWindow;
                var now = DateTimeOffset.UtcNow;
                if (now - _lastReceiverPacingLogUtc >= TimeSpan.FromSeconds(2))
                {
                    _lastReceiverPacingLogUtc = now;
                    Debug.WriteLine(
                        $"[ScreenShare:H264] Receiver decode pressure active; pacing capture/encode to {ReceiverSafe1080pFps}fps at {CurrentQuality.Name} without dropping encoded H.264 frames. reason={reason}; dropped={droppedReceiveFrames}; renderBacklog={renderBacklog}.");
                    Debug.WriteLine(
                        $"[ScreenShare:H264] Receiver-safe cadence will stay active for {ReceiverPressurePacingWindow.TotalSeconds:0}s after the latest pressure signal to prevent 60fps rebound delay.");
                }
            }
            else if (!IsAdaptiveLatencyModeEnabled)
            {
                _receiverPressurePacingUntilUtc = DateTimeOffset.MinValue;
            }

            if (IsAdaptiveLatencyModeEnabled)
            {
                var severe =
                    receiverPressure &&
                    (receiverPressureSignals >= ReceiverPressureSignalsBeforeResolutionDrop ||
                     droppedReceiveFrames >= 60 ||
                     renderBacklog > 0);
                ApplyAdaptivePressure(
                    $"{reason}; receiverDropped={droppedReceiveFrames}; renderBacklog={renderBacklog}",
                    severe,
                    receiverPressure);
                return;
            }

            AdaptiveState = IsAdaptiveLatencyModeEnabled && DateTimeOffset.UtcNow < _receiverPressurePacingUntilUtc
                ? $"Locked {CurrentQuality.Name} receiver-safe @ {ReceiverSafe1080pFps} FPS"
                : $"Locked {CurrentQuality.Name} @ {CurrentTargetFps} FPS";
        }

        public void RequestRecoveryKeyFrame(string reason)
        {
            if (!IsRunning)
                return;

            var now = DateTimeOffset.UtcNow;
            var bypassThrottle = IsQualityChangeReason(reason);
            var startupKeyFrameRequest = IsStartupKeyFrameRequest(reason);
            if (!bypassThrottle)
            {
                var throttle = startupKeyFrameRequest && now - _streamStartedAtUtc < TimeSpan.FromSeconds(8)
                    ? StartupRecoveryKeyFrameThrottle
                    : RecoveryKeyFrameThrottle;

                if (now - _lastRecoveryKeyFrameQueuedUtc < throttle)
                {
                    Debug.WriteLine($"[ScreenShare:H264] Recovery keyframe request throttled during warmup/pacing: {reason}");
                    return;
                }
            }

            if (IsReceiverPlaybackPressureReason(reason))
            {
                if (now - _lastReceiverPressureKeyFrameQueuedUtc < TimeSpan.FromSeconds(3))
                {
                    Debug.WriteLine($"[ScreenShare:H264] Receiver pressure keyframe request throttled to avoid repeated NVENC restarts: {reason}");
                    return;
                }

                _lastReceiverPressureKeyFrameQueuedUtc = now;
            }

            _lastRecoveryKeyFrameQueuedUtc = now;
            RecoveryKeyFrameRequests++;
            Interlocked.Exchange(ref _pendingRecoveryKeyFrame, 1);
            Debug.WriteLine($"[ScreenShare:H264] Recovery keyframe queued: {reason}");
        }

        public void RequestEncoderRefresh(string reason)
        {
            if (!IsRunning)
                return;

            Interlocked.Exchange(ref _pendingEncoderRefresh, 1);
            Debug.WriteLine($"[ScreenShare:H264] Encoder refresh queued: {reason}");
        }

        public async Task StopAsync()
        {
            if (!IsRunning)
                return;

            Debug.WriteLine("[ScreenShare:H264] Stop requested.");
            DiagnosticLogService.Flush();
            IsRunning = false;

            try
            {
                _cts?.Cancel();

                if (_captureTask != null)
                    await _captureTask;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ScreenShare:H264] Stop capture task failed: {ex}");
            }
            finally
            {
                _captureTask = null;
                _cts?.Dispose();
                _cts = null;
                Debug.WriteLine("[ScreenShare:H264] Stop completed.");
                DiagnosticLogService.Flush();
            }
        }

        private async Task CaptureLoopAsync(CancellationToken cancellationToken)
        {
            var mmcssHandle = IntPtr.Zero;
            try
            {
                Thread.CurrentThread.Priority = ThreadPriority.Highest;
                mmcssHandle = TryEnableStreamingThreadScheduling();
            }
            catch
            {
            }

            var highResolutionTimerEnabled = TryBeginHighResolutionTimer();
            var outputClock = Stopwatch.StartNew();
            var nextFrameDueTicks = outputClock.ElapsedTicks;
            MediaFoundationH264Encoder? encoder = null;
            ScreenShareQualityProfile? encoderQuality = null;
            var encoderBitrate = 0;
            var encoderFrameRate = 0;
            byte[]? latestPreview = null;
            long latestPreviewTimestampMs = 0;
            var previewFrameInterval = GetPreviewFrameInterval(CurrentQuality);
            var captureFrameIndex = 0;
            var statsWindowStartedAt = DateTimeOffset.UtcNow;
            var capturedInWindow = 0;
            var encodedInWindow = 0;
            var encodedFramesSinceLastIdr = 0;
            var lastIdrOutputAtUtc = DateTimeOffset.MinValue;
            var lastPeriodicIdrRequestAtUtc = DateTimeOffset.MinValue;
            var lastFrameEventTimestampMs = -1L;
            var preferHardwareEncoder = true;
            var missingGpuFramePolls = 0;
            CapturedGpuFrame? latestReusableGpuFrame = null;
            var lastFreshGpuFrameAtUtc = DateTimeOffset.UtcNow;
            var lastWgcRestartAtUtc = DateTimeOffset.MinValue;
            var lowFreshCaptureWindows = 0;

            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var frameStartedAt = DateTimeOffset.UtcNow;
                    var effectiveTargetFps = GetEffectiveTargetFps();
                    var frameBudgetTicks = Math.Max(1L, Stopwatch.Frequency / effectiveTargetFps);

                    try
                    {
                        var quality = CurrentQuality;
                        var bitrate = CurrentBitrate;
                        var encoderResolutionChanged =
                            encoder == null ||
                            encoderQuality == null ||
                            encoderQuality.Width != quality.Width ||
                            encoderQuality.Height != quality.Height;
                        var encoderBitrateChanged = encoder != null && encoderBitrate != bitrate;
                        var encoderFrameRateChanged = encoder != null && encoderFrameRate != effectiveTargetFps;
                        var recoveryKeyFrameRequested = Interlocked.Exchange(ref _pendingRecoveryKeyFrame, 0) == 1;
                        var encoderRefreshRequested = Interlocked.Exchange(ref _pendingEncoderRefresh, 0) == 1;

                        if (encoderResolutionChanged || encoderBitrateChanged || encoderFrameRateChanged || encoderRefreshRequested)
                        {
                            if (encoderBitrateChanged)
                                Debug.WriteLine($"[ScreenShare:H264] Bitrate target changed {encoderBitrate} -> {bitrate}; recreating encoder so NVENC applies the realtime rate.");
                            if (encoderFrameRateChanged)
                                Debug.WriteLine($"[ScreenShare:H264] Target FPS changed {encoderFrameRate} -> {effectiveTargetFps}; recreating encoder so timestamps and GOP cadence match output.");
                            if (encoderRefreshRequested)
                                Debug.WriteLine("[ScreenShare:H264] Recreating encoder for a fresh stream keyframe boundary.");

                            encoder?.Dispose();
                            encoder = null;
                            encoderQuality = quality;
                            encoderBitrate = bitrate;
                            encoderFrameRate = 0;
                            missingGpuFramePolls = 0;
                            latestReusableGpuFrame?.Dispose();
                            latestReusableGpuFrame = null;
                            encodedFramesSinceLastIdr = 0;
                            lastIdrOutputAtUtc = DateTimeOffset.MinValue;
                            lastPeriodicIdrRequestAtUtc = DateTimeOffset.MinValue;
                        }

                        if (recoveryKeyFrameRequested)
                        {
                            if (encoder != null)
                            {
                                Debug.WriteLine("[ScreenShare:H264] Recovery keyframe requested; forcing a GPU IDR without recreating NVENC.");
                                encoder.ForceNextKeyFrame();
                                lastPeriodicIdrRequestAtUtc = DateTimeOffset.UtcNow;
                            }
                            else
                            {
                                Debug.WriteLine("[ScreenShare:H264] Recovery keyframe requested while encoder is starting; next encoder output should begin with an IDR.");
                            }
                        }

                        if (encoder == null)
                        {
                            encoder = CreateEncoderWithFallback(
                                quality,
                                bitrate,
                                effectiveTargetFps,
                                preferHardware: preferHardwareEncoder,
                                requireHardware: RequireHardwareEncoder);
                            encoderBitrate = bitrate;
                            encoderFrameRate = effectiveTargetFps;
                            encoder.ForceNextKeyFrame();
                            Debug.WriteLine($"[ScreenShare:H264] Encoder created for {quality.Width}x{quality.Height}; forcing first GPU output to IDR.");
                            ApplyEncoderDetails(encoder);
                            WriteGpuStreamDiagnostics("encoder-created");
                        }

                        previewFrameInterval = GetPreviewFrameInterval(quality);
                        if (PrioritizeStreamingPerformance)
                            previewFrameInterval = Math.Max(previewFrameInterval, Math.Max(1, effectiveTargetFps / 12));

                        var captureStartedAt = DateTimeOffset.UtcNow;
                        var gpuWaitMilliseconds = latestReusableGpuFrame == null ? 15 : 0;
                        using var newGpuFrame = CaptureGpuFrameWithBestAvailablePath(encoder, gpuWaitMilliseconds);
                        if (newGpuFrame != null)
                        {
                            latestReusableGpuFrame?.Dispose();
                            latestReusableGpuFrame = newGpuFrame.Detach();
                        }

                        var hasFreshGpuFrame = newGpuFrame != null;
                        var gpuFrame = newGpuFrame ?? latestReusableGpuFrame;
                        LastCaptureMilliseconds = (DateTimeOffset.UtcNow - captureStartedAt).TotalMilliseconds;
                        var freshFrameNow = DateTimeOffset.UtcNow;
                        if (hasFreshGpuFrame)
                        {
                            lastFreshGpuFrameAtUtc = freshFrameNow;
                        }
                        else if (gpuFrame != null &&
                                 freshFrameNow - lastFreshGpuFrameAtUtc >= FreshCaptureStallThreshold &&
                                 freshFrameNow - lastWgcRestartAtUtc >= FreshCaptureRestartCooldown)
                        {
                            lastWgcRestartAtUtc = freshFrameNow;
                            Debug.WriteLine($"[ScreenShare:WGC] No fresh GPU capture frame for {(freshFrameNow - lastFreshGpuFrameAtUtc).TotalSeconds:0.0}s; restarting Windows Graphics Capture source to recover a frozen stream.");
                            DiagnosticLogService.WriteLine($"[ScreenShare:WGC] No fresh GPU capture frame for {(freshFrameNow - lastFreshGpuFrameAtUtc).TotalSeconds:0.0}s; restarting capture source for stale-frame recovery.");
                            latestReusableGpuFrame?.Dispose();
                            latestReusableGpuFrame = null;
                            encoder?.Dispose();
                            encoder = null;
                            encoderQuality = quality;
                            encoderBitrate = 0;
                            encoderFrameRate = 0;
                            encodedFramesSinceLastIdr = 0;
                            lastIdrOutputAtUtc = DateTimeOffset.MinValue;
                            lastPeriodicIdrRequestAtUtc = DateTimeOffset.MinValue;
                            missingGpuFramePolls = 0;
                            lowFreshCaptureWindows = 0;
                            if (_wgcCapture is not null && await _wgcCapture.RestartAsync())
                            {
                                lastFreshGpuFrameAtUtc = DateTimeOffset.UtcNow;
                                Interlocked.Exchange(ref _pendingEncoderRefresh, 1);
                                Interlocked.Exchange(ref _pendingRecoveryKeyFrame, 1);
                            }

                            gpuFrame = null;
                        }

                        if (gpuFrame == null)
                        {
                            missingGpuFramePolls++;
                            if (RequireHardwareEncoder && missingGpuFramePolls == TargetFps)
                            {
                                Debug.WriteLine("[ScreenShare:WGC] Waiting for Windows Graphics Capture GPU textures; keeping the stream alive instead of falling back to bitmap readback.");
                            }

                            if (RequireHardwareEncoder && missingGpuFramePolls >= TargetFps * 5)
                            {
                                throw new InvalidOperationException(
                                    "Windows Graphics Capture did not deliver GPU textures for 5 seconds. Restart the stream or close apps that are holding exclusive capture/display resources.");
                            }

                            LastLoopMilliseconds = (DateTimeOffset.UtcNow - frameStartedAt).TotalMilliseconds;
                            nextFrameDueTicks += frameBudgetTicks;
                            await WaitForNextOutputFrameAsync(outputClock, nextFrameDueTicks, frameBudgetTicks, cancellationToken);
                            continue;
                        }

                        if (!hasFreshGpuFrame && DropLateDuplicateFrames)
                        {
                            LastLoopMilliseconds = (DateTimeOffset.UtcNow - frameStartedAt).TotalMilliseconds;
                            var skippedNow = DateTimeOffset.UtcNow;
                            var skippedStatsElapsed = skippedNow - statsWindowStartedAt;
                            if (skippedStatsElapsed >= TimeSpan.FromSeconds(1))
                            {
                                var seconds = Math.Max(0.001, skippedStatsElapsed.TotalSeconds);
                                CaptureFps = capturedInWindow / seconds;
                                EncodedFps = encodedInWindow / seconds;
                                capturedInWindow = 0;
                                encodedInWindow = 0;
                                statsWindowStartedAt = skippedNow;
                                Debug.WriteLine($"[ScreenShare:H264:STATS] capture={CaptureFps:0.0}fps encoded={EncodedFps:0.0}fps captureMs={LastCaptureMilliseconds:0.0} encodeMs={LastEncodeMilliseconds:0.0} previewMs={LastPreviewMilliseconds:0.0} loopMs={LastLoopMilliseconds:0.0}; duplicate GPU frame dropped to protect game-stream performance.");
                                if (EncodedFps < effectiveTargetFps * 0.85)
                                    DiagnosticLogService.Flush();
                            }

                            nextFrameDueTicks += frameBudgetTicks;
                            await WaitForNextOutputFrameAsync(outputClock, nextFrameDueTicks, frameBudgetTicks, cancellationToken);
                            continue;
                        }

                        missingGpuFramePolls = newGpuFrame == null ? missingGpuFramePolls + 1 : 0;
                        if (hasFreshGpuFrame)
                            capturedInWindow++;

                        if (EnablePreviewFrames &&
                            ShouldGeneratePreview(quality, latestPreview, captureFrameIndex, previewFrameInterval))
                        {
                            using var previewBitmap = CaptureBitmapWithBestAvailablePath(quality);
                            if (previewBitmap != null)
                            {
                                var previewStartedAt = DateTimeOffset.UtcNow;
                                latestPreview = EncodePreviewJpeg(previewBitmap, quality, PrioritizeStreamingPerformance);
                                latestPreviewTimestampMs = previewStartedAt.ToUnixTimeMilliseconds();
                                LastPreviewMilliseconds = (DateTimeOffset.UtcNow - previewStartedAt).TotalMilliseconds;
                            }
                            else if (latestPreview == null)
                            {
                                LastPreviewMilliseconds = 0;
                            }
                        }
                        else if (EnablePreviewFrames && latestPreview == null)
                        {
                            LastPreviewMilliseconds = 0;
                        }

                        captureFrameIndex++;

                        var encodeStartedAt = DateTimeOffset.UtcNow;
                        IReadOnlyList<H264EncodedFrame> encodedFrames;
                        var restartEncoderAfterFrame = false;
                        try
                        {
                            encodedFrames = encoder.EncodeGpuBgraTexture(gpuFrame.Texture, gpuFrame.Width, gpuFrame.Height);
                        }
                        catch (Exception ex) when (encoder.IsHardwareAccelerated)
                        {
                            throw new InvalidOperationException(
                                "GPU hardware H.264 encoder failed during direct GPU texture encode. Software and bitmap fallbacks are disabled.",
                                ex);
                        }

                        LastEncodeMilliseconds = (DateTimeOffset.UtcNow - encodeStartedAt).TotalMilliseconds;
                        encodedInWindow += encodedFrames.Count;
                        if (encodedFrames.Count == 0)
                        {
                            _emptyEncodeCount++;
                            var emptyEncodeNow = DateTimeOffset.UtcNow;
                            if (emptyEncodeNow - _lastEmptyEncodeLogUtc >= TimeSpan.FromSeconds(2))
                            {
                                _lastEmptyEncodeLogUtc = emptyEncodeNow;
                                Debug.WriteLine($"[ScreenShare:H264] Encoder produced no output for this poll; consecutiveEmptyPolls={_emptyEncodeCount}.");
                            }

                            if (encoder.IsHardwareAccelerated && _emptyEncodeCount >= Math.Max(4, effectiveTargetFps / 3))
                            {
                                var refreshAge = emptyEncodeNow - _lastEncoderStarvationRefreshUtc;
                                if (refreshAge >= EncoderStarvationRefreshThrottle)
                                {
                                    _lastEncoderStarvationRefreshUtc = emptyEncodeNow;
                                    Debug.WriteLine(
                                        $"[ScreenShare:H264] GPU encoder produced no output for {_emptyEncodeCount} consecutive polls under load; refreshing NVENC instead of stopping the stream.");
                                    restartEncoderAfterFrame = true;
                                }
                            }
                        }
                        else
                        {
                            _emptyEncodeCount = 0;
                        }

                        if (encodedFrames.Count > 0)
                        {
                            if (encodedFrames.Any(encoded => encoded.IsKeyFrame))
                            {
                                encodedFramesSinceLastIdr = 0;
                                lastIdrOutputAtUtc = DateTimeOffset.UtcNow;
                                lastPeriodicIdrRequestAtUtc = DateTimeOffset.MinValue;
                            }
                            else
                            {
                                encodedFramesSinceLastIdr += encodedFrames.Count;
                                var recoveryInterval = encoder.RecoveryKeyFrameInterval > 0
                                    ? encoder.RecoveryKeyFrameInterval
                                    : effectiveTargetFps * 2;
                                var idrAge = lastIdrOutputAtUtc == DateTimeOffset.MinValue
                                    ? TimeSpan.Zero
                                    : DateTimeOffset.UtcNow - lastIdrOutputAtUtc;
                                var idrRequestAge = lastPeriodicIdrRequestAtUtc == DateTimeOffset.MinValue
                                    ? TimeSpan.MaxValue
                                    : DateTimeOffset.UtcNow - lastPeriodicIdrRequestAtUtc;

                                if (encodedFramesSinceLastIdr >= Math.Max(effectiveTargetFps * 2, recoveryInterval))
                                {
                                    Debug.WriteLine(
                                        $"[ScreenShare:H264] GPU encoder reached the Twitch keyframe boundary without an IDR ({encodedFramesSinceLastIdr} delta outputs); refreshing NVENC so the next output is a real IDR.");
                                    restartEncoderAfterFrame = true;
                                }
                                else if (lastPeriodicIdrRequestAtUtc != DateTimeOffset.MinValue &&
                                         idrRequestAge >= TimeSpan.FromSeconds(3) &&
                                         encodedFramesSinceLastIdr >= effectiveTargetFps * 2)
                                {
                                    Debug.WriteLine(
                                        $"[ScreenShare:H264] GPU encoder ignored forced IDR for {idrRequestAge.TotalMilliseconds:0}ms after {encodedFramesSinceLastIdr} delta outputs; refreshing encoder to unblock realtime recovery.");
                                    restartEncoderAfterFrame = true;
                                }
                                else if (idrAge >= TimeSpan.FromSeconds(2) && idrRequestAge >= TimeSpan.FromSeconds(2))
                                {
                                    lastPeriodicIdrRequestAtUtc = DateTimeOffset.UtcNow;
                                    Debug.WriteLine(
                                        $"[ScreenShare:H264] GPU encoder has produced no recovery IDR for {idrAge.TotalSeconds:0.0}s ({encodedFramesSinceLastIdr} delta outputs); keeping the stream steady and waiting for NVENC's configured keyframe interval.");
                                }
                            }

                        }

                        foreach (var encodedFrame in encodedFrames)
                        {
                            var frameEventTimestampMs = _streamStartedAtUtc.ToUnixTimeMilliseconds() +
                                Math.Max(0, (long)outputClock.Elapsed.TotalMilliseconds);
                            if (frameEventTimestampMs <= lastFrameEventTimestampMs)
                                frameEventTimestampMs = lastFrameEventTimestampMs + 1;
                            lastFrameEventTimestampMs = frameEventTimestampMs;

                            FrameReady?.Invoke(this, new NativeScreenFrameEventArgs(
                                encodedFrame.Data,
                                quality.Width,
                                quality.Height,
                                quality.Name,
                                frameEventTimestampMs,
                                "h264",
                                encodedFrame.IsKeyFrame,
                                latestPreview,
                                latestPreviewTimestampMs));
                        }

                        if (restartEncoderAfterFrame)
                        {
                            encoder.Dispose();
                            encoder = null;
                            encoderQuality = quality;
                            encoderBitrate = 0;
                            encoderFrameRate = 0;
                            _emptyEncodeCount = 0;
                            encodedFramesSinceLastIdr = 0;
                            lastIdrOutputAtUtc = DateTimeOffset.MinValue;
                            lastPeriodicIdrRequestAtUtc = DateTimeOffset.MinValue;
                        }

                        var now = DateTimeOffset.UtcNow;
                        var statsElapsed = now - statsWindowStartedAt;
                        if (statsElapsed >= TimeSpan.FromSeconds(1))
                        {
                            var seconds = Math.Max(0.001, statsElapsed.TotalSeconds);
                            CaptureFps = capturedInWindow / seconds;
                            EncodedFps = encodedInWindow / seconds;
                            capturedInWindow = 0;
                            encodedInWindow = 0;
                            statsWindowStartedAt = now;
                            var lowFreshCaptureFpsThreshold = Math.Max(1.0, effectiveTargetFps * LowFreshCaptureFpsThresholdScale);
                            var lowFreshCaptureEncodedFpsThreshold = Math.Max(1.0, effectiveTargetFps * LowFreshCaptureEncodedFpsThresholdScale);
                            var lowFreshCaptureWindow =
                                now - _streamStartedAtUtc >= LowFreshCaptureWarmup &&
                                CaptureFps > 0 &&
                                CaptureFps < lowFreshCaptureFpsThreshold &&
                                EncodedFps >= lowFreshCaptureEncodedFpsThreshold;
                            if (lowFreshCaptureWindow)
                            {
                                lowFreshCaptureWindows++;
                                Debug.WriteLine($"[ScreenShare:WGC] Fresh GPU capture under target for {lowFreshCaptureWindows} window(s): capture={CaptureFps:0.0}fps; encoded={EncodedFps:0.0}fps; keeping WGC alive to avoid restart stutter.");
                            }
                            else
                            {
                                lowFreshCaptureWindows = 0;
                            }

                            Debug.WriteLine($"[ScreenShare:H264:STATS] capture={CaptureFps:0.0}fps encoded={EncodedFps:0.0}fps captureMs={LastCaptureMilliseconds:0.0} encodeMs={LastEncodeMilliseconds:0.0} previewMs={LastPreviewMilliseconds:0.0} loopMs={LastLoopMilliseconds:0.0}");
                            if (EncodedFps < effectiveTargetFps * 0.85 || LastEncodeMilliseconds > 10 || LastLoopMilliseconds > 20)
                            {
                                Debug.WriteLine($"[ScreenShare:GPU:VIDEO] pressure capture={CaptureFps:0.0}fps encoded={EncodedFps:0.0}fps target={effectiveTargetFps}; captureMs={LastCaptureMilliseconds:0.0}; encodeMs={LastEncodeMilliseconds:0.0}; previewMs={LastPreviewMilliseconds:0.0}; loopMs={LastLoopMilliseconds:0.0}; encoder='{EncoderMode}'; input='{EncoderInputFormat}'; gpu='{EncoderGpuDeviceMode}'; bitrate={CurrentBitrate}; quality={CurrentQuality.Width}x{CurrentQuality.Height}; directGpuTexture={EnableDirectGpuTexturePath}; hardwareRequired={RequireHardwareEncoder}; dx12Required={RequireDirectX12CapturePath}.");
                                DiagnosticLogService.Flush();
                            }
                            UpdateAdaptiveState();
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[ScreenShare:H264] Capture or encode failed: {ex}");
                        IsRunning = false;
                        StreamingFailed?.Invoke(this, ex.Message);
                        return;
                    }

                    var elapsed = DateTimeOffset.UtcNow - frameStartedAt;
                    LastLoopMilliseconds = elapsed.TotalMilliseconds;
                    nextFrameDueTicks += frameBudgetTicks;
                    await WaitForNextOutputFrameAsync(outputClock, nextFrameDueTicks, frameBudgetTicks, cancellationToken);

                    if (outputClock.ElapsedTicks - nextFrameDueTicks > frameBudgetTicks * 2)
                    {
                        nextFrameDueTicks = outputClock.ElapsedTicks;
                        Debug.WriteLine($"[ScreenShare:H264] {effectiveTargetFps} FPS output clock resynced after encode/capture overrun.");
                    }
                }
            }
            finally
            {
                if (mmcssHandle != IntPtr.Zero)
                    NativeMethods.AvRevertMmThreadCharacteristics(mmcssHandle);

                encoder?.Dispose();
                latestReusableGpuFrame?.Dispose();
                _wgcCapture?.Dispose();
                _wgcCapture = null;
                _dxgiCapture?.Dispose();
                _dxgiCapture = null;
                EncoderMode = IsRunning ? EncoderMode : "Stopped";
                if (highResolutionTimerEnabled)
                    NativeMethods.timeEndPeriod(1);
            }
        }

        private static bool TryBeginHighResolutionTimer()
        {
            try
            {
                var result = NativeMethods.timeBeginPeriod(1);
                Debug.WriteLine($"[ScreenShare:H264] Realtime output clock enabled; high-resolution timer result={result}.");
                return result == 0;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ScreenShare:H264] High-resolution timer unavailable; using default timer. {ex.Message}");
                return false;
            }
        }

        private static IntPtr TryEnableStreamingThreadScheduling()
        {
            try
            {
                var handle = NativeMethods.AvSetMmThreadCharacteristics("Capture", out var taskIndex);
                if (handle == IntPtr.Zero)
                {
                    Debug.WriteLine($"[ScreenShare:H264] MMCSS capture scheduling unavailable; lastError={Marshal.GetLastWin32Error()}.");
                    return IntPtr.Zero;
                }

                NativeMethods.AvSetMmThreadPriority(handle, AvrtPriority.High);
                Debug.WriteLine($"[ScreenShare:H264] MMCSS capture scheduling enabled for realtime streaming; taskIndex={taskIndex}.");
                return handle;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ScreenShare:H264] MMCSS capture scheduling unavailable: {ex.Message}");
                return IntPtr.Zero;
            }
        }

        private int GetEffectiveTargetFps()
        {
            var targetFps = CurrentTargetFps;
            var quality = CurrentQuality;
            if (IsAdaptiveLatencyModeEnabled && quality.Height >= 1080 && DateTimeOffset.UtcNow < _receiverPressurePacingUntilUtc)
                return Math.Min(targetFps, ReceiverSafe1080pFps);

            return targetFps;
        }

        private static async Task WaitForNextOutputFrameAsync(
            Stopwatch clock,
            long dueTicks,
            long frameBudgetTicks,
            CancellationToken cancellationToken)
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var remainingTicks = dueTicks - clock.ElapsedTicks;
                if (remainingTicks <= 0)
                    return;

                var remainingMs = remainingTicks * 1000.0 / Stopwatch.Frequency;
                if (remainingMs > 2.0)
                {
                    await Task.Delay(Math.Max(1, (int)Math.Floor(remainingMs - 1.0)), cancellationToken);
                    continue;
                }

                if (remainingTicks > frameBudgetTicks / 24)
                    Thread.Sleep(0);
                else
                    Thread.SpinWait(64);
            }
        }

        private static bool IsReceiverRecoveryReason(string reason)
        {
            return reason.Contains("decoder", StringComparison.OrdinalIgnoreCase) ||
                reason.Contains("stalled", StringComparison.OrdinalIgnoreCase) ||
                reason.Contains("no RTP", StringComparison.OrdinalIgnoreCase) ||
                reason.Contains("keyframe", StringComparison.OrdinalIgnoreCase) ||
                reason.Contains("IDR", StringComparison.OrdinalIgnoreCase) ||
                reason.Contains("first visible frame", StringComparison.OrdinalIgnoreCase) ||
                reason.Contains("no frame", StringComparison.OrdinalIgnoreCase) ||
                reason.Contains("stale decoded", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsStartupKeyFrameRequest(string reason)
        {
            return reason.Contains("first visible frame", StringComparison.OrdinalIgnoreCase) ||
                reason.Contains("waiting for keyframe", StringComparison.OrdinalIgnoreCase) ||
                reason.Contains("waiting for IDR", StringComparison.OrdinalIgnoreCase) ||
                reason.Contains("needs an IDR to start", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsQualityChangeReason(string reason)
        {
            return reason.Contains("quality changed", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsReceiverPlaybackPressureReason(string reason)
        {
            return reason.Contains("receiver GPU playback keyframe requested after realtime queue pressure", StringComparison.OrdinalIgnoreCase) ||
                reason.Contains("receiver RTP backlog", StringComparison.OrdinalIgnoreCase);
        }

        private MediaFoundationH264Encoder CreateEncoderWithFallback(
            ScreenShareQualityProfile quality,
            int bitrate,
            int frameRate,
            bool preferHardware,
            bool requireHardware)
        {
            try
            {
                var encoder = new MediaFoundationH264Encoder(
                    quality.Width,
                    quality.Height,
                    bitrate,
                    preferHardware,
                    requireHardware,
                    EnableDirectGpuTexturePath ? _wgcCapture?.CaptureDevice : null,
                    frameRate);

                if (requireHardware && !IsNvidiaNvencEncoder(encoder))
                {
                    var encoderMode = encoder.EncoderMode;
                    encoder.Dispose();
                    throw new InvalidOperationException(
                        $"NVIDIA NVENC is required for Twitch streaming, but Windows selected '{encoderMode}'.");
                }

                return encoder;
            }
            catch (Exception ex) when (preferHardware && !requireHardware)
            {
                HardwareEncoderFallbackCount++;
                Debug.WriteLine($"[ScreenShare:H264] Hardware encoder startup failed, falling back to software MFT: {ex.Message}");
                return new MediaFoundationH264Encoder(quality.Width, quality.Height, bitrate, preferHardware: false, frameRate: frameRate);
            }
        }

        private static bool IsNvidiaNvencEncoder(MediaFoundationH264Encoder encoder)
        {
            var encoderMode = encoder.EncoderMode;
            return encoderMode.Contains("NVENC", StringComparison.OrdinalIgnoreCase) ||
                encoderMode.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase);
        }

        private CapturedGpuFrame? CaptureGpuFrameWithBestAvailablePath(MediaFoundationH264Encoder encoder, int waitMilliseconds)
        {
            if (!EnableDirectGpuTexturePath ||
                _wgcCapture?.IsAvailable != true ||
                !encoder.CanEncodeGpuTexture)
            {
                return null;
            }

            var immediateFrame = _wgcCapture.TryGetLatestGpuFrame();
            if (immediateFrame != null || waitMilliseconds <= 0)
                return immediateFrame;

            var waitStartedAt = Stopwatch.StartNew();
            while (waitStartedAt.ElapsedMilliseconds < waitMilliseconds)
            {
                var gpuFrame = _wgcCapture.TryGetLatestGpuFrame();
                if (gpuFrame != null)
                    return gpuFrame;

                Thread.Yield();
            }

            return null;
        }

        private void ApplyEncoderDetails(MediaFoundationH264Encoder encoder)
        {
            EncoderMode = encoder.EncoderMode;
            EncoderInputFormat = encoder.InputFormat;
            EncoderGpuDeviceMode = encoder.IsHardwareAccelerated
                ? encoder.GpuDeviceManagerMode
                : (RequireHardwareEncoder ? "GPU required but not active" : "Software H.264 fallback active");
            RecoveryKeyFrameInterval = encoder.RecoveryKeyFrameInterval;
            EncoderRealtimeModeEnabled = encoder.RealtimeModeEnabled;
            EncoderLowLatencyOutputEnabled = encoder.LowLatencyOutputEnabled;
        }

        private void WriteGpuStreamDiagnostics(string stage)
        {
            var quality = CurrentQuality;
            Debug.WriteLine($"[ScreenShare:GPU:VIDEO] {stage}; device={Environment.MachineName}; target={CurrentTargetFps}fps; quality={quality.Width}x{quality.Height}; bitrate={CurrentBitrate}; requestedPreset={_qualityPreset}; effectivePreset={_effectiveQualityPreset}; captureDx12Required={RequireDirectX12CapturePath}; hardwareEncoderRequired={RequireHardwareEncoder}; directGpuTexture={EnableDirectGpuTexturePath}; dropLateDuplicateFrames={DropLateDuplicateFrames}; encoder='{EncoderMode}'; input='{EncoderInputFormat}'; gpu='{EncoderGpuDeviceMode}'; adaptive='{AdaptiveState}'; log='{DiagnosticLogService.CurrentLogPath}'.");
        }

        private static Bitmap CaptureBitmap(ScreenShareQualityProfile quality)
        {
            var bounds = GetVirtualScreenBounds();
            var target = new Bitmap(quality.Width, quality.Height, PixelFormat.Format32bppArgb);
            var screenDc = IntPtr.Zero;

            using var graphics = Graphics.FromImage(target);
            var targetDc = graphics.GetHdc();
            try
            {
                screenDc = NativeMethods.GetDC(IntPtr.Zero);
                NativeMethods.SetStretchBltMode(targetDc, NativeMethods.COLORONCOLOR);
                var copied = NativeMethods.StretchBlt(
                    targetDc,
                    0,
                    0,
                    quality.Width,
                    quality.Height,
                    screenDc,
                    bounds.Left,
                    bounds.Top,
                    bounds.Width,
                    bounds.Height,
                    NativeMethods.SRCCOPY | NativeMethods.CAPTUREBLT);

                if (!copied)
                    throw new InvalidOperationException($"Screen capture failed: {Marshal.GetLastWin32Error()}");
            }
            finally
            {
                graphics.ReleaseHdc(targetDc);
                if (screenDc != IntPtr.Zero)
                    NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
            }

            return target;
        }

        private Bitmap? CaptureBitmapWithBestAvailablePath(ScreenShareQualityProfile quality)
        {
            if (_wgcCapture?.IsAvailable == true)
            {
                var waitStartedAt = Stopwatch.StartNew();
                while (waitStartedAt.ElapsedMilliseconds < 3)
                {
                    var wgcFrame = _wgcCapture.TryGetLatestFrame();
                    if (wgcFrame != null)
                        return wgcFrame;

                    Thread.Yield();
                }

                return null;
            }

            if (RequireDirectX12CapturePath)
                throw new InvalidOperationException("DirectX 12 Windows Graphics Capture is required, but it is not available.");

            _dxgiCapture ??= new DxgiScreenCaptureService();
            if (_dxgiCapture.IsAvailable)
            {
                var dxgiFrame = _dxgiCapture.TryCapture(quality);
                if (dxgiFrame != null)
                    return dxgiFrame;
            }

            return CaptureBitmap(quality);
        }

        private static int GetPreviewFrameInterval(ScreenShareQualityProfile quality)
        {
            return Math.Max(1, quality.PreviewFrameInterval);
        }

        internal static int GetLivePreviewFrameInterval()
        {
            return Math.Max(1, TargetFps / LivePreviewFps);
        }

        private static bool ShouldGeneratePreview(
            ScreenShareQualityProfile quality,
            byte[]? latestPreview,
            int captureFrameIndex,
            int previewFrameInterval)
        {
            if (latestPreview == null)
                return true;

            if (quality.Height >= 1440)
                return captureFrameIndex > 0 && captureFrameIndex % previewFrameInterval == 0;

            return captureFrameIndex % previewFrameInterval == 0;
        }

        private static byte[] EncodePreviewJpeg(Bitmap bitmap, ScreenShareQualityProfile quality, bool prioritizeStreamingPerformance)
        {
            var jpegQuality = prioritizeStreamingPerformance
                ? Math.Min(quality.PreviewJpegQuality, 76L)
                : quality.PreviewJpegQuality;
            var previewBitmap = bitmap;
            Bitmap? scaledPreview = null;
            var maxPreviewWidth = prioritizeStreamingPerformance
                ? Math.Min(quality.PreviewMaxWidth, quality.Height >= 1080 ? 960 : 854)
                : quality.PreviewMaxWidth;
            if (bitmap.Width > maxPreviewWidth)
            {
                var scale = (double)maxPreviewWidth / bitmap.Width;
                var previewWidth = Math.Max(1, (int)Math.Round(bitmap.Width * scale));
                var previewHeight = Math.Max(1, (int)Math.Round(bitmap.Height * scale));
                scaledPreview = new Bitmap(previewWidth, previewHeight, PixelFormat.Format32bppArgb);
                using var graphics = Graphics.FromImage(scaledPreview);
                graphics.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighSpeed;
                graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
                graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighSpeed;
                graphics.DrawImage(bitmap, 0, 0, previewWidth, previewHeight);
                previewBitmap = scaledPreview;
            }

            try
            {
                return EncodeJpeg(previewBitmap, jpegQuality);
            }
            finally
            {
                scaledPreview?.Dispose();
            }
        }

        private static byte[] EncodeJpeg(Bitmap bitmap, long quality)
        {
            using var ms = new MemoryStream();
            var encoder = ImageCodecInfo.GetImageEncoders().First(x => x.MimeType == "image/jpeg");
            using var parameters = new EncoderParameters(1);
            parameters.Param[0] = new EncoderParameter(Encoder.Quality, quality);
            bitmap.Save(ms, encoder, parameters);
            return ms.ToArray();
        }

        private static int GetBitrate(ScreenShareQualityProfile quality)
        {
            return quality.Bitrate;
        }

        private int GetConfiguredBitrate(ScreenShareQualityProfile quality)
        {
            return _bitrateOverride.HasValue
                ? Math.Max(1_000_000, _bitrateOverride.Value)
                : GetBitrate(quality);
        }

        private static int GetMinimumBitrate(ScreenShareQualityProfile quality)
        {
            return quality.MinimumBitrate;
        }

        private int GetAdaptiveBitrate(ScreenShareQualityProfile quality, int scalePercent)
        {
            var target = GetConfiguredBitrate(quality) * Math.Clamp(scalePercent, 45, 100) / 100;
            if (_bitrateOverride.HasValue)
                return Math.Max(1_000_000, target);

            return Math.Max(GetMinimumBitrate(quality), target);
        }

        private void UpdateAdaptiveState()
        {
            if (!IsAdaptiveLatencyModeEnabled ||
                DateTimeOffset.UtcNow - _streamStartedAtUtc < AdaptationWarmup)
            {
                return;
            }

            var targetFps = CurrentTargetFps;
            var frameBudgetMs = 1000.0 / targetFps;
            var fpsPressure = EncodedFps > 0 && EncodedFps < targetFps * AdaptiveFpsPressureThreshold;
            var encodePressure = LastEncodeMilliseconds > frameBudgetMs * 1.35;
            var loopPressure = LastLoopMilliseconds > frameBudgetMs * 1.75;
            var pressure = fpsPressure || encodePressure || loopPressure;

            if (pressure)
            {
                ApplyAdaptivePressure(
                    $"{targetFps}fps latency budget exceeded",
                    severe:
                        LastEncodeMilliseconds > frameBudgetMs * 1.8 ||
                        LastLoopMilliseconds > frameBudgetMs * 2.4 ||
                        (EncodedFps > 0 && EncodedFps < targetFps * AdaptiveSevereFpsPressureThreshold));
                return;
            }

            lock (_qualitySync)
            {
                _healthyWindows++;
                if (_healthyWindows < 8 || DateTimeOffset.UtcNow - _lastAdaptedAtUtc < TimeSpan.FromSeconds(4))
                    return;

                if (_bitrateScalePercent < 100)
                {
                    _bitrateScalePercent = Math.Min(100, _bitrateScalePercent + 10);
                    CurrentBitrate = GetAdaptiveBitrate(ScreenShareQualityProfile.FromPreset(_effectiveQualityPreset), _bitrateScalePercent);
                    AdaptiveState = $"Realtime recovering bitrate ({_bitrateScalePercent}%)";
                    _healthyWindows = 0;
                    _lastAdaptedAtUtc = DateTimeOffset.UtcNow;
                    return;
                }

                AdaptiveState = _effectiveQualityPreset == _qualityPreset
                    ? "Realtime stable"
                    : $"Realtime stable at {ScreenShareQualityProfile.FromPreset(_effectiveQualityPreset).Name}";
                _healthyWindows = 0;
                _lastAdaptedAtUtc = DateTimeOffset.UtcNow;
            }
        }

        private void ApplyAdaptivePressure(string reason, bool severe, bool receiverPressure = false)
        {
            if (!IsAdaptiveLatencyModeEnabled || !IsRunning)
                return;

            var queueRecoveryKeyFrame = false;
            lock (_qualitySync)
            {
                var now = DateTimeOffset.UtcNow;
                if (now - _streamStartedAtUtc < AdaptationWarmup ||
                    now - _lastAdaptedAtUtc < AdaptationCooldown)
                {
                    return;
                }

                _bitrateScalePercent = Math.Max(55, _bitrateScalePercent - (severe ? 15 : 10));

                if (severe && _effectiveQualityPreset != ScreenShareQualityPreset.Hd720p)
                {
                    _effectiveQualityPreset = ScreenShareQualityPreset.Hd720p;
                    _bitrateScalePercent = 100;
                    AutoDowngradeCount++;
                    CurrentBitrate = GetAdaptiveBitrate(ScreenShareQualityProfile.FromPreset(_effectiveQualityPreset), _bitrateScalePercent);
                    AdaptiveState = $"Realtime switched to 720p60: {reason}";
                    _healthyWindows = 0;
                    _lastAdaptedAtUtc = now;
                    Debug.WriteLine($"[ScreenShare:Adaptive] {AdaptiveState}; bitrate={CurrentBitrate}; congestion={CongestionSignals}");
                    return;
                }

                AdaptiveState = receiverPressure
                    ? $"Realtime receiver relief bitrate ({_bitrateScalePercent}%): {reason}"
                    : $"Realtime reduced bitrate ({_bitrateScalePercent}%): {reason}";

                CurrentBitrate = GetAdaptiveBitrate(ScreenShareQualityProfile.FromPreset(_effectiveQualityPreset), _bitrateScalePercent);
                _healthyWindows = 0;
                _lastAdaptedAtUtc = now;
                Debug.WriteLine($"[ScreenShare:Adaptive] {AdaptiveState}; bitrate={CurrentBitrate}; congestion={CongestionSignals}");
            }

            if (queueRecoveryKeyFrame)
                RequestRecoveryKeyFrame(reason);
        }

        private static ScreenShareQualityPreset GetLowerRealtimePreset(ScreenShareQualityPreset preset)
        {
            return preset switch
            {
                ScreenShareQualityPreset.UltraHd4K => ScreenShareQualityPreset.QuadHd2K,
                ScreenShareQualityPreset.QuadHd2K => ScreenShareQualityPreset.FullHd1080p,
                ScreenShareQualityPreset.FullHd1080p => ScreenShareQualityPreset.Hd720p,
                ScreenShareQualityPreset.Hd720p => ScreenShareQualityPreset.Performance540p,
                _ => preset
            };
        }

        private static Rectangle GetVirtualScreenBounds()
        {
            var left = NativeMethods.GetSystemMetrics(NativeMethods.SM_XVIRTUALSCREEN);
            var top = NativeMethods.GetSystemMetrics(NativeMethods.SM_YVIRTUALSCREEN);
            var width = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN);
            var height = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN);

            if (width <= 0 || height <= 0)
                throw new InvalidOperationException("Unable to read desktop bounds.");

            return new Rectangle(left, top, width, height);
        }

        private static Bitmap ScaleFrame(Bitmap source, ScreenShareQualityProfile quality)
        {
            if (source.Width == quality.Width && source.Height == quality.Height)
                return new Bitmap(source);

            var scale = Math.Min((double)quality.Width / source.Width, (double)quality.Height / source.Height);
            var width = Math.Max(1, (int)Math.Round(source.Width * scale));
            var height = Math.Max(1, (int)Math.Round(source.Height * scale));

            var scaled = new Bitmap(width, height);
            using var graphics = Graphics.FromImage(scaled);
            graphics.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighSpeed;
            graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Low;
            graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighSpeed;
            graphics.DrawImage(source, 0, 0, width, height);

            return scaled;
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync();
        }
    }

    public sealed class NativeScreenFrameEventArgs : EventArgs
    {
        public NativeScreenFrameEventArgs(
            byte[] frameData,
            int width,
            int height,
            string qualityName,
            long timestamp,
            string codec = "jpeg",
            bool isKeyFrame = true,
            byte[]? previewFrameData = null,
            long previewTimestamp = 0)
        {
            FrameData = frameData;
            Width = width;
            Height = height;
            QualityName = qualityName;
            Timestamp = timestamp;
            Codec = codec;
            IsKeyFrame = isKeyFrame;
            PreviewFrameData = previewFrameData ??
                (string.Equals(codec, "h264", StringComparison.OrdinalIgnoreCase) ? Array.Empty<byte>() : frameData);
            PreviewTimestamp = previewTimestamp > 0 ? previewTimestamp : timestamp;
        }

        public byte[] FrameData { get; }
        public int Width { get; }
        public int Height { get; }
        public string QualityName { get; }
        public long Timestamp { get; }
        public string Codec { get; }
        public bool IsKeyFrame { get; }
        public byte[] PreviewFrameData { get; }
        public long PreviewTimestamp { get; }
    }

    public enum ScreenShareQualityPreset
    {
        Performance540p,
        Hd720p,
        FullHd1080p,
        QuadHd2K,
        UltraHd4K
    }

    public enum NativeCaptureSourceMode
    {
        Desktop,
        GameOrWindow
    }

    public sealed class ScreenShareQualityProfile
    {
        private ScreenShareQualityProfile(
            ScreenShareQualityPreset preset,
            string name,
            int width,
            int height,
            int bitrate,
            int minimumBitrate,
            int previewFrameInterval,
            int previewMaxWidth,
            long previewJpegQuality)
        {
            Preset = preset;
            Name = name;
            Width = width;
            Height = height;
            Bitrate = bitrate;
            MinimumBitrate = minimumBitrate;
            PreviewFrameInterval = previewFrameInterval;
            PreviewMaxWidth = previewMaxWidth;
            PreviewJpegQuality = previewJpegQuality;
        }

        public ScreenShareQualityPreset Preset { get; }
        public string Name { get; }
        public int Width { get; }
        public int Height { get; }
        public int Bitrate { get; }
        public int MinimumBitrate { get; }
        public int PreviewFrameInterval { get; }
        public int PreviewMaxWidth { get; }
        public long PreviewJpegQuality { get; }

        public static ScreenShareQualityProfile FromPreset(ScreenShareQualityPreset preset)
        {
            return preset switch
            {
                ScreenShareQualityPreset.Performance540p => new ScreenShareQualityProfile(
                    preset,
                    "540p realtime",
                    960,
                    540,
                    bitrate: 3_500_000,
                    minimumBitrate: 2_500_000,
                    previewFrameInterval: NativeScreenShareStreamingService.GetLivePreviewFrameInterval(),
                    previewMaxWidth: 960,
                    previewJpegQuality: NativeScreenShareStreamingService.JpegQuality),
                ScreenShareQualityPreset.Hd720p => new ScreenShareQualityProfile(
                    preset,
                    "720p",
                    1280,
                    720,
                    bitrate: 8_000_000,
                    minimumBitrate: 5_500_000,
                    previewFrameInterval: NativeScreenShareStreamingService.GetLivePreviewFrameInterval(),
                    previewMaxWidth: 1280,
                    previewJpegQuality: NativeScreenShareStreamingService.JpegQuality),
                ScreenShareQualityPreset.FullHd1080p => new ScreenShareQualityProfile(
                    preset,
                    "1080p",
                    1920,
                    1080,
                    bitrate: 12_000_000,
                    minimumBitrate: 8_000_000,
                    previewFrameInterval: NativeScreenShareStreamingService.GetLivePreviewFrameInterval(),
                    previewMaxWidth: 1920,
                    previewJpegQuality: NativeScreenShareStreamingService.JpegQuality),
                ScreenShareQualityPreset.QuadHd2K => new ScreenShareQualityProfile(
                    preset,
                    "1440p",
                    2560,
                    1440,
                    bitrate: 22_000_000,
                    minimumBitrate: 14_000_000,
                    previewFrameInterval: NativeScreenShareStreamingService.TargetFps * 6,
                    previewMaxWidth: 1280,
                    previewJpegQuality: 68L),
                ScreenShareQualityPreset.UltraHd4K => new ScreenShareQualityProfile(
                    preset,
                    "4K",
                    3840,
                    2160,
                    bitrate: 36_000_000,
                    minimumBitrate: 24_000_000,
                    previewFrameInterval: NativeScreenShareStreamingService.TargetFps * 10,
                    previewMaxWidth: 1280,
                    previewJpegQuality: 68L),
                _ => FromPreset(ScreenShareQualityPreset.Hd720p)
            };
        }
    }
}
