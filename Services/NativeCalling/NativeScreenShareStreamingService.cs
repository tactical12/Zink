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
        public const long JpegQuality = 62L;
        private const int ReceiverSafe1080pFps = 24;
        internal const bool EnableDirectGpuTexturePath = false;
        private static readonly TimeSpan AdaptationWarmup = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan AdaptationCooldown = TimeSpan.FromSeconds(1);
        private static readonly TimeSpan ReceiverPressurePacingWindow = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan StartupRecoveryKeyFrameThrottle = TimeSpan.FromMilliseconds(2200);
        private static readonly TimeSpan RecoveryKeyFrameThrottle = TimeSpan.FromMilliseconds(900);
        private const int ReceiverPressureSignalsBeforeResolutionDrop = 2;

        private readonly object _qualitySync = new();
        private CancellationTokenSource? _cts;
        private Task? _captureTask;
        private ScreenShareQualityPreset _qualityPreset = ScreenShareQualityPreset.Hd720p;
        private ScreenShareQualityPreset _effectiveQualityPreset = ScreenShareQualityPreset.Hd720p;
        private int _bitrateScalePercent = 100;
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
        private int _healthyWindows;
        private int _pendingRecoveryKeyFrame;
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
        public int RecoveryKeyFrameInterval { get; private set; }
        public bool EncoderRealtimeModeEnabled { get; private set; }
        public bool EncoderLowLatencyOutputEnabled { get; private set; }
        public int RecoveryKeyFrameRequests { get; private set; }
        public int HardwareEncoderFallbackCount { get; private set; }

        private static bool IsArm64Process =>
            RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ||
            RuntimeInformation.OSArchitecture == Architecture.Arm64;

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
                CurrentBitrate = ScreenShareQualityProfile.FromPreset(_effectiveQualityPreset).Bitrate;
                AdaptiveState = $"Locked {ScreenShareQualityProfile.FromPreset(_effectiveQualityPreset).Name} @ {TargetFps} FPS";
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
                Interlocked.Exchange(ref _pendingRecoveryKeyFrame, 0);
            }

            if (IsArm64Process)
            {
                DiagnosticLogService.WriteLine(
                    "[ScreenShare:UI] ARM64 sender detected; skipping WGC/SharpDX startup capture path and using the crash-safe bitmap capture path for this device.");
                EncoderGpuDeviceMode = RequireHardwareEncoder
                    ? "ARM64 sender safety mode; GPU encoder may be used after bitmap capture"
                    : "ARM64 sender safety mode; software encoder fallback allowed";
            }
            else
            {
                DiagnosticLogService.WriteLine("[ScreenShare:UI] Starting Windows Graphics Capture source.");
                DiagnosticLogService.Flush();
                _wgcCapture = new WindowsGraphicsCaptureScreenSource();
                var wgcStarted = await _wgcCapture.StartAsync();
                DiagnosticLogService.WriteLine($"[ScreenShare:UI] Windows Graphics Capture source start result: {wgcStarted}; available={_wgcCapture.IsAvailable}.");
            }

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
                CurrentBitrate = ScreenShareQualityProfile.FromPreset(_effectiveQualityPreset).Bitrate;
                AdaptiveState = $"Locked {ScreenShareQualityProfile.FromPreset(preset).Name} @ {TargetFps} FPS";
                _healthyWindows = 0;
                _receiverPressureSignals = 0;
                if (IsRunning)
                    RequestRecoveryKeyFrame($"screen-share quality changed to {ScreenShareQualityProfile.FromPreset(preset).Name}");
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
                : $"Locked {CurrentQuality.Name} @ {TargetFps} FPS";
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
            try
            {
                Thread.CurrentThread.Priority = ThreadPriority.AboveNormal;
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
            byte[]? latestPreview = null;
            var previewFrameInterval = GetPreviewFrameInterval(CurrentQuality);
            var captureFrameIndex = 0;
            var statsWindowStartedAt = DateTimeOffset.UtcNow;
            var capturedInWindow = 0;
            var encodedInWindow = 0;
            var encodedFramesSinceLastIdr = 0;
            var lastIdrOutputAtUtc = DateTimeOffset.MinValue;
            var lastPeriodicIdrRequestAtUtc = DateTimeOffset.MinValue;
            var preferHardwareEncoder = true;
            Bitmap? latestReusableFrame = null;

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
                        var recoveryKeyFrameRequested = Interlocked.Exchange(ref _pendingRecoveryKeyFrame, 0) == 1;

                        if (encoderResolutionChanged || encoderBitrateChanged)
                        {
                            if (encoderBitrateChanged)
                                Debug.WriteLine($"[ScreenShare:H264] Bitrate target changed {encoderBitrate} -> {bitrate}; recreating encoder so NVENC applies the realtime rate.");

                            encoder?.Dispose();
                            encoder = null;
                            encoderQuality = quality;
                            encoderBitrate = bitrate;
                            latestReusableFrame?.Dispose();
                            latestReusableFrame = null;
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
                                preferHardware: preferHardwareEncoder,
                                requireHardware: RequireHardwareEncoder);
                            encoderBitrate = bitrate;
                            encoder.ForceNextKeyFrame();
                            Debug.WriteLine($"[ScreenShare:H264] Encoder created for {quality.Width}x{quality.Height}; forcing first GPU output to IDR.");
                            ApplyEncoderDetails(encoder);
                            WriteGpuStreamDiagnostics("encoder-created");
                        }

                        previewFrameInterval = GetPreviewFrameInterval(quality);

                        var captureStartedAt = DateTimeOffset.UtcNow;
                        using var gpuFrame = CaptureGpuFrameWithBestAvailablePath(encoder);
                        var capturedFrame = gpuFrame == null
                            ? CaptureBitmapWithBestAvailablePath(quality)
                            : null;
                        if (capturedFrame != null)
                        {
                            latestReusableFrame?.Dispose();
                            latestReusableFrame = capturedFrame;
                        }

                        var frame = capturedFrame ?? latestReusableFrame;
                        LastCaptureMilliseconds = (DateTimeOffset.UtcNow - captureStartedAt).TotalMilliseconds;
                        if (gpuFrame == null && frame == null)
                        {
                            LastLoopMilliseconds = (DateTimeOffset.UtcNow - frameStartedAt).TotalMilliseconds;
                            nextFrameDueTicks += frameBudgetTicks;
                            await WaitForNextOutputFrameAsync(outputClock, nextFrameDueTicks, frameBudgetTicks, cancellationToken);
                            continue;
                        }

                        capturedInWindow++;

                        if (frame != null && ShouldGeneratePreview(quality, latestPreview, captureFrameIndex, previewFrameInterval))
                        {
                            var previewStartedAt = DateTimeOffset.UtcNow;
                            latestPreview = EncodePreviewJpeg(frame, quality);
                            LastPreviewMilliseconds = (DateTimeOffset.UtcNow - previewStartedAt).TotalMilliseconds;
                        }

                        captureFrameIndex++;

                        var encodeStartedAt = DateTimeOffset.UtcNow;
                        IReadOnlyList<H264EncodedFrame> encodedFrames;
                        var restartEncoderAfterFrame = false;
                        try
                        {
                            encodedFrames = gpuFrame != null
                                ? encoder.EncodeGpuBgraTexture(gpuFrame.Texture, gpuFrame.Width, gpuFrame.Height)
                                : encoder.Encode(frame!);
                        }
                        catch (Exception ex) when (encoder.IsHardwareAccelerated)
                        {
                            if (RequireHardwareEncoder)
                            {
                                throw new InvalidOperationException(
                                    "GPU hardware H.264 encoder failed during encode.",
                                    ex);
                            }

                            Debug.WriteLine($"[ScreenShare:H264] Hardware encoder failed during encode, falling back to software MFT: {ex.Message}");
                            HardwareEncoderFallbackCount++;
                            preferHardwareEncoder = false;
                            encoder.Dispose();
                            encoder = new MediaFoundationH264Encoder(quality.Width, quality.Height, bitrate, preferHardware: false);
                            encoderBitrate = bitrate;
                            ApplyEncoderDetails(encoder);
                            encodedFramesSinceLastIdr = 0;
                            lastIdrOutputAtUtc = DateTimeOffset.MinValue;
                            lastPeriodicIdrRequestAtUtc = DateTimeOffset.MinValue;
                            encodedFrames = frame != null
                                ? encoder.Encode(frame)
                                : Array.Empty<H264EncodedFrame>();
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

                            if (encoder.IsHardwareAccelerated && _emptyEncodeCount >= Math.Max(8, TargetFps / 2))
                            {
                                if (RequireHardwareEncoder)
                                {
                                    throw new InvalidOperationException("GPU hardware H.264 encoder produced no output.");
                                }

                                Debug.WriteLine("[ScreenShare:H264] Hardware encoder produced no output, falling back to software MFT.");
                            HardwareEncoderFallbackCount++;
                            preferHardwareEncoder = false;
                            encoder.Dispose();
                            encoder = new MediaFoundationH264Encoder(quality.Width, quality.Height, bitrate, preferHardware: false);
                            encoderBitrate = bitrate;
                            ApplyEncoderDetails(encoder);
                                _emptyEncodeCount = 0;
                                encodedFramesSinceLastIdr = 0;
                                lastIdrOutputAtUtc = DateTimeOffset.MinValue;
                                lastPeriodicIdrRequestAtUtc = DateTimeOffset.MinValue;
                                encodedFrames = frame != null
                                    ? encoder.Encode(frame)
                                    : Array.Empty<H264EncodedFrame>();
                                encodedInWindow += encodedFrames.Count;
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
                                    : TargetFps * 2;
                                var idrAge = lastIdrOutputAtUtc == DateTimeOffset.MinValue
                                    ? TimeSpan.Zero
                                    : DateTimeOffset.UtcNow - lastIdrOutputAtUtc;
                                var idrRequestAge = lastPeriodicIdrRequestAtUtc == DateTimeOffset.MinValue
                                    ? TimeSpan.MaxValue
                                    : DateTimeOffset.UtcNow - lastPeriodicIdrRequestAtUtc;

                                if (encodedFramesSinceLastIdr >= recoveryInterval ||
                                    (idrAge >= TimeSpan.FromSeconds(2) && idrRequestAge >= TimeSpan.FromSeconds(1)))
                                {
                                    Debug.WriteLine(
                                        $"[ScreenShare:H264] GPU encoder has produced no recovery IDR for {idrAge.TotalSeconds:0.0}s ({encodedFramesSinceLastIdr} delta outputs); requesting an IDR without restarting NVENC.");
                                    encoder.ForceNextKeyFrame();
                                    lastPeriodicIdrRequestAtUtc = DateTimeOffset.UtcNow;
                                    encodedFramesSinceLastIdr = 0;
                                }
                                else if (lastPeriodicIdrRequestAtUtc != DateTimeOffset.MinValue &&
                                         idrRequestAge >= TimeSpan.FromMilliseconds(1_500) &&
                                         encodedFramesSinceLastIdr >= (TargetFps * 3 / 4))
                                {
                                    Debug.WriteLine(
                                        $"[ScreenShare:H264] GPU encoder ignored forced IDR for {idrRequestAge.TotalMilliseconds:0}ms after {encodedFramesSinceLastIdr} delta outputs; refreshing encoder to unblock realtime recovery.");
                                    restartEncoderAfterFrame = true;
                                }
                            }
                        }

                        foreach (var encodedFrame in encodedFrames)
                        {
                            FrameReady?.Invoke(this, new NativeScreenFrameEventArgs(
                                encodedFrame.Data,
                                quality.Width,
                                quality.Height,
                                quality.Name,
                                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                                "h264",
                                encodedFrame.IsKeyFrame,
                                latestPreview));
                        }

                        if (restartEncoderAfterFrame)
                        {
                            encoder.Dispose();
                            encoder = null;
                            encoderQuality = quality;
                            encoderBitrate = 0;
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
                        Debug.WriteLine("[ScreenShare:H264] 60 FPS output clock resynced after encode/capture overrun.");
                    }
                }
            }
            finally
            {
                encoder?.Dispose();
                latestReusableFrame?.Dispose();
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
                Debug.WriteLine($"[ScreenShare:H264] 60 FPS output clock enabled; high-resolution timer result={result}.");
                return result == 0;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ScreenShare:H264] High-resolution timer unavailable; using default timer. {ex.Message}");
                return false;
            }
        }

        private int GetEffectiveTargetFps()
        {
            var quality = CurrentQuality;
            if (IsAdaptiveLatencyModeEnabled && quality.Height >= 1080 && DateTimeOffset.UtcNow < _receiverPressurePacingUntilUtc)
                return ReceiverSafe1080pFps;

            return TargetFps;
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
            bool preferHardware,
            bool requireHardware)
        {
            try
            {
                return new MediaFoundationH264Encoder(
                    quality.Width,
                    quality.Height,
                    bitrate,
                    preferHardware,
                    requireHardware,
                    EnableDirectGpuTexturePath ? _wgcCapture?.CaptureDevice : null);
            }
            catch (Exception ex) when (preferHardware && !requireHardware)
            {
                HardwareEncoderFallbackCount++;
                Debug.WriteLine($"[ScreenShare:H264] Hardware encoder startup failed, falling back to software MFT: {ex.Message}");
                return new MediaFoundationH264Encoder(quality.Width, quality.Height, bitrate, preferHardware: false);
            }
        }

        private CapturedGpuFrame? CaptureGpuFrameWithBestAvailablePath(MediaFoundationH264Encoder encoder)
        {
            if (!EnableDirectGpuTexturePath ||
                IsArm64Process ||
                _wgcCapture?.IsAvailable != true ||
                !encoder.CanEncodeGpuTexture)
            {
                return null;
            }

            var waitStartedAt = Stopwatch.StartNew();
            while (waitStartedAt.ElapsedMilliseconds < 15)
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
            Debug.WriteLine($"[ScreenShare:GPU:VIDEO] {stage}; device={Environment.MachineName}; target={TargetFps}fps; quality={quality.Width}x{quality.Height}; bitrate={CurrentBitrate}; requestedPreset={_qualityPreset}; effectivePreset={_effectiveQualityPreset}; captureDx12Required={RequireDirectX12CapturePath}; hardwareEncoderRequired={RequireHardwareEncoder}; directGpuTexture={EnableDirectGpuTexturePath}; encoder='{EncoderMode}'; input='{EncoderInputFormat}'; gpu='{EncoderGpuDeviceMode}'; adaptive='{AdaptiveState}'; log='{DiagnosticLogService.CurrentLogPath}'.");
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

            if (IsArm64Process)
                return CaptureBitmap(quality);

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

        private static byte[] EncodePreviewJpeg(Bitmap bitmap, ScreenShareQualityProfile quality)
        {
            var jpegQuality = quality.PreviewJpegQuality;
            var previewBitmap = bitmap;
            Bitmap? scaledPreview = null;
            var maxPreviewWidth = quality.PreviewMaxWidth;
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

        private static int GetMinimumBitrate(ScreenShareQualityProfile quality)
        {
            return quality.MinimumBitrate;
        }

        private static int GetAdaptiveBitrate(ScreenShareQualityProfile quality, int scalePercent)
        {
            var target = GetBitrate(quality) * Math.Clamp(scalePercent, 45, 100) / 100;
            return Math.Max(GetMinimumBitrate(quality), target);
        }

        private void UpdateAdaptiveState()
        {
            if (!IsAdaptiveLatencyModeEnabled ||
                DateTimeOffset.UtcNow - _streamStartedAtUtc < AdaptationWarmup)
            {
                return;
            }

            var frameBudgetMs = 1000.0 / TargetFps;
            var fpsPressure = EncodedFps > 0 && EncodedFps < TargetFps * 0.70;
            var encodePressure = LastEncodeMilliseconds > frameBudgetMs * 1.35;
            var loopPressure = LastLoopMilliseconds > frameBudgetMs * 1.75;
            var pressure = fpsPressure || encodePressure || loopPressure;

            if (pressure)
            {
                ApplyAdaptivePressure(
                    "60fps latency budget exceeded",
                    severe:
                        LastEncodeMilliseconds > frameBudgetMs * 1.8 ||
                        LastLoopMilliseconds > frameBudgetMs * 2.4 ||
                        (EncodedFps > 0 && EncodedFps < TargetFps * 0.55));
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
            byte[]? previewFrameData = null)
        {
            FrameData = frameData;
            Width = width;
            Height = height;
            QualityName = qualityName;
            Timestamp = timestamp;
            Codec = codec;
            IsKeyFrame = isKeyFrame;
            PreviewFrameData = previewFrameData ?? frameData;
        }

        public byte[] FrameData { get; }
        public int Width { get; }
        public int Height { get; }
        public string QualityName { get; }
        public long Timestamp { get; }
        public string Codec { get; }
        public bool IsKeyFrame { get; }
        public byte[] PreviewFrameData { get; }
    }

    public enum ScreenShareQualityPreset
    {
        Performance540p,
        Hd720p,
        FullHd1080p,
        QuadHd2K,
        UltraHd4K
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
                    previewFrameInterval: NativeScreenShareStreamingService.TargetFps,
                    previewMaxWidth: 960,
                    previewJpegQuality: NativeScreenShareStreamingService.JpegQuality),
                ScreenShareQualityPreset.Hd720p => new ScreenShareQualityProfile(
                    preset,
                    "720p",
                    1280,
                    720,
                    bitrate: 6_000_000,
                    minimumBitrate: 4_500_000,
                    previewFrameInterval: NativeScreenShareStreamingService.TargetFps,
                    previewMaxWidth: 1280,
                    previewJpegQuality: 66L),
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
                _ => new ScreenShareQualityProfile(
                    ScreenShareQualityPreset.FullHd1080p,
                    "1080p",
                    1920,
                    1080,
                    bitrate: 14_000_000,
                    minimumBitrate: 9_000_000,
                    previewFrameInterval: NativeScreenShareStreamingService.TargetFps,
                    previewMaxWidth: 1920,
                    previewJpegQuality: 70L)
            };
        }
    }
}
