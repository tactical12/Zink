using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Media.Playback;
using Windows.Storage.Streams;
using Zink.Services;
using Zink.Services.NativeCalling;
using Zink.Services.NativeCalling.Models;
using Zink.Services.Social;

namespace Zink.Pages.Social
{
    public sealed partial class CallPage : Page
    {
        private long _targetUserId;
        private bool _isScreenShare;

        private bool _isMicEnabled = true;
        private bool _isMuted = false;
        private bool _isDeafened = false;
        private bool _isSharingScreen = false;
        private string _selectedAudioOutput = "Default";
        private string _selectedMicrophone = "Default microphone";
        private bool _screenShareHooksAttached;
        private bool _isRemoteVideoVisible;
        private bool _isRemoteScreenShareLoading;
        private bool _isRemoteScreenShareWatchPromptVisible;
        private bool _isFullscreen;
        private bool _isLocalPreviewHidden;
        private bool _isScreenShareSoundEnabled = true;
        private bool _screenShareToggleInProgress;
        private bool _screenShareFeedbackDialogOpen;
        private bool _callFeedbackDialogOpen;
        private string _lastCallFeedbackPromptKey = "";
        private long _localUserId;
        private const int MaxCallParticipants = 10;
        private readonly HashSet<long> _callParticipantIds = new();
        private readonly HashSet<long> _leftParticipantIds = new();
        private readonly HashSet<long> _pendingRemoteScreenShareUserIds = new();
        private readonly HashSet<long> _acceptedRemoteScreenShareUserIds = new();
        private string _remoteScreenShareWatchPromptSignature = "";
        private readonly Dictionary<long, string> _participantDisplayNames = new();
        private readonly Dictionary<long, NativePeerConnectionService> _screenSharePeers = new();
        private MediaFoundationH264Decoder? _h264Decoder;
        private int _screenShareFrameCounter;
        private double _screenShareObservedFps;
        private int _screenShareLastWidth;
        private int _screenShareLastHeight;
        private long _screenShareLastBytes;
        private string _screenShareLastQuality = NativeScreenShareStreamingService.Instance.CurrentQuality.Name;
        private string _selectedScreenShareSoundDevice = SystemAudioShareService.Instance.SelectedDeviceName;
        private DateTimeOffset _screenShareFpsWindowStartedUtc = DateTimeOffset.UtcNow;
        private DateTimeOffset? _screenShareLastFrameAtUtc;
        private int _screenShareTransmitCounter;
        private int _screenShareDroppedFrames;
        private int _screenShareDroppedReceiveFrames;
        private DateTimeOffset _localPreviewLastRenderedUtc = DateTimeOffset.MinValue;
        private DateTimeOffset _localStagePreviewLastRenderedUtc = DateTimeOffset.MinValue;
        private DateTimeOffset _fallbackFrameLastSentUtc = DateTimeOffset.MinValue;
        private DateTimeOffset _previewFallbackLastSentUtc = DateTimeOffset.MinValue;
        private DateTimeOffset? _screenShareStartedAtUtc;
        private int _screenShareRtpFrames;
        private int _screenShareFallbackFrames;
        private int _screenSharePreviewFallbackFrames;
        private int _screenShareRtpMetadataFrames;
        private int _screenShareSoundPacketsSent;
        private DateTimeOffset? _screenShareSoundLastPacketAtUtc;
        private DateTimeOffset _lastRtpMetadataSentUtc = DateTimeOffset.MinValue;
        private int _lastRtpMetadataWidth;
        private int _lastRtpMetadataHeight;
        private int _lastRemoteBitstreamWidth;
        private int _lastRemoteBitstreamHeight;
        private long _remoteScreenShareSenderId;
        private DateTimeOffset _lastQosSentUtc = DateTimeOffset.MinValue;
        private int _lastQosDroppedReceiveFrames;
        private DateTimeOffset _lastScreenShareSendSummaryUtc = DateTimeOffset.MinValue;
        private DateTimeOffset? _remoteScreenLastReceivedAtUtc;
        private DateTimeOffset? _remoteScreenLastRenderedAtUtc;
        private DateTimeOffset _lastRemoteDecoderResetAtUtc = DateTimeOffset.MinValue;
        private DateTimeOffset _lastRemoteReceiveStallRecoveryUtc = DateTimeOffset.MinValue;
        private DateTimeOffset _lastRemoteBitstreamResolutionAtUtc = DateTimeOffset.MinValue;
        private DateTimeOffset _remoteFallbackLastReceivedAtUtc = DateTimeOffset.MinValue;
        private DateTimeOffset _remoteFallbackFirstReceivedAtUtc = DateTimeOffset.MinValue;
        private long _remoteRtpFrameCount;
        private long _remoteFallbackFrameCount;
        private long _remotePreviewFrameCount;
        private long _remoteRenderedFrameCount;
        private DateTimeOffset _remoteReceiveFpsWindowStartedUtc = DateTimeOffset.UtcNow;
        private DateTimeOffset _remotePlaybackFpsWindowStartedUtc = DateTimeOffset.UtcNow;
        private int _remoteReceiveFramesInWindow;
        private int _remotePlaybackFramesInWindow;
        private double _remoteReceiveObservedFps;
        private double _remotePlaybackObservedFps;
        private int _remoteGpuPlaybackQueuePeak;
        private long _remoteGpuPlaybackQueueTotal;
        private long _remoteGpuPlaybackQueueSamples;
        private double _remoteGpuPlaybackAverageQueue;
        private DateTimeOffset _lastRemoteRealtimeStatsLogUtc = DateTimeOffset.MinValue;
        private int _remoteDecoderResetCount;
        private int _remoteEmptyDecodeCount;
        private int _remoteDecodeFailureCount;
        private bool _remoteH264WaitingForKeyFrame = true;
        private int _remoteH264FramesDroppedWaitingForKeyFrame;
        private DateTimeOffset _lastLocalFrameFlowLogUtc = DateTimeOffset.MinValue;
        private DateTimeOffset _lastRemoteReceiveFlowLogUtc = DateTimeOffset.MinValue;
        private DateTimeOffset _lastRemoteQueueFlowLogUtc = DateTimeOffset.MinValue;
        private DateTimeOffset _lastRemoteDecodeFlowLogUtc = DateTimeOffset.MinValue;
        private DateTimeOffset _lastRemoteRenderFlowLogUtc = DateTimeOffset.MinValue;
        private DateTimeOffset _lastRemoteRenderCoalescedLogUtc = DateTimeOffset.MinValue;
        private DateTimeOffset _lastRemotePreviewFlowLogUtc = DateTimeOffset.MinValue;
        private DateTimeOffset _lastRemoteWatchPreviewRenderedUtc = DateTimeOffset.MinValue;
        private DateTimeOffset _lastRemoteFallbackHoldLogUtc = DateTimeOffset.MinValue;
        private DateTimeOffset _lastRemoteKeyFrameRequestAtUtc = DateTimeOffset.MinValue;
        private DateTimeOffset _lastRemoteBacklogTrimLogUtc = DateTimeOffset.MinValue;
        private DateTimeOffset _lastRemoteBacklogJumpLogUtc = DateTimeOffset.MinValue;
        private int _remoteBacklogTrimmedSinceLastLog;
        private int _remoteBacklogJumpedSinceLastLog;
        private long _lastScreenShareMetadataTimestamp;
        private long _remoteDecodeGeneration;
        private uint _lastLocalEncodedFingerprint;
        private int _localEncodedFingerprintRepeat;
        private uint _lastLocalPreviewFingerprint;
        private int _localPreviewFingerprintRepeat;
        private uint _lastRemoteEncodedFingerprint;
        private int _remoteEncodedFingerprintRepeat;
        private uint _lastRemoteDecodedFingerprint;
        private int _remoteDecodedFingerprintRepeat;
        private uint _lastRemoteRenderedFingerprint;
        private int _remoteRenderedFingerprintRepeat;
        private uint _lastRemotePreviewFingerprint;
        private int _remotePreviewFingerprintRepeat;
        private const int FallbackMaxFps = 3;
        private const int PreviewFallbackMaxFps = 8;
        private const int WebSocketFallbackMaxDeltaBytes = 24 * 1024;
        private static readonly TimeSpan WebSocketFallbackWarmupDuration = TimeSpan.FromSeconds(1);
        private static readonly TimeSpan WebSocketFallbackGpuStartDelay = TimeSpan.FromMilliseconds(1600);
        private const int MaxRemoteH264DecodeQueue = 12;
        private const int RemoteGpuBacklogKeyFrameRequestQueue = 18;
        private const int MaxRemoteGpuPlaybackQueue = 36;
        private const int RealtimeBacklogRetainFrames = 2;
        private static readonly TimeSpan RemoteGpuFrameDuration = TimeSpan.FromTicks(TimeSpan.TicksPerSecond / NativeScreenShareStreamingService.TargetFps);
        private static readonly TimeSpan RemoteGpuMinFrameDuration = TimeSpan.FromTicks(TimeSpan.TicksPerSecond / 75);
        private static readonly TimeSpan RemoteGpuMaxFrameDuration = TimeSpan.FromTicks(TimeSpan.TicksPerSecond / 24);
        private static readonly TimeSpan RemoteGpuSampleWaitTimeout = TimeSpan.FromSeconds(6);
        private static readonly TimeSpan RtpMetadataInterval = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan QosFeedbackInterval = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan RemoteFrameReceiveStallThreshold = TimeSpan.FromSeconds(3);
        private static readonly TimeSpan RemoteRenderStallThreshold = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan RemoteDecoderResetCooldown = TimeSpan.FromSeconds(1);
        private static readonly TimeSpan RemoteFallbackRtpSuppressWindow = TimeSpan.FromSeconds(1);
        private static readonly TimeSpan ScreenShareSendTimeout = TimeSpan.FromMilliseconds(120);
        private static readonly TimeSpan FullscreenChromeHideDelay = TimeSpan.FromSeconds(4);
        private readonly SemaphoreSlim _screenShareSendLock = new(1, 1);
        private readonly SemaphoreSlim _screenShareDecodeLock = new(1, 1);
        private readonly SemaphoreSlim _screenShareLifecycleLock = new(1, 1);
        private readonly SemaphoreSlim _screenShareAutoReportLock = new(1, 1);
        private readonly object _screenSharePeerRestartSync = new();
        private readonly object _rtpFrameSync = new();
        private readonly object _remoteDecoderSync = new();
        private readonly Queue<PendingRemoteH264Frame> _pendingRemoteH264Frames = new();
        private readonly Dictionary<long, DateTimeOffset> _screenSharePeerRestartLastAttemptUtc = new();
        private long _pendingRtpFrameSequence;
        private int _rtpDecodePumpRunning;
        private int _remoteRenderQueued;
        private readonly object _remoteRenderSync = new();
        private byte[]? _pendingRemoteRenderFrame;
        private int _pendingRemoteRenderWidth;
        private int _pendingRemoteRenderHeight;
        private DateTimeOffset _pendingRemoteRenderQueuedAtUtc;
        private WriteableBitmap? _remoteScreenBitmap;
        private int _remoteScreenBitmapWidth;
        private int _remoteScreenBitmapHeight;
        private MediaFoundationH264Decoder? _rtpH264Decoder;
        private int _rtpDecoderWidth;
        private int _rtpDecoderHeight;
        private readonly object _remoteGpuPlaybackSync = new();
        private readonly Queue<PendingRemoteH264Frame> _pendingRemoteGpuPlaybackFrames = new();
        private MediaPlayer? _remoteGpuMediaPlayer;
        private MediaStreamSource? _remoteGpuMediaStreamSource;
        private bool _remoteGpuPlaybackEnabled;
        private bool _remoteGpuPlaybackStarting;
        private bool _remoteGpuPlaybackStopping;
        private int _remoteGpuPlaybackWidth;
        private int _remoteGpuPlaybackHeight;
        private long _remoteGpuPlaybackSequence;
        private TimeSpan _remoteGpuPlaybackSampleTime;
        private TimeSpan _remoteGpuEstimatedFrameDuration = RemoteGpuFrameDuration;
        private DateTimeOffset? _remoteGpuLastQueuedAtUtc;
        private bool _remoteGpuWaitingForKeyFrame;
        private bool _remoteGpuBacklogKeyFrameRequested;
        private bool _remoteGpuFirstSampleSubmitted;
        private int _remoteGpuConsecutiveSampleStarvations;
        private DateTimeOffset _lastRemoteGpuPlaybackDropLogUtc = DateTimeOffset.MinValue;

        private DispatcherTimer? _callTimer;
        private DispatcherTimer? _fullscreenChromeTimer;
        private DateTimeOffset? _connectedAtUtc;

        public CallPage()
        {
            this.InitializeComponent();
            Loaded += CallPage_Loaded;
            Unloaded += CallPage_Unloaded;
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            if (e.Parameter is CallPageArgs args)
            {
                _targetUserId = args.TargetUserId;
                _isScreenShare = args.IsScreenShare;
            }

            _isSharingScreen = _isScreenShare;

            var session = NativeCallCoordinator.Instance.CurrentSession;

            if (CallLaunchState.TryConsumeOutgoing(_targetUserId, _isScreenShare, out var consumedCallId))
            {
                session.CallId = consumedCallId;
                session.TargetUserId = _targetUserId;
                session.RemoteUserId = _targetUserId;
                session.IsScreenShare = _isScreenShare;
                session.State = NativeCallState.Calling;
                session.StatusText = "Calling...";
                session.PeerText = $"Calling user {_targetUserId}";
            }

            if (_targetUserId > 0)
                AddCallParticipant(_targetUserId);

            _ = LoadLocalUserIdAsync();

            SyncPageStateFromSession(session);
            ApplySessionToUi(session);
            UpdateDockVisualStates();
            UpdateSpeakingIndicators(session, AudioActivityService.Instance.Current);
        }

        private void CallPage_Loaded(object sender, RoutedEventArgs e)
        {
            NativeSignalingBridge.Instance.EnsureHooked();

            NativeCallCoordinator.Instance.SessionChanged += NativeCallCoordinator_SessionChanged;
            NativeSignalingBridge.Instance.IncomingCallReceived += NativeSignalingBridge_IncomingCallReceived;
            NativeSignalingBridge.Instance.CallAnsweredReceived += NativeSignalingBridge_CallAnsweredReceived;
            NativeSignalingBridge.Instance.CallRejectedReceived += NativeSignalingBridge_CallRejectedReceived;
            NativeSignalingBridge.Instance.CallEndedReceived += NativeSignalingBridge_CallEndedReceived;

            AudioActivityService.Instance.ActivityChanged += AudioActivityService_ActivityChanged;
            CallLayoutRoot.PointerMoved += CallMediaStage_PointerMoved;
            CallStageBorder.PointerMoved += CallMediaStage_PointerMoved;
            CallMediaStage.PointerMoved += CallMediaStage_PointerMoved;
            RemoteScreenImage.PointerMoved += CallMediaStage_PointerMoved;
            RemoteScreenPlayer.PointerMoved += CallMediaStage_PointerMoved;
            FullscreenPointerSurface.PointerMoved += CallMediaStage_PointerMoved;
            FullscreenButton.PointerMoved += CallMediaStage_PointerMoved;
            FullscreenExitButton.PointerMoved += CallMediaStage_PointerMoved;
            HookScreenShare();

            if (string.IsNullOrWhiteSpace(TokenStateText.Text) || TokenStateText.Text == "Token: not loaded")
                TokenStateText.Text = "Token: loaded";

            RealtimeStateText.Text = "Realtime: connected";

            _selectedAudioOutput = AudioPlaybackService.Instance.GetCurrentOutputDisplayName();
            _selectedMicrophone = GetCurrentMicDisplayName();
            _selectedScreenShareSoundDevice = SystemAudioShareService.Instance.SelectedDeviceName;

            EnsureTimerCreated();
            UpdateCallTimerText();

            SyncPageStateFromSession(NativeCallCoordinator.Instance.CurrentSession);
            ApplySessionToUi(NativeCallCoordinator.Instance.CurrentSession);
        }

        private void CallPage_Unloaded(object sender, RoutedEventArgs e)
        {
            NativeCallCoordinator.Instance.SessionChanged -= NativeCallCoordinator_SessionChanged;
            NativeSignalingBridge.Instance.IncomingCallReceived -= NativeSignalingBridge_IncomingCallReceived;
            NativeSignalingBridge.Instance.CallAnsweredReceived -= NativeSignalingBridge_CallAnsweredReceived;
            NativeSignalingBridge.Instance.CallRejectedReceived -= NativeSignalingBridge_CallRejectedReceived;
            NativeSignalingBridge.Instance.CallEndedReceived -= NativeSignalingBridge_CallEndedReceived;

            AudioActivityService.Instance.ActivityChanged -= AudioActivityService_ActivityChanged;
            CallLayoutRoot.PointerMoved -= CallMediaStage_PointerMoved;
            CallStageBorder.PointerMoved -= CallMediaStage_PointerMoved;
            CallMediaStage.PointerMoved -= CallMediaStage_PointerMoved;
            RemoteScreenImage.PointerMoved -= CallMediaStage_PointerMoved;
            RemoteScreenPlayer.PointerMoved -= CallMediaStage_PointerMoved;
            FullscreenPointerSurface.PointerMoved -= CallMediaStage_PointerMoved;
            FullscreenButton.PointerMoved -= CallMediaStage_PointerMoved;
            FullscreenExitButton.PointerMoved -= CallMediaStage_PointerMoved;
            UnhookScreenShare();
            _ = StopLocalScreenShareAsync(true);
            if (_isFullscreen)
                App.MainWindow?.ExitFullscreenMode();
            _h264Decoder?.Dispose();
            _h264Decoder = null;
            _rtpH264Decoder?.Dispose();
            _rtpH264Decoder = null;
            _rtpDecoderWidth = 0;
            _rtpDecoderHeight = 0;

            StopCallTimer();
            StopFullscreenChromeTimer();
        }

        private void HookScreenShare()
        {
            if (_screenShareHooksAttached)
                return;

            _screenShareHooksAttached = true;
            NativeScreenShareStreamingService.Instance.FrameReady += NativeScreenShare_FrameReady;
            NativeScreenShareStreamingService.Instance.StreamingFailed += NativeScreenShare_StreamingFailed;
            SystemAudioShareService.Instance.AudioCaptured += SystemAudioShare_AudioCaptured;
            SocialManager.Instance.Realtime.ScreenShareStarted += Realtime_ScreenShareStarted;
            SocialManager.Instance.Realtime.ScreenShareStopped += Realtime_ScreenShareStopped;
            SocialManager.Instance.Realtime.ScreenFrameReceived += Realtime_ScreenFrameReceived;
            SocialManager.Instance.Realtime.EncodedScreenFrameReceived += Realtime_EncodedScreenFrameReceived;
            SocialManager.Instance.Realtime.ScreenShareMetadataReceived += Realtime_ScreenShareMetadataReceived;
            SocialManager.Instance.Realtime.ScreenShareQosReceived += Realtime_ScreenShareQosReceived;
            SocialManager.Instance.Realtime.WebRtcOfferReceived += Realtime_WebRtcOfferReceived;
            SocialManager.Instance.Realtime.WebRtcAnswerReceived += Realtime_WebRtcAnswerReceived;
            SocialManager.Instance.Realtime.WebRtcIceReceived += Realtime_WebRtcIceReceived;
        }

        private void UnhookScreenShare()
        {
            if (!_screenShareHooksAttached)
                return;

            _screenShareHooksAttached = false;
            NativeScreenShareStreamingService.Instance.FrameReady -= NativeScreenShare_FrameReady;
            NativeScreenShareStreamingService.Instance.StreamingFailed -= NativeScreenShare_StreamingFailed;
            SystemAudioShareService.Instance.AudioCaptured -= SystemAudioShare_AudioCaptured;
            SocialManager.Instance.Realtime.ScreenShareStarted -= Realtime_ScreenShareStarted;
            SocialManager.Instance.Realtime.ScreenShareStopped -= Realtime_ScreenShareStopped;
            SocialManager.Instance.Realtime.ScreenFrameReceived -= Realtime_ScreenFrameReceived;
            SocialManager.Instance.Realtime.EncodedScreenFrameReceived -= Realtime_EncodedScreenFrameReceived;
            SocialManager.Instance.Realtime.ScreenShareMetadataReceived -= Realtime_ScreenShareMetadataReceived;
            SocialManager.Instance.Realtime.ScreenShareQosReceived -= Realtime_ScreenShareQosReceived;
            SocialManager.Instance.Realtime.WebRtcOfferReceived -= Realtime_WebRtcOfferReceived;
            SocialManager.Instance.Realtime.WebRtcAnswerReceived -= Realtime_WebRtcAnswerReceived;
            SocialManager.Instance.Realtime.WebRtcIceReceived -= Realtime_WebRtcIceReceived;
        }

        private async Task StartLocalScreenShareAsync()
        {
            ScreenShareCrashBreadcrumb.Mark("CallPage StartLocalScreenShareAsync requested");
            DiagnosticLogService.WriteLine(
                $"[ScreenShare:UI] StartLocalScreenShareAsync requested; processArch={System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}; osArch={System.Runtime.InteropServices.RuntimeInformation.OSArchitecture}; is64Bit={Environment.Is64BitProcess}; packageBase={AppContext.BaseDirectory}");
            DiagnosticLogService.Flush();

            await _screenShareLifecycleLock.WaitAsync();
            try
            {
                var session = NativeCallCoordinator.Instance.CurrentSession;
                DiagnosticLogService.WriteLine(
                    $"[ScreenShare:UI] StartLocalScreenShareAsync entered; state={session.State}; callId={session.CallId}; localSharing={_isSharingScreen}; nativeRunning={NativeScreenShareStreamingService.Instance.IsRunning}; requestedQuality={NativeScreenShareStreamingService.Instance.RequestedQuality.Name}");
                if (session.State != NativeCallState.Connected)
                    throw new InvalidOperationException("Start or accept the call before sharing your screen.");

                var participants = GetCallParticipants(session).ToList();
                DiagnosticLogService.WriteLine($"[ScreenShare:UI] Start participants={participants.Count}; participantIds={string.Join(",", participants)}");
                if (participants.Count == 0 || string.IsNullOrWhiteSpace(session.CallId))
                    throw new InvalidOperationException("No remote users are connected for screen share.");

                if (NativeScreenShareStreamingService.Instance.IsRunning)
                    return;

                ResetScreenShareStats();
                _screenShareStartedAtUtc = DateTimeOffset.UtcNow;
                Debug.WriteLine($"[ScreenShare:H264] Starting with locked selected quality: {NativeScreenShareStreamingService.Instance.RequestedQuality.Name}.");
                TryEnqueueOnUi(() =>
                {
                    _remoteScreenShareSenderId = 0;
                    _isRemoteVideoVisible = false;
                    _isRemoteScreenShareLoading = false;
                    RemoteScreenImage.Source = null;
                    RemotePlaceholderTitleText.Text = "You are sharing";
                    RemotePlaceholderSubtitleText.Text = "Starting live local preview...";
                    MediaOverlayText.Text = "Starting screen share...";
                    UpdateMediaLayerVisibility();
                });

                ScreenShareCrashBreadcrumb.Mark("CallPage before NativeScreenShareStreamingService.StartAsync");
                var startStreamingTask = NativeScreenShareStreamingService.Instance.StartAsync();
                var signalingTasks = participants
                    .Select(participantId => StartScreenShareParticipantSignalingAsync(participantId, session.CallId))
                    .ToList();

                await startStreamingTask;
                ScreenShareCrashBreadcrumb.Mark("CallPage after NativeScreenShareStreamingService.StartAsync");
                DiagnosticLogService.WriteLine("[ScreenShare:UI] Native screen-share streaming service started.");
                Debug.WriteLine("[ScreenShare:H264] Using WebRTC RTP plus continuous WebSocket H.264 for live screen-share transport.");

                await Task.WhenAll(signalingTasks);
                ScreenShareCrashBreadcrumb.Mark("CallPage screen-share signaling completed");

                if (_isScreenShareSoundEnabled)
                    await TryStartScreenShareSoundAsync(session, showSuccessStatus: false);

                DiagnosticLogService.WriteLine("[ScreenShare:REPORT] Screen-share start event captured; queueing automatic server diagnostics upload.");
                _ = UploadAutomaticScreenShareReportAsync(
                    BuildScreenShareReportContext("started", "Automatic report: screen share started."),
                    "started");
            }
            catch (Exception ex)
            {
                DiagnosticLogService.WriteLine("[ScreenShare:UI] StartLocalScreenShareAsync failed: " + ex);
                DiagnosticLogService.Flush();
                _ = UploadAutomaticScreenShareReportAsync(
                    BuildScreenShareReportContext("start-failed", "Automatic report: screen share failed to start. " + ex),
                    "start-failed");
                throw;
            }
            finally
            {
                _screenShareLifecycleLock.Release();
            }
        }

        private async Task StartScreenShareParticipantSignalingAsync(long participantId, string callId)
        {
            try
            {
                Debug.WriteLine($"[ScreenShare:RTP] Notifying participant {participantId} that screen share is starting in call {callId}.");
                await SocialManager.Instance.Realtime.SendScreenShareStartedAsync(participantId, callId);
                _ = StartScreenShareRtpOfferAsync(participantId, callId);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ScreenShare:RTP] Start notification failed for participant {participantId}: {ex.Message}");
            }
        }

        private async Task StopLocalScreenShareAsync(bool notifyRemote, bool promptForFeedback = false)
        {
            var shouldPromptForFeedback = false;
            ScreenShareReportContext? feedbackContext = null;

            await _screenShareLifecycleLock.WaitAsync();
            try
            {
                var wasRunning = NativeScreenShareStreamingService.Instance.IsRunning;
                Debug.WriteLine($"[ScreenShare:H264] StopLocalScreenShareAsync begin; notifyRemote={notifyRemote}; wasRunning={wasRunning}; uiSharing={_isSharingScreen}.");

                if (wasRunning)
                    feedbackContext = BuildScreenShareReportContext("pending", "");

                try
                {
                    await NativeScreenShareStreamingService.Instance.StopAsync();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[ScreenShare:H264] Stop capture failed: {ex}");
                }

                try
                {
                    await SystemAudioShareService.Instance.StopAsync();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[ScreenShare:Audio] Stop failed: {ex}");
                }

                try
                {
                    await CloseScreenSharePeersAsync();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[ScreenShare:RTP] Closing peers failed: {ex}");
                }

                if (wasRunning && feedbackContext != null)
                {
                    DiagnosticLogService.WriteLine($"[ScreenShare:REPORT] Screen-share finish event captured; notifyRemote={notifyRemote}; queueing automatic server diagnostics upload.");
                    _ = UploadAutomaticScreenShareReportAsync(
                        WithScreenShareFeedback(feedbackContext, "finished", $"Automatic report: screen share finished. notifyRemote={notifyRemote}."),
                        "finished");
                }

                ResetScreenShareStats();

                TryEnqueueOnUi(() =>
                {
                    if (_remoteScreenShareSenderId <= 0)
                    {
                        _isRemoteVideoVisible = false;
                        _isRemoteScreenShareLoading = false;
                        RemoteScreenImage.Source = null;
                        UpdateMediaLayerVisibility();
                    }
                });

                if (!wasRunning || !notifyRemote)
                    return;

                shouldPromptForFeedback = promptForFeedback;

                var session = NativeCallCoordinator.Instance.CurrentSession;
                if (!string.IsNullOrWhiteSpace(session.CallId))
                {
                    foreach (var participantId in GetCallParticipants(session))
                    {
                        try
                        {
                            await SocialManager.Instance.Realtime.SendScreenShareStoppedAsync(participantId, session.CallId);
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"[ScreenShare:H264] Stop notification failed for {participantId}: {ex.Message}");
                        }
                    }
                }
            }
            finally
            {
                Debug.WriteLine("[ScreenShare:H264] StopLocalScreenShareAsync completed.");
                DiagnosticLogService.Flush();
                _screenShareLifecycleLock.Release();
            }

            if (shouldPromptForFeedback && feedbackContext != null)
                await ShowScreenShareExperienceDialogAsync(feedbackContext);
        }

        private ScreenShareReportContext BuildScreenShareReportContext(string experience, string notes)
        {
            var session = NativeCallCoordinator.Instance.CurrentSession;
            var duration = _screenShareStartedAtUtc.HasValue
                ? DateTimeOffset.UtcNow - _screenShareStartedAtUtc.Value
                : TimeSpan.Zero;

            var remoteUserId = session.RemoteUserId > 0
                ? session.RemoteUserId
                : session.TargetUserId;

            return new ScreenShareReportContext
            {
                SessionType = "Screen share",
                Experience = experience,
                Notes = notes,
                CallId = session.CallId ?? "",
                Quality = _screenShareLastQuality,
                LocalUser = _localUserId > 0 ? GetParticipantDisplayName(_localUserId) : "You",
                RemoteUser = remoteUserId > 0 ? GetParticipantDisplayName(remoteUserId) : "Remote participant",
                Duration = duration <= TimeSpan.Zero ? "-" : duration.ToString(@"hh\:mm\:ss"),
                SenderFps = _screenShareObservedFps,
                ReceiverFps = _remotePlaybackObservedFps,
                LastWidth = _screenShareLastWidth,
                LastHeight = _screenShareLastHeight,
                LastBytes = _screenShareLastBytes,
                SentFrames = _screenShareTransmitCounter,
                ReceivedFrames = _remoteRtpFrameCount,
                RenderedFrames = _remoteRenderedFrameCount,
                DroppedSendFrames = _screenShareDroppedFrames,
                DroppedReceiveFrames = _screenShareDroppedReceiveFrames,
                DecodeFailures = _remoteDecodeFailureCount,
                DecoderResets = _remoteDecoderResetCount,
                AudioDevice = _selectedScreenShareSoundDevice,
                AudioPacketsSent = _screenShareSoundPacketsSent
            };
        }

        private async Task ShowScreenShareExperienceDialogAsync(ScreenShareReportContext snapshot)
        {
            if (_screenShareFeedbackDialogOpen || XamlRoot == null)
                return;

            _screenShareFeedbackDialogOpen = true;

            try
            {
                var dialog = new ContentDialog
                {
                    XamlRoot = XamlRoot,
                    Title = "How was your screen share?",
                    Content = new TextBlock
                    {
                        Text = "Your feedback can upload the screen-share report and logs so support can look at quality, stutter, audio, and connection issues.",
                        TextWrapping = TextWrapping.WrapWholeWords
                    },
                    PrimaryButtonText = "Good",
                    SecondaryButtonText = "Bad experience",
                    CloseButtonText = "Not now",
                    DefaultButton = ContentDialogButton.Primary
                };

                var result = await dialog.ShowAsync();

                if (result == ContentDialogResult.Primary)
                {
                    await UploadScreenShareFeedbackAsync(snapshot, "good", "User marked the screen share as good.");
                    return;
                }

                if (result == ContentDialogResult.Secondary)
                    await ShowBadScreenShareExperienceDialogAsync(snapshot);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ScreenShare:Report] Feedback dialog failed: {ex}");
                NativeCallCoordinator.Instance.SetStatus(
                    NativeCallCoordinator.Instance.CurrentSession.State,
                    $"Screen-share feedback failed: {ex.Message}");
            }
            finally
            {
                _screenShareFeedbackDialogOpen = false;
            }
        }

        private async Task ShowBadScreenShareExperienceDialogAsync(ScreenShareReportContext snapshot)
        {
            if (XamlRoot == null)
                return;

            var notesBox = new TextBox
            {
                Header = "What went wrong?",
                PlaceholderText = "Lag, freezing, audio stutter, bad colours, 1080p issue...",
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                MinHeight = 120
            };

            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "Upload screen-share report to support?",
                Content = new StackPanel
                {
                    Spacing = 12,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = "Zink will upload a focused screen-share report and recent diagnostic logs to the Zink call server. No upload happens unless you choose Upload report.",
                            TextWrapping = TextWrapping.WrapWholeWords
                        },
                        notesBox
                    }
                },
                PrimaryButtonText = "Upload report",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary
            };

            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
                await UploadScreenShareFeedbackAsync(snapshot, "bad", notesBox.Text);
        }

        private async Task UploadScreenShareFeedbackAsync(ScreenShareReportContext snapshot, string experience, string notes)
        {
            try
            {
                NativeCallCoordinator.Instance.SetStatus(
                    NativeCallCoordinator.Instance.CurrentSession.State,
                    "Uploading screen-share report...");

                var context = WithScreenShareFeedback(snapshot, experience, notes);
                var bundlePath = await ScreenShareReportService.CreateBundleAsync(context);
                var result = await DiagnosticsUploadService.TryUploadSupportBundleAsync(bundlePath);

                if (!result.Success)
                {
                    NativeCallCoordinator.Instance.SetStatus(
                        NativeCallCoordinator.Instance.CurrentSession.State,
                        $"Screen-share report saved locally. Upload failed: {result.Error}");
                    await ShowSupportBundleSavedDialogAsync(
                        "Screen-share report saved locally",
                        bundlePath,
                        "Zink could not upload the report to the call server, but it saved the full support bundle on this device.");
                    return;
                }

                NativeCallCoordinator.Instance.SetStatus(
                    NativeCallCoordinator.Instance.CurrentSession.State,
                    $"Screen-share report uploaded. Report id: {result.ReportId}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ScreenShare:Report] Upload failed: {ex}");
                NativeCallCoordinator.Instance.SetStatus(
                    NativeCallCoordinator.Instance.CurrentSession.State,
                    $"Screen-share report upload failed: {ex.Message}");
            }
        }

        private async Task UploadAutomaticScreenShareReportAsync(ScreenShareReportContext snapshot, string eventName)
        {
            await _screenShareAutoReportLock.WaitAsync();
            try
            {
                DiagnosticLogService.EnsureLogFile("automatic screen-share report " + eventName);
                DiagnosticLogService.WriteLine(
                    $"[ScreenShare:REPORT] Automatic upload starting; event={eventName}; callId={snapshot.CallId}; quality={snapshot.Quality}; sent={snapshot.SentFrames}; received={snapshot.ReceivedFrames}; rendered={snapshot.RenderedFrames}; droppedSend={snapshot.DroppedSendFrames}; droppedReceive={snapshot.DroppedReceiveFrames}; decodeFailures={snapshot.DecodeFailures}; decoderResets={snapshot.DecoderResets}.");
                DiagnosticLogService.Flush();

                var bundlePath = await ScreenShareReportService.CreateBundleAsync(snapshot);
                var result = await DiagnosticsUploadService.TryUploadSupportBundleAsync(bundlePath);

                if (result.Success)
                {
                    DiagnosticLogService.WriteLine(
                        $"[ScreenShare:REPORT] Automatic upload completed; event={eventName}; reportId={result.ReportId}; downloadUrl={result.DownloadUrl}; bundle={bundlePath}.");
                }
                else
                {
                    DiagnosticLogService.WriteLine(
                        $"[ScreenShare:REPORT] Automatic upload failed; event={eventName}; error={result.Error}; savedBundle={bundlePath}.");
                }
            }
            catch (Exception ex)
            {
                DiagnosticLogService.WriteLine($"[ScreenShare:REPORT] Automatic upload crashed; event={eventName}; error={ex}");
            }
            finally
            {
                DiagnosticLogService.Flush();
                _screenShareAutoReportLock.Release();
            }
        }

        private static ScreenShareReportContext WithScreenShareFeedback(ScreenShareReportContext snapshot, string experience, string notes)
        {
            return new ScreenShareReportContext
            {
                SessionType = snapshot.SessionType,
                Experience = experience,
                Notes = notes,
                CallId = snapshot.CallId,
                Quality = snapshot.Quality,
                LocalUser = snapshot.LocalUser,
                RemoteUser = snapshot.RemoteUser,
                Duration = snapshot.Duration,
                SenderFps = snapshot.SenderFps,
                ReceiverFps = snapshot.ReceiverFps,
                LastWidth = snapshot.LastWidth,
                LastHeight = snapshot.LastHeight,
                LastBytes = snapshot.LastBytes,
                SentFrames = snapshot.SentFrames,
                ReceivedFrames = snapshot.ReceivedFrames,
                RenderedFrames = snapshot.RenderedFrames,
                DroppedSendFrames = snapshot.DroppedSendFrames,
                DroppedReceiveFrames = snapshot.DroppedReceiveFrames,
                DecodeFailures = snapshot.DecodeFailures,
                DecoderResets = snapshot.DecoderResets,
                AudioDevice = snapshot.AudioDevice,
                AudioPacketsSent = snapshot.AudioPacketsSent
            };
        }

        private ScreenShareReportContext BuildCallReportContext(string experience, string notes)
        {
            var session = NativeCallCoordinator.Instance.CurrentSession;
            var duration = _connectedAtUtc.HasValue
                ? DateTimeOffset.UtcNow - _connectedAtUtc.Value
                : TimeSpan.Zero;
            var remoteUserId = session.RemoteUserId > 0
                ? session.RemoteUserId
                : _callParticipantIds.FirstOrDefault(id => id > 0 && id != _localUserId);

            return new ScreenShareReportContext
            {
                SessionType = "Call",
                Experience = experience,
                Notes = notes,
                CallId = session.CallId,
                Quality = _isSharingScreen || _screenShareLastWidth > 0 ? $"Screen share {_screenShareLastQuality}" : "Voice call",
                LocalUser = _localUserId > 0 ? GetParticipantDisplayName(_localUserId) : "You",
                RemoteUser = remoteUserId > 0 ? GetParticipantDisplayName(remoteUserId) : "Remote participant",
                Duration = duration <= TimeSpan.Zero ? "-" : duration.ToString(@"hh\:mm\:ss"),
                SenderFps = _screenShareObservedFps,
                ReceiverFps = _remotePlaybackObservedFps,
                LastWidth = _screenShareLastWidth,
                LastHeight = _screenShareLastHeight,
                LastBytes = _screenShareLastBytes,
                SentFrames = _screenShareTransmitCounter,
                ReceivedFrames = _remoteRtpFrameCount,
                RenderedFrames = _remoteRenderedFrameCount,
                DroppedSendFrames = _screenShareDroppedFrames,
                DroppedReceiveFrames = _screenShareDroppedReceiveFrames,
                DecodeFailures = _remoteDecodeFailureCount,
                DecoderResets = _remoteDecoderResetCount,
                AudioDevice = _selectedScreenShareSoundDevice,
                AudioPacketsSent = _screenShareSoundPacketsSent
            };
        }

        private async Task PromptForCallExperienceOnceAsync(string reason)
        {
            var session = NativeCallCoordinator.Instance.CurrentSession;
            var key = !string.IsNullOrWhiteSpace(session.CallId)
                ? session.CallId
                : $"{session.RemoteUserId}:{session.TargetUserId}:{_connectedAtUtc:O}";

            if (string.IsNullOrWhiteSpace(key) ||
                string.Equals(_lastCallFeedbackPromptKey, key, StringComparison.Ordinal) ||
                _callFeedbackDialogOpen ||
                XamlRoot == null)
            {
                return;
            }

            _lastCallFeedbackPromptKey = key;
            await ShowCallExperienceDialogAsync(BuildCallReportContext(reason, ""));
        }

        private async Task ShowCallExperienceDialogAsync(ScreenShareReportContext snapshot)
        {
            if (_callFeedbackDialogOpen || XamlRoot == null)
                return;

            _callFeedbackDialogOpen = true;

            try
            {
                var dialog = new ContentDialog
                {
                    XamlRoot = XamlRoot,
                    Title = "How was your Zink call?",
                    Content = new TextBlock
                    {
                        Text = "Your answer can upload the call report and logs so support can check audio, screen share, lag, connection, and device issues.",
                        TextWrapping = TextWrapping.WrapWholeWords
                    },
                    PrimaryButtonText = "Good",
                    SecondaryButtonText = "Bad experience",
                    CloseButtonText = "Not now",
                    DefaultButton = ContentDialogButton.Primary
                };

                var result = await dialog.ShowAsync();

                if (result == ContentDialogResult.Primary)
                {
                    await UploadCallFeedbackAsync(snapshot, "good", "User marked the call as good.");
                    return;
                }

                if (result == ContentDialogResult.Secondary)
                    await ShowBadCallExperienceDialogAsync(snapshot);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Call:Report] Feedback dialog failed: {ex}");
                NativeCallCoordinator.Instance.SetStatus(
                    NativeCallCoordinator.Instance.CurrentSession.State,
                    $"Call feedback failed: {ex.Message}");
            }
            finally
            {
                _callFeedbackDialogOpen = false;
            }
        }

        private async Task ShowBadCallExperienceDialogAsync(ScreenShareReportContext snapshot)
        {
            if (XamlRoot == null)
                return;

            var notesBox = new TextBox
            {
                Header = "What went wrong?",
                PlaceholderText = "Audio stutter, delay, call dropped, screen share lag, no sound...",
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                MinHeight = 120
            };

            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "Upload call report to support?",
                Content = new StackPanel
                {
                    Spacing = 12,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = "Zink will upload a focused call report and recent diagnostic logs to the Zink call server. No upload happens unless you choose Upload report.",
                            TextWrapping = TextWrapping.WrapWholeWords
                        },
                        notesBox
                    }
                },
                PrimaryButtonText = "Upload report",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary
            };

            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
                await UploadCallFeedbackAsync(snapshot, "bad", notesBox.Text);
        }

        private async Task UploadCallFeedbackAsync(ScreenShareReportContext snapshot, string experience, string notes)
        {
            try
            {
                NativeCallCoordinator.Instance.SetStatus(
                    NativeCallCoordinator.Instance.CurrentSession.State,
                    "Uploading call report...");

                var context = WithScreenShareFeedback(snapshot, experience, notes);
                var bundlePath = await ScreenShareReportService.CreateBundleAsync(context);
                var result = await DiagnosticsUploadService.TryUploadSupportBundleAsync(bundlePath);

                if (!result.Success)
                {
                    NativeCallCoordinator.Instance.SetStatus(
                        NativeCallCoordinator.Instance.CurrentSession.State,
                        $"Call report saved locally. Upload failed: {result.Error}");
                    await ShowSupportBundleSavedDialogAsync(
                        "Call report saved locally",
                        bundlePath,
                        "Zink could not upload the report to the call server, but it saved the full support bundle on this device.");
                    return;
                }

                NativeCallCoordinator.Instance.SetStatus(
                    NativeCallCoordinator.Instance.CurrentSession.State,
                    $"Call report uploaded. Report id: {result.ReportId}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Call:Report] Upload failed: {ex}");
                NativeCallCoordinator.Instance.SetStatus(
                    NativeCallCoordinator.Instance.CurrentSession.State,
                    $"Call report upload failed: {ex.Message}");
            }
        }

        private async Task ShowSupportBundleSavedDialogAsync(string title, string bundlePath, string message)
        {
            if (XamlRoot == null)
                return;

            var pathBox = new TextBox
            {
                Header = "Support bundle",
                Text = bundlePath,
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap
            };

            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = title,
                Content = new StackPanel
                {
                    Spacing = 12,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = message + " Copy this path from the device that had the problem and send the zip file.",
                            TextWrapping = TextWrapping.WrapWholeWords
                        },
                        pathBox
                    }
                },
                PrimaryButtonText = "Copy path",
                CloseButtonText = "OK",
                DefaultButton = ContentDialogButton.Primary
            };

            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                var package = new DataPackage();
                package.SetText(bundlePath);
                Clipboard.SetContent(package);
                Clipboard.Flush();
            }
        }

        private async Task<bool> TryStartScreenShareSoundAsync(NativeCallSession session, bool showSuccessStatus)
        {
            try
            {
                await SystemAudioShareService.Instance.StartAsync();
                _selectedScreenShareSoundDevice = SystemAudioShareService.Instance.SelectedDeviceName;

                if (showSuccessStatus)
                {
                    NativeCallCoordinator.Instance.SetStatus(
                        session.State,
                        $"Screen share sound enabled on {_selectedScreenShareSoundDevice}.");
                }

                return true;
            }
            catch (Exception ex)
            {
                _isScreenShareSoundEnabled = false;
                _selectedScreenShareSoundDevice = SystemAudioShareService.Instance.SelectedDeviceName;
                Debug.WriteLine($"[ScreenShare:Audio] Start failed: {ex}");

                NativeCallCoordinator.Instance.SetStatus(
                    session.State,
                    $"Screen share started. Screen sound could not start: {ex.Message}");

                return false;
            }
        }

        private void NativeScreenShare_FrameReady(object? sender, NativeScreenFrameEventArgs e)
        {
            var session = NativeCallCoordinator.Instance.CurrentSession;
            if (session.State != NativeCallState.Connected ||
                string.IsNullOrWhiteSpace(session.CallId))
            {
                return;
            }

            var participants = GetCallParticipants(session).ToList();
            if (participants.Count == 0)
                return;

            _ = SendLocalScreenFrameAndTrackAsync(participants, session.CallId, e);

            TryEnqueueOnUi(async () =>
            {
                if (_isSharingScreen)
                {
                    var now = DateTimeOffset.UtcNow;

                    if (_remoteScreenShareSenderId <= 0 &&
                        now - _localStagePreviewLastRenderedUtc >= TimeSpan.FromMilliseconds(250))
                    {
                        _localStagePreviewLastRenderedUtc = now;
                        await RenderScreenFrameAsync(e.PreviewFrameData, RemoteScreenImage);
                        _isRemoteVideoVisible = true;
                        _remoteScreenLastRenderedAtUtc = now;
                        RemotePlaceholderTitleText.Text = "You are sharing";
                        RemotePlaceholderSubtitleText.Text = "Live local screen preview";
                        MediaOverlayText.Text = $"Your screen {e.Width} x {e.Height}";
                        UpdateMediaLayerVisibility();
                    }

                    if (now - _localPreviewLastRenderedUtc >= TimeSpan.FromSeconds(1))
                    {
                        _localPreviewLastRenderedUtc = now;
                        await RenderScreenFrameAsync(e.PreviewFrameData, LocalPreviewImage);
                        LocalPreviewPlaceholder.Visibility = Visibility.Collapsed;
                    }
                }
            });
        }

        private async Task SendLocalScreenFrameAndTrackAsync(IReadOnlyList<long> participantIds, string callId, NativeScreenFrameEventArgs e)
        {
            try
            {
                if (e.Codec.Equals("h264", StringComparison.OrdinalIgnoreCase))
                {
                    var sent = await TrySendScreenFrameBestAvailableAsync(participantIds, callId, e);
                    LogLocalScreenShareFlow(e, participantIds.Count, sent);
                    if (!sent)
                        return;
                }
                else
                {
                    await SendWithTimeoutAsync(
                        SocialManager.Instance.Realtime.SendScreenFrameAsync(
                            0,
                            callId,
                            e.FrameData,
                            e.Width,
                            e.Height,
                            e.Timestamp),
                        "legacy screen frame");
                }

                UpdateScreenShareStats(e);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ScreenShare:SEND] Failed to send local frame: {ex.Message}");
                DiagnosticLogService.WriteLine($"[ScreenShare:SEND] Failed to send local frame: {ex.Message}");
            }
        }

        private async Task<bool> TrySendScreenFrameBestAvailableAsync(IReadOnlyList<long> participantIds, string callId, NativeScreenFrameEventArgs e)
        {
            if (!_screenShareSendLock.Wait(0))
            {
                _screenShareDroppedFrames++;
                if (_screenShareDroppedFrames == 1 || _screenShareDroppedFrames % NativeScreenShareStreamingService.TargetFps == 0)
                    Debug.WriteLine($"[ScreenShare:H264] Dropped stale frame while previous send is still in flight. dropped={_screenShareDroppedFrames}");

                NativeScreenShareStreamingService.Instance.ReportSendCongestion("send backlog");
                return false;
            }

            try
            {
                var sendStartedAt = Stopwatch.StartNew();
                var sentToAnyPeer = false;
                var fallbackParticipantIds = new List<long>();

                foreach (var participantId in participantIds)
                {
                    if (_screenSharePeers.TryGetValue(participantId, out var peer) &&
                        await TrySendWithTimeoutAsync(
                            peer.SendEncodedVideoFrameAsync(e.FrameData),
                            $"RTP frame to participant {participantId}"))
                    {
                        sentToAnyPeer = true;
                        _screenShareRtpFrames++;
                    }
                    else
                    {
                        fallbackParticipantIds.Add(participantId);
                    }
                }

                var rtpFailedTargets = fallbackParticipantIds.Distinct().ToList();
                var shouldSendWebSocketFallback = ShouldSendFallbackFrame(e);
                var websocketFallbackTargets = new List<long>();
                if (shouldSendWebSocketFallback)
                {
                    var useStartupRecoveryLane = IsWebSocketFallbackWarmupActive() || !sentToAnyPeer;
                    websocketFallbackTargets = useStartupRecoveryLane
                        ? participantIds.Distinct().ToList()
                        : rtpFailedTargets.ToList();
                }

                var shouldSendRtpMetadata = sentToAnyPeer && ShouldSendRtpMetadataFrame(e);
                var sentFallback = false;
                var sentFallbackTargets = 0;
                var sentPreview = false;

                if (shouldSendRtpMetadata)
                {
                    foreach (var participantId in participantIds.Distinct())
                    {
                        await SocialManager.Instance.Realtime.SendScreenShareMetadataAsync(
                            participantId,
                            callId,
                            e.Width,
                            e.Height,
                            e.Timestamp,
                            e.Codec);

                        _screenShareRtpMetadataFrames++;
                    }
                }

                if (websocketFallbackTargets.Count > 0)
                {
                    foreach (var participantId in websocketFallbackTargets)
                    {
                        try
                        {
                            if (e.Codec.Equals("h264", StringComparison.OrdinalIgnoreCase))
                            {
                                await SendWithTimeoutAsync(
                                    SocialManager.Instance.Realtime.SendEncodedScreenFrameBinaryAsync(
                                        participantId,
                                        callId,
                                        e.FrameData,
                                        e.Width,
                                        e.Height,
                                        e.Timestamp,
                                        e.IsKeyFrame),
                                    $"binary H.264 fallback to participant {participantId}");
                            }
                            else
                            {
                                await SendWithTimeoutAsync(
                                    SocialManager.Instance.Realtime.SendEncodedScreenFrameAsync(
                                        participantId,
                                        callId,
                                        e.FrameData,
                                        e.Width,
                                        e.Height,
                                        e.Timestamp,
                                        e.Codec,
                                        e.IsKeyFrame),
                                    $"encoded fallback to participant {participantId}");
                            }

                            sentFallback = true;
                            sentFallbackTargets++;
                            _screenShareFallbackFrames++;
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"[ScreenShare:FALLBACK] Send failed for participant {participantId}: {ex.Message}");
                        }
                    }
                }
                else if (!sentToAnyPeer && rtpFailedTargets.Count > 0)
                {
                    _screenShareDroppedFrames++;
                    NativeScreenShareStreamingService.Instance.ReportSendCongestion("fallback transport backlog");
                }

                if ((!sentToAnyPeer || !sentFallback) &&
                    ShouldSendPreviewFallbackFrame(e))
                {
                    var previewTargets = (!sentToAnyPeer && !sentFallback)
                        ? rtpFailedTargets
                        : participantIds.Distinct().ToList();

                    foreach (var participantId in previewTargets)
                    {
                        try
                        {
                            await SendWithTimeoutAsync(
                                SocialManager.Instance.Realtime.SendScreenFrameAsync(
                                    participantId,
                                    callId,
                                    e.PreviewFrameData,
                                    e.Width,
                                    e.Height,
                                    e.Timestamp),
                                $"JPEG preview fallback to participant {participantId}");

                            _screenSharePreviewFallbackFrames++;
                            sentPreview = true;
                            if (_screenSharePreviewFallbackFrames == 1 ||
                                _screenSharePreviewFallbackFrames % (PreviewFallbackMaxFps * 2) == 0)
                            {
                                Debug.WriteLine(
                                    $"[ScreenShare:PREVIEW] Sent {_screenSharePreviewFallbackFrames} JPEG preview frames; target={participantId}; bytes={e.PreviewFrameData.Length}; {e.Width}x{e.Height}.");
                            }
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"[ScreenShare:PREVIEW] Send failed for participant {participantId}: {ex.Message}");
                        }
                    }
                }

                LogScreenShareSendSummary(
                    participantIds.Count,
                    sentToAnyPeer,
                    sentFallback ? sentFallbackTargets : rtpFailedTargets.Count,
                    e,
                    sendStartedAt.Elapsed);

                if (sendStartedAt.Elapsed > TimeSpan.FromMilliseconds(20))
                {
                    var slowMessage = $"[ScreenShare:SEND:SLOW] sendMs={sendStartedAt.Elapsed.TotalMilliseconds:0.0}; rtp={sentToAnyPeer}; ws={sentFallback}; preview={sentPreview}; frameBytes={e.FrameData.Length}; previewBytes={e.PreviewFrameData.Length}; participants={participantIds.Count}.";
                    Debug.WriteLine(slowMessage);
                    DiagnosticLogService.WriteLine(slowMessage);
                }

                return sentToAnyPeer || sentFallback || sentPreview;
            }
            finally
            {
                _screenShareSendLock.Release();
            }
        }

        private bool ShouldSendFallbackFrame(NativeScreenFrameEventArgs e)
        {
            if (!e.IsKeyFrame && e.FrameData.Length > WebSocketFallbackMaxDeltaBytes)
                return false;

            if (FallbackMaxFps >= NativeScreenShareStreamingService.TargetFps)
                return true;

            var now = DateTimeOffset.UtcNow;
            var fallbackInterval = TimeSpan.FromMilliseconds(1000.0 / FallbackMaxFps);
            if (_fallbackFrameLastSentUtc != DateTimeOffset.MinValue &&
                now - _fallbackFrameLastSentUtc < fallbackInterval)
            {
                return false;
            }

            _fallbackFrameLastSentUtc = now;
            return true;
        }

        private async Task SendWithTimeoutAsync(Task sendTask, string description)
        {
            if (await Task.WhenAny(sendTask, Task.Delay(ScreenShareSendTimeout)) == sendTask)
            {
                await sendTask;
                return;
            }

            _ = sendTask.ContinueWith(
                task =>
                {
                    _ = task.Exception;
                },
                TaskContinuationOptions.OnlyOnFaulted);
            DiagnosticLogService.WriteLine($"[ScreenShare:SEND] Timed out sending {description} after {ScreenShareSendTimeout.TotalMilliseconds:0}ms.");
            throw new TimeoutException($"Timed out sending {description}.");
        }

        private async Task<bool> TrySendWithTimeoutAsync(Task<bool> sendTask, string description)
        {
            if (await Task.WhenAny(sendTask, Task.Delay(ScreenShareSendTimeout)) == sendTask)
                return await sendTask;

            _ = sendTask.ContinueWith(
                task =>
                {
                    _ = task.Exception;
                },
                TaskContinuationOptions.OnlyOnFaulted);
            DiagnosticLogService.WriteLine($"[ScreenShare:SEND] Timed out sending {description} after {ScreenShareSendTimeout.TotalMilliseconds:0}ms.");
            return false;
        }

        private bool IsWebSocketFallbackWarmupActive()
        {
            return _screenShareStartedAtUtc.HasValue &&
                DateTimeOffset.UtcNow - _screenShareStartedAtUtc.Value <= WebSocketFallbackWarmupDuration;
        }

        private bool ShouldSendPreviewFallbackFrame(NativeScreenFrameEventArgs e)
        {
            if (e.PreviewFrameData.Length == 0)
                return false;

            var now = DateTimeOffset.UtcNow;
            if (now - _previewFallbackLastSentUtc < TimeSpan.FromMilliseconds(1000.0 / PreviewFallbackMaxFps))
                return false;

            _previewFallbackLastSentUtc = now;
            return true;
        }

        private bool ShouldSendRtpMetadataFrame(NativeScreenFrameEventArgs e)
        {
            if (!e.IsKeyFrame)
                return false;

            var now = DateTimeOffset.UtcNow;
            var resolutionChanged = _lastRtpMetadataWidth != e.Width || _lastRtpMetadataHeight != e.Height;
            if (!resolutionChanged && now - _lastRtpMetadataSentUtc < RtpMetadataInterval)
                return false;

            _lastRtpMetadataSentUtc = now;
            _lastRtpMetadataWidth = e.Width;
            _lastRtpMetadataHeight = e.Height;
            return true;
        }

        private void NativeScreenShare_StreamingFailed(object? sender, string message)
        {
            ScreenShareCrashBreadcrumb.Mark("NativeScreenShare_StreamingFailed: " + message);
            DiagnosticLogService.WriteLine("[ScreenShare:REPORT] Screen-share failure event captured; queueing automatic server diagnostics upload. message=" + message);
            DiagnosticLogService.Flush();
            _ = UploadAutomaticScreenShareReportAsync(
                BuildScreenShareReportContext("failed", "Automatic report: screen share failed or crashed. " + message),
                "failed");

            TryEnqueueOnUi(async () =>
            {
                _isSharingScreen = false;
                _isScreenShare = false;
                NativeCallCoordinator.Instance.CurrentSession.IsScreenShare = false;
                await SystemAudioShareService.Instance.StopAsync();

                NativeCallCoordinator.Instance.SetStatus(
                    NativeCallCoordinator.Instance.CurrentSession.State,
                    $"H.264 screen share failed: {message}");

                LocalPreviewImage.Source = null;
                LocalPreviewPlaceholder.Visibility = Visibility.Visible;
                UpdateDockVisualStates();
                ApplySessionToUi(NativeCallCoordinator.Instance.CurrentSession);
            });
        }

        private async void SystemAudioShare_AudioCaptured(byte[] data)
        {
            try
            {
                var session = NativeCallCoordinator.Instance.CurrentSession;
                if (!_isSharingScreen ||
                    !_isScreenShareSoundEnabled ||
                    session.State != NativeCallState.Connected ||
                    string.IsNullOrWhiteSpace(session.CallId) ||
                    data.Length == 0)
                {
                    return;
                }

                if (DateTimeOffset.UtcNow - NativeCallCoordinator.Instance.LastRemoteVoiceAudioReceivedUtc < TimeSpan.FromMilliseconds(350))
                    return;

                var participants = GetCallParticipants(session).ToList();
                foreach (var participantId in participants)
                {
                    await SocialManager.Instance.Realtime.SendAudioChunkAsync(
                        participantId,
                        session.CallId,
                        data);
                }

                _screenShareSoundPacketsSent++;
                _screenShareSoundLastPacketAtUtc = DateTimeOffset.UtcNow;
                if (_screenShareSoundPacketsSent == 1 ||
                    _screenShareSoundPacketsSent % 100 == 0)
                {
                    Debug.WriteLine($"[ScreenShare:Audio] Sent system-audio packet {_screenShareSoundPacketsSent}; bytes={data.Length}; targets={participants.Count}; broadcast={participants.Count > 1}.");
                }
            }
            catch (Exception ex)
            {
                TryEnqueueOnUi(() =>
                {
                    NativeCallCoordinator.Instance.SetStatus(
                        NativeCallCoordinator.Instance.CurrentSession.State,
                        $"Screen share sound failed: {ex.Message}");
                });
            }
        }

        private void ResetScreenShareStats()
        {
            _screenShareFrameCounter = 0;
            _screenShareObservedFps = 0;
            _screenShareLastWidth = 0;
            _screenShareLastHeight = 0;
            _screenShareLastBytes = 0;
            _screenShareLastQuality = NativeScreenShareStreamingService.Instance.CurrentQuality.Name;
            _screenShareFpsWindowStartedUtc = DateTimeOffset.UtcNow;
            _screenShareLastFrameAtUtc = null;
            _screenShareTransmitCounter = 0;
            _screenShareDroppedFrames = 0;
            _screenShareDroppedReceiveFrames = 0;
            _localPreviewLastRenderedUtc = DateTimeOffset.MinValue;
            _localStagePreviewLastRenderedUtc = DateTimeOffset.MinValue;
            _fallbackFrameLastSentUtc = DateTimeOffset.MinValue;
            _previewFallbackLastSentUtc = DateTimeOffset.MinValue;
            _screenShareStartedAtUtc = null;
            _screenShareRtpFrames = 0;
            _screenShareFallbackFrames = 0;
            _screenSharePreviewFallbackFrames = 0;
            _screenShareRtpMetadataFrames = 0;
            _screenShareSoundPacketsSent = 0;
            _screenShareSoundLastPacketAtUtc = null;
            _lastRtpMetadataSentUtc = DateTimeOffset.MinValue;
            _lastRtpMetadataWidth = 0;
            _lastRtpMetadataHeight = 0;
            _lastQosSentUtc = DateTimeOffset.MinValue;
            _lastQosDroppedReceiveFrames = 0;
            _lastScreenShareSendSummaryUtc = DateTimeOffset.MinValue;
            _remoteScreenShareSenderId = 0;
            _remoteScreenLastReceivedAtUtc = null;
            _remoteScreenLastRenderedAtUtc = null;
            _lastRemoteDecoderResetAtUtc = DateTimeOffset.MinValue;
            _lastRemoteReceiveStallRecoveryUtc = DateTimeOffset.MinValue;
            _remoteFallbackLastReceivedAtUtc = DateTimeOffset.MinValue;
            _remoteFallbackFirstReceivedAtUtc = DateTimeOffset.MinValue;
            Interlocked.Exchange(ref _remoteRtpFrameCount, 0);
            Interlocked.Exchange(ref _remoteFallbackFrameCount, 0);
            Interlocked.Exchange(ref _remotePreviewFrameCount, 0);
            Interlocked.Exchange(ref _remoteRenderedFrameCount, 0);
            ResetRemoteRealtimeCounters();
            _remoteDecoderResetCount = 0;
            _remoteEmptyDecodeCount = 0;
            _remoteDecodeFailureCount = 0;
            _remoteH264WaitingForKeyFrame = true;
            _remoteH264FramesDroppedWaitingForKeyFrame = 0;
            _lastScreenShareMetadataTimestamp = 0;
            ResetScreenShareFlowDiagnostics();
            lock (_rtpFrameSync)
            {
                _pendingRemoteH264Frames.Clear();
                _pendingRtpFrameSequence = 0;
                _remoteDecodeGeneration++;
            }

            lock (_remoteRenderSync)
            {
                _pendingRemoteRenderFrame = null;
                _pendingRemoteRenderWidth = 0;
                _pendingRemoteRenderHeight = 0;
            }

            Interlocked.Exchange(ref _remoteRenderQueued, 0);
        }

        private void ResetScreenShareFlowDiagnostics()
        {
            _lastLocalFrameFlowLogUtc = DateTimeOffset.MinValue;
            _lastRemoteReceiveFlowLogUtc = DateTimeOffset.MinValue;
            _lastRemoteQueueFlowLogUtc = DateTimeOffset.MinValue;
            _lastRemoteDecodeFlowLogUtc = DateTimeOffset.MinValue;
            _lastRemoteRenderFlowLogUtc = DateTimeOffset.MinValue;
            _lastRemotePreviewFlowLogUtc = DateTimeOffset.MinValue;
            _lastRemoteFallbackHoldLogUtc = DateTimeOffset.MinValue;
            _lastRemoteBacklogTrimLogUtc = DateTimeOffset.MinValue;
            _lastRemoteBacklogJumpLogUtc = DateTimeOffset.MinValue;
            _remoteBacklogTrimmedSinceLastLog = 0;
            _remoteBacklogJumpedSinceLastLog = 0;
            _lastLocalEncodedFingerprint = 0;
            _localEncodedFingerprintRepeat = 0;
            _lastLocalPreviewFingerprint = 0;
            _localPreviewFingerprintRepeat = 0;
            _lastRemoteEncodedFingerprint = 0;
            _remoteEncodedFingerprintRepeat = 0;
            _lastRemoteDecodedFingerprint = 0;
            _remoteDecodedFingerprintRepeat = 0;
            _lastRemoteRenderedFingerprint = 0;
            _remoteRenderedFingerprintRepeat = 0;
            _lastRemotePreviewFingerprint = 0;
            _remotePreviewFingerprintRepeat = 0;
        }

        private void ResetRemoteRealtimeCounters()
        {
            _remoteReceiveFpsWindowStartedUtc = DateTimeOffset.UtcNow;
            _remotePlaybackFpsWindowStartedUtc = DateTimeOffset.UtcNow;
            _remoteReceiveFramesInWindow = 0;
            _remotePlaybackFramesInWindow = 0;
            _remoteReceiveObservedFps = 0;
            _remotePlaybackObservedFps = 0;
            _remoteGpuPlaybackQueuePeak = 0;
            _remoteGpuPlaybackQueueTotal = 0;
            _remoteGpuPlaybackQueueSamples = 0;
            _remoteGpuPlaybackAverageQueue = 0;
            _lastRemoteRealtimeStatsLogUtc = DateTimeOffset.MinValue;
        }

        private void ResetRemoteScreenShareReceiveState(bool clearImage)
        {
            StopRemoteGpuPlayback(clearSurface: clearImage);
            _remoteScreenLastReceivedAtUtc = null;
            _remoteScreenLastRenderedAtUtc = null;
            _remoteFallbackLastReceivedAtUtc = DateTimeOffset.MinValue;
            _remoteFallbackFirstReceivedAtUtc = DateTimeOffset.MinValue;
            _lastRemoteBitstreamWidth = 0;
            _lastRemoteBitstreamHeight = 0;
            _lastRemoteBitstreamResolutionAtUtc = DateTimeOffset.MinValue;
            _lastScreenShareMetadataTimestamp = 0;
            Interlocked.Exchange(ref _remoteRtpFrameCount, 0);
            Interlocked.Exchange(ref _remoteFallbackFrameCount, 0);
            Interlocked.Exchange(ref _remotePreviewFrameCount, 0);
            Interlocked.Exchange(ref _remoteRenderedFrameCount, 0);
            Interlocked.Exchange(ref _remoteRenderQueued, 0);
            ResetRemoteRealtimeCounters();
            _remoteEmptyDecodeCount = 0;
            _remoteDecodeFailureCount = 0;
            _remoteH264WaitingForKeyFrame = true;
            _remoteH264FramesDroppedWaitingForKeyFrame = 0;
            ResetScreenShareFlowDiagnostics();

            lock (_rtpFrameSync)
            {
                _pendingRemoteH264Frames.Clear();
                _pendingRtpFrameSequence = 0;
                _remoteDecodeGeneration++;
            }

            lock (_remoteRenderSync)
            {
                _pendingRemoteRenderFrame = null;
                _pendingRemoteRenderWidth = 0;
                _pendingRemoteRenderHeight = 0;
                _pendingRemoteRenderQueuedAtUtc = default;
            }

            lock (_remoteDecoderSync)
            {
                try
                {
                    _rtpH264Decoder?.Dispose();
                }
                catch
                {
                }

                _rtpH264Decoder = null;
                _rtpDecoderWidth = 0;
                _rtpDecoderHeight = 0;
            }

            if (clearImage)
            {
                RemoteScreenImage.Source = null;
                _remoteScreenBitmap = null;
                _remoteScreenBitmapWidth = 0;
                _remoteScreenBitmapHeight = 0;
            }
        }

        private void UpdateScreenShareStats(NativeScreenFrameEventArgs e)
        {
            _screenShareLastWidth = e.Width;
            _screenShareLastHeight = e.Height;
            _screenShareLastBytes = e.FrameData.LongLength;
            _screenShareLastQuality = e.QualityName;
            _screenShareLastFrameAtUtc = DateTimeOffset.UtcNow;
            _screenShareFrameCounter++;

            var elapsed = _screenShareLastFrameAtUtc.Value - _screenShareFpsWindowStartedUtc;
            if (elapsed.TotalSeconds >= 1)
            {
                _screenShareObservedFps = _screenShareFrameCounter / elapsed.TotalSeconds;
                _screenShareFrameCounter = 0;
                _screenShareFpsWindowStartedUtc = _screenShareLastFrameAtUtc.Value;
            }
        }

        private void Realtime_ScreenShareStarted(object? sender, (string CallId, long FromUserId) e)
        {
            TryEnqueueOnUi(() =>
            {
                var session = NativeCallCoordinator.Instance.CurrentSession;
                if (e.CallId != session.CallId)
                    return;

                var wasAlreadyAccepted = IsRemoteScreenShareAccepted(e.FromUserId);
                ResetRemoteScreenShareReceiveState(clearImage: true);
                _isRemoteVideoVisible = false;
                _remoteScreenShareSenderId = e.FromUserId;
                _ = ResetScreenSharePeerAsync(e.FromUserId, "remote screen-share restarted");

                if (wasAlreadyAccepted)
                {
                    _acceptedRemoteScreenShareUserIds.Add(e.FromUserId);
                    _pendingRemoteScreenShareUserIds.Remove(e.FromUserId);
                    _remoteScreenShareWatchPromptSignature = "";
                    DiagnosticLogService.WriteLine($"[ScreenShare:WATCH] Retaining accepted watch state for participant {e.FromUserId} after fresh start signal.");
                    RefreshRemoteScreenShareWatchPrompt();
                    ShowRemoteScreenShareLoading(
                        $"{GetParticipantDisplayName(e.FromUserId)} is sharing their screen",
                        "Connecting to the live screen share...");
                }
                else
                {
                    _isRemoteScreenShareLoading = false;
                    _pendingRemoteScreenShareUserIds.Add(e.FromUserId);
                    _acceptedRemoteScreenShareUserIds.Remove(e.FromUserId);
                    ShowRemoteScreenShareWatchPrompt();
                }

                UpdateMediaLayerVisibility();
                NativeCallCoordinator.Instance.SetStatus(
                    session.State,
                    $"{GetParticipantDisplayName(e.FromUserId)} is ready to stream.",
                    session.PeerText);
            });
        }

        private void Realtime_ScreenShareStopped(object? sender, (string CallId, long FromUserId) e)
        {
            TryEnqueueOnUi(() =>
            {
                var session = NativeCallCoordinator.Instance.CurrentSession;
                if (e.CallId != session.CallId)
                    return;

                var displayName = GetParticipantDisplayName(e.FromUserId);
                var stoppedText = $"{displayName} stopped screen sharing";
                _isRemoteVideoVisible = false;
                _isRemoteScreenShareLoading = false;
                _pendingRemoteScreenShareUserIds.Remove(e.FromUserId);
                _acceptedRemoteScreenShareUserIds.Remove(e.FromUserId);
                if (_remoteScreenShareSenderId == e.FromUserId)
                    _remoteScreenShareSenderId = 0;
                ResetRemoteScreenShareReceiveState(clearImage: true);
                _ = ResetScreenSharePeerAsync(e.FromUserId, "remote screen-share stopped");
                RemotePlaceholderTitleText.Text = stoppedText;
                RemotePlaceholderSubtitleText.Text = $"User {e.FromUserId} has stopped their screen share.";
                MediaOverlayText.Text = stoppedText;
                RefreshRemoteScreenShareWatchPrompt();
                UpdateMediaLayerVisibility();
                NativeCallCoordinator.Instance.SetStatus(session.State, stoppedText, session.PeerText);
            });
        }

        private void Realtime_ScreenShareMetadataReceived(object? sender, ScreenShareMetadataEventArgs e)
        {
            var session = NativeCallCoordinator.Instance.CurrentSession;
            if (!IsCurrentCallSignal(session, e.CallId) || e.Width <= 0 || e.Height <= 0)
                return;

            TryEnqueueOnUi(() =>
            {
                if (e.Timestamp > 0 &&
                    _lastScreenShareMetadataTimestamp > 0 &&
                    e.Timestamp < _lastScreenShareMetadataTimestamp)
                {
                    Debug.WriteLine($"[ScreenShare:H264] Ignored stale metadata {e.Width}x{e.Height}; ts={e.Timestamp}; latest={_lastScreenShareMetadataTimestamp}.");
                    return;
                }

                if (e.Timestamp > 0)
                    _lastScreenShareMetadataTimestamp = e.Timestamp;

                if (_lastRemoteBitstreamWidth > 0 &&
                    _lastRemoteBitstreamHeight > 0 &&
                    DateTimeOffset.UtcNow - _lastRemoteBitstreamResolutionAtUtc < TimeSpan.FromSeconds(3) &&
                    (_lastRemoteBitstreamWidth != e.Width || _lastRemoteBitstreamHeight != e.Height))
                {
                    Debug.WriteLine($"[ScreenShare:H264] Ignored metadata {e.Width}x{e.Height}; recent H.264 SPS says {_lastRemoteBitstreamWidth}x{_lastRemoteBitstreamHeight}. ts={e.Timestamp}.");
                    return;
                }

                var hadPreviousResolution = _screenShareLastWidth > 0 && _screenShareLastHeight > 0;
                var resolutionChanged = hadPreviousResolution &&
                    (_screenShareLastWidth != e.Width || _screenShareLastHeight != e.Height);
                _remoteScreenShareSenderId = e.FromUserId;
                _screenShareLastWidth = e.Width;
                _screenShareLastHeight = e.Height;
                _screenShareLastQuality = GetResolutionTier(e.Height);
                _screenShareLastFrameAtUtc = DateTimeOffset.UtcNow;

                if (!_remoteScreenLastRenderedAtUtc.HasValue)
                {
                    if (IsRemoteScreenShareAccepted(e.FromUserId))
                    {
                        ShowRemoteScreenShareLoading(
                            $"{GetParticipantDisplayName(e.FromUserId)} is sharing their screen",
                            $"Preparing {e.Width} x {e.Height} stream...");
                    }
                    else
                    {
                        _pendingRemoteScreenShareUserIds.Add(e.FromUserId);
                        ShowRemoteScreenShareWatchPrompt();
                    }

                    UpdateMediaLayerVisibility();
                }

                if (resolutionChanged)
                {
                    Debug.WriteLine($"[ScreenShare:H264] Metadata resolution changed to {e.Width}x{e.Height}; ts={e.Timestamp}; clearing decoder pipeline.");
                    ResetRemoteH264Decoder("sender screen-share quality changed");
                }
            });
        }

        private void Realtime_ScreenShareQosReceived(object? sender, ScreenShareQosEventArgs e)
        {
            var session = NativeCallCoordinator.Instance.CurrentSession;
            if (e.CallId != session.CallId || !_isSharingScreen)
                return;

            NativeScreenShareStreamingService.Instance.ReportSendCongestion(
                e.Reason,
                e.DroppedReceiveFrames,
                e.RenderBacklog);

            Debug.WriteLine($"[ScreenShare:RTP] Receiver QoS from {e.FromUserId}: {e.Reason}; dropped={e.DroppedReceiveFrames}; renderBacklog={e.RenderBacklog}");

            if (IsReceiverRtpRestartRequest(e.Reason))
                _ = RestartScreenShareRtpOfferForParticipantAsync(e.FromUserId, session.CallId, e.Reason);
        }

        private void Realtime_ScreenFrameReceived(object? sender, ScreenFrameEventArgs e)
        {
            TryEnqueueOnUi(async () =>
            {
                var session = NativeCallCoordinator.Instance.CurrentSession;
                if (e.CallId != session.CallId || e.FrameData.Length == 0)
                {
                    Debug.WriteLine($"[ScreenShare:H264] Ignored incoming frame callId={e.CallId} sessionCallId={session.CallId} bytes={e.FrameData.Length}");
                    return;
                }

                if (!ShouldRenderRemoteScreenShareFrame(e.FromUserId, "preview"))
                {
                    await TryRenderRemoteScreenShareWatchPreviewAsync(e);
                    return;
                }

                var previewFrames = Interlocked.Increment(ref _remotePreviewFrameCount);
                if (previewFrames == 1 || previewFrames % (PreviewFallbackMaxFps * 2) == 0)
                {
                    Debug.WriteLine(
                        $"[ScreenShare:PREVIEW] Received {previewFrames} JPEG preview frames from {e.FromUserId}; bytes={e.FrameData.Length}; {e.Width}x{e.Height}.");
                }

                var now = DateTimeOffset.UtcNow;
                if (_remoteScreenLastRenderedAtUtc.HasValue &&
                    now - _remoteScreenLastRenderedAtUtc.Value < TimeSpan.FromMilliseconds(350) &&
                    Volatile.Read(ref _remoteRenderedFrameCount) > 0)
                {
                    LogRemotePreviewFlow(e, rendered: false);
                    return;
                }

                _remoteScreenShareSenderId = e.FromUserId;
                _remoteScreenLastReceivedAtUtc = now;
                try
                {
                    await RenderScreenFrameAsync(e.FrameData, RemoteScreenImage);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[ScreenShare:PREVIEW] Render failed from {e.FromUserId}: {ex}");
                    return;
                }

                _isRemoteVideoVisible = true;
                _isRemoteScreenShareLoading = false;
                _remoteScreenLastRenderedAtUtc = DateTimeOffset.UtcNow;
                LogRemotePreviewFlow(e, rendered: true);
                UpdateMediaLayerVisibility();
                MediaOverlayText.Text = $"Screen preview {e.Width} x {e.Height}";
            });
        }

        private void Realtime_EncodedScreenFrameReceived(object? sender, ScreenFrameEventArgs e)
        {
            var session = NativeCallCoordinator.Instance.CurrentSession;
            if (e.CallId != session.CallId || e.FrameData.Length == 0)
                return;

            if (!ShouldRenderRemoteScreenShareFrame(e.FromUserId, "websocket"))
                return;

            if (Volatile.Read(ref _remoteRtpFrameCount) > 0 &&
                _remoteScreenLastRenderedAtUtc.HasValue &&
                DateTimeOffset.UtcNow - _remoteScreenLastRenderedAtUtc.Value < TimeSpan.FromMilliseconds(500))
            {
                return;
            }

            if (e.Width > 0 && e.Height > 0)
            {
                if (_screenShareLastWidth > 0 &&
                    _screenShareLastHeight > 0 &&
                    (_screenShareLastWidth != e.Width || _screenShareLastHeight != e.Height))
                {
                    ResetRemoteH264Decoder("sender screen-share resolution changed");
                }

                _screenShareLastWidth = e.Width;
                _screenShareLastHeight = e.Height;
                _screenShareLastQuality = GetResolutionTier(e.Height);
            }

            _screenShareLastBytes = e.FrameData.LongLength;
            _screenShareLastFrameAtUtc = DateTimeOffset.UtcNow;
            _remoteScreenShareSenderId = e.FromUserId;

            if (e.Codec.Equals("h264", StringComparison.OrdinalIgnoreCase) && e.Width > 0 && e.Height > 0)
            {
                var hasIdr = ContainsH264IdrFrame(e.FrameData);
                var isKeyFrame = hasIdr;
                var now = DateTimeOffset.UtcNow;
                _remoteFallbackLastReceivedAtUtc = now;
                if (_remoteFallbackFirstReceivedAtUtc == DateTimeOffset.MinValue)
                    _remoteFallbackFirstReceivedAtUtc = now;

                var receivedFallbackFrames = Interlocked.Increment(ref _remoteFallbackFrameCount);
                LogRemoteEncodedReceiveFlow("websocket", e.FromUserId, e.FrameData, e.Width, e.Height, e.IsKeyFrame, hasIdr, receivedFallbackFrames);
                if (receivedFallbackFrames == 1 || receivedFallbackFrames % (NativeScreenShareStreamingService.TargetFps * 2) == 0)
                {
                    Debug.WriteLine(
                        $"[ScreenShare:FALLBACK] Received {receivedFallbackFrames} WebSocket H.264 frames from {e.FromUserId}; bytes={e.FrameData.Length}; {e.Width}x{e.Height}; keyFlag={e.IsKeyFrame}; idr={hasIdr}.");
                }

                var noRtpFrames = Volatile.Read(ref _remoteRtpFrameCount) == 0;
                var fallbackWarmupElapsed = now - _remoteFallbackFirstReceivedAtUtc;
                if (noRtpFrames && fallbackWarmupElapsed < WebSocketFallbackGpuStartDelay)
                {
                    if (receivedFallbackFrames == 1 ||
                        ShouldLogFlow(ref _lastRemoteFallbackHoldLogUtc, TimeSpan.FromSeconds(1)))
                    {
                        Debug.WriteLine(
                            $"[ScreenShare:FALLBACK] Holding WebSocket H.264 warmup frame until RTP starts; elapsedMs={fallbackWarmupElapsed.TotalMilliseconds:0}; bytes={e.FrameData.Length}; key={hasIdr}.");
                    }

                    return;
                }

                if (noRtpFrames)
                {
                    ResetRemoteH264Decoder("receiver switched to websocket H.264 stream after RTP startup timeout");
                }
                else if (receivedFallbackFrames == 1)
                {
                    Debug.WriteLine("[ScreenShare:H264] WebSocket H.264 backup stream is active; keeping existing decoder to avoid resetting during RTP decode.");
                }

                QueueRemoteH264Frame(e.FrameData, e.Width, e.Height, isKeyFrame, "receiver websocket H.264 backlog");
                return;
            }

            TryEnqueueOnUi(async () =>
            {
                try
                {
                    await RenderScreenFrameAsync(e.FrameData, RemoteScreenImage);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[ScreenShare:PREVIEW] Encoded fallback render failed from {e.FromUserId}; codec={e.Codec}; bytes={e.FrameData.Length}: {ex}");
                    return;
                }

                _isRemoteVideoVisible = true;
                _isRemoteScreenShareLoading = false;
                _remoteScreenLastRenderedAtUtc = DateTimeOffset.UtcNow;
                UpdateMediaLayerVisibility();
                MediaOverlayText.Text = $"{e.Codec.ToUpperInvariant()} stream {e.Width} x {e.Height}";
            });
        }

        private async void Realtime_WebRtcOfferReceived(object? sender, WebRtcOfferEventArgs e)
        {
            var session = NativeCallCoordinator.Instance.CurrentSession;
            if (!IsCurrentCallSignal(session, e.CallId))
                return;

            try
            {
                Debug.WriteLine($"[ScreenShare:RTP] Offer received from participant {e.FromUserId}: type={e.SdpType}, sdpLength={e.Sdp?.Length ?? 0}.");
                AddCallParticipant(e.FromUserId);
                _pendingRemoteScreenShareUserIds.Add(e.FromUserId);
                if (!IsRemoteScreenShareAccepted(e.FromUserId))
                {
                    TryEnqueueOnUi(() =>
                    {
                        ShowRemoteScreenShareWatchPrompt();
                        UpdateMediaLayerVisibility();
                    });
                }

                await ResetScreenSharePeerAsync(e.FromUserId, "new remote offer");
                var peer = GetOrCreateScreenSharePeer(e.FromUserId);
                await peer.SetRemoteOfferAsync(new SessionDescriptionModel
                {
                    Type = e.SdpType,
                    Sdp = e.Sdp
                });

                var answer = await peer.CreateAnswerAsync();
                await SocialManager.Instance.Realtime.SendAnswerAsync(e.FromUserId, e.CallId, answer.Sdp, answer.Type);
                Debug.WriteLine($"[ScreenShare:RTP] Answer sent to participant {e.FromUserId}: type={answer.Type}, sdpLength={answer.Sdp.Length}.");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ScreenShare:RTP] Offer handling failed for participant {e.FromUserId}: {ex}");
            }
        }

        private async void Realtime_WebRtcAnswerReceived(object? sender, WebRtcAnswerEventArgs e)
        {
            var session = NativeCallCoordinator.Instance.CurrentSession;
            if (!IsCurrentCallSignal(session, e.CallId) || !_screenSharePeers.TryGetValue(e.FromUserId, out var peer))
                return;

            try
            {
                Debug.WriteLine($"[ScreenShare:RTP] Answer received from participant {e.FromUserId}: type={e.SdpType}, sdpLength={e.Sdp?.Length ?? 0}.");
                await peer.SetRemoteAnswerAsync(new SessionDescriptionModel
                {
                    Type = e.SdpType,
                    Sdp = e.Sdp
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ScreenShare:RTP] Answer handling failed for participant {e.FromUserId}: {ex}");
            }
        }

        private async void Realtime_WebRtcIceReceived(object? sender, WebRtcIceEventArgs e)
        {
            var session = NativeCallCoordinator.Instance.CurrentSession;
            if (!IsCurrentCallSignal(session, e.CallId))
                return;

            try
            {
                var peer = GetOrCreateScreenSharePeer(e.FromUserId);
                await peer.AddIceCandidateAsync(new IceCandidateModel
                {
                    Candidate = e.Candidate,
                    Mid = e.Mid,
                    MLineIndex = e.MLineIndex
                });
                Debug.WriteLine($"[ScreenShare:RTP] ICE candidate applied from participant {e.FromUserId}: mid={e.Mid}, index={e.MLineIndex}.");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ScreenShare:RTP] ICE candidate failed from participant {e.FromUserId}: {ex}");
            }
        }

        private async Task StartScreenShareRtpOfferAsync(long participantId, string callId)
        {
            NativePeerConnectionService? peer = null;

            try
            {
                Debug.WriteLine($"[ScreenShare:RTP] Starting offer for participant {participantId}.");
                peer = GetOrCreateScreenSharePeer(participantId);
                await peer.AttachScreenShareAsync(null!);

                var offer = await peer.CreateOfferAsync();
                await SocialManager.Instance.Realtime.SendOfferAsync(participantId, callId, offer.Sdp, offer.Type);
                Debug.WriteLine($"[ScreenShare:RTP] Offer sent to participant {participantId}.");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ScreenShare:RTP] Offer failed for participant {participantId}: {ex}");

                if (peer != null)
                    await RemoveScreenSharePeerAsync(participantId, peer);
            }
        }

        private async Task SendEncodedVideoFrameToPeerAsync(long participantId, NativePeerConnectionService peer, byte[] frameData)
        {
            try
            {
                await peer.SendEncodedVideoFrameAsync(frameData);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ScreenShare:RTP] Encoded frame send failed for participant {participantId}: {ex.Message}");
                await RemoveScreenSharePeerAsync(participantId, peer);
            }
        }

        private async Task RemoveScreenSharePeerAsync(long participantId, NativePeerConnectionService peer)
        {
            if (_screenSharePeers.TryGetValue(participantId, out var existingPeer) &&
                ReferenceEquals(existingPeer, peer))
            {
                _screenSharePeers.Remove(participantId);
            }

            try
            {
                await peer.CloseAsync();
            }
            catch
            {
            }
        }

        private async Task ResetScreenSharePeerAsync(long participantId, string reason)
        {
            if (!_screenSharePeers.TryGetValue(participantId, out var peer))
                return;

            Debug.WriteLine($"[ScreenShare:RTP] Resetting participant {participantId} peer: {reason}.");
            await RemoveScreenSharePeerAsync(participantId, peer);
        }

        private NativePeerConnectionService GetOrCreateScreenSharePeer(long participantId)
        {
            if (_screenSharePeers.TryGetValue(participantId, out var existingPeer))
                return existingPeer;

            var peer = new NativePeerConnectionService($"participant {participantId}");
            peer.LocalIceCandidateReady += async (_, ice) =>
            {
                var session = NativeCallCoordinator.Instance.CurrentSession;
                if (!string.IsNullOrWhiteSpace(session.CallId))
                {
                    await SocialManager.Instance.Realtime.SendIceCandidateAsync(
                        participantId,
                        session.CallId,
                        ice.Candidate,
                        ice.Mid,
                        ice.MLineIndex);
                    Debug.WriteLine($"[ScreenShare:RTP] ICE candidate sent to participant {participantId}: mid={ice.Mid}, index={ice.MLineIndex}.");
                }
            };
            peer.EncodedVideoFrameReceived += ScreenSharePeer_EncodedVideoFrameReceived;
            peer.ConnectionStateChanged += (_, state) =>
            {
                _ = HandleScreenSharePeerConnectionStateChangedAsync(participantId, state);
            };

            _screenSharePeers[participantId] = peer;
            return peer;
        }

        private async Task HandleScreenSharePeerConnectionStateChangedAsync(long participantId, string state)
        {
            if (!state.Equals("failed", StringComparison.OrdinalIgnoreCase) &&
                !state.Equals("disconnected", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var session = NativeCallCoordinator.Instance.CurrentSession;
            if (!_isSharingScreen ||
                !NativeScreenShareStreamingService.Instance.IsRunning ||
                session.State != NativeCallState.Connected ||
                string.IsNullOrWhiteSpace(session.CallId))
            {
                return;
            }

            var now = DateTimeOffset.UtcNow;
            lock (_screenSharePeerRestartSync)
            {
                if (_screenSharePeerRestartLastAttemptUtc.TryGetValue(participantId, out var lastAttempt) &&
                    now - lastAttempt < TimeSpan.FromSeconds(3))
                {
                    return;
                }

                _screenSharePeerRestartLastAttemptUtc[participantId] = now;
            }

            try
            {
                Debug.WriteLine($"[ScreenShare:RTP] Restarting participant {participantId} RTP offer after connectionState={state}.");
                await ResetScreenSharePeerAsync(participantId, $"RTP connection {state}");
                await StartScreenShareRtpOfferAsync(participantId, session.CallId);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ScreenShare:RTP] Restart after connectionState={state} failed for participant {participantId}: {ex.Message}");
            }
        }

        private async Task RestartScreenShareRtpOfferForParticipantAsync(long participantId, string callId, string reason)
        {
            var session = NativeCallCoordinator.Instance.CurrentSession;
            if (!_isSharingScreen ||
                !NativeScreenShareStreamingService.Instance.IsRunning ||
                session.State != NativeCallState.Connected ||
                string.IsNullOrWhiteSpace(callId))
            {
                return;
            }

            var now = DateTimeOffset.UtcNow;
            lock (_screenSharePeerRestartSync)
            {
                if (_screenSharePeerRestartLastAttemptUtc.TryGetValue(participantId, out var lastAttempt) &&
                    now - lastAttempt < TimeSpan.FromSeconds(4))
                {
                    Debug.WriteLine($"[ScreenShare:RTP] Ignored receiver media-stall restart for participant {participantId}; restart is cooling down. reason={reason}");
                    return;
                }

                _screenSharePeerRestartLastAttemptUtc[participantId] = now;
            }

            try
            {
                Debug.WriteLine($"[ScreenShare:RTP] Receiver requested a fresh RTP offer for participant {participantId}: {reason}");
                await ResetScreenSharePeerAsync(participantId, $"receiver media stall: {reason}");
                await SocialManager.Instance.Realtime.SendScreenShareStartedAsync(participantId, callId);
                await StartScreenShareRtpOfferAsync(participantId, callId);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ScreenShare:RTP] Receiver media-stall restart failed for participant {participantId}: {ex}");
            }
        }

        private static bool IsReceiverRtpRestartRequest(string reason)
        {
            return reason.Contains("restart screen-share offer", StringComparison.OrdinalIgnoreCase) ||
                reason.Contains("RTP stalled", StringComparison.OrdinalIgnoreCase);
        }

        private void ScreenSharePeer_EncodedVideoFrameReceived(object? sender, NativeRtpVideoFrameEventArgs e)
        {
            var fromUserId = GetScreenSharePeerParticipantId(sender);
            if (fromUserId > 0 && !ShouldRenderRemoteScreenShareFrame(fromUserId, "rtp"))
                return;

            var width = _lastRemoteBitstreamWidth;
            var height = _lastRemoteBitstreamHeight;
            var isKeyFrame = ContainsH264IdrFrame(e.FrameData);
            if (TryReadH264Dimensions(e.FrameData, out var bitstreamWidth, out var bitstreamHeight))
            {
                if (!IsPlausibleRemoteBitstreamResolution(bitstreamWidth, bitstreamHeight, width, height))
                {
                    Debug.WriteLine($"[ScreenShare:H264] Ignored implausible RTP SPS size {bitstreamWidth}x{bitstreamHeight}; keeping {width}x{height}.");
                }
                else
                {
                    if (bitstreamWidth != width || bitstreamHeight != height)
                    {
                        Debug.WriteLine($"[ScreenShare:H264] RTP bitstream SPS overrides assumed size {width}x{height} -> {bitstreamWidth}x{bitstreamHeight}.");
                    }

                    width = bitstreamWidth;
                    height = bitstreamHeight;
                    _lastRemoteBitstreamWidth = bitstreamWidth;
                    _lastRemoteBitstreamHeight = bitstreamHeight;
                    _lastRemoteBitstreamResolutionAtUtc = DateTimeOffset.UtcNow;
                    _screenShareLastWidth = bitstreamWidth;
                    _screenShareLastHeight = bitstreamHeight;
                    _screenShareLastQuality = GetResolutionTier(bitstreamHeight);
                }
            }

            if (width <= 0 || height <= 0)
            {
                _remoteScreenLastReceivedAtUtc = DateTimeOffset.UtcNow;
                _screenShareDroppedReceiveFrames++;
                _ = SendScreenShareQosIfNeededAsync("receiver waiting for H.264 SPS dimensions");
                RequestRemoteVideoKeyFrame("receiver needs an IDR with SPS before starting GPU playback");
                if (ShouldLogFlow(ref _lastRemoteGpuPlaybackDropLogUtc, TimeSpan.FromSeconds(1)))
                {
                    Debug.WriteLine(
                        $"[ScreenShare:H264] Dropped RTP frame before trusted bitstream dimensions were known; bytes={e.FrameData.Length}; key={isKeyFrame}. Waiting for an IDR/SPS instead of opening the hardware player with stale metadata.");
                }
                return;
            }

            var receivedRtpFrames = Interlocked.Increment(ref _remoteRtpFrameCount);
            if (receivedRtpFrames == 1)
                _remoteFallbackFirstReceivedAtUtc = DateTimeOffset.MinValue;

            if (fromUserId > 0)
                _remoteScreenShareSenderId = fromUserId;

            LogRemoteEncodedReceiveFlow("rtp", _remoteScreenShareSenderId, e.FrameData, width, height, isKeyFrame, isKeyFrame, receivedRtpFrames);
            if (receivedRtpFrames == 1 || receivedRtpFrames % (NativeScreenShareStreamingService.TargetFps * 2) == 0)
            {
                Debug.WriteLine(
                    $"[ScreenShare:RTP] Received {receivedRtpFrames} remote RTP H.264 frames: bytes={e.FrameData.Length}, assumed={width}x{height}, key={isKeyFrame}.");
            }

            if (DateTimeOffset.UtcNow - _remoteFallbackLastReceivedAtUtc < RemoteFallbackRtpSuppressWindow)
            {
                if (receivedRtpFrames == 1 || receivedRtpFrames % (NativeScreenShareStreamingService.TargetFps * 2) == 0)
                    Debug.WriteLine("[ScreenShare:RTP] RTP frame arrived while WebSocket backup was active; preferring RTP for lower latency.");

                _remoteFallbackLastReceivedAtUtc = DateTimeOffset.MinValue;
            }

            QueueRemoteH264Frame(e.FrameData, width, height, isKeyFrame, "receiver RTP backlog");
        }

        private void LogScreenShareSendSummary(int participantCount, bool sentToAnyPeer, int fallbackCount, NativeScreenFrameEventArgs e, TimeSpan sendElapsed)
        {
            var now = DateTimeOffset.UtcNow;
            if (now - _lastScreenShareSendSummaryUtc < TimeSpan.FromSeconds(2))
                return;

            _lastScreenShareSendSummaryUtc = now;
            var peerSummary = _screenSharePeers.Count == 0
                ? "no peers"
                : string.Join(", ", _screenSharePeers.Select(pair => $"{pair.Key}:{pair.Value.ConnectionState}/sent={pair.Value.SentVideoFrames}/recv={pair.Value.ReceivedVideoFrames}"));

            var message = $"[ScreenShare:RTP] Send summary: participants={participantCount}; rtpSent={_screenShareRtpFrames}; fallbackSent={_screenShareFallbackFrames}; fallbackTargets={fallbackCount}; sentToAnyPeer={sentToAnyPeer}; sendMs={sendElapsed.TotalMilliseconds:0.0}; frameBytes={e.FrameData.Length}; previewBytes={e.PreviewFrameData.Length}; previewSent={_screenSharePreviewFallbackFrames}; key={e.IsKeyFrame}; peers=[{peerSummary}].";
            Debug.WriteLine(message);
            DiagnosticLogService.WriteLine(message);
        }

        private void QueueRemoteH264Frame(byte[] frameData, int width, int height, bool isKeyFrame, string backlogReason)
        {
            _remoteScreenLastReceivedAtUtc = DateTimeOffset.UtcNow;
            var hasIdr = ContainsH264IdrFrame(frameData);
            if (isKeyFrame && !hasIdr)
            {
                Debug.WriteLine(
                    $"[ScreenShare:FLOW:QUEUE] Keyframe flag ignored because compressed frame has no IDR. bytes={frameData.Length}; {width}x{height}; reason={backlogReason}.");
            }

            isKeyFrame = hasIdr;

            if (isKeyFrame)
            {
                _remoteH264WaitingForKeyFrame = false;
                _remoteH264FramesDroppedWaitingForKeyFrame = 0;
            }

            if (QueueRemoteGpuPlaybackFrame(frameData, width, height, isKeyFrame, backlogReason))
                return;

            long sequence;
            int queueCount;
            lock (_rtpFrameSync)
            {
                if (_remoteH264WaitingForKeyFrame)
                {
                    if (!isKeyFrame)
                    {
                        _screenShareDroppedReceiveFrames++;
                        _remoteH264FramesDroppedWaitingForKeyFrame++;
                        if (_remoteH264FramesDroppedWaitingForKeyFrame == 1 ||
                            _remoteH264FramesDroppedWaitingForKeyFrame % NativeScreenShareStreamingService.TargetFps == 0)
                        {
                            Debug.WriteLine(
                                $"[ScreenShare:H264] Waiting for keyframe; dropped {_remoteH264FramesDroppedWaitingForKeyFrame} delta frames after decoder reset.");
                        }

                        _ = SendScreenShareQosIfNeededAsync("receiver waiting for keyframe");
                        return;
                    }

                    _remoteH264WaitingForKeyFrame = false;
                    _remoteH264FramesDroppedWaitingForKeyFrame = 0;
                    _pendingRemoteH264Frames.Clear();
                    Debug.WriteLine($"[ScreenShare:H264] Keyframe received; restarting remote decoder at {width}x{height}.");
                }

                PruneRemoteH264Backlog(backlogReason);

                if (_remoteH264WaitingForKeyFrame)
                {
                    if (!isKeyFrame)
                    {
                        _screenShareDroppedReceiveFrames++;
                        _remoteH264FramesDroppedWaitingForKeyFrame++;
                        if (_remoteH264FramesDroppedWaitingForKeyFrame == 1 ||
                            _remoteH264FramesDroppedWaitingForKeyFrame % NativeScreenShareStreamingService.TargetFps == 0)
                        {
                            Debug.WriteLine(
                                $"[ScreenShare:H264] Waiting for keyframe after realtime backlog prune; dropped {_remoteH264FramesDroppedWaitingForKeyFrame} delta frames.");
                        }

                        _ = SendScreenShareQosIfNeededAsync("receiver waiting for keyframe after realtime backlog prune");
                        return;
                    }

                    _remoteH264WaitingForKeyFrame = false;
                    _remoteH264FramesDroppedWaitingForKeyFrame = 0;
                    _pendingRemoteH264Frames.Clear();
                    Debug.WriteLine($"[ScreenShare:H264] Keyframe received after realtime backlog prune; restarting remote decoder at {width}x{height}.");
                }

                if (_pendingRemoteH264Frames.Count >= MaxRemoteH264DecodeQueue * 2)
                {
                    var beforeDrop = _pendingRemoteH264Frames.Count;
                    _pendingRemoteH264Frames.Clear();
                    var dropped = beforeDrop;
                    _screenShareDroppedReceiveFrames += dropped;
                    _remoteBacklogTrimmedSinceLastLog += dropped;
                    _remoteDecodeGeneration++;
                    _ = SendScreenShareQosIfNeededAsync(backlogReason);

                    if (!isKeyFrame)
                    {
                        _screenShareDroppedReceiveFrames++;
                        _remoteH264WaitingForKeyFrame = true;
                        _remoteH264FramesDroppedWaitingForKeyFrame = 1;
                        RequestRemoteVideoKeyFrame("receiver decode queue overflow");
                        Debug.WriteLine($"[ScreenShare:H264] Decode queue pressure; dropped {dropped} queued frames and the current delta frame, then requested a fresh IDR to avoid H.264 colour corruption.");
                        return;
                    }

                    _remoteH264WaitingForKeyFrame = false;
                    _remoteH264FramesDroppedWaitingForKeyFrame = 0;
                    Debug.WriteLine($"[ScreenShare:H264] Decode queue pressure; dropped {dropped} queued frames and resumed from the current IDR keyframe.");
                }

                _pendingRtpFrameSequence++;
                sequence = _pendingRtpFrameSequence;
                var generation = _remoteDecodeGeneration;
                _pendingRemoteH264Frames.Enqueue(new PendingRemoteH264Frame(
                    frameData,
                    width,
                    height,
                    isKeyFrame,
                    sequence,
                    generation));
                queueCount = _pendingRemoteH264Frames.Count;
            }

            LogRemoteQueueFlow(sequence, queueCount, isKeyFrame, backlogReason);

            if (Interlocked.CompareExchange(ref _rtpDecodePumpRunning, 1, 0) == 0)
            {
                _ = Task.Factory.StartNew(
                    ProcessLatestRtpFramesAsync,
                    CancellationToken.None,
                    TaskCreationOptions.DenyChildAttach | TaskCreationOptions.LongRunning,
                    TaskScheduler.Default).Unwrap();
            }
        }

        private void PruneRemoteH264Backlog(string reason)
        {
            if (_pendingRemoteH264Frames.Count < MaxRemoteH264DecodeQueue)
                return;

            var frames = _pendingRemoteH264Frames.ToList();
            var latestKeyIndex = frames.FindLastIndex(frame => frame.IsKeyFrame);
            if (latestKeyIndex < 0)
            {
                DropRemoteBacklogAndRequestKeyFrame(reason, "receiver realtime queue contains only dependent delta frames");
                return;
            }

            if (latestKeyIndex == 0)
            {
                if (_pendingRemoteH264Frames.Count >= MaxRemoteH264DecodeQueue)
                    DropRemoteBacklogAndRequestKeyFrame(reason, "receiver realtime queue exceeded low-latency budget after current IDR");

                return;
            }

            _pendingRemoteH264Frames.Clear();
            for (var i = latestKeyIndex; i < frames.Count; i++)
                _pendingRemoteH264Frames.Enqueue(frames[i]);

            _screenShareDroppedReceiveFrames += latestKeyIndex;
            _remoteBacklogJumpedSinceLastLog += latestKeyIndex;
            _ = SendScreenShareQosIfNeededAsync(reason);
            if (ShouldLogFlow(ref _lastRemoteBacklogJumpLogUtc, TimeSpan.FromSeconds(2)))
            {
                Debug.WriteLine($"[ScreenShare:H264] Realtime backlog jumped to latest keyframe; dropped {_remoteBacklogJumpedSinceLastLog} queued frames; queue={_pendingRemoteH264Frames.Count}; reason={reason}.");
                _remoteBacklogJumpedSinceLastLog = 0;
            }
        }

        private void DropRemoteBacklogAndRequestKeyFrame(string reason, string keyFrameReason)
        {
            var dropped = _pendingRemoteH264Frames.Count;
            if (dropped <= 0)
                return;

            _pendingRemoteH264Frames.Clear();
            _screenShareDroppedReceiveFrames += dropped;
            _remoteBacklogTrimmedSinceLastLog += dropped;
            _remoteH264WaitingForKeyFrame = true;
            _remoteH264FramesDroppedWaitingForKeyFrame = 0;
            _remoteDecodeGeneration++;
            _ = SendScreenShareQosIfNeededAsync(reason);
            RequestRemoteVideoKeyFrame(keyFrameReason);
            if (ShouldLogFlow(ref _lastRemoteBacklogTrimLogUtc, TimeSpan.FromSeconds(1)))
            {
                Debug.WriteLine($"[ScreenShare:H264] 60fps receiver recovery dropped {_remoteBacklogTrimmedSinceLastLog} queued frames and requested a fresh IDR; queue={_pendingRemoteH264Frames.Count}; reason={reason}; keyRequest={keyFrameReason}.");
                _remoteBacklogTrimmedSinceLastLog = 0;
            }
        }

        private bool QueueRemoteGpuPlaybackFrame(byte[] frameData, int width, int height, bool isKeyFrame, string backlogReason)
        {
            if (width <= 0 || height <= 0 || frameData.Length == 0)
                return false;

            var shouldStartPlayback = false;
            lock (_remoteGpuPlaybackSync)
            {
                if (_remoteGpuPlaybackStopping)
                    return true;

                var dimensionsMatch = (_remoteGpuPlaybackEnabled || _remoteGpuPlaybackStarting) &&
                    _remoteGpuPlaybackWidth == width &&
                    _remoteGpuPlaybackHeight == height;

                if (!dimensionsMatch)
                {
                    if (!isKeyFrame)
                    {
                        _screenShareDroppedReceiveFrames++;
                        _ = SendScreenShareQosIfNeededAsync("receiver GPU playback waiting for IDR");
                        RequestRemoteVideoKeyFrame("receiver GPU playback needs an IDR to start");
                        return true;
                    }

                    var dropped = _pendingRemoteGpuPlaybackFrames.Count;
                    _pendingRemoteGpuPlaybackFrames.Clear();
                    _screenShareDroppedReceiveFrames += dropped;
                    _remoteGpuPlaybackStarting = true;
                    _remoteGpuPlaybackWidth = width;
                    _remoteGpuPlaybackHeight = height;
                    _remoteGpuWaitingForKeyFrame = false;
                    _remoteGpuBacklogKeyFrameRequested = false;
                    _remoteGpuFirstSampleSubmitted = false;
                    _remoteGpuEstimatedFrameDuration = RemoteGpuFrameDuration;
                    _remoteGpuLastQueuedAtUtc = null;
                    shouldStartPlayback = true;
                    if (dropped > 0)
                        Debug.WriteLine($"[ScreenShare:GPU:RECEIVER] Restarting hardware playback from IDR at {width}x{height}; dropped {dropped} stale compressed frames.");
                }
                else if (_remoteGpuPlaybackStarting && !_remoteGpuPlaybackEnabled && !isKeyFrame)
                {
                    _screenShareDroppedReceiveFrames++;
                    return true;
                }

                if (_remoteGpuWaitingForKeyFrame)
                {
                    if (!isKeyFrame)
                    {
                        _screenShareDroppedReceiveFrames++;
                        return true;
                    }

                    _pendingRemoteGpuPlaybackFrames.Clear();
                    _remoteGpuWaitingForKeyFrame = false;
                    _remoteGpuBacklogKeyFrameRequested = false;
                    _remoteGpuEstimatedFrameDuration = RemoteGpuFrameDuration;
                    _remoteGpuLastQueuedAtUtc = null;
                    Debug.WriteLine($"[ScreenShare:GPU:RECEIVER] Fresh IDR received; restarted compressed GPU playback queue at {width}x{height}.");
                }

                if (isKeyFrame && _remoteGpuBacklogKeyFrameRequested && _pendingRemoteGpuPlaybackFrames.Count > 0)
                {
                    var dropped = _pendingRemoteGpuPlaybackFrames.Count;
                    _pendingRemoteGpuPlaybackFrames.Clear();
                    _screenShareDroppedReceiveFrames += dropped;
                    _remoteBacklogTrimmedSinceLastLog += dropped;
                    _remoteGpuBacklogKeyFrameRequested = false;
                    _remoteGpuEstimatedFrameDuration = RemoteGpuFrameDuration;
                    _remoteGpuLastQueuedAtUtc = null;
                    Debug.WriteLine($"[ScreenShare:GPU:RECEIVER] Fresh IDR caught up realtime playback; dropped {dropped} old dependent compressed frames before decode.");
                }

                if (!isKeyFrame &&
                    !_remoteGpuBacklogKeyFrameRequested &&
                    _pendingRemoteGpuPlaybackFrames.Count >= RemoteGpuBacklogKeyFrameRequestQueue)
                {
                    _remoteGpuBacklogKeyFrameRequested = true;
                    var keyFrameReason = "receiver GPU playback keyframe requested after realtime queue pressure";
                    _ = SendScreenShareQosIfNeededAsync(keyFrameReason);
                    RequestRemoteVideoKeyFrame(keyFrameReason);
                    if (ShouldLogFlow(ref _lastRemoteGpuPlaybackDropLogUtc, TimeSpan.FromSeconds(1)))
                    {
                        Debug.WriteLine($"[ScreenShare:GPU:RECEIVER] GPU playback queue reached {RemoteGpuBacklogKeyFrameRequestQueue}; preserving H.264 dependency chain while requesting a fresh IDR. queue={_pendingRemoteGpuPlaybackFrames.Count}; reason={backlogReason}.");
                        _remoteBacklogTrimmedSinceLastLog = 0;
                    }
                }

                if (_pendingRemoteGpuPlaybackFrames.Count >= MaxRemoteGpuPlaybackQueue)
                {
                    if (isKeyFrame)
                    {
                        var dropped = _pendingRemoteGpuPlaybackFrames.Count;
                        _pendingRemoteGpuPlaybackFrames.Clear();
                        _screenShareDroppedReceiveFrames += dropped;
                        _remoteBacklogTrimmedSinceLastLog += dropped;
                        _remoteGpuBacklogKeyFrameRequested = false;
                        Debug.WriteLine($"[ScreenShare:GPU:RECEIVER] GPU playback queue caught up from a fresh IDR; dropped {dropped} old compressed frames before decode.");
                    }
                    else
                    {
                        if (ShouldLogFlow(ref _lastRemoteGpuPlaybackDropLogUtc, TimeSpan.FromSeconds(1)))
                        {
                            Debug.WriteLine($"[ScreenShare:GPU:RECEIVER] GPU playback queue reached {MaxRemoteGpuPlaybackQueue}; dropping incoming delta until a fresh IDR arrives to avoid corrupting the H.264 chain. reason={backlogReason}.");
                            _remoteBacklogTrimmedSinceLastLog = 0;
                        }

                        _screenShareDroppedReceiveFrames++;
                        return true;
                    }
                }

                _remoteGpuPlaybackSequence++;
                var queuedAtUtc = DateTimeOffset.UtcNow;
                var sampleDuration = EstimateRemoteGpuSampleDuration(queuedAtUtc);
                _pendingRemoteGpuPlaybackFrames.Enqueue(new PendingRemoteH264Frame(
                    frameData,
                    width,
                    height,
                    isKeyFrame,
                    _remoteGpuPlaybackSequence,
                    Volatile.Read(ref _remoteDecodeGeneration),
                    sampleDuration));
                var queuedCount = _pendingRemoteGpuPlaybackFrames.Count;
                _remoteGpuPlaybackQueuePeak = Math.Max(_remoteGpuPlaybackQueuePeak, queuedCount);
                if (queuedCount >= RemoteGpuBacklogKeyFrameRequestQueue / 2 &&
                    ShouldLogFlow(ref _lastRemoteGpuPlaybackDropLogUtc, TimeSpan.FromSeconds(2)))
                {
                    Debug.WriteLine($"[ScreenShare:GPU:RECEIVER] 60fps queue watch: queue={queuedCount}; requestThreshold={RemoteGpuBacklogKeyFrameRequestQueue}; hardLimit={MaxRemoteGpuPlaybackQueue}; receiveFps={_remoteReceiveObservedFps:0.0}; playbackFps={_remotePlaybackObservedFps:0.0}; reason={backlogReason}.");
                }
                Monitor.PulseAll(_remoteGpuPlaybackSync);
            }

            if (shouldStartPlayback)
                TryEnqueueOnUi(() => StartRemoteGpuPlayback(width, height));

            _remoteScreenLastRenderedAtUtc = DateTimeOffset.UtcNow;
            _isRemoteVideoVisible = true;
            _isRemoteScreenShareLoading = false;
            TryEnqueueOnUi(() =>
            {
                RemoteScreenImage.Visibility = Visibility.Collapsed;
                RemoteScreenPlayer.Visibility = Visibility.Visible;
                UpdateMediaLayerVisibility();
                MediaOverlayText.Text = $"GPU H.264 stream {width} x {height}";
            });

            return true;
        }

        private TimeSpan EstimateRemoteGpuSampleDuration(DateTimeOffset queuedAtUtc)
        {
            if (_remoteGpuLastQueuedAtUtc.HasValue)
            {
                var observed = queuedAtUtc - _remoteGpuLastQueuedAtUtc.Value;
                if (observed >= RemoteGpuMinFrameDuration && observed <= RemoteGpuMaxFrameDuration)
                {
                    var smoothedTicks = (long)((_remoteGpuEstimatedFrameDuration.Ticks * 0.82) + (observed.Ticks * 0.18));
                    _remoteGpuEstimatedFrameDuration = TimeSpan.FromTicks(smoothedTicks);
                }
            }

            _remoteGpuLastQueuedAtUtc = queuedAtUtc;
            return _remoteGpuEstimatedFrameDuration;
        }

        private void StartRemoteGpuPlayback(int width, int height)
        {
            if (_remoteGpuPlaybackEnabled &&
                _remoteGpuPlaybackWidth == width &&
                _remoteGpuPlaybackHeight == height)
            {
                return;
            }

            StopRemoteGpuPlayback(clearSurface: false, preservePendingFrames: true);

            var videoProperties = VideoEncodingProperties.CreateH264();
            videoProperties.Width = (uint)width;
            videoProperties.Height = (uint)height;
            videoProperties.FrameRate.Numerator = NativeScreenShareStreamingService.TargetFps;
            videoProperties.FrameRate.Denominator = 1;
            videoProperties.PixelAspectRatio.Numerator = 1;
            videoProperties.PixelAspectRatio.Denominator = 1;

            var descriptor = new VideoStreamDescriptor(videoProperties);
            var source = new MediaStreamSource(descriptor)
            {
                BufferTime = TimeSpan.Zero,
                CanSeek = false,
                Duration = TimeSpan.Zero
            };

            source.SampleRequested += RemoteGpuMediaStreamSource_SampleRequested;
            source.Starting += RemoteGpuMediaStreamSource_Starting;

            var player = new MediaPlayer
            {
                AutoPlay = true,
                RealTimePlayback = true
            };

            player.Source = MediaSource.CreateFromMediaStreamSource(source);
            RemoteScreenPlayer.SetMediaPlayer(player);
            RemoteScreenPlayer.Visibility = Visibility.Visible;
            RemoteScreenImage.Visibility = Visibility.Collapsed;
            player.Play();

            lock (_remoteGpuPlaybackSync)
            {
                _remoteGpuPlaybackEnabled = true;
                _remoteGpuPlaybackStarting = false;
                _remoteGpuPlaybackStopping = false;
                _remoteGpuPlaybackWidth = width;
                _remoteGpuPlaybackHeight = height;
                _remoteGpuPlaybackSampleTime = TimeSpan.Zero;
                _remoteGpuEstimatedFrameDuration = RemoteGpuFrameDuration;
                _remoteGpuLastQueuedAtUtc = null;
                _remoteGpuWaitingForKeyFrame = false;
                _remoteGpuBacklogKeyFrameRequested = false;
                _remoteGpuFirstSampleSubmitted = false;
                Monitor.PulseAll(_remoteGpuPlaybackSync);
            }

            _remoteGpuMediaStreamSource = source;
            _remoteGpuMediaPlayer = player;
            Debug.WriteLine($"[ScreenShare:GPU:RECEIVER] Started hardware MediaStreamSource playback surface {width}x{height} @ {NativeScreenShareStreamingService.TargetFps}fps. Decoded frames stay out of managed BGRA/WriteableBitmap path.");
        }

        private void RemoteGpuMediaStreamSource_Starting(MediaStreamSource sender, MediaStreamSourceStartingEventArgs args)
        {
            args.Request.SetActualStartPosition(TimeSpan.Zero);
            Debug.WriteLine("[ScreenShare:GPU:RECEIVER] MediaStreamSource starting at live position 0.");
        }

        private void RemoteGpuMediaStreamSource_SampleRequested(MediaStreamSource sender, MediaStreamSourceSampleRequestedEventArgs args)
        {
            var deferral = args.Request.GetDeferral();
            try
            {
                PendingRemoteH264Frame? frame = null;
                lock (_remoteGpuPlaybackSync)
                {
                    var waitUntil = DateTimeOffset.UtcNow + RemoteGpuSampleWaitTimeout;
                    var requestedRecoveryForCurrentUnderrun = false;
                    while (!_remoteGpuPlaybackStopping)
                    {
                        if (_pendingRemoteGpuPlaybackFrames.Count > 0)
                        {
                            frame = _pendingRemoteGpuPlaybackFrames.Dequeue();
                            if (_remoteGpuFirstSampleSubmitted || frame.IsKeyFrame)
                                break;

                            frame = null;
                            _screenShareDroppedReceiveFrames++;
                            _remoteGpuWaitingForKeyFrame = true;
                            RequestRemoteVideoKeyFrame("receiver GPU playback first sample was not an IDR");
                        }

                        var now = DateTimeOffset.UtcNow;
                        var lastReceiveAge = _remoteScreenLastReceivedAtUtc.HasValue
                            ? now - _remoteScreenLastReceivedAtUtc.Value
                            : TimeSpan.MaxValue;
                        if (!requestedRecoveryForCurrentUnderrun &&
                            _remoteGpuFirstSampleSubmitted &&
                            lastReceiveAge >= TimeSpan.FromMilliseconds(900))
                        {
                            requestedRecoveryForCurrentUnderrun = true;
                            _screenShareDroppedReceiveFrames++;
                            _ = SendScreenShareQosIfNeededAsync("receiver GPU playback pacing underrun");
                            RequestRemoteVideoKeyFrame("receiver GPU playback pacing underrun");
                            if (ShouldLogFlow(ref _lastRemoteGpuPlaybackDropLogUtc, TimeSpan.FromSeconds(1)))
                            {
                                Debug.WriteLine(
                                    $"[ScreenShare:GPU:RECEIVER] Hardware playback queue empty for {lastReceiveAge.TotalMilliseconds:0}ms; holding MediaStreamSource sample request open instead of returning an empty sample.");
                            }
                        }

                        var remaining = waitUntil - DateTimeOffset.UtcNow;
                        if (remaining <= TimeSpan.Zero)
                            break;
                        Monitor.Wait(_remoteGpuPlaybackSync, remaining > TimeSpan.FromMilliseconds(100)
                            ? TimeSpan.FromMilliseconds(100)
                            : remaining);
                    }
                }

                if (frame == null)
                {
                    _remoteGpuConsecutiveSampleStarvations++;
                    var lastReceiveAge = _remoteScreenLastReceivedAtUtc.HasValue
                        ? DateTimeOffset.UtcNow - _remoteScreenLastReceivedAtUtc.Value
                        : TimeSpan.MaxValue;
                    bool playbackActive;
                    var hardStarved = lastReceiveAge > RemoteFrameReceiveStallThreshold ||
                        _remoteGpuConsecutiveSampleStarvations >= 3;
                    lock (_remoteGpuPlaybackSync)
                    {
                        playbackActive = _remoteGpuPlaybackEnabled || _remoteGpuPlaybackStarting;
                        if (playbackActive && hardStarved)
                        {
                            _remoteGpuBacklogKeyFrameRequested = true;
                        }
                    }

                    if (playbackActive)
                    {
                        _screenShareDroppedReceiveFrames++;
                        var reason = hardStarved
                            ? "receiver GPU playback hard sample starvation"
                            : "receiver GPU playback pacing underrun";
                        _ = SendScreenShareQosIfNeededAsync(reason);
                        RequestRemoteVideoKeyFrame(reason);
                    }

                    if (ShouldLogFlow(ref _lastRemoteGpuPlaybackDropLogUtc, TimeSpan.FromSeconds(1)))
                    {
                        Debug.WriteLine(
                            $"[ScreenShare:GPU:RECEIVER] Hardware player requested a sample but the realtime queue was empty; playbackActive={playbackActive}; hardStarved={hardStarved}; consecutive={_remoteGpuConsecutiveSampleStarvations}; lastReceiveAgeMs={lastReceiveAge.TotalMilliseconds:0}. Requested a keyframe while preserving the current H.264 dependency chain.");
                    }
                    return;
                }

                var sample = MediaStreamSample.CreateFromBuffer(frame.FrameData.AsBuffer(), _remoteGpuPlaybackSampleTime);
                sample.Duration = frame.SampleDuration;
                sample.KeyFrame = frame.IsKeyFrame;
                args.Request.Sample = sample;
                _remoteGpuPlaybackSampleTime += frame.SampleDuration;
                _remoteGpuFirstSampleSubmitted = true;
                _remoteGpuConsecutiveSampleStarvations = 0;
                _remoteScreenLastRenderedAtUtc = DateTimeOffset.UtcNow;

                var renderedFrames = Interlocked.Increment(ref _remoteRenderedFrameCount);
                var queueDepth = GetPendingRemoteGpuPlaybackFrameCount();
                UpdateRemotePlaybackFps(queueDepth);
                if (renderedFrames == 1 || renderedFrames % (NativeScreenShareStreamingService.TargetFps * 2) == 0)
                {
                    Debug.WriteLine($"[ScreenShare:GPU:RECEIVER] Submitted {renderedFrames} compressed H.264 samples to hardware playback; frame={frame.Width}x{frame.Height}; key={frame.IsKeyFrame}; playbackFps={_remotePlaybackObservedFps:0.0}; receiveFps={_remoteReceiveObservedFps:0.0}; queued={queueDepth}; avgQueue={_remoteGpuPlaybackAverageQueue:0.0}; peakQueue={_remoteGpuPlaybackQueuePeak}.");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ScreenShare:GPU:RECEIVER] Sample request failed: {ex}");
            }
            finally
            {
                deferral.Complete();
            }
        }

        private int GetPendingRemoteGpuPlaybackFrameCount()
        {
            lock (_remoteGpuPlaybackSync)
                return _pendingRemoteGpuPlaybackFrames.Count;
        }

        private void StopRemoteGpuPlayback(bool clearSurface, bool preservePendingFrames = false)
        {
            MediaPlayer? player;
            MediaStreamSource? source;
            lock (_remoteGpuPlaybackSync)
            {
                _remoteGpuPlaybackStopping = true;
                _remoteGpuPlaybackEnabled = false;
                _remoteGpuPlaybackStarting = false;
                if (!preservePendingFrames)
                    _pendingRemoteGpuPlaybackFrames.Clear();
                _remoteGpuPlaybackWidth = 0;
                _remoteGpuPlaybackHeight = 0;
                _remoteGpuPlaybackSampleTime = TimeSpan.Zero;
                _remoteGpuEstimatedFrameDuration = RemoteGpuFrameDuration;
                _remoteGpuLastQueuedAtUtc = null;
                _remoteGpuWaitingForKeyFrame = false;
                _remoteGpuBacklogKeyFrameRequested = false;
                _remoteGpuFirstSampleSubmitted = false;
                _remoteGpuConsecutiveSampleStarvations = 0;
                Monitor.PulseAll(_remoteGpuPlaybackSync);
            }

            player = _remoteGpuMediaPlayer;
            source = _remoteGpuMediaStreamSource;
            _remoteGpuMediaPlayer = null;
            _remoteGpuMediaStreamSource = null;

            if (source != null)
            {
                source.SampleRequested -= RemoteGpuMediaStreamSource_SampleRequested;
                source.Starting -= RemoteGpuMediaStreamSource_Starting;
            }

            try
            {
                player?.Pause();
                player?.Dispose();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ScreenShare:GPU:RECEIVER] Stop playback failed: {ex.Message}");
            }

            if (clearSurface)
            {
                RemoteScreenPlayer.SetMediaPlayer(null);
                RemoteScreenPlayer.Visibility = Visibility.Collapsed;
                RemoteScreenImage.Visibility = Visibility.Visible;
            }

            lock (_remoteGpuPlaybackSync)
            {
                _remoteGpuPlaybackStopping = false;
                Monitor.PulseAll(_remoteGpuPlaybackSync);
            }
        }

        private async Task ProcessLatestRtpFramesAsync()
        {
            try
            {
                Thread.CurrentThread.Priority = ThreadPriority.AboveNormal;
            }
            catch
            {
            }

            try
            {
                while (true)
                {
                    PendingRemoteH264Frame? pendingFrame;

                    lock (_rtpFrameSync)
                    {
                        pendingFrame = _pendingRemoteH264Frames.Count > 0
                            ? _pendingRemoteH264Frames.Dequeue()
                            : null;
                    }

                    if (pendingFrame == null ||
                        pendingFrame.FrameData.Length == 0 ||
                        pendingFrame.Width <= 0 ||
                        pendingFrame.Height <= 0)
                    {
                        break;
                    }

                    if (pendingFrame.Generation != Volatile.Read(ref _remoteDecodeGeneration))
                    {
                        Debug.WriteLine($"[ScreenShare:H264] Dropped stale compressed frame from old decoder generation. seq={pendingFrame.Sequence}; frame={pendingFrame.Width}x{pendingFrame.Height}; currentGeneration={Volatile.Read(ref _remoteDecodeGeneration)}; frameGeneration={pendingFrame.Generation}.");
                        continue;
                    }

                    try
                    {
                        byte[]? bgraFrame;
                        var decodeTimer = Stopwatch.StartNew();
                        lock (_remoteDecoderSync)
                        {
                            if (_rtpH264Decoder == null ||
                                _rtpDecoderWidth != pendingFrame.Width ||
                                _rtpDecoderHeight != pendingFrame.Height)
                            {
                                _rtpH264Decoder?.Dispose();
                                _rtpH264Decoder = new MediaFoundationH264Decoder(pendingFrame.Width, pendingFrame.Height);
                                _rtpDecoderWidth = pendingFrame.Width;
                                _rtpDecoderHeight = pendingFrame.Height;
                            }

                            bgraFrame = _rtpH264Decoder.DecodeToBgra(
                                pendingFrame.FrameData,
                                pendingFrame.Width,
                                pendingFrame.Height);
                        }
                        decodeTimer.Stop();
                        var queueRemaining = GetPendingRemoteH264FrameCount();

                        if (bgraFrame != null)
                        {
                            _remoteEmptyDecodeCount = 0;
                            _remoteDecodeFailureCount = 0;
                            LogRemoteDecodeFlow(pendingFrame, bgraFrame, queueRemaining, decodeTimer.Elapsed.TotalMilliseconds);
                            QueueRemoteRender(bgraFrame, pendingFrame.Width, pendingFrame.Height);
                        }
                        else
                        {
                            _remoteEmptyDecodeCount++;
                            LogRemoteDecodeFlow(pendingFrame, null, queueRemaining, decodeTimer.Elapsed.TotalMilliseconds);
                            if (_remoteEmptyDecodeCount == NativeScreenShareStreamingService.TargetFps)
                            {
                                var switchedInputMode = false;
                                lock (_remoteDecoderSync)
                                {
                                    switchedInputMode = _rtpH264Decoder?.TrySwitchToLengthPrefixedInput("receiver decoder accepted input but produced no frames") == true;
                                }

                                if (switchedInputMode)
                                {
                                    lock (_rtpFrameSync)
                                    {
                                        _pendingRemoteH264Frames.Clear();
                                        _remoteH264WaitingForKeyFrame = true;
                                        _remoteH264FramesDroppedWaitingForKeyFrame = 0;
                                        _remoteDecodeGeneration++;
                                    }

                                    RequestRemoteVideoKeyFrame("receiver decoder switched H.264 input format");
                                    Debug.WriteLine("[ScreenShare:H264] Receiver decoder input format changed; waiting for a fresh IDR keyframe before resuming decode.");
                                    continue;
                                }
                            }

                            if (_remoteEmptyDecodeCount >= NativeScreenShareStreamingService.TargetFps * 5 ||
                                IsRemoteRenderStalled(DateTimeOffset.UtcNow))
                            {
                                ResetRemoteH264Decoder("receiver decoder produced no frame");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[ScreenShare:RTP] Decode failed: {ex.Message}");
                        _remoteDecodeFailureCount++;
                        if (_remoteDecodeFailureCount >= 3)
                            ResetRemoteH264Decoder("receiver decoder failure");
                    }
                }

                await Task.CompletedTask;
            }
            finally
            {
                Interlocked.Exchange(ref _rtpDecodePumpRunning, 0);

                lock (_rtpFrameSync)
                {
                    if (_pendingRemoteH264Frames.Count > 0 &&
                        Interlocked.CompareExchange(ref _rtpDecodePumpRunning, 1, 0) == 0)
                    {
                        _ = Task.Run(ProcessLatestRtpFramesAsync);
                    }
                }
            }
        }

        private void QueueRemoteRender(byte[] bgraFrame, int width, int height)
        {
            bool replacedPendingFrame;
            lock (_remoteRenderSync)
            {
                replacedPendingFrame = _pendingRemoteRenderFrame != null;
                _pendingRemoteRenderFrame = bgraFrame;
                _pendingRemoteRenderWidth = width;
                _pendingRemoteRenderHeight = height;
                _pendingRemoteRenderQueuedAtUtc = DateTimeOffset.UtcNow;
            }

            if (replacedPendingFrame)
            {
                if (ShouldLogFlow(ref _lastRemoteRenderCoalescedLogUtc, TimeSpan.FromSeconds(2)))
                    Debug.WriteLine("[ScreenShare:H264] Coalesced decoded frames because the UI renderer was still presenting the previous frame.");
            }

            if (Interlocked.CompareExchange(ref _remoteRenderQueued, 1, 0) == 0)
                EnqueueLatestRemoteRender();
        }

        private void EnqueueLatestRemoteRender()
        {
            if (!TryEnqueueOnUi(RenderLatestRemoteFrame))
                Interlocked.Exchange(ref _remoteRenderQueued, 0);
        }

        private void RenderLatestRemoteFrame()
        {
            byte[]? bgraFrame;
            int width;
            int height;
            DateTimeOffset queuedAtUtc;

            lock (_remoteRenderSync)
            {
                bgraFrame = _pendingRemoteRenderFrame;
                width = _pendingRemoteRenderWidth;
                height = _pendingRemoteRenderHeight;
                queuedAtUtc = _pendingRemoteRenderQueuedAtUtc;
                _pendingRemoteRenderFrame = null;
            }

            try
            {
                if (bgraFrame != null && width > 0 && height > 0)
                {
                    _screenShareLastWidth = width;
                    _screenShareLastHeight = height;
                    _screenShareLastBytes = bgraFrame.LongLength;
                    _screenShareLastQuality = GetResolutionTier(height);
                    _screenShareLastFrameAtUtc = DateTimeOffset.UtcNow;
                    _remoteScreenLastRenderedAtUtc = _screenShareLastFrameAtUtc;
                    _remoteEmptyDecodeCount = 0;
                    _remoteDecodeFailureCount = 0;
                    var renderTimer = Stopwatch.StartNew();
                    RenderBgraFrame(bgraFrame, width, height, RemoteScreenImage);
                    renderTimer.Stop();
                    var renderedFrames = Interlocked.Increment(ref _remoteRenderedFrameCount);
                    var queuedMs = queuedAtUtc == default || !_screenShareLastFrameAtUtc.HasValue
                        ? 0
                        : Math.Max(0, (_screenShareLastFrameAtUtc.Value - queuedAtUtc).TotalMilliseconds);
                    LogRemoteRenderFlow(bgraFrame, width, height, renderedFrames, renderTimer.Elapsed.TotalMilliseconds, queuedMs);
                    if (renderedFrames == 1 || renderedFrames % (NativeScreenShareStreamingService.TargetFps * 2) == 0)
                    {
                        Debug.WriteLine(
                            $"[ScreenShare:H264] Rendered {renderedFrames} remote frames: {width}x{height}; bgraBytes={bgraFrame.Length}.");
                    }

                    _isRemoteVideoVisible = true;
                    _isRemoteScreenShareLoading = false;
                    UpdateMediaLayerVisibility();
                    MediaOverlayText.Text = $"H.264 stream {width} x {height}";
                }
            }
            finally
            {
                var hasPendingFrame = false;
                lock (_remoteRenderSync)
                {
                    hasPendingFrame = _pendingRemoteRenderFrame != null;
                }

                if (hasPendingFrame)
                {
                    EnqueueLatestRemoteRender();
                }
                else
                {
                    Interlocked.Exchange(ref _remoteRenderQueued, 0);

                    lock (_remoteRenderSync)
                    {
                        hasPendingFrame = _pendingRemoteRenderFrame != null;
                    }

                    if (hasPendingFrame &&
                        Interlocked.CompareExchange(ref _remoteRenderQueued, 1, 0) == 0)
                    {
                        EnqueueLatestRemoteRender();
                    }
                }
            }
        }

        private async Task SendScreenShareQosIfNeededAsync(string reason)
        {
            var session = NativeCallCoordinator.Instance.CurrentSession;
            if (_remoteScreenShareSenderId <= 0 ||
                string.IsNullOrWhiteSpace(session.CallId) ||
                session.State != NativeCallState.Connected)
            {
                return;
            }

            var now = DateTimeOffset.UtcNow;
            if (now - _lastQosSentUtc < QosFeedbackInterval)
            {
                return;
            }

            _lastQosSentUtc = now;
            _lastQosDroppedReceiveFrames = _screenShareDroppedReceiveFrames;

            try
            {
                Debug.WriteLine($"[ScreenShare:QOS] Sending receiver pressure to {_remoteScreenShareSenderId}: dropped={_screenShareDroppedReceiveFrames}; renderQueued={Volatile.Read(ref _remoteRenderQueued)}; reason={reason}.");
                await SocialManager.Instance.Realtime.SendScreenShareQosAsync(
                    _remoteScreenShareSenderId,
                    session.CallId,
                    _screenShareDroppedReceiveFrames,
                    Volatile.Read(ref _remoteRenderQueued),
                    reason);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ScreenShare:QOS] Failed to send receiver pressure: {ex.Message}");
            }
        }

        private void ResetRemoteH264Decoder(string reason)
        {
            var now = DateTimeOffset.UtcNow;
            lock (_rtpFrameSync)
            {
                _remoteH264WaitingForKeyFrame = true;
                _remoteH264FramesDroppedWaitingForKeyFrame = 0;
            }

            if (now - _lastRemoteDecoderResetAtUtc < RemoteDecoderResetCooldown)
            {
                RequestRemoteVideoKeyFrame(reason);
                return;
            }

            _lastRemoteDecoderResetAtUtc = now;
            _remoteDecoderResetCount++;
            _remoteEmptyDecodeCount = 0;
            _remoteDecodeFailureCount = 0;

            Debug.WriteLine($"[ScreenShare:H264] Resetting remote decoder: {reason}");
            RequestRemoteVideoKeyFrame(reason);

            lock (_remoteDecoderSync)
            {
                try
                {
                    _rtpH264Decoder?.Dispose();
                }
                catch
                {
                }

                _rtpH264Decoder = null;
                _rtpDecoderWidth = 0;
                _rtpDecoderHeight = 0;
            }
            lock (_rtpFrameSync)
            {
                _pendingRemoteH264Frames.Clear();
                _remoteH264WaitingForKeyFrame = true;
                _remoteH264FramesDroppedWaitingForKeyFrame = 0;
                _remoteDecodeGeneration++;
            }

            try
            {
                _h264Decoder?.Dispose();
            }
            catch
            {
            }

            _h264Decoder = null;
            _screenShareDroppedReceiveFrames++;
            _ = SendScreenShareQosIfNeededAsync(reason);
        }

        private void RequestRemoteVideoKeyFrame(string reason)
        {
            if (_remoteScreenShareSenderId <= 0)
                return;

            var now = DateTimeOffset.UtcNow;
            if (now - _lastRemoteKeyFrameRequestAtUtc < TimeSpan.FromMilliseconds(750))
                return;

            _lastRemoteKeyFrameRequestAtUtc = now;

            if (_screenSharePeers.TryGetValue(_remoteScreenShareSenderId, out var peer))
            {
                Debug.WriteLine($"[ScreenShare:FLOW:KEYFRAME] Requesting remote RTP keyframe from {_remoteScreenShareSenderId}: {reason}");
                peer.RequestVideoKeyFrame(reason);
            }
            else
            {
                Debug.WriteLine($"[ScreenShare:FLOW:KEYFRAME] Cannot request RTP keyframe from {_remoteScreenShareSenderId}: no peer. reason={reason}");
            }
        }

        private void LogLocalScreenShareFlow(NativeScreenFrameEventArgs e, int participantCount, bool sent)
        {
            var encodedFingerprint = SampleBytesFingerprint(e.FrameData);
            var previewFingerprint = SampleBytesFingerprint(e.PreviewFrameData);
            var encodedRepeat = UpdateFingerprintRepeat(
                ref _lastLocalEncodedFingerprint,
                ref _localEncodedFingerprintRepeat,
                encodedFingerprint);
            var previewRepeat = UpdateFingerprintRepeat(
                ref _lastLocalPreviewFingerprint,
                ref _localPreviewFingerprintRepeat,
                previewFingerprint);

            if (!ShouldLogFlow(ref _lastLocalFrameFlowLogUtc, TimeSpan.FromSeconds(2)) &&
                encodedRepeat != NativeScreenShareStreamingService.TargetFps * 3 &&
                previewRepeat != PreviewFallbackMaxFps * 3)
            {
                return;
            }

            var hasIdr = ContainsH264IdrFrame(e.FrameData);
            var message = $"[ScreenShare:FLOW:SEND] participants={participantCount}; sent={sent}; {e.Width}x{e.Height}; codec={e.Codec}; bytes={e.FrameData.Length}; keyFlag={e.IsKeyFrame}; idr={hasIdr}; encodedHash=0x{encodedFingerprint:X8}; sameEncoded={encodedRepeat}; previewBytes={e.PreviewFrameData.Length}; previewHash=0x{previewFingerprint:X8}; samePreview={previewRepeat}; rtpSent={_screenShareRtpFrames}; wsSent={_screenShareFallbackFrames}; previewSent={_screenSharePreviewFallbackFrames}; droppedSend={_screenShareDroppedFrames}.";
            Debug.WriteLine(message);
            DiagnosticLogService.WriteLine(message);
        }

        private void LogRemoteEncodedReceiveFlow(string transport, long fromUserId, byte[] frameData, int width, int height, bool keyFlag, bool hasIdr, long receivedFrames)
        {
            UpdateRemoteReceiveFps();

            if (keyFlag && !hasIdr)
            {
                Debug.WriteLine(
                    $"[ScreenShare:FLOW:RECV] {transport} frame from {fromUserId} was flagged as keyframe but has no IDR; it will be treated as delta. bytes={frameData.Length}; {width}x{height}.");
            }

            var fingerprint = SampleBytesFingerprint(frameData);
            var repeat = UpdateFingerprintRepeat(
                ref _lastRemoteEncodedFingerprint,
                ref _remoteEncodedFingerprintRepeat,
                fingerprint);

            if (!ShouldLogFlow(ref _lastRemoteReceiveFlowLogUtc, TimeSpan.FromSeconds(2)) &&
                repeat != NativeScreenShareStreamingService.TargetFps * 3 &&
                receivedFrames % (NativeScreenShareStreamingService.TargetFps * 5) != 0)
            {
                return;
            }

            var now = DateTimeOffset.UtcNow;
            Debug.WriteLine(
                $"[ScreenShare:FLOW:RECV] transport={transport}; from={fromUserId}; frames={receivedFrames}; {width}x{height}; receiveFps={_remoteReceiveObservedFps:0.0}; playbackFps={_remotePlaybackObservedFps:0.0}; bytes={frameData.Length}; keyFlag={keyFlag}; idr={hasIdr}; encodedHash=0x{fingerprint:X8}; sameEncoded={repeat}; queue={GetPendingRemoteH264FrameCount()}; gpuQueue={GetPendingRemoteGpuPlaybackFrameCount()}; gpuAvgQueue={_remoteGpuPlaybackAverageQueue:0.0}; gpuPeakQueue={_remoteGpuPlaybackQueuePeak}; waitingKey={_remoteH264WaitingForKeyFrame}; rendered={Volatile.Read(ref _remoteRenderedFrameCount)}; lastRenderAge={FormatAge(_remoteScreenLastRenderedAtUtc, now)}; droppedRecv={_screenShareDroppedReceiveFrames}.");
        }

        private void UpdateRemoteReceiveFps()
        {
            _remoteReceiveFramesInWindow++;
            var now = DateTimeOffset.UtcNow;
            var elapsed = now - _remoteReceiveFpsWindowStartedUtc;
            if (elapsed.TotalSeconds < 1)
                return;

            _remoteReceiveObservedFps = _remoteReceiveFramesInWindow / Math.Max(0.001, elapsed.TotalSeconds);
            _remoteReceiveFramesInWindow = 0;
            _remoteReceiveFpsWindowStartedUtc = now;
            LogRemoteRealtimeStatsIfNeeded("receive-fps-window");
        }

        private void UpdateRemotePlaybackFps(int queueDepth)
        {
            _remotePlaybackFramesInWindow++;
            _remoteGpuPlaybackQueuePeak = Math.Max(_remoteGpuPlaybackQueuePeak, queueDepth);
            _remoteGpuPlaybackQueueTotal += queueDepth;
            _remoteGpuPlaybackQueueSamples++;
            _remoteGpuPlaybackAverageQueue = _remoteGpuPlaybackQueueTotal / Math.Max(1d, _remoteGpuPlaybackQueueSamples);

            var now = DateTimeOffset.UtcNow;
            var elapsed = now - _remotePlaybackFpsWindowStartedUtc;
            if (elapsed.TotalSeconds < 1)
                return;

            _remotePlaybackObservedFps = _remotePlaybackFramesInWindow / Math.Max(0.001, elapsed.TotalSeconds);
            _remotePlaybackFramesInWindow = 0;
            _remotePlaybackFpsWindowStartedUtc = now;
            LogRemoteRealtimeStatsIfNeeded("playback-fps-window");
        }

        private void LogRemoteRealtimeStatsIfNeeded(string reason)
        {
            if (!ShouldLogFlow(ref _lastRemoteRealtimeStatsLogUtc, TimeSpan.FromSeconds(2)))
                return;

            var target = NativeScreenShareStreamingService.TargetFps;
            var receiveLocked = _remoteReceiveObservedFps >= target - 2;
            var playbackLocked = _remotePlaybackObservedFps >= target - 2;
            Debug.WriteLine(
                $"[ScreenShare:60FPS] reason={reason}; target={target}; receive={_remoteReceiveObservedFps:0.0}fps; playback={_remotePlaybackObservedFps:0.0}fps; locked={(receiveLocked && playbackLocked ? "yes" : "no")}; gpuQueue={GetPendingRemoteGpuPlaybackFrameCount()}; avgQueue={_remoteGpuPlaybackAverageQueue:0.0}; peakQueue={_remoteGpuPlaybackQueuePeak}; droppedRecv={_screenShareDroppedReceiveFrames}; lastReceive={FormatAge(_remoteScreenLastReceivedAtUtc, DateTimeOffset.UtcNow)}; lastRender={FormatAge(_remoteScreenLastRenderedAtUtc, DateTimeOffset.UtcNow)}.");
        }

        private void LogRemotePreviewFlow(ScreenFrameEventArgs e, bool rendered)
        {
            var fingerprint = SampleBytesFingerprint(e.FrameData);
            var repeat = UpdateFingerprintRepeat(
                ref _lastRemotePreviewFingerprint,
                ref _remotePreviewFingerprintRepeat,
                fingerprint);

            if (!ShouldLogFlow(ref _lastRemotePreviewFlowLogUtc, TimeSpan.FromSeconds(2)) &&
                repeat != PreviewFallbackMaxFps * 3)
            {
                return;
            }

            Debug.WriteLine(
                $"[ScreenShare:FLOW:PREVIEW] from={e.FromUserId}; rendered={rendered}; {e.Width}x{e.Height}; bytes={e.FrameData.Length}; jpegHash=0x{fingerprint:X8}; samePreview={repeat}; h264Rendered={Volatile.Read(ref _remoteRenderedFrameCount)}.");
        }

        private void LogRemoteQueueFlow(long sequence, int queueCount, bool isKeyFrame, string backlogReason)
        {
            if (!ShouldLogFlow(ref _lastRemoteQueueFlowLogUtc, TimeSpan.FromSeconds(2)))
                return;

            Debug.WriteLine(
                $"[ScreenShare:FLOW:QUEUE] seq={sequence}; queue={queueCount}; key={isKeyFrame}; waitingKey={_remoteH264WaitingForKeyFrame}; decodePump={Volatile.Read(ref _rtpDecodePumpRunning)}; reason={backlogReason}; droppedRecv={_screenShareDroppedReceiveFrames}.");
        }

        private void LogRemoteDecodeFlow(PendingRemoteH264Frame frame, byte[]? bgraFrame, int queueRemaining, double decodeMilliseconds)
        {
            var decoded = bgraFrame != null;
            var decodedFingerprint = decoded ? SampleBytesFingerprint(bgraFrame!) : 0;
            var decodedRepeat = decoded
                ? UpdateFingerprintRepeat(
                    ref _lastRemoteDecodedFingerprint,
                    ref _remoteDecodedFingerprintRepeat,
                    decodedFingerprint)
                : _remoteDecodedFingerprintRepeat;

            if (!ShouldLogFlow(ref _lastRemoteDecodeFlowLogUtc, TimeSpan.FromSeconds(2)) &&
                decoded &&
                decodedRepeat != NativeScreenShareStreamingService.TargetFps * 3)
            {
                return;
            }

            Debug.WriteLine(
                $"[ScreenShare:FLOW:DECODE] seq={frame.Sequence}; key={frame.IsKeyFrame}; {frame.Width}x{frame.Height}; inputBytes={frame.FrameData.Length}; inputHash=0x{SampleBytesFingerprint(frame.FrameData):X8}; decoded={decoded}; decodedBytes={(bgraFrame?.Length ?? 0)}; decodedHash=0x{decodedFingerprint:X8}; sameDecoded={decodedRepeat}; decodeMs={decodeMilliseconds:0.0}; empty={_remoteEmptyDecodeCount}; failures={_remoteDecodeFailureCount}; queueRemaining={queueRemaining}; renderQueued={Volatile.Read(ref _remoteRenderQueued)}.");
        }

        private void LogRemoteRenderFlow(byte[] bgraFrame, int width, int height, long renderedFrames, double renderMilliseconds, double queuedMilliseconds)
        {
            var fingerprint = SampleBytesFingerprint(bgraFrame);
            var repeat = UpdateFingerprintRepeat(
                ref _lastRemoteRenderedFingerprint,
                ref _remoteRenderedFingerprintRepeat,
                fingerprint);

            if (!ShouldLogFlow(ref _lastRemoteRenderFlowLogUtc, TimeSpan.FromSeconds(2)) &&
                renderedFrames != 1 &&
                repeat != NativeScreenShareStreamingService.TargetFps * 3)
            {
                return;
            }

            var now = DateTimeOffset.UtcNow;
            Debug.WriteLine(
                $"[ScreenShare:FLOW:RENDER] rendered={renderedFrames}; {width}x{height}; bgraBytes={bgraFrame.Length}; renderHash=0x{fingerprint:X8}; sameRendered={repeat}; renderMs={renderMilliseconds:0.0}; queuedMs={queuedMilliseconds:0.0}; receiveAge={FormatAge(_remoteScreenLastReceivedAtUtc, now)}; droppedRecv={_screenShareDroppedReceiveFrames}.");
        }

        private int GetPendingRemoteH264FrameCount()
        {
            lock (_rtpFrameSync)
            {
                return _pendingRemoteH264Frames.Count;
            }
        }

        private static bool ShouldLogFlow(ref DateTimeOffset lastLoggedUtc, TimeSpan interval)
        {
            var now = DateTimeOffset.UtcNow;
            if (now - lastLoggedUtc < interval)
                return false;

            lastLoggedUtc = now;
            return true;
        }

        private static int UpdateFingerprintRepeat(ref uint lastFingerprint, ref int repeatCount, uint fingerprint)
        {
            if (lastFingerprint == fingerprint)
            {
                repeatCount++;
            }
            else
            {
                lastFingerprint = fingerprint;
                repeatCount = 1;
            }

            return repeatCount;
        }

        private static string FormatAge(DateTimeOffset? timestampUtc, DateTimeOffset nowUtc)
        {
            if (!timestampUtc.HasValue)
                return "never";

            return $"{Math.Max(0, (nowUtc - timestampUtc.Value).TotalMilliseconds):0}ms";
        }

        private static uint SampleBytesFingerprint(byte[] data)
        {
            unchecked
            {
                const uint offsetBasis = 2166136261;
                const uint prime = 16777619;
                var hash = offsetBasis;
                hash = (hash ^ (uint)data.Length) * prime;

                if (data.Length == 0)
                    return hash;

                var step = Math.Max(1, data.Length / 1024);
                for (var i = 0; i < data.Length; i += step)
                    hash = (hash ^ data[i]) * prime;

                hash = (hash ^ data[^1]) * prime;
                return hash;
            }
        }

        private static bool ContainsH264IdrFrame(byte[] frameData)
        {
            if (frameData.Length == 0)
                return false;

            if ((frameData[0] & 0x1F) == 5)
                return true;

            if (frameData.Length < 5)
                return false;

            var offset = 0;
            while (offset + 4 <= frameData.Length)
            {
                var startCodeLength = GetAnnexBStartCodeLength(frameData, offset);
                if (startCodeLength > 0)
                {
                    var nalStart = offset + startCodeLength;
                    if (nalStart < frameData.Length && (frameData[nalStart] & 0x1F) == 5)
                        return true;

                    offset = nalStart + 1;
                    continue;
                }

                offset++;
            }

            offset = 0;
            while (offset + 4 <= frameData.Length)
            {
                var nalLength =
                    (frameData[offset] << 24) |
                    (frameData[offset + 1] << 16) |
                    (frameData[offset + 2] << 8) |
                    frameData[offset + 3];

                offset += 4;
                if (nalLength <= 0 || nalLength > frameData.Length - offset)
                    return false;

                if ((frameData[offset] & 0x1F) == 5)
                    return true;

                offset += nalLength;
            }

            return false;
        }

        private static bool TryReadH264Dimensions(byte[] frameData, out int width, out int height)
        {
            width = 0;
            height = 0;

            foreach (var nal in EnumerateH264NalUnits(frameData))
            {
                if (nal.Count < 2 || (nal.Array![nal.Offset] & 0x1F) != 7)
                    continue;

                return TryReadSpsDimensions(nal.Array, nal.Offset + 1, nal.Count - 1, out width, out height);
            }

            return false;
        }

        private static bool IsPlausibleRemoteBitstreamResolution(int width, int height, int expectedWidth, int expectedHeight)
        {
            if (width < 320 || height < 180 || width > 8192 || height > 8192)
                return false;

            if ((width & 1) != 0 || (height & 1) != 0)
                return false;

            var aspect = (double)width / height;
            if (aspect < 1.2 || aspect > 2.4)
                return false;

            if (expectedWidth <= 0 || expectedHeight <= 0)
                return true;

            var expectedAspect = (double)expectedWidth / expectedHeight;
            return Math.Abs(aspect - expectedAspect) <= 0.35;
        }

        private static IEnumerable<ArraySegment<byte>> EnumerateH264NalUnits(byte[] frameData)
        {
            var offset = 0;
            var foundStartCode = false;

            while (offset + 3 < frameData.Length)
            {
                var startCodeLength = GetAnnexBStartCodeLength(frameData, offset);
                if (startCodeLength == 0)
                {
                    offset++;
                    continue;
                }

                foundStartCode = true;
                var nalStart = offset + startCodeLength;
                var nextStart = nalStart;
                while (nextStart + 3 < frameData.Length && GetAnnexBStartCodeLength(frameData, nextStart) == 0)
                    nextStart++;

                var nalLength = nextStart - nalStart;
                if (nalLength > 0)
                    yield return new ArraySegment<byte>(frameData, nalStart, nalLength);

                offset = nextStart;
            }

            if (foundStartCode)
                yield break;

            offset = 0;
            while (offset + 4 <= frameData.Length)
            {
                var nalLength =
                    (frameData[offset] << 24) |
                    (frameData[offset + 1] << 16) |
                    (frameData[offset + 2] << 8) |
                    frameData[offset + 3];

                offset += 4;
                if (nalLength <= 0 || nalLength > frameData.Length - offset)
                    yield break;

                yield return new ArraySegment<byte>(frameData, offset, nalLength);
                offset += nalLength;
            }
        }

        private static bool TryReadSpsDimensions(byte[] data, int offset, int length, out int width, out int height)
        {
            width = 0;
            height = 0;

            try
            {
                var rbsp = RemoveH264EmulationPreventionBytes(data, offset, length);
                var reader = new BitReader(rbsp);

                var profileIdc = reader.ReadBits(8);
                reader.ReadBits(8);
                reader.ReadBits(8);
                reader.ReadUnsignedExpGolomb();

                var chromaFormatIdc = 1u;
                if (profileIdc is 100 or 110 or 122 or 244 or 44 or 83 or 86 or 118 or 128 or 138 or 139 or 134 or 135)
                {
                    chromaFormatIdc = reader.ReadUnsignedExpGolomb();
                    if (chromaFormatIdc == 3)
                        reader.ReadBit();

                    reader.ReadUnsignedExpGolomb();
                    reader.ReadUnsignedExpGolomb();
                    reader.ReadBit();
                    if (reader.ReadBit())
                    {
                        var scalingListCount = chromaFormatIdc != 3 ? 8 : 12;
                        for (var i = 0; i < scalingListCount; i++)
                        {
                            if (reader.ReadBit())
                                SkipH264ScalingList(reader, i < 6 ? 16 : 64);
                        }
                    }
                }

                reader.ReadUnsignedExpGolomb();
                var picOrderCntType = reader.ReadUnsignedExpGolomb();
                if (picOrderCntType == 0)
                {
                    reader.ReadUnsignedExpGolomb();
                }
                else if (picOrderCntType == 1)
                {
                    reader.ReadBit();
                    reader.ReadSignedExpGolomb();
                    reader.ReadSignedExpGolomb();
                    var cycleCount = reader.ReadUnsignedExpGolomb();
                    for (var i = 0; i < cycleCount; i++)
                        reader.ReadSignedExpGolomb();
                }

                reader.ReadUnsignedExpGolomb();
                reader.ReadBit();
                var picWidthInMbsMinus1 = reader.ReadUnsignedExpGolomb();
                var picHeightInMapUnitsMinus1 = reader.ReadUnsignedExpGolomb();
                var frameMbsOnlyFlag = reader.ReadBit();
                if (!frameMbsOnlyFlag)
                    reader.ReadBit();

                reader.ReadBit();

                var cropLeft = 0u;
                var cropRight = 0u;
                var cropTop = 0u;
                var cropBottom = 0u;
                if (reader.ReadBit())
                {
                    cropLeft = reader.ReadUnsignedExpGolomb();
                    cropRight = reader.ReadUnsignedExpGolomb();
                    cropTop = reader.ReadUnsignedExpGolomb();
                    cropBottom = reader.ReadUnsignedExpGolomb();
                }

                var cropUnitX = chromaFormatIdc == 0 ? 1u : chromaFormatIdc == 3 ? 1u : 2u;
                var cropUnitY = chromaFormatIdc == 0
                    ? (frameMbsOnlyFlag ? 1u : 2u)
                    : (chromaFormatIdc == 1 ? 2u : 1u) * (frameMbsOnlyFlag ? 1u : 2u);

                var computedWidth = ((picWidthInMbsMinus1 + 1) * 16) - ((cropLeft + cropRight) * cropUnitX);
                var computedHeight = ((2 - (frameMbsOnlyFlag ? 1u : 0u)) * (picHeightInMapUnitsMinus1 + 1) * 16) -
                                     ((cropTop + cropBottom) * cropUnitY);

                if (computedWidth <= 0 || computedHeight <= 0 || computedWidth > 8192 || computedHeight > 8192)
                    return false;

                width = (int)computedWidth;
                height = (int)computedHeight;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static byte[] RemoveH264EmulationPreventionBytes(byte[] data, int offset, int length)
        {
            var output = new List<byte>(length);
            var zeros = 0;
            for (var i = offset; i < offset + length; i++)
            {
                var value = data[i];
                if (zeros >= 2 && value == 0x03)
                {
                    zeros = 0;
                    continue;
                }

                output.Add(value);
                zeros = value == 0 ? zeros + 1 : 0;
            }

            return output.ToArray();
        }

        private static void SkipH264ScalingList(BitReader reader, int size)
        {
            var lastScale = 8;
            var nextScale = 8;
            for (var j = 0; j < size; j++)
            {
                if (nextScale != 0)
                {
                    var deltaScale = reader.ReadSignedExpGolomb();
                    nextScale = (lastScale + deltaScale + 256) % 256;
                }

                lastScale = nextScale == 0 ? lastScale : nextScale;
            }
        }

        private static int GetAnnexBStartCodeLength(byte[] frameData, int offset)
        {
            if (offset + 3 <= frameData.Length &&
                frameData[offset] == 0 &&
                frameData[offset + 1] == 0 &&
                frameData[offset + 2] == 1)
            {
                return 3;
            }

            if (offset + 4 <= frameData.Length &&
                frameData[offset] == 0 &&
                frameData[offset + 1] == 0 &&
                frameData[offset + 2] == 0 &&
                frameData[offset + 3] == 1)
            {
                return 4;
            }

            return 0;
        }

        private void WatchRemoteScreenShareHealth()
        {
            var session = NativeCallCoordinator.Instance.CurrentSession;
            if (_remoteScreenShareSenderId <= 0 ||
                session.State != NativeCallState.Connected)
            {
                return;
            }

            var now = DateTimeOffset.UtcNow;
            if (!_remoteScreenLastRenderedAtUtc.HasValue)
            {
                if (!IsRemoteScreenShareAccepted(_remoteScreenShareSenderId))
                {
                    ShowRemoteScreenShareWatchPrompt();
                    UpdateMediaLayerVisibility();
                    return;
                }

                ShowRemoteScreenShareLoading("Screen share is loading", "Waiting for the first live frame...");
                UpdateMediaLayerVisibility();
                _ = SendScreenShareQosIfNeededAsync("receiver waiting for keyframe before first visible frame");
                RequestRemoteVideoKeyFrame("receiver waiting for first visible frame");
                return;
            }

            if (_remoteGpuPlaybackEnabled || _remoteGpuPlaybackStarting)
            {
                if (_remoteScreenLastReceivedAtUtc.HasValue &&
                    now - _remoteScreenLastReceivedAtUtc.Value > RemoteFrameReceiveStallThreshold &&
                    now - _lastRemoteReceiveStallRecoveryUtc > TimeSpan.FromSeconds(2))
                {
                    var receiveStallAge = now - _remoteScreenLastReceivedAtUtc.Value;
                    var reason = receiveStallAge > TimeSpan.FromSeconds(4)
                        ? "receiver RTP stalled; restart screen-share offer"
                        : "receiver GPU playback stopped receiving RTP frames";

                    _lastRemoteReceiveStallRecoveryUtc = now;
                    _screenShareDroppedReceiveFrames++;
                    Debug.WriteLine($"[ScreenShare:HEALTH] Receiver has not received RTP video for {receiveStallAge.TotalMilliseconds:0}ms; reason={reason}.");
                    _ = SendScreenShareQosIfNeededAsync(reason);
                    RequestRemoteVideoKeyFrame(reason);
                    ShowRemoteScreenShareLoading("Screen share reconnecting", "The stream paused while waiting for new frames...");
                    UpdateMediaLayerVisibility();
                    MediaOverlayText.Text = "Screen share reconnecting...";
                }

                return;
            }

            if (_remoteScreenLastReceivedAtUtc.HasValue &&
                now - _remoteScreenLastReceivedAtUtc.Value > RemoteFrameReceiveStallThreshold &&
                now - _lastRemoteReceiveStallRecoveryUtc > TimeSpan.FromSeconds(2))
            {
                var receiveStallAge = now - _remoteScreenLastReceivedAtUtc.Value;
                var reason = receiveStallAge > TimeSpan.FromSeconds(4)
                    ? "receiver RTP stalled; restart screen-share offer"
                    : "receiver no RTP frames";

                _lastRemoteReceiveStallRecoveryUtc = now;
                _screenShareDroppedReceiveFrames++;
                ResetRemoteH264Decoder(reason);
                ShowRemoteScreenShareLoading("Screen share reconnecting", "The stream paused while waiting for new frames...");
                UpdateMediaLayerVisibility();
                MediaOverlayText.Text = "Screen share reconnecting...";
                return;
            }

            if (IsRemoteRenderStalled(now))
            {
                _screenShareDroppedReceiveFrames++;
                ResetRemoteH264Decoder("receiver render stalled");
                ShowRemoteScreenShareLoading("Screen share recovering", "The stream is catching up...");
                UpdateMediaLayerVisibility();
                MediaOverlayText.Text = "Screen share recovering...";
            }
        }

        private bool IsRemoteRenderStalled(DateTimeOffset now)
        {
            return _remoteScreenLastReceivedAtUtc.HasValue &&
                _remoteScreenLastRenderedAtUtc.HasValue &&
                _remoteScreenLastReceivedAtUtc.Value > _remoteScreenLastRenderedAtUtc.Value &&
                now - _remoteScreenLastRenderedAtUtc.Value > RemoteRenderStallThreshold;
        }

        private async Task CloseScreenSharePeersAsync()
        {
            var peers = _screenSharePeers.Values.ToList();
            _screenSharePeers.Clear();

            foreach (var peer in peers)
                await peer.CloseAsync();
        }

        private static bool IsCurrentCallSignal(NativeCallSession session, string callId)
        {
            return !string.IsNullOrWhiteSpace(callId) &&
                string.Equals(session.CallId, callId, StringComparison.Ordinal);
        }

        private async Task RenderScreenFrameAsync(byte[] frameData, Image target)
        {
            using var stream = new InMemoryRandomAccessStream();
            var writer = new DataWriter(stream.GetOutputStreamAt(0));
            try
            {
                writer.WriteBytes(frameData);
                await writer.StoreAsync();
                await writer.FlushAsync();
                writer.DetachStream();
            }
            finally
            {
                writer.Dispose();
            }

            stream.Seek(0);

            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(stream);
            target.Source = bitmap;
        }

        private void RenderBgraFrame(byte[] frameData, int width, int height, Image target)
        {
            if (frameData.Length < width * height * 4)
            {
                Debug.WriteLine($"[ScreenShare:FLOW:RENDER] Skipped BGRA render: bytes={frameData.Length}; expected={width * height * 4}; {width}x{height}.");
                return;
            }

            if (_remoteScreenBitmap == null ||
                _remoteScreenBitmapWidth != width ||
                _remoteScreenBitmapHeight != height)
            {
                _remoteScreenBitmap = new WriteableBitmap(width, height);
                _remoteScreenBitmapWidth = width;
                _remoteScreenBitmapHeight = height;
                target.Source = _remoteScreenBitmap;
                Debug.WriteLine($"[ScreenShare:FLOW:RENDER] Created remote WriteableBitmap {width}x{height}.");
            }

            using var pixelStream = _remoteScreenBitmap.PixelBuffer.AsStream();
            pixelStream.Seek(0, System.IO.SeekOrigin.Begin);
            pixelStream.Write(frameData, 0, width * height * 4);
            _remoteScreenBitmap.Invalidate();
        }

        private void AudioActivityService_ActivityChanged(object? sender, AudioActivityState e)
        {
            TryEnqueueOnUi(() =>
            {
                UpdateSpeakingIndicators(NativeCallCoordinator.Instance.CurrentSession, e);
            });
        }

        private void EnsureTimerCreated()
        {
            if (_callTimer != null)
                return;

            _callTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _callTimer.Tick += CallTimer_Tick;
        }

        private void CallTimer_Tick(object? sender, object e)
        {
            UpdateCallTimerText();
            WatchRemoteScreenShareHealth();
        }

        private void StartCallTimerIfNeeded()
        {
            EnsureTimerCreated();

            if (_connectedAtUtc == null)
                _connectedAtUtc = DateTimeOffset.UtcNow;

            if (_callTimer != null && !_callTimer.IsEnabled)
                _callTimer.Start();

            UpdateCallTimerText();
        }

        private void StopCallTimer()
        {
            if (_callTimer != null && _callTimer.IsEnabled)
                _callTimer.Stop();
        }

        private void ResetCallTimer()
        {
            StopCallTimer();
            _connectedAtUtc = null;
            UpdateCallTimerText();
        }

        private void UpdateCallTimerText()
        {
            if (_connectedAtUtc == null)
            {
                CallTimerText.Text = "Duration: 00:00";
                return;
            }

            var elapsed = DateTimeOffset.UtcNow - _connectedAtUtc.Value;
            var totalHours = (int)elapsed.TotalHours;

            CallTimerText.Text = totalHours > 0
                ? $"Duration: {totalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}"
                : $"Duration: {elapsed.Minutes:00}:{elapsed.Seconds:00}";
        }

        private void NativeCallCoordinator_SessionChanged(object? sender, NativeCallSession e)
        {
            TryEnqueueOnUi(() =>
            {
                SyncPageStateFromSession(e);
                ApplySessionToUi(e);
            });
        }

        private void NativeSignalingBridge_IncomingCallReceived(object? sender, IncomingCallEventArgs e)
        {
            IncomingCallRingtoneService.TryStart();
            TryEnqueueOnUi(() =>
            {
                if (NativeCallCoordinator.Instance.CurrentSession.State == NativeCallState.Incoming ||
                    string.IsNullOrWhiteSpace(NativeCallCoordinator.Instance.CurrentSession.CallId))
                {
                    NativeCallCoordinator.Instance.SetIncoming(
                        e.CallId,
                        e.FromUserId,
                        string.IsNullOrWhiteSpace(e.FromDisplayName) ? e.FromUsername : e.FromDisplayName,
                        false);
                }

                AddCallParticipant(e.FromUserId);
                RememberParticipantName(e.FromUserId, e.FromDisplayName, e.FromUsername);

                SyncPageStateFromSession(NativeCallCoordinator.Instance.CurrentSession);
                ApplySessionToUi(NativeCallCoordinator.Instance.CurrentSession);
            });
        }

        private void NativeSignalingBridge_CallAnsweredReceived(object? sender, CallSignalEventArgs e)
        {
            IncomingCallRingtoneService.TryStop();
            TryEnqueueOnUi(async () =>
            {
                var session = NativeCallCoordinator.Instance.CurrentSession;
                RememberParticipantName(e.FromUserId, e.FromDisplayName, e.FromUsername);

                if (!string.IsNullOrWhiteSpace(session.CallId) && session.CallId == e.CallId)
                {
                    session.RemoteUserId = e.FromUserId;
                    if (session.TargetUserId <= 0)
                        session.TargetUserId = e.FromUserId;
                }

                AddCallParticipant(e.FromUserId);

                SyncPageStateFromSession(session);
                ApplySessionToUi(session);

                if (_isSharingScreen && session.State == NativeCallState.Connected)
                {
                    try
                    {
                        await StartLocalScreenShareAsync();
                    }
                    catch (Exception ex)
                    {
                        _isSharingScreen = false;
                        _isScreenShare = false;
                        session.IsScreenShare = false;
                        NativeCallCoordinator.Instance.SetStatus(session.State, $"Screen share failed: {ex.Message}", session.PeerText);
                        UpdateDockVisualStates();
                        ApplySessionToUi(session);
                    }
                }
            });
        }

        private void NativeSignalingBridge_CallRejectedReceived(object? sender, CallSignalEventArgs e)
        {
            IncomingCallRingtoneService.TryStop();
            TryEnqueueOnUi(() =>
            {
                RememberParticipantName(e.FromUserId, e.FromDisplayName, e.FromUsername);
                SyncPageStateFromSession(NativeCallCoordinator.Instance.CurrentSession);
                ApplySessionToUi(NativeCallCoordinator.Instance.CurrentSession);
            });
        }

        private void NativeSignalingBridge_CallEndedReceived(object? sender, CallSignalEventArgs e)
        {
            IncomingCallRingtoneService.TryStop();
            TryEnqueueOnUi(() =>
            {
                RememberParticipantName(e.FromUserId, e.FromDisplayName, e.FromUsername);
                _leftParticipantIds.Add(e.FromUserId);
                _callParticipantIds.Remove(e.FromUserId);
                var leaveText = GetLeaveStatusText(e.FromUserId, e.Reason);

                var session = NativeCallCoordinator.Instance.CurrentSession;
                if (string.Equals(session.CallId, e.CallId, StringComparison.Ordinal))
                {
                    var wasRemoteScreenSharing = _remoteScreenShareSenderId == e.FromUserId || _isRemoteVideoVisible || _isRemoteScreenShareLoading;
                    var screenShareEndedText = wasRemoteScreenSharing
                        ? GetScreenShareEndedStatusText(e.FromUserId, e.Reason)
                        : leaveText;
                    _isRemoteVideoVisible = false;
                    _isRemoteScreenShareLoading = false;
                    _remoteScreenShareSenderId = 0;
                    ResetRemoteScreenShareReceiveState(clearImage: true);
                    RemotePlaceholderTitleText.Text = screenShareEndedText;
                    RemotePlaceholderSubtitleText.Text = leaveText;
                    MediaOverlayText.Text = screenShareEndedText;
                    NativeCallCoordinator.Instance.SetStatus(
                        NativeCallState.Ended,
                        leaveText,
                        leaveText);
                }

                SyncPageStateFromSession(session);
                ApplySessionToUi(session);
                _ = PromptForCallExperienceOnceAsync("remote-ended");
            });
        }

        private async void EndButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var session = NativeCallCoordinator.Instance.CurrentSession;
                if (!string.IsNullOrWhiteSpace(session.CallId))
                {
                    foreach (var participantId in GetCallParticipants(session))
                    {
                        if (participantId != session.RemoteUserId)
                            await SocialManager.Instance.Realtime.EndCallAsync(participantId, session.CallId, "left-call");
                    }
                }

                await NativeCallCoordinator.Instance.EndAsync("left-call");
                await StopLocalScreenShareAsync(false);
                AudioActivityService.Instance.Reset();
                LocalPreviewImage.Source = null;
                LocalPreviewPlaceholder.Visibility = Visibility.Visible;
                _isLocalPreviewHidden = false;
                ApplyLocalPreviewVisibility();
                await PromptForCallExperienceOnceAsync("local-ended");
            }
            catch (Exception ex)
            {
                NativeCallCoordinator.Instance.SetStatus(NativeCallState.Failed, ex.Message);
            }
        }

        private void HeadphonesButton_Click(object sender, RoutedEventArgs e)
        {
            var flyout = new MenuFlyout();

            foreach (var device in AudioPlaybackService.Instance.GetOutputDevices())
            {
                AddAudioOutputMenuItem(flyout, device);
            }

            flyout.ShowAt(HeadphonesButton);
        }

        private void AddAudioOutputMenuItem(MenuFlyout flyout, AudioPlaybackService.OutputDeviceInfo device)
        {
            var item = new ToggleMenuFlyoutItem
            {
                Text = device.Name,
                IsChecked = device.DeviceNumber == AudioPlaybackService.Instance.SelectedOutputDeviceNumber
            };

            item.Click += (_, __) =>
            {
                if (AudioPlaybackService.Instance.SetOutputDevice(device.DeviceNumber))
                {
                    _selectedAudioOutput = device.Name;

                    NativeCallCoordinator.Instance.SetStatus(
                        NativeCallCoordinator.Instance.CurrentSession.State,
                        $"Audio output set to {_selectedAudioOutput}.");
                }
            };

            flyout.Items.Add(item);
        }

        private void MuteToggleButton_Click(object sender, RoutedEventArgs e)
        {
            _isMuted = !_isMuted;
            _isMicEnabled = !_isMuted;

            if (_isMuted)
                AudioActivityService.Instance.UpdateLocalLevel(0);

            NativeCallCoordinator.Instance.SetLocalAudioMuted(_isMuted || !_isMicEnabled);

            NativeCallCoordinator.Instance.SetStatus(
                NativeCallCoordinator.Instance.CurrentSession.State,
                _isMuted ? "Microphone muted." : "Microphone unmuted.");

            UpdateDockVisualStates();
            UpdateSpeakingIndicators(NativeCallCoordinator.Instance.CurrentSession, AudioActivityService.Instance.Current);
        }

        private bool TryEnqueueOnUi(Action action)
        {
            var queue = DispatcherQueue;
            if (queue == null)
            {
                Debug.WriteLine("[CallPage] DispatcherQueue is unavailable; skipping queued UI work.");
                return false;
            }

            return queue.TryEnqueue(() => action());
        }

        private bool TryEnqueueOnUi(Func<Task> action)
        {
            var queue = DispatcherQueue;
            if (queue == null)
            {
                Debug.WriteLine("[CallPage] DispatcherQueue is unavailable; skipping queued UI work.");
                return false;
            }

            return queue.TryEnqueue(async () =>
            {
                try
                {
                    await action();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[CallPage] Queued UI work failed: {ex}");
                }
            });
        }

        private void DeafenToggleButton_Click(object sender, RoutedEventArgs e)
        {
            _isDeafened = !_isDeafened;

            if (_isDeafened)
                AudioActivityService.Instance.UpdateRemoteLevel(0);

            NativeCallCoordinator.Instance.SetRemoteAudioDeafened(_isDeafened);

            NativeCallCoordinator.Instance.SetStatus(
                NativeCallCoordinator.Instance.CurrentSession.State,
                _isDeafened ? "Incoming audio deafened." : "Incoming audio restored.");

            UpdateDockVisualStates();
            UpdateSpeakingIndicators(NativeCallCoordinator.Instance.CurrentSession, AudioActivityService.Instance.Current);
        }

        private async void ScreenShareToggleButton_Click(object sender, RoutedEventArgs e)
        {
            ScreenShareCrashBreadcrumb.Mark("CallPage ScreenShareToggleButton_Click entered");
            DiagnosticLogService.EnsureLogFile("screen-share toggle clicked");

            try
            {
                DiagnosticLogService.WriteLine(
                    $"[ScreenShare:UI] Toggle clicked; state={NativeCallCoordinator.Instance.CurrentSession.State}; currentSharing={_isSharingScreen}; nativeRunning={NativeScreenShareStreamingService.Instance.IsRunning}; processArch={System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}; osArch={System.Runtime.InteropServices.RuntimeInformation.OSArchitecture}; is64Bit={Environment.Is64BitProcess}; base={AppContext.BaseDirectory}");
                DiagnosticLogService.Flush();
                ScreenShareReportService.SaveLatestState(
                    BuildScreenShareReportContext("toggle-clicked", "Screen-share toggle clicked before startup state changed."));

                if (_screenShareToggleInProgress)
                {
                    DiagnosticLogService.WriteLine("[ScreenShare:UI] Toggle ignored because a screen-share start/stop operation is already in progress.");
                    NativeCallCoordinator.Instance.SetStatus(
                        NativeCallCoordinator.Instance.CurrentSession.State,
                        "Screen share is still starting or stopping. Please wait a moment.");
                    return;
                }

                if (NativeCallCoordinator.Instance.CurrentSession.State != NativeCallState.Connected)
                {
                    NativeCallCoordinator.Instance.SetStatus(
                        NativeCallCoordinator.Instance.CurrentSession.State,
                        "Start or accept the call before sharing your screen.");
                    return;
                }

                _screenShareToggleInProgress = true;
                ScreenShareToggleButton.IsEnabled = false;

                _isSharingScreen = !_isSharingScreen;
                _isScreenShare = _isSharingScreen;
                NativeCallCoordinator.Instance.CurrentSession.IsScreenShare = _isSharingScreen;

                ModeText.Text = _isSharingScreen ? "Mode: 4K Screen Share + Voice" : "Mode: Voice Call";

                NativeCallCoordinator.Instance.SetStatus(
                    NativeCallCoordinator.Instance.CurrentSession.State,
                    _isSharingScreen ? "Screen share enabled." : "Screen share disabled.");

                UpdateDockVisualStates();

                if (NativeCallCoordinator.Instance.CurrentSession.State == NativeCallState.Connected)
                {
                    try
                    {
                        if (_isSharingScreen)
                        {
                            ScreenShareCrashBreadcrumb.Mark("CallPage ScreenShareToggleButton_Click before StartLocalScreenShareAsync");
                            await StartLocalScreenShareAsync();
                            await Task.Delay(1200);
                        }
                        else
                        {
                            ScreenShareCrashBreadcrumb.Mark("CallPage ScreenShareToggleButton_Click before StopLocalScreenShareAsync");
                            await StopLocalScreenShareAsync(true, promptForFeedback: true);
                            LocalPreviewImage.Source = null;
                            LocalPreviewPlaceholder.Visibility = Visibility.Visible;
                        }
                    }
                    catch (Exception ex)
                    {
                        ScreenShareCrashBreadcrumb.Mark("CallPage ScreenShareToggleButton_Click toggle failed: " + ex.GetType().Name);
                        DiagnosticLogService.WriteLine("[ScreenShare:UI] Toggle failed: " + ex);
                        DiagnosticLogService.Flush();

                        _isSharingScreen = !_isSharingScreen;
                        _isScreenShare = _isSharingScreen;
                        NativeCallCoordinator.Instance.CurrentSession.IsScreenShare = _isSharingScreen;

                        NativeCallCoordinator.Instance.SetStatus(
                            NativeCallCoordinator.Instance.CurrentSession.State,
                            $"Screen share failed: {ex.Message}");

                        UpdateDockVisualStates();
                        ApplySessionToUi(NativeCallCoordinator.Instance.CurrentSession);
                    }
                }
            }
            catch (Exception ex)
            {
                ScreenShareCrashBreadcrumb.Mark("CallPage ScreenShareToggleButton_Click fatal: " + ex.GetType().Name);
                DiagnosticLogService.WriteLine("[ScreenShare:UI] Toggle fatal failure: " + ex);
                DiagnosticLogService.Flush();
                NativeCallCoordinator.Instance.SetStatus(
                    NativeCallCoordinator.Instance.CurrentSession.State,
                    $"Screen share failed: {ex.Message}");
            }
            finally
            {
                _screenShareToggleInProgress = false;
                ScreenShareToggleButton.IsEnabled = NativeCallCoordinator.Instance.CurrentSession.State == NativeCallState.Connected;
                ScreenShareCrashBreadcrumb.Mark("CallPage ScreenShareToggleButton_Click exiting");
                DiagnosticLogService.Flush();
            }
        }

        private void FullscreenButton_Click(object sender, RoutedEventArgs e)
        {
            _fullscreenChromeTimer?.Stop();
            FullscreenButton.Visibility = Visibility.Visible;
            FullscreenExitButton.Visibility = Visibility.Visible;
            _isFullscreen = !_isFullscreen;
            ApplyScreenShareFocusMode();
            UpdateDockVisualStates();
        }

        private void CallMediaStage_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (_isFullscreen)
                ShowFullscreenChrome();
        }

        private void EnsureFullscreenChromeTimer()
        {
            if (_fullscreenChromeTimer != null)
                return;

            _fullscreenChromeTimer = new DispatcherTimer
            {
                Interval = FullscreenChromeHideDelay
            };

            _fullscreenChromeTimer.Tick += FullscreenChromeTimer_Tick;
        }

        private void FullscreenChromeTimer_Tick(object? sender, object e)
        {
            _fullscreenChromeTimer?.Stop();

            if (_isFullscreen)
                FullscreenExitButton.Visibility = Visibility.Collapsed;
        }

        private void ShowFullscreenChrome()
        {
            if (_isFullscreen)
            {
                FullscreenPointerSurface.Visibility = Visibility.Visible;
                FullscreenButton.Visibility = Visibility.Collapsed;
                FullscreenExitButton.Visibility = Visibility.Visible;
                FullscreenExitButton.Opacity = 1;
            }
            else
            {
                FullscreenButton.Visibility = Visibility.Visible;
                FullscreenExitButton.Visibility = Visibility.Collapsed;
                FullscreenButton.Opacity = 1;
            }

            if (!_isFullscreen)
                return;

            EnsureFullscreenChromeTimer();
            _fullscreenChromeTimer?.Stop();
            _fullscreenChromeTimer?.Start();
        }

        private void StopFullscreenChromeTimer()
        {
            if (_fullscreenChromeTimer == null)
                return;

            _fullscreenChromeTimer.Stop();
            _fullscreenChromeTimer.Tick -= FullscreenChromeTimer_Tick;
            _fullscreenChromeTimer = null;
        }

        private void LocalPreviewHideButton_Click(object sender, RoutedEventArgs e)
        {
            _isLocalPreviewHidden = true;
            ApplyLocalPreviewVisibility();
            UpdateDockVisualStates();
        }

        private void LocalPreviewRestoreButton_Click(object sender, RoutedEventArgs e)
        {
            _isLocalPreviewHidden = false;
            ApplyLocalPreviewVisibility();
            UpdateDockVisualStates();
        }

        private void StreamInformationButton_Click(object sender, RoutedEventArgs e)
        {
            var flyout = new Flyout
            {
                Content = BuildStreamInformationPanel()
            };

            flyout.ShowAt((FrameworkElement)sender);
        }

        private void ScreenShareDetailsButton_Click(object sender, RoutedEventArgs e)
        {
            var flyout = new Flyout
            {
                Content = BuildScreenShareSummaryPanel()
            };

            flyout.ShowAt((FrameworkElement)sender);
        }

        private Border BuildScreenShareSummaryPanel()
        {
            var isSharing = NativeScreenShareStreamingService.Instance.IsRunning && _isSharingScreen;
            var resolutionText = _screenShareLastWidth > 0 && _screenShareLastHeight > 0
                ? $"{_screenShareLastWidth} x {_screenShareLastHeight}"
                : "Waiting for frames";

            var panel = new StackPanel
            {
                Width = 240,
                Spacing = 8
            };

            panel.Children.Add(new TextBlock
            {
                Text = "Call details",
                FontSize = 16,
                Foreground = new SolidColorBrush(Colors.White)
            });

            panel.Children.Add(CreateDetailsText($"Status: {(isSharing ? "Sharing" : "Not sharing")}"));
            panel.Children.Add(CreateDetailsText($"Quality: {NativeScreenShareStreamingService.Instance.CurrentQuality.Name}"));
            panel.Children.Add(CreateDetailsText($"FPS: {_screenShareObservedFps:0.0} / {NativeScreenShareStreamingService.TargetFps}"));
            panel.Children.Add(CreateDetailsText($"Receiver FPS: {_remoteReceiveObservedFps:0.0} in / {_remotePlaybackObservedFps:0.0} view"));
            panel.Children.Add(CreateDetailsText($"Bitrate: {NativeScreenShareStreamingService.Instance.CurrentBitrate / 1_000_000d:0.0} Mbps"));
            panel.Children.Add(CreateDetailsText($"Resolution: {resolutionText}"));
            panel.Children.Add(CreateDetailsText($"Last frame: {FormatBytes(_screenShareLastBytes)}"));

            return new Border
            {
                Padding = new Thickness(14),
                CornerRadius = new CornerRadius(16),
                Background = new SolidColorBrush(ColorHelper.FromArgb(255, 17, 19, 24)),
                BorderBrush = new SolidColorBrush(ColorHelper.FromArgb(48, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                Child = panel
            };
        }

        private Border BuildStreamInformationPanel()
        {
            var isSharing = NativeScreenShareStreamingService.Instance.IsRunning && _isSharingScreen;
            var resolutionText = _screenShareLastWidth > 0 && _screenShareLastHeight > 0
                ? $"{_screenShareLastWidth} x {_screenShareLastHeight} ({GetResolutionTier(_screenShareLastHeight)})"
                : "Waiting for frames";

            var panel = new StackPanel
            {
                Width = 340,
                Spacing = 8
            };

            panel.Children.Add(new TextBlock
            {
                Text = "Stream information",
                FontSize = 16,
                Foreground = new SolidColorBrush(Colors.White)
            });

            panel.Children.Add(CreateDetailsText($"Status: {(isSharing ? "Sharing" : "Not sharing")}"));
            panel.Children.Add(CreateDetailsText($"Transport codec: H.264"));
            panel.Children.Add(CreateDetailsText($"Transport path: {NativeScreenShareStreamingService.Instance.TransportPipeline}"));
            panel.Children.Add(CreateDetailsText($"Capture policy: {(NativeScreenShareStreamingService.Instance.RequireDirectX12CapturePath ? "DirectX 12 WGC required" : "Best available")}"));
            panel.Children.Add(CreateDetailsText($"Encoder policy: {(NativeScreenShareStreamingService.Instance.RequireHardwareEncoder ? "DirectX 12 GPU hardware required" : "GPU preferred with software fallback")}"));
            panel.Children.Add(CreateDetailsText($"Encoder mode: {NativeScreenShareStreamingService.Instance.EncoderMode}"));
            panel.Children.Add(CreateDetailsText($"GPU path: {NativeScreenShareStreamingService.Instance.EncoderGpuDeviceMode}"));
            panel.Children.Add(CreateDetailsText($"Encoder input: {NativeScreenShareStreamingService.Instance.EncoderInputFormat}"));
            panel.Children.Add(CreateDetailsText($"Low latency flags: {(NativeScreenShareStreamingService.Instance.EncoderRealtimeModeEnabled ? "encoder" : "encoder pending")} / {(NativeScreenShareStreamingService.Instance.EncoderLowLatencyOutputEnabled ? "output" : "output pending")}"));
            panel.Children.Add(CreateDetailsText($"Recovery keyframe: every {NativeScreenShareStreamingService.Instance.RecoveryKeyFrameInterval} frames"));
            panel.Children.Add(CreateDetailsText($"Recovery requests: {NativeScreenShareStreamingService.Instance.RecoveryKeyFrameRequests}"));
            panel.Children.Add(CreateDetailsText($"Mode: locked realtime"));
            panel.Children.Add(CreateDetailsText($"Quality state: {NativeScreenShareStreamingService.Instance.AdaptiveState}"));
            panel.Children.Add(CreateDetailsText($"Target FPS: {NativeScreenShareStreamingService.TargetFps} locked"));
            panel.Children.Add(CreateDetailsText($"Sender throughput: {_screenShareObservedFps:0.0} FPS"));
            panel.Children.Add(CreateDetailsText($"Capture loop: {NativeScreenShareStreamingService.Instance.CaptureFps:0.0} FPS"));
            panel.Children.Add(CreateDetailsText($"Encoder output: {NativeScreenShareStreamingService.Instance.EncodedFps:0.0} FPS"));
            panel.Children.Add(CreateDetailsText($"Receiver input: {_remoteReceiveObservedFps:0.0} FPS"));
            panel.Children.Add(CreateDetailsText($"Receiver playback: {_remotePlaybackObservedFps:0.0} FPS"));
            panel.Children.Add(CreateDetailsText($"Receiver GPU queue: avg {_remoteGpuPlaybackAverageQueue:0.0}, peak {_remoteGpuPlaybackQueuePeak}, now {GetPendingRemoteGpuPlaybackFrameCount()}"));
            panel.Children.Add(CreateDetailsText($"Gaming readiness: {GetGamingReadinessText()}"));
            panel.Children.Add(CreateDetailsText($"Capture/encode: {NativeScreenShareStreamingService.Instance.LastCaptureMilliseconds:0}ms / {NativeScreenShareStreamingService.Instance.LastEncodeMilliseconds:0}ms"));
            panel.Children.Add(CreateDetailsText($"Bitrate: {NativeScreenShareStreamingService.Instance.CurrentBitrate / 1_000_000d:0.0} Mbps"));
            panel.Children.Add(CreateDetailsText($"Resolution: {resolutionText}"));
            panel.Children.Add(CreateDetailsText($"Preference: {NativeScreenShareStreamingService.Instance.RequestedQuality.Name}"));
            panel.Children.Add(CreateDetailsText($"Active quality: {NativeScreenShareStreamingService.Instance.CurrentQuality.Name}"));
            panel.Children.Add(CreateDetailsText($"Auto quality changes: Off"));
            panel.Children.Add(CreateDetailsText($"Congestion signals: {NativeScreenShareStreamingService.Instance.CongestionSignals}"));
            panel.Children.Add(CreateDetailsText($"Encoder fallbacks: {NativeScreenShareStreamingService.Instance.HardwareEncoderFallbackCount}"));
            panel.Children.Add(CreateDetailsText($"Local preview: throttled snapshot"));
            panel.Children.Add(CreateDetailsText($"Remote render: hardware H.264 playback"));
            panel.Children.Add(CreateDetailsText($"Render target: {NativeScreenShareStreamingService.Instance.CurrentQuality.Width} x {NativeScreenShareStreamingService.Instance.CurrentQuality.Height}"));
            panel.Children.Add(CreateDetailsText($"RTP frames: {_screenShareRtpFrames}"));
            panel.Children.Add(CreateDetailsText($"RTP metadata frames: {_screenShareRtpMetadataFrames}"));
            panel.Children.Add(CreateDetailsText($"RTCP recovery: PLI {_screenSharePeers.Values.Sum(peer => peer.SentPliRequests)}, NACK {_screenSharePeers.Values.Sum(peer => peer.SentNackRequests)}"));
            panel.Children.Add(CreateDetailsText($"Fallback frames: {_screenShareFallbackFrames}"));
            panel.Children.Add(CreateDetailsText($"Dropped send frames: {_screenShareDroppedFrames}"));
            panel.Children.Add(CreateDetailsText($"Dropped receive frames: {_screenShareDroppedReceiveFrames}"));
            panel.Children.Add(CreateDetailsText($"Receiver last packet: {FormatRelativeTimestamp(_remoteScreenLastReceivedAtUtc)}"));
            panel.Children.Add(CreateDetailsText($"Receiver last render: {FormatRelativeTimestamp(_remoteScreenLastRenderedAtUtc)}"));
            panel.Children.Add(CreateDetailsText($"Receiver decoder resets: {_remoteDecoderResetCount}"));
            panel.Children.Add(CreateDetailsText($"Screen sound: {(_isScreenShareSoundEnabled ? (SystemAudioShareService.Instance.IsRunning ? "On, capturing" : "On, waiting") : "Off")}"));
            panel.Children.Add(CreateDetailsText($"Sound device: {_selectedScreenShareSoundDevice}"));
            panel.Children.Add(CreateDetailsText($"Sound packets sent: {_screenShareSoundPacketsSent}"));
            panel.Children.Add(CreateDetailsText($"Last sound packet: {FormatRelativeTimestamp(_screenShareSoundLastPacketAtUtc)}"));
            panel.Children.Add(CreateDetailsText($"Last frame: {FormatBytes(_screenShareLastBytes)}"));

            return new Border
            {
                Padding = new Thickness(14),
                CornerRadius = new CornerRadius(16),
                Background = new SolidColorBrush(ColorHelper.FromArgb(255, 17, 19, 24)),
                BorderBrush = new SolidColorBrush(ColorHelper.FromArgb(48, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                Child = new ScrollViewer
                {
                    MaxHeight = 560,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    Content = panel
                }
            };
        }

        private static TextBlock CreateDetailsText(string text)
        {
            return new TextBlock
            {
                Text = text,
                FontSize = 13,
                Foreground = new SolidColorBrush(ColorHelper.FromArgb(255, 218, 222, 230)),
                TextWrapping = TextWrapping.Wrap
            };
        }

        private static string GetResolutionTier(int height)
        {
            if (height >= 2160)
                return "4K";
            if (height >= 1440)
                return "1440p";
            if (height >= 1080)
                return "1080p";
            if (height >= 720)
                return "720p";

            return $"{height}p";
        }

        private string GetGamingReadinessText()
        {
            var target = NativeScreenShareStreamingService.TargetFps;
            var senderLocked =
                NativeScreenShareStreamingService.Instance.CaptureFps >= target - 2 &&
                NativeScreenShareStreamingService.Instance.EncodedFps >= target - 2;
            var receiverLocked =
                _remoteReceiveObservedFps >= target - 2 &&
                _remotePlaybackObservedFps >= target - 2;
            var queueHealthy = _remoteGpuPlaybackAverageQueue <= 12 &&
                _remoteGpuPlaybackQueuePeak < RemoteGpuBacklogKeyFrameRequestQueue;

            if (senderLocked && receiverLocked && queueHealthy)
                return "Ready: sender and receiver near 60fps";

            if (!senderLocked)
                return "Sender below 60fps";

            if (!receiverLocked)
                return "Receiver below 60fps";

            return "Queue pressure";
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes <= 0)
                return "-";

            return bytes >= 1024 * 1024
                ? $"{bytes / 1024d / 1024d:0.0} MB"
                : $"{bytes / 1024d:0.0} KB";
        }

        private static string FormatRelativeTimestamp(DateTimeOffset? timestamp)
        {
            if (!timestamp.HasValue)
                return "-";

            var elapsed = DateTimeOffset.UtcNow - timestamp.Value;
            return elapsed.TotalSeconds < 1
                ? "now"
                : $"{elapsed.TotalSeconds:0}s ago";
        }

        private void ScreenShareQualityButton_Click(object sender, RoutedEventArgs e)
        {
            var flyout = new MenuFlyout();
            AddScreenShareQualityItem(flyout, ScreenShareQualityPreset.Performance540p);
            AddScreenShareQualityItem(flyout, ScreenShareQualityPreset.Hd720p);
            AddScreenShareQualityItem(flyout, ScreenShareQualityPreset.FullHd1080p);
            AddScreenShareQualityItem(flyout, ScreenShareQualityPreset.QuadHd2K);
            AddScreenShareQualityItem(flyout, ScreenShareQualityPreset.UltraHd4K);
            flyout.ShowAt((FrameworkElement)sender);
        }

        private void AddScreenShareQualityItem(MenuFlyout flyout, ScreenShareQualityPreset preset)
        {
            var profile = ScreenShareQualityProfile.FromPreset(preset);
            var item = new ToggleMenuFlyoutItem
            {
                Text = $"{profile.Name} screen share quality",
                IsChecked = NativeScreenShareStreamingService.Instance.QualityPreset == preset
            };

            item.Click += (_, __) =>
            {
                var wasSharing = _isSharingScreen && NativeScreenShareStreamingService.Instance.IsRunning;
                ApplyScreenShareQuality(preset);

                NativeCallCoordinator.Instance.SetStatus(
                    NativeCallCoordinator.Instance.CurrentSession.State,
                    wasSharing
                        ? $"Switching live screen share quality to {profile.Name}."
                        : $"Screen share quality set to {profile.Name}.");

                if (wasSharing)
                {
                    _fallbackFrameLastSentUtc = DateTimeOffset.MinValue;
                }

                UpdateDockVisualStates();
            };

            flyout.Items.Add(item);
        }

        private void ApplyScreenShareQuality(ScreenShareQualityPreset preset)
        {
            var profile = ScreenShareQualityProfile.FromPreset(preset);

            NativeScreenShareStreamingService.Instance.SetQuality(preset);
            _screenShareLastQuality = profile.Name;
            _screenShareLastWidth = profile.Width;
            _screenShareLastHeight = profile.Height;
            ScreenShareQualityLabel.Text = profile.Name;
            _screenShareFpsWindowStartedUtc = DateTimeOffset.UtcNow;
            _screenShareFrameCounter = 0;
            _screenShareObservedFps = 0;
            _lastRtpMetadataSentUtc = DateTimeOffset.MinValue;
            _lastRtpMetadataWidth = 0;
            _lastRtpMetadataHeight = 0;
            ScreenShareReportService.SaveLatestState(
                BuildScreenShareReportContext("quality-changed", $"Screen-share quality set to {profile.Name}."));
            _h264Decoder?.Dispose();
            _h264Decoder = null;
            _rtpH264Decoder?.Dispose();
            _rtpH264Decoder = null;
            _rtpDecoderWidth = 0;
            _rtpDecoderHeight = 0;
            _remoteScreenBitmap = null;
        }

        private async void ScreenShareSoundButton_Click(object sender, RoutedEventArgs e)
        {
            var flyout = new MenuFlyout();

            var shareSoundItem = new ToggleMenuFlyoutItem
            {
                Text = "Share screen sound",
                IsChecked = _isScreenShareSoundEnabled
            };
            shareSoundItem.Click += async (_, __) =>
            {
                _isScreenShareSoundEnabled = !_isScreenShareSoundEnabled;
                var wantedEnabled = _isScreenShareSoundEnabled;

                if (_isSharingScreen && NativeScreenShareStreamingService.Instance.IsRunning)
                {
                    if (wantedEnabled)
                        await TryStartScreenShareSoundAsync(NativeCallCoordinator.Instance.CurrentSession, showSuccessStatus: true);
                    else
                        await SystemAudioShareService.Instance.StopAsync();
                }

                if (!wantedEnabled)
                {
                    NativeCallCoordinator.Instance.SetStatus(
                        NativeCallCoordinator.Instance.CurrentSession.State,
                        "Screen share sound disabled.");
                }

                UpdateDockVisualStates();
            };

            flyout.Items.Add(shareSoundItem);
            flyout.Items.Add(new MenuFlyoutSeparator());

            var deviceMenu = new MenuFlyoutSubItem
            {
                Text = $"Sound device: {_selectedScreenShareSoundDevice}"
            };

            foreach (var device in await SystemAudioShareService.Instance.GetOutputDevicesAsync())
            {
                AddScreenShareSoundDeviceItem(deviceMenu, device);
            }

            flyout.Items.Add(deviceMenu);
            flyout.ShowAt((FrameworkElement)sender);
        }

        private void AddScreenShareSoundDeviceItem(MenuFlyoutSubItem parent, Zink.Models.RecorderDeviceItem device)
        {
            var selectedId = SystemAudioShareService.Instance.SelectedDeviceId ?? "";
            var item = new ToggleMenuFlyoutItem
            {
                Text = device.Name,
                IsChecked = string.Equals(device.Id ?? "", selectedId, StringComparison.OrdinalIgnoreCase)
            };

            item.Click += async (_, __) =>
            {
                await SystemAudioShareService.Instance.SetOutputDeviceAsync(device.Id, device.Name);
                _selectedScreenShareSoundDevice = SystemAudioShareService.Instance.SelectedDeviceName;

                NativeCallCoordinator.Instance.SetStatus(
                    NativeCallCoordinator.Instance.CurrentSession.State,
                    $"Screen share sound device set to {_selectedScreenShareSoundDevice}.");

                UpdateDockVisualStates();
            };

            parent.Items.Add(item);
        }

        private async void AddFriendToCallButton_Click(object sender, RoutedEventArgs e)
        {
            var session = NativeCallCoordinator.Instance.CurrentSession;
            if (session.State != NativeCallState.Connected && session.State != NativeCallState.Calling)
            {
                NativeCallCoordinator.Instance.SetStatus(session.State, "Start the call before adding friends.");
                return;
            }

            if (_callParticipantIds.Count >= MaxCallParticipants - 1)
            {
                NativeCallCoordinator.Instance.SetStatus(session.State, "This call already has the maximum of 10 people.");
                return;
            }

            var flyout = new MenuFlyout();

            try
            {
                var friends = await SocialManager.Instance.Api.GetFriendsAsync();
                var availableFriends = friends
                    .Where(friend => friend.UserId > 0 && !_callParticipantIds.Contains(friend.UserId))
                    .OrderByDescending(friend => friend.IsOnline)
                    .ThenBy(friend => string.IsNullOrWhiteSpace(friend.DisplayName) ? friend.Username : friend.DisplayName)
                    .ToList();

                if (availableFriends.Count == 0)
                {
                    flyout.Items.Add(new MenuFlyoutItem
                    {
                        Text = "No friends available"
                    });
                }
                else
                {
                    foreach (var friend in availableFriends)
                    {
                        var item = new MenuFlyoutItem
                        {
                            Text = $"{GetFriendDisplayName(friend)}{(friend.IsOnline ? " - online" : " - offline")}"
                        };

                        item.Click += async (_, __) => await InviteFriendToCurrentCallAsync(friend);
                        flyout.Items.Add(item);
                    }
                }
            }
            catch (Exception ex)
            {
                flyout.Items.Add(new MenuFlyoutItem
                {
                    Text = $"Could not load friends: {ex.Message}"
                });
            }

            flyout.ShowAt((FrameworkElement)sender);
        }

        private async Task InviteFriendToCurrentCallAsync(FriendDto friend)
        {
            var session = NativeCallCoordinator.Instance.CurrentSession;
            if (string.IsNullOrWhiteSpace(session.CallId))
            {
                NativeCallCoordinator.Instance.SetStatus(session.State, "No active call to invite friends into.");
                return;
            }

            if (_callParticipantIds.Count >= MaxCallParticipants - 1)
            {
                NativeCallCoordinator.Instance.SetStatus(session.State, "This call already has the maximum of 10 people.");
                return;
            }

            AddCallParticipant(friend.UserId);
            RememberParticipantName(friend.UserId, friend.DisplayName, friend.Username);
            await SocialManager.Instance.Realtime.CallUserAsync(friend.UserId, session.CallId);
            if (NativeScreenShareStreamingService.Instance.IsRunning)
                _ = StartScreenShareRtpOfferAsync(friend.UserId, session.CallId);

            NativeCallCoordinator.Instance.SetStatus(
                session.State,
                $"Invited {GetFriendDisplayName(friend)} to the call.");

            UpdateMembersPanel(session);
        }

        private static string GetFriendDisplayName(FriendDto friend)
        {
            if (!string.IsNullOrWhiteSpace(friend.DisplayName))
                return friend.DisplayName;

            if (!string.IsNullOrWhiteSpace(friend.Username))
                return friend.Username;

            return $"User {friend.UserId}";
        }

        private void RememberParticipantName(long userId, string? displayName, string? username = null)
        {
            if (userId <= 0)
                return;

            var name = !string.IsNullOrWhiteSpace(displayName)
                ? displayName.Trim()
                : (!string.IsNullOrWhiteSpace(username) ? username.Trim() : "");

            if (!string.IsNullOrWhiteSpace(name))
                _participantDisplayNames[userId] = name;
        }

        private string GetParticipantDisplayName(long userId)
        {
            if (userId > 0 &&
                _participantDisplayNames.TryGetValue(userId, out var name) &&
                !string.IsNullOrWhiteSpace(name))
            {
                return name;
            }

            return userId > 0 ? $"User {userId}" : "Remote user";
        }

        private string GetLeaveStatusText(long userId, string? reason)
        {
            var name = GetParticipantDisplayName(userId);
            return string.Equals(reason, "closed-app", StringComparison.OrdinalIgnoreCase)
                ? $"{name} closed the app and left the call"
                : $"{name} left the call";
        }

        private string GetScreenShareEndedStatusText(long userId, string? reason)
        {
            var name = GetParticipantDisplayName(userId);
            if (string.Equals(reason, "closed-app", StringComparison.OrdinalIgnoreCase))
                return $"{name}'s screen share ended because they closed Zink";

            return $"{name} ended their screen share";
        }

        private static string GetAvatarInitial(string displayName)
        {
            if (string.IsNullOrWhiteSpace(displayName))
                return "U";

            return char.ToUpperInvariant(displayName.Trim()[0]).ToString();
        }

        private IEnumerable<long> GetCallParticipants(NativeCallSession session)
        {
            if (session.RemoteUserId > 0)
                _callParticipantIds.Add(session.RemoteUserId);

            if (session.TargetUserId > 0)
                _callParticipantIds.Add(session.TargetUserId);

            return _callParticipantIds
                .Where(id => id > 0 && (_localUserId <= 0 || id != _localUserId) && !_leftParticipantIds.Contains(id))
                .Distinct()
                .Take(MaxCallParticipants - 1)
                .ToList();
        }

        private void AddCallParticipant(long userId)
        {
            if (userId <= 0 ||
                (_localUserId > 0 && userId == _localUserId) ||
                _callParticipantIds.Count >= MaxCallParticipants - 1)
                return;

            _leftParticipantIds.Remove(userId);
            _callParticipantIds.Add(userId);
            RenderAdditionalMembers();
        }

        private async Task LoadLocalUserIdAsync()
        {
            try
            {
                var userInfo = await Zink.Services.Calling.TokenStore.Instance.GetUserInfoAsync();
                if (userInfo == null)
                    return;

                _localUserId = userInfo.Value.userId;
                _callParticipantIds.Remove(_localUserId);
                Debug.WriteLine($"[Call] Local user id loaded: {_localUserId}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Call] Failed to load local user id: {ex.Message}");
            }
        }

        private void RenderAdditionalMembers()
        {
            AdditionalMembersPanel.Children.Clear();

            var session = NativeCallCoordinator.Instance.CurrentSession;
            var primaryRemote = session.RemoteUserId > 0 ? session.RemoteUserId : session.TargetUserId;

            foreach (var participantId in _callParticipantIds.Where(id => id > 0 && id != primaryRemote).Take(MaxCallParticipants - 2))
            {
                AdditionalMembersPanel.Children.Add(CreateAdditionalMemberCard(participantId));
            }
        }

        private Border CreateAdditionalMemberCard(long participantId)
        {
            return new Border
            {
                CornerRadius = new CornerRadius(18),
                Background = new SolidColorBrush(ColorHelper.FromArgb(255, 18, 21, 26)),
                Padding = new Thickness(14),
                Child = new Grid
                {
                    ColumnSpacing = 12,
                    ColumnDefinitions =
                    {
                        new ColumnDefinition { Width = new GridLength(44) },
                        new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                        new ColumnDefinition { Width = GridLength.Auto }
                    },
                    Children =
                    {
                        CreateMemberAvatar(GetParticipantDisplayName(participantId)),
                        CreateAdditionalMemberText(participantId),
                        CreateAdditionalMemberBadge()
                    }
                }
            };
        }

        private static Grid CreateMemberAvatar(string displayName)
        {
            var avatar = new Grid
            {
                Width = 44,
                Height = 44
            };

            avatar.Children.Add(new Microsoft.UI.Xaml.Shapes.Ellipse
            {
                Fill = new SolidColorBrush(ColorHelper.FromArgb(255, 35, 38, 43))
            });
            avatar.Children.Add(new TextBlock
            {
                Text = GetAvatarInitial(displayName),
                Foreground = new SolidColorBrush(Colors.White),
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            });

            return avatar;
        }

        private StackPanel CreateAdditionalMemberText(long participantId)
        {
            var panel = new StackPanel
            {
                Spacing = 2,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(panel, 1);

            panel.Children.Add(new TextBlock
            {
                Text = GetParticipantDisplayName(participantId),
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Colors.White)
            });
            panel.Children.Add(new TextBlock
            {
                Text = "Invited",
                Foreground = new SolidColorBrush(ColorHelper.FromArgb(255, 168, 168, 168)),
                FontSize = 13
            });

            return panel;
        }

        private static Border CreateAdditionalMemberBadge()
        {
            var badge = new Border
            {
                Padding = new Thickness(10, 4, 10, 4),
                CornerRadius = new CornerRadius(999),
                Background = new SolidColorBrush(ColorHelper.FromArgb(255, 45, 49, 56)),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = "Joined",
                    Foreground = new SolidColorBrush(Colors.White),
                    FontSize = 12,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
                }
            };
            Grid.SetColumn(badge, 2);
            return badge;
        }

        private void MoreButton_Click(object sender, RoutedEventArgs e)
        {
            var flyout = BuildMoreFlyout();
            flyout.ShowAt((FrameworkElement)sender);
        }

        private MenuFlyout BuildMoreFlyout()
        {
            var flyout = new MenuFlyout();

            var micHeader = new MenuFlyoutSubItem
            {
                Text = $"Microphone: {_selectedMicrophone}"
            };

            foreach (var device in MicCaptureService.Instance.GetInputDevices())
            {
                AddMicrophoneMenuItem(micHeader, device);
            }

            flyout.Items.Add(micHeader);
            flyout.Items.Add(new MenuFlyoutSeparator());

            var reconnectItem = new MenuFlyoutItem
            {
                Text = "Reconnect"
            };
            reconnectItem.Click += MoreReconnect_Click;
            flyout.Items.Add(reconnectItem);

            var resetItem = new MenuFlyoutItem
            {
                Text = "Reset Call"
            };
            resetItem.Click += MoreReset_Click;
            flyout.Items.Add(resetItem);

            if (Frame.CanGoBack)
            {
                var backItem = new MenuFlyoutItem
                {
                    Text = "Back"
                };
                backItem.Click += MoreBack_Click;
                flyout.Items.Add(backItem);
            }

            return flyout;
        }

        private void AddMicrophoneMenuItem(MenuFlyoutSubItem parent, MicCaptureService.InputDeviceInfo device)
        {
            var item = new ToggleMenuFlyoutItem
            {
                Text = device.Name,
                IsChecked = device.DeviceNumber == MicCaptureService.Instance.SelectedInputDeviceNumber
            };

            item.Click += (_, __) =>
            {
                if (MicCaptureService.Instance.SetInputDevice(device.DeviceNumber))
                {
                    _selectedMicrophone = device.Name;

                    NativeCallCoordinator.Instance.SetStatus(
                        NativeCallCoordinator.Instance.CurrentSession.State,
                        $"Microphone set to {_selectedMicrophone}.");
                }
            };

            parent.Items.Add(item);
        }

        private string GetCurrentMicDisplayName()
        {
            var devices = MicCaptureService.Instance.GetInputDevices();
            var current = devices.FirstOrDefault(x => x.DeviceNumber == MicCaptureService.Instance.SelectedInputDeviceNumber);
            return current?.Name ?? "Default microphone";
        }

        private void MoreBack_Click(object? sender, RoutedEventArgs e)
        {
            if (Frame.CanGoBack)
                Frame.GoBack();
        }

        private void MoreReconnect_Click(object? sender, RoutedEventArgs e)
        {
            NativeCallCoordinator.Instance.SetStatus(
                NativeCallCoordinator.Instance.CurrentSession.State,
                "Reconnect requested.");
        }

        private void MoreReset_Click(object? sender, RoutedEventArgs e)
        {
            NativeCallCoordinator.Instance.Reset();
            _callParticipantIds.Clear();
            _leftParticipantIds.Clear();
            AdditionalMembersPanel.Children.Clear();
            _isMicEnabled = true;
            _isMuted = false;
            _isDeafened = false;
            NativeCallCoordinator.Instance.SetLocalAudioMuted(false);
            NativeCallCoordinator.Instance.SetRemoteAudioDeafened(false);
            _isSharingScreen = false;
            _isRemoteVideoVisible = false;
            _isRemoteScreenShareLoading = false;
            _remoteScreenShareSenderId = 0;
            ResetRemoteScreenShareReceiveState(clearImage: true);
            ResetCallTimer();
            AudioActivityService.Instance.Reset();
            _ = StopLocalScreenShareAsync(true);
            LocalPreviewImage.Source = null;
            LocalPreviewPlaceholder.Visibility = Visibility.Visible;
            _isLocalPreviewHidden = false;
            ApplyLocalPreviewVisibility();
            UpdateDockVisualStates();
            SyncPageStateFromSession(NativeCallCoordinator.Instance.CurrentSession);
            ApplySessionToUi(NativeCallCoordinator.Instance.CurrentSession);
        }

        private void SyncPageStateFromSession(NativeCallSession session)
        {
            if (session.TargetUserId > 0)
                _targetUserId = session.TargetUserId;
            else if (session.RemoteUserId > 0)
                _targetUserId = session.RemoteUserId;

            if (session.TargetUserId > 0)
                AddCallParticipant(session.TargetUserId);

            if (session.RemoteUserId > 0)
                AddCallParticipant(session.RemoteUserId);

            if (session.IsScreenShare)
                _isScreenShare = true;

            _isSharingScreen = _isScreenShare;
        }

        private void ApplySessionToUi(NativeCallSession session)
        {
            SyncPageStateFromSession(session);

            AvatarInitialText.Text = _isScreenShare ? "S" : "V";
            ModeText.Text = _isScreenShare ? "Mode: 4K Screen Share + Voice" : "Mode: Voice Call";

            CallIdText.Text = $"Call ID: {(string.IsNullOrWhiteSpace(session.CallId) ? "-" : session.CallId)}";
            TargetUserText.Text = $"Target user: {(session.TargetUserId > 0 ? session.TargetUserId.ToString() : "-")}";
            RemoteUserText.Text = $"Remote user: {(session.RemoteUserId > 0 ? session.RemoteUserId.ToString() : "-")}";
            PeerText.Text = string.IsNullOrWhiteSpace(session.PeerText) ? "Preparing call..." : session.PeerText;
            StatusText.Text = string.IsNullOrWhiteSpace(session.StatusText) ? "Waiting..." : session.StatusText;
            MediaOverlayText.Text = GetCallStateDisplayText(session);
            RemotePlaceholderTitleText.Text = session.State == NativeCallState.Ended ? "Call ended" : "Remote participant";
            RemotePlaceholderSubtitleText.Text = session.State == NativeCallState.Ended
                ? (!string.IsNullOrWhiteSpace(session.PeerText)
                    ? session.PeerText
                    : (session.RemoteUserId > 0 ? $"{GetParticipantDisplayName(session.RemoteUserId)} left the call" : "The remote participant left"))
                : "Remote media surface";

            if (session.State == NativeCallState.Connected)
            {
                StartCallTimerIfNeeded();
            }
            else if (session.State == NativeCallState.Ended ||
                     session.State == NativeCallState.Rejected ||
                     session.State == NativeCallState.Failed ||
                     session.State == NativeCallState.Idle)
            {
                StopCallTimer();
                if (session.State == NativeCallState.Idle)
                    ResetCallTimer();
                _ = StopLocalScreenShareAsync(false);
            }

            UpdateMembersPanel(session);
            UpdateMediaLayerVisibility();
            UpdateSpeakingIndicators(session, AudioActivityService.Instance.Current);
            SetConnectionState(session.State);
            UpdateDiscordCallPresence(session);
        }

        private void UpdateDiscordCallPresence(NativeCallSession session)
        {
            try
            {
                var isActiveCall =
                    session.State == NativeCallState.Calling ||
                    session.State == NativeCallState.Incoming ||
                    session.State == NativeCallState.Accepted ||
                    session.State == NativeCallState.Negotiating ||
                    session.State == NativeCallState.Connected;

                if (!isActiveCall)
                {
                    if (session.State == NativeCallState.Ended ||
                        session.State == NativeCallState.Rejected ||
                        session.State == NativeCallState.Failed)
                    {
                        DiscordPresenceService.Instance.SetAppPresence("Call ended");
                    }
                    else if (session.State == NativeCallState.Idle ||
                             session.State == NativeCallState.Ready)
                    {
                        DiscordPresenceService.Instance.SetAppPresence("Calls ready");
                    }

                    return;
                }

                var isScreenSharing =
                    _isSharingScreen ||
                    session.IsScreenShare ||
                    NativeScreenShareStreamingService.Instance.IsRunning;

                var participantCount = Math.Min(
                    MaxCallParticipants,
                    Math.Max(1, 1 + GetCallParticipants(session).Count()));

                TimeSpan? connectedFor = null;
                if (session.State == NativeCallState.Connected && _connectedAtUtc.HasValue)
                    connectedFor = DateTimeOffset.UtcNow - _connectedAtUtc.Value;

                var status = session.State switch
                {
                    NativeCallState.Calling => isScreenSharing ? "Starting a screen share in Zink" : "Calling on Zink",
                    NativeCallState.Incoming => isScreenSharing ? "Incoming screen share on Zink" : "Incoming Zink call",
                    NativeCallState.Accepted => "Joining a Zink call",
                    NativeCallState.Negotiating => "Connecting a Zink call",
                    NativeCallState.Connected => isScreenSharing ? "Screen sharing in Zink" : "In a Zink call",
                    _ => "Using Zink Calls"
                };

                DiscordPresenceService.Instance.SetCallPresence(
                    status,
                    participantCount,
                    isScreenSharing,
                    _isMuted,
                    _isDeafened,
                    connectedFor);
            }
            catch { }
        }

        private void UpdateMembersPanel(NativeCallSession session)
        {
            var remoteName = session.RemoteUserId > 0 ? GetParticipantDisplayName(session.RemoteUserId) : "Remote user";
            RemoteMemberTitleText.Text = remoteName;
            RemoteAvatarText.Text = session.RemoteUserId > 0 ? GetAvatarInitial(remoteName) : "R";

            var remoteLeft = session.State == NativeCallState.Ended ||
                (session.RemoteUserId > 0 && _leftParticipantIds.Contains(session.RemoteUserId));
            var participantCount = Math.Min(MaxCallParticipants, 1 + GetCallParticipants(session).Count());
            MembersSummaryText.Text = session.State == NativeCallState.Ended
                ? "Call ended"
                : $"In call - {participantCount}";
            AddFriendToCallButton.IsEnabled = participantCount < MaxCallParticipants &&
                (session.State == NativeCallState.Connected || session.State == NativeCallState.Calling);
            RenderAdditionalMembers();
            RemoteMemberCard.Opacity = remoteLeft ? 0.62 : 1.0;

            YouMemberStateText.Text = session.State switch
            {
                NativeCallState.Calling => "Calling",
                NativeCallState.Incoming => "Ringing",
                NativeCallState.Accepted => "Accepted",
                NativeCallState.Connected => "Connected",
                NativeCallState.Ended => "Ended",
                NativeCallState.Rejected => "Rejected",
                NativeCallState.Failed => "Failed",
                _ => "Ready"
            };

            RemoteMemberStateText.Text = remoteLeft
                ? "Left call"
                : (session.State switch
                {
                    NativeCallState.Calling => "Ringing",
                    NativeCallState.Incoming => "Calling you",
                    NativeCallState.Accepted => "Joining",
                    NativeCallState.Connected => "Connected",
                    NativeCallState.Ended => "Left call",
                    NativeCallState.Rejected => "Declined",
                    NativeCallState.Failed => "Unavailable",
                    _ => session.RemoteUserId > 0 ? "Waiting" : "Not connected"
                });
        }

        private void UpdateMediaLayerVisibility()
        {
            var showWatchPrompt = _isRemoteScreenShareWatchPromptVisible && !_isRemoteVideoVisible;
            var showLoading = _isRemoteScreenShareLoading && !showWatchPrompt;

            RemotePlaceholderPanel.Visibility = (_isRemoteVideoVisible || showLoading || showWatchPrompt)
                ? Visibility.Collapsed
                : Visibility.Visible;
            RemoteScreenWatchPanel.Visibility = showWatchPrompt ? Visibility.Visible : Visibility.Collapsed;
            RemoteScreenLoadingPanel.Visibility = showLoading ? Visibility.Visible : Visibility.Collapsed;
            RemoteScreenWatchPanel.IsHitTestVisible = showWatchPrompt;
            RemoteScreenLoadingPanel.IsHitTestVisible = false;
            RemoteScreenLoadingRing.IsActive = showLoading;
            ApplyLocalPreviewVisibility();
        }

        private void ShowRemoteScreenShareLoading(string title, string subtitle)
        {
            if (_remoteScreenShareSenderId > 0 && !IsRemoteScreenShareAccepted(_remoteScreenShareSenderId))
            {
                ShowRemoteScreenShareWatchPrompt();
                return;
            }

            _isRemoteScreenShareLoading = true;
            _isRemoteScreenShareWatchPromptVisible = false;
            RemoteScreenLoadingTitle.Text = title;
            RemoteScreenLoadingSubtitle.Text = subtitle;
        }

        private void ShowRemoteScreenShareWatchPrompt()
        {
            _isRemoteScreenShareLoading = false;
            _isRemoteScreenShareWatchPromptVisible = _pendingRemoteScreenShareUserIds.Count > 0;
            RefreshRemoteScreenShareWatchPrompt();
        }

        private void RefreshRemoteScreenShareWatchPrompt()
        {
            var pending = _pendingRemoteScreenShareUserIds
                .Where(id => !_acceptedRemoteScreenShareUserIds.Contains(id))
                .Distinct()
                .ToList();
            _isRemoteScreenShareWatchPromptVisible = pending.Count > 0 && !_isRemoteVideoVisible;
            var signature = string.Join("|", pending.OrderBy(id => id));
            if (_isRemoteScreenShareWatchPromptVisible &&
                string.Equals(_remoteScreenShareWatchPromptSignature, signature, StringComparison.Ordinal) &&
                RemoteScreenWatchUsersPanel.Children.Count == pending.Count)
            {
                return;
            }

            _remoteScreenShareWatchPromptSignature = signature;
            RemoteScreenWatchUsersPanel.Children.Clear();

            if (pending.Count == 0)
            {
                _isRemoteScreenShareWatchPromptVisible = false;
                _remoteScreenShareWatchPromptSignature = "";
                RemoteScreenWatchTitle.Text = "A stream is ready";
                RemoteScreenWatchSubtitle.Text = "Choose a stream to watch.";
                ClearRemoteScreenShareWatchPreview();
                return;
            }

            RemoteScreenWatchTitle.Text = pending.Count == 1
                ? $"{GetParticipantDisplayName(pending[0])} is streaming"
                : $"{pending.Count} streams are ready";
            RemoteScreenWatchSubtitle.Text = pending.Count == 1
                ? "Click Watch the Stream to connect when you are ready."
                : "Choose who you want to watch.";

            foreach (var userId in pending)
            {
                var button = new Button
                {
                    Tag = userId,
                    MinHeight = 54,
                    Padding = new Thickness(14, 0, 14, 0),
                    CornerRadius = new CornerRadius(18),
                    Background = new SolidColorBrush(ColorHelper.FromArgb(120, 61, 220, 132)),
                    BorderBrush = new SolidColorBrush(ColorHelper.FromArgb(115, 124, 255, 178)),
                    BorderThickness = new Thickness(1),
                    Foreground = new SolidColorBrush(Colors.White),
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch
                };

                button.Click += WatchRemoteScreenShare_Click;
                button.Content = new Grid
                {
                    ColumnDefinitions =
                    {
                        new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                        new ColumnDefinition { Width = GridLength.Auto }
                    },
                    Children =
                    {
                        new StackPanel
                        {
                            Spacing = 1,
                            VerticalAlignment = VerticalAlignment.Center,
                            Children =
                            {
                                new TextBlock
                                {
                                    Text = GetParticipantDisplayName(userId),
                                    Foreground = new SolidColorBrush(Colors.White),
                                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                                    FontSize = 14
                                },
                                new TextBlock
                                {
                                    Text = "Screen share",
                                    Foreground = new SolidColorBrush(ColorHelper.FromArgb(220, 214, 232, 238)),
                                    FontSize = 12
                                }
                            }
                        },
                        new StackPanel
                        {
                            Orientation = Orientation.Horizontal,
                            Spacing = 8,
                            VerticalAlignment = VerticalAlignment.Center,
                            Children =
                            {
                                new FontIcon
                                {
                                    Glyph = "\uE8D4",
                                    FontFamily = new FontFamily("Segoe Fluent Icons"),
                                    FontSize = 16,
                                    Foreground = new SolidColorBrush(Colors.White)
                                },
                                new TextBlock
                                {
                                    Text = "Watch the Stream",
                                    Foreground = new SolidColorBrush(Colors.White),
                                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                                    FontSize = 13,
                                    VerticalAlignment = VerticalAlignment.Center
                                }
                            }
                        }
                    }
                };

                Grid.SetColumn((FrameworkElement)((Grid)button.Content).Children[1], 1);
                RemoteScreenWatchUsersPanel.Children.Add(button);
            }
        }

        private async Task TryRenderRemoteScreenShareWatchPreviewAsync(ScreenFrameEventArgs e)
        {
            if (e.FromUserId <= 0 ||
                e.FrameData.Length == 0 ||
                !_pendingRemoteScreenShareUserIds.Contains(e.FromUserId) ||
                _acceptedRemoteScreenShareUserIds.Contains(e.FromUserId))
            {
                return;
            }

            var now = DateTimeOffset.UtcNow;
            if (now - _lastRemoteWatchPreviewRenderedUtc < TimeSpan.FromMilliseconds(500))
                return;

            _lastRemoteWatchPreviewRenderedUtc = now;
            try
            {
                await RenderScreenFrameAsync(e.FrameData, RemoteScreenWatchPreviewImage);
                RemoteScreenWatchPreviewPlaceholder.Visibility = Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ScreenShare:WATCH] Preview render failed from {e.FromUserId}: {ex}");
                RemoteScreenWatchPreviewPlaceholder.Visibility = Visibility.Visible;
            }
        }

        private void ClearRemoteScreenShareWatchPreview()
        {
            RemoteScreenWatchPreviewImage.Source = null;
            RemoteScreenWatchPreviewPlaceholder.Visibility = Visibility.Visible;
            _lastRemoteWatchPreviewRenderedUtc = DateTimeOffset.MinValue;
        }

        private void WatchRemoteScreenShare_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement element || element.Tag is not long userId)
                return;

            DiagnosticLogService.WriteLine($"[ScreenShare:WATCH] Watch the Stream clicked for participant {userId}; pending={_pendingRemoteScreenShareUserIds.Count}; accepted={_acceptedRemoteScreenShareUserIds.Count}; currentSender={_remoteScreenShareSenderId}.");
            _acceptedRemoteScreenShareUserIds.Add(userId);
            _pendingRemoteScreenShareUserIds.Remove(userId);
            _remoteScreenShareWatchPromptSignature = "";
            _isRemoteScreenShareWatchPromptVisible = false;
            _remoteScreenShareSenderId = userId;
            _isRemoteVideoVisible = false;
            ClearRemoteScreenShareWatchPreview();
            RefreshRemoteScreenShareWatchPrompt();
            var hadVisibleRemoteStream = _remoteScreenLastRenderedAtUtc.HasValue ||
                Volatile.Read(ref _remoteRenderedFrameCount) > 0;
            _remoteScreenLastRenderedAtUtc = null;
            if (hadVisibleRemoteStream)
                ResetRemoteScreenShareReceiveState(clearImage: true);
            else
                RemoteScreenImage.Source = null;
            ShowRemoteScreenShareLoading(
                $"Watching {GetParticipantDisplayName(userId)}'s stream",
                "Connecting to the live screen share...");
            UpdateMediaLayerVisibility();
            MediaOverlayText.Text = $"Watching {GetParticipantDisplayName(userId)}";
            RequestRemoteVideoKeyFrame("viewer clicked Watch the Stream");
            _ = RequestFreshScreenShareAfterWatchAsync(userId);
        }

        private async Task RequestFreshScreenShareAfterWatchAsync(long userId)
        {
            try
            {
                var session = NativeCallCoordinator.Instance.CurrentSession;
                if (userId <= 0 ||
                    session.State != NativeCallState.Connected ||
                    string.IsNullOrWhiteSpace(session.CallId))
                {
                    return;
                }

                _remoteScreenShareSenderId = userId;
                _lastQosSentUtc = DateTimeOffset.MinValue;
                DiagnosticLogService.WriteLine($"[ScreenShare:WATCH] Requesting fresh stream offer from {userId} after Watch the Stream click.");
                await SendScreenShareQosIfNeededAsync("receiver RTP stalled; restart screen-share offer after Watch the Stream clicked");
                RequestRemoteVideoKeyFrame("viewer clicked Watch the Stream and requested fresh offer");
            }
            catch (Exception ex)
            {
                DiagnosticLogService.WriteLine($"[ScreenShare:WATCH] Fresh stream request failed for {userId}: {ex}");
            }
            finally
            {
                DiagnosticLogService.Flush();
            }
        }

        private bool IsRemoteScreenShareAccepted(long userId)
        {
            return userId > 0 && _acceptedRemoteScreenShareUserIds.Contains(userId);
        }

        private bool ShouldRenderRemoteScreenShareFrame(long userId, string transport)
        {
            if (userId <= 0)
                return true;

            if (!_acceptedRemoteScreenShareUserIds.Contains(userId))
            {
                var added = _pendingRemoteScreenShareUserIds.Add(userId);
                if (added || !_isRemoteScreenShareWatchPromptVisible)
                {
                    TryEnqueueOnUi(() =>
                    {
                        ShowRemoteScreenShareWatchPrompt();
                        UpdateMediaLayerVisibility();
                    });
                }

                Debug.WriteLine($"[ScreenShare:WATCH] Holding {transport} frame from {userId} until Watch the Stream is clicked.");
                return false;
            }

            if (_remoteScreenShareSenderId > 0 && _remoteScreenShareSenderId != userId)
            {
                Debug.WriteLine($"[ScreenShare:WATCH] Ignored {transport} frame from {userId}; currently watching {_remoteScreenShareSenderId}.");
                return false;
            }

            return true;
        }

        private long GetScreenSharePeerParticipantId(object? peerObject)
        {
            if (peerObject == null)
                return 0;

            foreach (var pair in _screenSharePeers)
            {
                if (ReferenceEquals(pair.Value, peerObject))
                    return pair.Key;
            }

            return 0;
        }

        private void ApplyScreenShareFocusMode()
        {
            CallHeaderPanel.Visibility = Visibility.Collapsed;
            CallSidePanel.Visibility = _isFullscreen ? Visibility.Collapsed : Visibility.Visible;
            CallStatusPanel.Visibility = Visibility.Collapsed;
            CallControlDock.Visibility = _isFullscreen ? Visibility.Collapsed : Visibility.Visible;
            FullscreenButton.Visibility = _isFullscreen ? Visibility.Collapsed : Visibility.Visible;
            FullscreenExitButton.Visibility = _isFullscreen ? Visibility.Visible : Visibility.Collapsed;
            FullscreenPointerSurface.Visibility = _isFullscreen ? Visibility.Visible : Visibility.Collapsed;
            StreamInformationButton.Visibility = _isFullscreen ? Visibility.Collapsed : Visibility.Visible;
            MediaOverlayText.Visibility = _isFullscreen ? Visibility.Collapsed : Visibility.Visible;

            if (_isFullscreen)
                App.MainWindow?.EnterFullscreenMode();
            else
                App.MainWindow?.ExitFullscreenMode();

            CallSideColumn.Width = _isFullscreen ? new GridLength(0) : new GridLength(330);
            CallLayoutRoot.Padding = _isFullscreen ? new Thickness(0) : new Thickness(24);
            CallMainContentGrid.ColumnSpacing = _isFullscreen ? 0 : 18;
            CallMediaStage.Margin = _isFullscreen ? new Thickness(0) : new Thickness(18, 18, 18, 120);
            RemoteScreenImage.Margin = _isFullscreen ? new Thickness(0) : new Thickness(12);
            CallStageBorder.CornerRadius = _isFullscreen ? new CornerRadius(0) : new CornerRadius(30);
            CallStageBorder.BorderThickness = _isFullscreen ? new Thickness(0) : new Thickness(1);
            FullscreenIcon.Glyph = _isFullscreen ? "\uE73F" : "\uE740";
            ToolTipService.SetToolTip(FullscreenButton, _isFullscreen ? "Exit fullscreen" : "Fullscreen screen share");

            if (_isFullscreen)
            {
                ShowFullscreenChrome();
            }
            else
            {
                _fullscreenChromeTimer?.Stop();
                FullscreenPointerSurface.Visibility = Visibility.Collapsed;
                FullscreenButton.Visibility = Visibility.Visible;
                FullscreenExitButton.Visibility = Visibility.Collapsed;
            }

            ApplyLocalPreviewVisibility();
        }

        private void ApplyLocalPreviewVisibility()
        {
            bool showPreview = _isSharingScreen &&
                NativeScreenShareStreamingService.Instance.IsRunning &&
                !_isLocalPreviewHidden &&
                !_isFullscreen;
            LocalPreviewBorder.Visibility = showPreview ? Visibility.Visible : Visibility.Collapsed;
            LocalPreviewRestorePanel.Visibility = _isSharingScreen &&
                NativeScreenShareStreamingService.Instance.IsRunning &&
                _isLocalPreviewHidden &&
                !_isFullscreen
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private void UpdateSpeakingIndicators(NativeCallSession session, AudioActivityState activity)
        {
            var youSpeaking = activity.LocalSpeaking && _isMicEnabled && !_isMuted && session.State == NativeCallState.Connected;
            var remoteLeft = session.State == NativeCallState.Ended ||
                (session.RemoteUserId > 0 && _leftParticipantIds.Contains(session.RemoteUserId));
            var remoteSpeaking = activity.RemoteSpeaking && !_isDeafened && session.State == NativeCallState.Connected && !remoteLeft;

            YouSpeakingBadgeText.Text = youSpeaking ? $"Speaking {activity.LocalLevel:P0}" : (_isMuted ? "Muted" : "Idle");
            RemoteSpeakingBadgeText.Text = remoteLeft ? "Left" : (remoteSpeaking ? $"Speaking {activity.RemoteLevel:P0}" : "Idle");

            YouSpeakingBadge.Background = new SolidColorBrush(
                youSpeaking
                    ? ColorHelper.FromArgb(255, 36, 88, 62)
                    : (_isMuted
                        ? ColorHelper.FromArgb(255, 95, 40, 40)
                        : ColorHelper.FromArgb(255, 45, 49, 56)));

            RemoteSpeakingBadge.Background = new SolidColorBrush(
                remoteSpeaking
                    ? ColorHelper.FromArgb(255, 36, 88, 62)
                    : ColorHelper.FromArgb(255, 45, 49, 56));

            YouMemberCard.BorderThickness = new Thickness(youSpeaking ? 2 : 0);
            RemoteMemberCard.BorderThickness = new Thickness(remoteSpeaking ? 2 : 0);

            YouMemberCard.BorderBrush = new SolidColorBrush(
                youSpeaking
                    ? ColorHelper.FromArgb(255, 60, 140, 90)
                    : Colors.Transparent);

            RemoteMemberCard.BorderBrush = new SolidColorBrush(
                remoteSpeaking
                    ? ColorHelper.FromArgb(255, 60, 140, 90)
                    : Colors.Transparent);

            if (session.State == NativeCallState.Connected)
            {
                YouMemberStateText.Text = youSpeaking ? "Speaking" : (_isMuted ? "Muted" : "Connected");
                RemoteMemberStateText.Text = remoteLeft ? "Left call" : (remoteSpeaking ? "Speaking" : "Connected");

                if (youSpeaking && remoteSpeaking)
                {
                    PeerText.Text = $"Connected with user {session.RemoteUserId} - both speaking";
                }
                else if (youSpeaking)
                {
                    PeerText.Text = $"Connected with user {session.RemoteUserId} - you are speaking";
                }
                else if (remoteSpeaking)
                {
                    PeerText.Text = $"Connected with user {session.RemoteUserId} - remote is speaking";
                }
            }
            else if (session.State == NativeCallState.Ended)
            {
                YouMemberStateText.Text = "Call ended";
                RemoteMemberStateText.Text = "Left call";
                PeerText.Text = string.IsNullOrWhiteSpace(session.PeerText) ? "Call ended" : session.PeerText;
                StatusText.Text = string.IsNullOrWhiteSpace(session.StatusText) ? "Call ended." : session.StatusText;
            }
        }

        private static string GetCallStateDisplayText(NativeCallSession session)
        {
            return session.State switch
            {
                NativeCallState.Connected => "Connected",
                NativeCallState.Ended => "Call ended",
                NativeCallState.Rejected => "Call rejected",
                NativeCallState.Failed => "Call failed",
                NativeCallState.Calling => "Calling",
                NativeCallState.Incoming => "Incoming call",
                NativeCallState.Accepted => "Accepted",
                NativeCallState.Negotiating => "Connecting",
                NativeCallState.Ready => "Ready",
                _ => string.IsNullOrWhiteSpace(session.StatusText) ? session.State.ToString() : session.StatusText
            };
        }

        private void SetConnectionState(NativeCallState state)
        {
            ConnectionBadgeText.Text = state.ToString();

            switch (state)
            {
                case NativeCallState.Ready:
                case NativeCallState.Connected:
                    ConnectionBadge.Background = new SolidColorBrush(ColorHelper.FromArgb(255, 36, 64, 36));
                    break;

                case NativeCallState.Calling:
                case NativeCallState.Incoming:
                case NativeCallState.Accepted:
                case NativeCallState.Negotiating:
                    ConnectionBadge.Background = new SolidColorBrush(ColorHelper.FromArgb(255, 72, 56, 24));
                    break;

                case NativeCallState.Rejected:
                case NativeCallState.Ended:
                case NativeCallState.Failed:
                    ConnectionBadge.Background = new SolidColorBrush(ColorHelper.FromArgb(255, 80, 28, 28));
                    break;

                default:
                    ConnectionBadge.Background = new SolidColorBrush(ColorHelper.FromArgb(255, 46, 46, 46));
                    break;
            }
        }

        private void UpdateDockVisualStates()
        {
            MuteToggleButton.Background = _isMuted
                ? new SolidColorBrush(ColorHelper.FromArgb(255, 95, 40, 40))
                : new SolidColorBrush(ColorHelper.FromArgb(255, 23, 26, 32));

            DeafenToggleButton.Background = _isDeafened
                ? new SolidColorBrush(ColorHelper.FromArgb(255, 95, 40, 40))
                : new SolidColorBrush(ColorHelper.FromArgb(255, 23, 26, 32));

            ScreenShareToggleButton.Background = _isSharingScreen
                ? new SolidColorBrush(ColorHelper.FromArgb(255, 36, 88, 62))
                : new SolidColorBrush(ColorHelper.FromArgb(255, 23, 26, 32));

            HeadphonesButton.Background = new SolidColorBrush(ColorHelper.FromArgb(255, 23, 26, 32));
            MoreButton.Background = new SolidColorBrush(ColorHelper.FromArgb(255, 23, 26, 32));
            ScreenShareSoundButton.Background = _isScreenShareSoundEnabled
                ? new SolidColorBrush(ColorHelper.FromArgb(255, 36, 88, 62))
                : new SolidColorBrush(ColorHelper.FromArgb(255, 23, 26, 32));
            LocalPreviewRestoreButton.Background = _isLocalPreviewHidden
                ? new SolidColorBrush(ColorHelper.FromArgb(255, 36, 64, 96))
                : new SolidColorBrush(ColorHelper.FromArgb(255, 23, 26, 32));
            ScreenShareQualityButton.Background = NativeScreenShareStreamingService.Instance.QualityPreset == ScreenShareQualityPreset.UltraHd4K
                ? new SolidColorBrush(ColorHelper.FromArgb(255, 36, 88, 62))
                : new SolidColorBrush(ColorHelper.FromArgb(255, 23, 26, 32));
            ScreenShareQualityLabel.Text = NativeScreenShareStreamingService.Instance.CurrentQuality.Name;
            ScreenShareDetailsButton.Background = _isSharingScreen
                ? new SolidColorBrush(ColorHelper.FromArgb(255, 36, 64, 96))
                : new SolidColorBrush(ColorHelper.FromArgb(255, 23, 26, 32));
            FullscreenButton.Background = _isFullscreen
                ? new SolidColorBrush(ColorHelper.FromArgb(220, 36, 64, 96))
                : new SolidColorBrush(ColorHelper.FromArgb(196, 17, 19, 24));

            MuteIcon.Glyph = _isMuted ? "\uE74F" : "\uE720";
        }

        private sealed class PendingRemoteH264Frame
        {
            public PendingRemoteH264Frame(byte[] frameData, int width, int height, bool isKeyFrame, long sequence)
                : this(frameData, width, height, isKeyFrame, sequence, 0)
            {
            }

            public PendingRemoteH264Frame(byte[] frameData, int width, int height, bool isKeyFrame, long sequence, long generation)
                : this(frameData, width, height, isKeyFrame, sequence, generation, RemoteGpuFrameDuration)
            {
            }

            public PendingRemoteH264Frame(byte[] frameData, int width, int height, bool isKeyFrame, long sequence, long generation, TimeSpan sampleDuration)
            {
                FrameData = frameData;
                Width = width;
                Height = height;
                IsKeyFrame = isKeyFrame;
                Sequence = sequence;
                Generation = generation;
                SampleDuration = sampleDuration;
            }

            public byte[] FrameData { get; }
            public int Width { get; }
            public int Height { get; }
            public bool IsKeyFrame { get; }
            public long Sequence { get; }
            public long Generation { get; }
            public TimeSpan SampleDuration { get; }
        }

        private sealed class BitReader
        {
            private readonly byte[] _data;
            private int _bitOffset;

            public BitReader(byte[] data)
            {
                _data = data;
            }

            public uint ReadBits(int count)
            {
                uint value = 0;
                for (var i = 0; i < count; i++)
                {
                    value = (value << 1) | (ReadBit() ? 1u : 0u);
                }

                return value;
            }

            public bool ReadBit()
            {
                if (_bitOffset >= _data.Length * 8)
                    throw new InvalidOperationException("Unexpected end of H.264 bitstream.");

                var byteIndex = _bitOffset / 8;
                var bitIndex = 7 - (_bitOffset % 8);
                _bitOffset++;
                return ((_data[byteIndex] >> bitIndex) & 1) != 0;
            }

            public uint ReadUnsignedExpGolomb()
            {
                var leadingZeroBits = 0;
                while (!ReadBit())
                    leadingZeroBits++;

                if (leadingZeroBits == 0)
                    return 0;

                return ((1u << leadingZeroBits) - 1u) + ReadBits(leadingZeroBits);
            }

            public int ReadSignedExpGolomb()
            {
                var value = ReadUnsignedExpGolomb();
                var signed = (int)((value + 1) / 2);
                return (value & 1) == 0 ? -signed : signed;
            }
        }
    }
}
