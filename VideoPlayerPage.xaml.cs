using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Windowing;

using global::Windows.Media.Core;
using global::Windows.Media.Playback;

using WStorage = global::Windows.Storage;
using WPickers = global::Windows.Storage.Pickers;
using WSystem = global::Windows.System;
using WAppModel = global::Windows.ApplicationModel;
using WDeployment = global::Windows.Management.Deployment;

using WinRT.Interop;

using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Collections.Generic;

using DispatcherTimer = Microsoft.UI.Xaml.DispatcherTimer;
using Zink.Services;

namespace Zink
{
    public sealed partial class SecondsToTimeConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            try
            {
                if (value is double seconds)
                {
                    var t = TimeSpan.FromSeconds(seconds);
                    return t.TotalHours >= 1 ? t.ToString(@"hh\:mm\:ss") : t.ToString(@"mm\:ss");
                }
            }
            catch { }
            return "00:00";
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language) => 0d;
    }

    public sealed partial class VideoPlayerPage : Page
    {
        private bool isFullScreen;
        private DispatcherTimer hideControlsTimer;
        private DispatcherTimer _videoBadgeHideTimer;
        private DispatcherTimer _nvidiaOverlaySuppressTimer;

        private WStorage.StorageFile _currentFile;

        private DispatcherTimer _positionTimer;
        private bool _isUserSeeking = false;
        private bool _ignoreSliderChange = false;
        private bool _mediaReadyForSeek = false;

        private DispatcherTimer _discordPresenceTimer;
        private bool _userPausedDiscordPresence = false;
        private bool _suppressDiscordPresenceRefresh = false;
        private int _lastDiscordPushedSecond = -1;
        private bool _forceStartFromBeginningOnNextLoad = false;
        private DateTime _discordPlaybackStartUtc = DateTime.MinValue;
        private TimeSpan _discordPlaybackDuration = TimeSpan.Zero;
        private bool _discordClockReady = false;
        private DateTime _lastDiscordPresencePushUtc = DateTime.MinValue;
        private const double DiscordPresencePushIntervalSeconds = 4.2;

        private bool _nativeSubtitlesEnabled = false;
        private MediaPlaybackItem _currentPlaybackItem;

        private const string VIDEO_POS_PREFIX = "Zink_VideoPos_";
        private const string VIDEO_CODEC_STATE_PREFIX = "Zink_VideoCodecState_";
        private double _pendingResumeSeconds = 0;
        private double _lastSavedPosSeconds = -1;
        private DateTime _lastPosSaveUtc = DateTime.MinValue;

        private const string DASH_LastKind = "HomeDash_LastKind";
        private const string DASH_LastPath = "HomeDash_LastPath";
        private const string DASH_LastTitle = "HomeDash_LastTitle";
        private const string DASH_LastSubtitle = "HomeDash_LastSubtitle";

        private bool _codecPromptAlreadyShownForCurrentFile = false;
        private string _lastCodecPromptedPath = null;
        private bool _videoSupportPromptAlreadyShownForCurrentFile = false;
        private string _lastVideoSupportPromptedPath = null;

        private bool _waitingForCodecInstallReturn = false;
        private bool _isHandlingCodecReturnReload = false;
        private bool _suppressCodecPromptOnce = false;
        private string _pendingReloadVideoPath = null;
        private double _pendingReloadResumeSeconds = 0;

        private const string CodecInstallerFolderName = "CodecInstallers";
        private const string DolbyDigitalPlusPrefix = "DolbyLaboratories.DolbyDigitalPlusDecoderOEM_";
        private const string DolbyAC4Prefix = "DolbyLaboratories.DolbyAC4DecoderOEM_";
        private const string MicrosoftHevcVideoExtensionPrefix = "Microsoft.HEVCVideoExtension";
        private const string MicrosoftHevcVideoExtensionsPrefix = "Microsoft.HEVCVideoExtensions";
        private const string MicrosoftHevcDeviceExtensionPrefix = "Microsoft.HEVCVideoExtensionsFromDeviceManufacturer";
        private const string DolbyAccessPrefix = "DolbyLaboratories.DolbyAccess";

        private const string CodecStateNotNeeded = "not_needed";
        private const string CodecStateInstalledDdp = "installed_ddp";
        private const string CodecStateInstalledAc4 = "installed_ac4";
        private const string CodecStatePendingDdp = "pending_ddp";
        private const string CodecStatePendingAc4 = "pending_ac4";

        private const string VIDEO_VOLUME_KEY = "Zink_VideoPlayer_Volume";
        private const string VIDEO_SURROUND_MODE_KEY = "Zink_VideoPlayer_SurroundMode";
        private double _lastNonZeroVolume = 1.0;
        private bool _volumeUiReady = false;
        private IReadOnlyList<AudioStreamInfo> _detectedAudioStreams = Array.Empty<AudioStreamInfo>();
        private AudioStreamInfo _selectedAudioStream;
        private string _audioInfoStatus = "No audio information detected yet.";
        private VideoMetadataInfo _detectedVideoMetadata = VideoMetadataInfo.Empty;
        private string _videoInfoStatus = "No video metadata detected yet.";
        private string _preferredSurroundMode = SurroundModeAuto;

        private Flyout _soundFlyout;
        private Slider _flyoutVolumeSlider;
        private TextBlock _flyoutVolumeText;
        private Button _flyoutMuteButton;
        private ComboBox _surroundModeComboBox;
        private TextBlock _surroundModeStatusText;

        private const string SurroundModeAuto = "auto";
        private const string SurroundModeAtmos = "atmos";
        private const string SurroundMode21 = "2.1";
        private const string SurroundMode51 = "5.1";
        private const string SurroundMode71 = "7.1";
        private const string SurroundMode72 = "7.2";
        private const string SurroundMode512 = "5.1.2";
        private const string SurroundMode712 = "7.1.2";
        private const string SurroundMode714 = "7.1.4";
        private const string SurroundMode914 = "9.1.4";
        private const string SurroundMode916 = "9.1.6";
        private const string SurroundMode24110 = "24.1.10";

        private sealed class AudioStreamInfo
        {
            public int StreamIndex { get; set; }
            public int AudioTrackNumber { get; set; }
            public string Codec { get; set; }
            public string CodecLongName { get; set; }
            public string Profile { get; set; }
            public int Channels { get; set; }
            public string ChannelLayout { get; set; }
            public string Language { get; set; }
            public string Title { get; set; }
            public bool IsDolbyAtmos { get; set; }
            public string SurroundLayout { get; set; }
        }

        private sealed class VideoMetadataInfo
        {
            public static VideoMetadataInfo Empty => new VideoMetadataInfo
            {
                DynamicRange = "SDR",
                Badge = "SDR",
                PlaybackPath = "Waiting for video metadata."
            };

            public string FileName { get; set; }
            public string Codec { get; set; }
            public string CodecLongName { get; set; }
            public string Profile { get; set; }
            public bool IsAvc { get; set; }
            public bool IsHevc { get; set; }
            public string PixelFormat { get; set; }
            public int BitDepth { get; set; }
            public string ColorSpace { get; set; }
            public string ColorTransfer { get; set; }
            public string ColorPrimaries { get; set; }
            public string ChromaSubsampling { get; set; }
            public string DynamicRange { get; set; }
            public string Badge { get; set; }
            public bool IsHdr10 { get; set; }
            public bool IsHdr10Plus { get; set; }
            public bool IsDolbyVision { get; set; }
            public bool IsHlg { get; set; }
            public string DolbyVisionProfile { get; set; }
            public bool WindowsHdrEnabled { get; set; }
            public bool DisplayHdrSupported { get; set; }
            public string DisplayAdvancedColorKind { get; set; }
            public bool HevcExtensionInstalled { get; set; }
            public bool DolbyAccessInstalled { get; set; }
            public string NativeCodecPath { get; set; }
            public string PlaybackPath { get; set; }
            public string DetectionSource { get; set; }
            public string Notes { get; set; }
        }

        private sealed class SurroundModeOption
        {
            public string Mode { get; set; }
            public string Label { get; set; }

            public override string ToString() => Label;
        }

        public VideoPlayerPage()
        {
            InitializeComponent();

            InitializeSoundFlyout();
            _volumeUiReady = true;

            hideControlsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            hideControlsTimer.Tick += (_, _) =>
            {
                ControlPanel.Visibility = Visibility.Collapsed;
                hideControlsTimer.Stop();
            };
            hideControlsTimer.Start();

            _videoBadgeHideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _videoBadgeHideTimer.Tick += (_, _) =>
            {
                try
                {
                    VideoFormatBadge.Visibility = Visibility.Collapsed;
                    _videoBadgeHideTimer.Stop();
                }
                catch { }
            };

            _positionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _positionTimer.Tick += (_, _) => UpdateSeekUI();
            _positionTimer.Start();

            _nvidiaOverlaySuppressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            _nvidiaOverlaySuppressTimer.Tick += (_, _) => SuppressNvidiaOverlayWindows();

            _discordPresenceTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(250)
            };
            _discordPresenceTimer.Tick += (_, _) =>
            {
                try
                {
                    var session = mediaPlayerElement?.MediaPlayer?.PlaybackSession;
                    if (session == null)
                        return;

                    if (session.PlaybackState != MediaPlaybackState.Playing)
                        return;

                    if (!_discordClockReady)
                        SyncDiscordPlaybackClockFromSession(force: true);

                    var elapsed = GetDiscordLiveElapsed();
                    int currentSecond = (int)Math.Floor(elapsed.TotalSeconds);

                    if (currentSecond != _lastDiscordPushedSecond)
                    {
                        _lastDiscordPushedSecond = currentSecond;
                    }

                    var nowUtc = DateTime.UtcNow;
                    if ((nowUtc - _lastDiscordPresencePushUtc).TotalSeconds >= DiscordPresencePushIntervalSeconds)
                    {
                        RefreshDiscordVideoPresence(forcePlaying: true, forcePush: false);
                    }
                }
                catch { }
            };

            mediaPlayerElement.MediaPlayer.MediaOpened += MediaPlayer_MediaOpened;
            mediaPlayerElement.MediaPlayer.MediaFailed += MediaPlayer_MediaFailed;
            mediaPlayerElement.MediaPlayer.MediaEnded += MediaPlayer_MediaEnded;
            mediaPlayerElement.MediaPlayer.PlaybackSession.PlaybackStateChanged += PlaybackSession_PlaybackStateChanged;

            SeekSlider.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(SeekSlider_PointerPressed), true);
            SeekSlider.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(SeekSlider_PointerMoved), true);
            SeekSlider.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(SeekSlider_PointerReleased), true);
            SeekSlider.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(SeekSlider_PointerCaptureLost), true);

            try
            {
                if (mediaPlayerElement?.MediaPlayer != null)
                {
                    ApplySavedVolume();
                }
            }
            catch { }

            try
            {
                if (App.MainWindow != null)
                {
                    App.MainWindow.Activated += MainWindow_Activated;
                }
            }
            catch { }
        }

        private void InitializeSoundFlyout()
        {
            _preferredSurroundMode = LoadSavedSurroundMode();

            _flyoutVolumeText = new TextBlock
            {
                Text = "100%",
                HorizontalAlignment = HorizontalAlignment.Center
            };

            _flyoutVolumeSlider = new Slider
            {
                Minimum = 0,
                Maximum = 100,
                Value = 100,
                StepFrequency = 1,
                SmallChange = 1,
                LargeChange = 10,
                Width = 220
            };
            _flyoutVolumeSlider.ValueChanged += FlyoutVolumeSlider_ValueChanged;

            _flyoutMuteButton = new Button
            {
                Content = "Mute",
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            _flyoutMuteButton.Click += FlyoutMuteButton_Click;

            _surroundModeStatusText = new TextBlock
            {
                Text = "Surround mode: Auto",
                TextWrapping = TextWrapping.Wrap,
                HorizontalAlignment = HorizontalAlignment.Center
            };

            _surroundModeComboBox = new ComboBox
            {
                Header = "Surround",
                HorizontalAlignment = HorizontalAlignment.Stretch,
                ItemsSource = new List<SurroundModeOption>
                {
                    new SurroundModeOption { Mode = SurroundModeAuto, Label = "Auto / best available" },
                    new SurroundModeOption { Mode = SurroundModeAtmos, Label = "Dolby Atmos" },
                    new SurroundModeOption { Mode = SurroundMode21, Label = "Surround 2.1" },
                    new SurroundModeOption { Mode = SurroundMode51, Label = "Surround 5.1" },
                    new SurroundModeOption { Mode = SurroundMode71, Label = "Surround 7.1" },
                    new SurroundModeOption { Mode = SurroundMode72, Label = "Surround 7.2" },
                    new SurroundModeOption { Mode = SurroundMode512, Label = "Dolby Atmos 5.1.2" },
                    new SurroundModeOption { Mode = SurroundMode712, Label = "Dolby Atmos 7.1.2" },
                    new SurroundModeOption { Mode = SurroundMode714, Label = "Surround 7.1.4 / Atmos" },
                    new SurroundModeOption { Mode = SurroundMode914, Label = "Surround 9.1.4 / Atmos" },
                    new SurroundModeOption { Mode = SurroundMode916, Label = "Surround 9.1.6 / Atmos" },
                    new SurroundModeOption { Mode = SurroundMode24110, Label = "Dolby Atmos 24.1.10" }
                },
                SelectedValuePath = "Mode",
                SelectedValue = _preferredSurroundMode
            };
            _surroundModeComboBox.SelectionChanged += SurroundModeComboBox_SelectionChanged;

            var titleText = new TextBlock
            {
                Text = "Sound",
                HorizontalAlignment = HorizontalAlignment.Center
            };

            var panel = new StackPanel
            {
                Spacing = 10,
                Width = 240
            };

            panel.Children.Add(titleText);
            panel.Children.Add(_flyoutVolumeSlider);
            panel.Children.Add(_flyoutVolumeText);
            panel.Children.Add(_flyoutMuteButton);
            panel.Children.Add(_surroundModeComboBox);
            panel.Children.Add(_surroundModeStatusText);

            _soundFlyout = new Flyout
            {
                Content = panel,
                Placement = FlyoutPlacementMode.Top
            };

            if (SoundButton != null)
            {
                SoundButton.Flyout = _soundFlyout;
            }
        }

        private async void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
        {
            try
            {
                if (args.WindowActivationState == WindowActivationState.Deactivated)
                    return;

                if (!_waitingForCodecInstallReturn)
                    return;

                if (_isHandlingCodecReturnReload)
                    return;

                _isHandlingCodecReturnReload = true;

                try
                {
                    await AutoReloadVideoAfterCodecInstallAsync();
                }
                finally
                {
                    _isHandlingCodecReturnReload = false;
                }
            }
            catch { }
        }

        private void SaveDashboardResumeCard_Video()
        {
            try
            {
                var path = _currentFile?.Path ?? "";
                if (string.IsNullOrWhiteSpace(path)) return;

                WStorage.ApplicationData.Current.LocalSettings.Values[DASH_LastKind] = "video";
                WStorage.ApplicationData.Current.LocalSettings.Values[DASH_LastPath] = path;
                WStorage.ApplicationData.Current.LocalSettings.Values[DASH_LastTitle] = _currentFile?.Name ?? Path.GetFileName(path);
                WStorage.ApplicationData.Current.LocalSettings.Values[DASH_LastSubtitle] = "Video";
            }
            catch { }
        }

        private void ForceSaveResumePositionNow_Video()
        {
            try
            {
                if (_currentFile == null || string.IsNullOrWhiteSpace(_currentFile.Path)) return;

                var session = mediaPlayerElement?.MediaPlayer?.PlaybackSession;
                if (session == null) return;

                var dur = session.NaturalDuration;
                if (dur.TotalSeconds <= 0) return;

                var pos = session.Position.TotalSeconds;
                if (pos < 1) return;

                if ((dur.TotalSeconds - pos) < 2.0)
                    return;

                SavePositionSeconds(_currentFile.Path, pos);
            }
            catch { }
        }

        private double GetCurrentPlaybackPositionSeconds()
        {
            try
            {
                var session = mediaPlayerElement?.MediaPlayer?.PlaybackSession;
                if (session == null) return 0;

                var seconds = session.Position.TotalSeconds;
                if (seconds < 0) return 0;

                return seconds;
            }
            catch
            {
                return 0;
            }
        }

        private void ResetDiscordSecondPushTracking()
        {
            try
            {
                _lastDiscordPushedSecond = -1;
            }
            catch { }
        }

        private void ResetDiscordPlaybackClock()
        {
            try
            {
                _discordPlaybackStartUtc = DateTime.MinValue;
                _discordPlaybackDuration = TimeSpan.Zero;
                _discordClockReady = false;
                _lastDiscordPushedSecond = -1;
                _lastDiscordPresencePushUtc = DateTime.MinValue;
            }
            catch { }
        }

        private void SyncDiscordPlaybackClockFromSession(bool force = false)
        {
            try
            {
                var session = mediaPlayerElement?.MediaPlayer?.PlaybackSession;
                if (session == null)
                    return;

                var duration = session.NaturalDuration;
                if (duration.TotalSeconds <= 0)
                    return;

                var position = session.Position;
                if (position < TimeSpan.Zero)
                    position = TimeSpan.Zero;
                if (position > duration)
                    position = duration;

                if (force || !_discordClockReady)
                {
                    _discordPlaybackDuration = duration;
                    _discordPlaybackStartUtc = DateTime.UtcNow - position;
                    _discordClockReady = true;
                    return;
                }

                var calculatedElapsed = DateTime.UtcNow - _discordPlaybackStartUtc;
                var drift = Math.Abs((calculatedElapsed - position).TotalSeconds);

                if (drift >= 1.5)
                {
                    _discordPlaybackDuration = duration;
                    _discordPlaybackStartUtc = DateTime.UtcNow - position;
                }
            }
            catch { }
        }

        private TimeSpan GetDiscordLiveElapsed()
        {
            try
            {
                if (!_discordClockReady)
                    return TimeSpan.Zero;

                var elapsed = DateTime.UtcNow - _discordPlaybackStartUtc;

                if (elapsed < TimeSpan.Zero)
                    elapsed = TimeSpan.Zero;

                if (_discordPlaybackDuration > TimeSpan.Zero && elapsed > _discordPlaybackDuration)
                    elapsed = _discordPlaybackDuration;

                return elapsed;
            }
            catch
            {
                return TimeSpan.Zero;
            }
        }

        private void Page_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            ControlPanel.Visibility = Visibility.Visible;
            hideControlsTimer.Stop();
            hideControlsTimer.Start();
        }

        private void Play_Click(object s, RoutedEventArgs e)
        {
            _userPausedDiscordPresence = false;
            mediaPlayerElement.MediaPlayer.Play();
            TryPushNowPlaying(true);

            SyncDiscordPlaybackClockFromSession(force: true);
            ResetDiscordSecondPushTracking();

            try { _discordPresenceTimer?.Start(); } catch { }

            RefreshDiscordVideoPresence(forcePlaying: true, forcePush: true);
        }

        private void Pause_Click(object s, RoutedEventArgs e)
        {
            _userPausedDiscordPresence = true;
            mediaPlayerElement.MediaPlayer.Pause();
            TryPushNowPlaying(false);

            SyncDiscordPlaybackClockFromSession(force: true);

            try { _discordPresenceTimer?.Stop(); } catch { }

            RefreshDiscordPausedPresence(forcePush: true);
        }

        private void Rewind_Click(object s, RoutedEventArgs e)
        {
            var session = mediaPlayerElement.MediaPlayer.PlaybackSession;
            if (session.CanSeek)
            {
                _userPausedDiscordPresence = false;
                session.Position -= TimeSpan.FromSeconds(10);

                SyncDiscordPlaybackClockFromSession(force: true);
                ResetDiscordSecondPushTracking();
                RefreshDiscordVideoPresence(forcePlaying: true, forcePush: true);
            }
        }

        private void Forward_Click(object s, RoutedEventArgs e)
        {
            var session = mediaPlayerElement.MediaPlayer.PlaybackSession;
            if (session.CanSeek)
            {
                _userPausedDiscordPresence = false;
                session.Position += TimeSpan.FromSeconds(10);

                SyncDiscordPlaybackClockFromSession(force: true);
                ResetDiscordSecondPushTracking();
                RefreshDiscordVideoPresence(forcePlaying: true, forcePush: true);
            }
        }

        private void Browse_Click(object s, RoutedEventArgs e) => PickVideoFile();

        private async void PickVideoFile()
        {
            var picker = new WPickers.FileOpenPicker();
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWindow));
            picker.SuggestedStartLocation = WPickers.PickerLocationId.VideosLibrary;

            picker.FileTypeFilter.Add(".mp4");
            picker.FileTypeFilter.Add(".mkv");
            picker.FileTypeFilter.Add(".avi");

            var file = await picker.PickSingleFileAsync();
            if (file != null)
            {
                _forceStartFromBeginningOnNextLoad = true;
                await LoadAndPlayAsync(file);
            }
        }

        private async System.Threading.Tasks.Task LoadAndPlayAsync(WStorage.StorageFile file)
        {
            _currentFile = file;
            _codecPromptAlreadyShownForCurrentFile = false;
            _lastCodecPromptedPath = null;
            _videoSupportPromptAlreadyShownForCurrentFile = false;
            _lastVideoSupportPromptedPath = null;
            _userPausedDiscordPresence = false;
            _detectedAudioStreams = Array.Empty<AudioStreamInfo>();
            _selectedAudioStream = null;
            _audioInfoStatus = "Detecting audio format...";
            _detectedVideoMetadata = VideoMetadataInfo.Empty;
            _videoInfoStatus = "Detecting video metadata...";
            UpdateVideoMetadataUI(_detectedVideoMetadata, showBadge: false);
            ResetDiscordPlaybackClock();

            if (_suppressCodecPromptOnce)
            {
                _suppressCodecPromptOnce = false;
            }
            else
            {
                await PromptForMissingCodecIfNeededAsync(file);
            }

            await DetectAndPrepareAudioInfoAsync(file);
            await DetectAndPrepareVideoMetadataAsync(file);

            if (_forceStartFromBeginningOnNextLoad)
            {
                _pendingResumeSeconds = 0;
                try { SavePositionSeconds(file.Path, 0); } catch { }
                _forceStartFromBeginningOnNextLoad = false;
            }
            else
            {
                if (_pendingResumeSeconds <= 0)
                    _pendingResumeSeconds = GetSavedPositionSeconds(file.Path);
            }

            _mediaReadyForSeek = false;
            SeekSlider.IsEnabled = false;
            _ignoreSliderChange = true;
            SeekSlider.Minimum = 0;
            SeekSlider.Maximum = 1;
            SeekSlider.Value = 0;
            _ignoreSliderChange = false;
            CurrentTimeText.Text = "00:00";
            TotalTimeText.Text = "00:00";

            _currentPlaybackItem = await BuildPlaybackItemWithNativeSubtitlesAsync(_currentFile);
            mediaPlayerElement.Source = _currentPlaybackItem;
            LogVideoPlaybackPath("MediaPlaybackItem assigned to WinUI MediaPlayerElement / Windows Media Foundation path.");

            ApplyNativeSubtitleTrackState(_nativeSubtitlesEnabled);

            if (_nativeSubtitlesEnabled)
                SubtitleOverlay.Visibility = Visibility.Collapsed;

            try
            {
                ApplySavedVolume();
            }
            catch { }

            mediaPlayerElement.MediaPlayer.Play();

            SyncDiscordPlaybackClockFromSession(force: true);

            SaveDashboardResumeCard_Video();

            try
            {
                ActivityHub.Record(
                    ActivityHub.ActivityKind.Video,
                    title: file?.Name ?? "",
                    subtitle: "Video opened",
                    payload: file?.Path ?? "",
                    listenedSeconds: 0
                );
            }
            catch { }

            TryPushNowPlaying(true);

            try { _discordPresenceTimer?.Start(); } catch { }
            RefreshDiscordVideoPresence(forcePlaying: true, forcePush: true);
        }

        private async System.Threading.Tasks.Task PromptForMissingCodecIfNeededAsync(WStorage.StorageFile file)
        {
            try
            {
                if (file == null || XamlRoot == null)
                    return;

                var path = file.Path ?? "";
                if (string.IsNullOrWhiteSpace(path))
                    return;

                if (_codecPromptAlreadyShownForCurrentFile &&
                    string.Equals(_lastCodecPromptedPath, path, StringComparison.OrdinalIgnoreCase))
                    return;

                var savedState = GetSavedCodecState(path);

                if (string.Equals(savedState, CodecStateNotNeeded, StringComparison.OrdinalIgnoreCase))
                    return;

                if (string.Equals(savedState, CodecStateInstalledDdp, StringComparison.OrdinalIgnoreCase))
                {
                    if (await IsCodecExtensionInstalledAsync(DolbyDigitalPlusPrefix))
                        return;

                    ClearSavedCodecState(path);
                }

                if (string.Equals(savedState, CodecStateInstalledAc4, StringComparison.OrdinalIgnoreCase))
                {
                    if (await IsCodecExtensionInstalledAsync(DolbyAC4Prefix))
                        return;

                    ClearSavedCodecState(path);
                }

                if (string.Equals(savedState, CodecStatePendingDdp, StringComparison.OrdinalIgnoreCase))
                {
                    if (await IsCodecExtensionInstalledAsync(DolbyDigitalPlusPrefix))
                    {
                        SaveCodecState(path, CodecStateInstalledDdp);
                    }

                    return;
                }

                if (string.Equals(savedState, CodecStatePendingAc4, StringComparison.OrdinalIgnoreCase))
                {
                    if (await IsCodecExtensionInstalledAsync(DolbyAC4Prefix))
                    {
                        SaveCodecState(path, CodecStateInstalledAc4);
                    }

                    return;
                }

                var codec = await DetectPrimaryAudioCodecAsync(file);

                if (string.IsNullOrWhiteSpace(codec))
                {
                    SaveCodecState(path, CodecStateNotNeeded);
                    return;
                }

                codec = codec.Trim().ToLowerInvariant();

                string friendlyCodec = null;
                string installerPrefix = null;
                string installerName = null;
                string pendingState = null;
                string installedState = null;

                switch (codec)
                {
                    case "eac3":
                    case "ac3":
                        friendlyCodec = codec == "eac3"
                            ? "EAC3 / Dolby Digital Plus"
                            : "AC3 / Dolby Digital";
                        installerPrefix = DolbyDigitalPlusPrefix;
                        installerName = "Dolby Digital Plus Decoder";
                        pendingState = CodecStatePendingDdp;
                        installedState = CodecStateInstalledDdp;
                        break;

                    case "ac4":
                        friendlyCodec = "AC4 / Dolby AC-4";
                        installerPrefix = DolbyAC4Prefix;
                        installerName = "Dolby AC-4 Decoder";
                        pendingState = CodecStatePendingAc4;
                        installedState = CodecStateInstalledAc4;
                        break;

                    default:
                        SaveCodecState(path, CodecStateNotNeeded);
                        return;
                }

                if (await IsCodecExtensionInstalledAsync(installerPrefix))
                {
                    SaveCodecState(path, installedState);
                    return;
                }

                _codecPromptAlreadyShownForCurrentFile = true;
                _lastCodecPromptedPath = path;

                var dialog = new ContentDialog
                {
                    Title = "Missing audio codec support",
                    Content =
                        $"This video uses {friendlyCodec} audio.\n\n" +
                        "You can still play the video, but without installing the required extension you may have no sound.",
                    PrimaryButtonText = $"Install {installerName}",
                    CloseButtonText = "Not now",
                    DefaultButton = ContentDialogButton.Primary,
                    XamlRoot = XamlRoot
                };

                var result = await dialog.ShowAsync();

                SaveCodecState(path, pendingState);

                if (result == ContentDialogResult.Primary)
                {
                    await TryLaunchCodecInstallerByPrefixAsync(installerPrefix, installerName);
                }
            }
            catch { }
        }

        private async System.Threading.Tasks.Task<string> DetectPrimaryAudioCodecAsync(WStorage.StorageFile file)
        {
            await System.Threading.Tasks.Task.CompletedTask;
            return null;
        }

        private async System.Threading.Tasks.Task DetectAndPrepareAudioInfoAsync(WStorage.StorageFile file)
        {
            try
            {
                var streams = await DetectAudioStreamsAsync(file);
                _detectedAudioStreams = streams;
                _selectedAudioStream = GetPreferredAudioStream(streams, _preferredSurroundMode);

                if (_selectedAudioStream != null)
                {
                    _audioInfoStatus =
                        $"{GetSurroundModeSelectionPrefix(_preferredSurroundMode)} selected: {FormatAudioStreamSummary(_selectedAudioStream)}";
                }
                else
                {
                    _audioInfoStatus = "No Dolby Atmos, 2.1, 5.1, 7.1, 7.2, DTS, or newer surround audio track detected.";
                }

                UpdateSurroundModeStatusText();
            }
            catch
            {
                _detectedAudioStreams = Array.Empty<AudioStreamInfo>();
                _selectedAudioStream = null;
                _audioInfoStatus = "Audio information could not be detected for this film.";
                UpdateSurroundModeStatusText();
            }
        }

        private async System.Threading.Tasks.Task<IReadOnlyList<AudioStreamInfo>> DetectAudioStreamsAsync(WStorage.StorageFile file)
        {
            await System.Threading.Tasks.Task.CompletedTask;
            return Array.Empty<AudioStreamInfo>();
        }

        private async System.Threading.Tasks.Task DetectAndPrepareVideoMetadataAsync(WStorage.StorageFile file)
        {
            try
            {
                var metadata = await DetectVideoMetadataAsync(file);
                _detectedVideoMetadata = metadata ?? VideoMetadataInfo.Empty;
                await ApplyNativeVideoCodecCapabilityAsync(file, _detectedVideoMetadata);
                _videoInfoStatus = FormatVideoMetadataInfo(_detectedVideoMetadata);
                UpdateVideoMetadataUI(_detectedVideoMetadata);
                LogVideoPlaybackPath(_detectedVideoMetadata.PlaybackPath);
            }
            catch (Exception ex)
            {
                _detectedVideoMetadata = VideoMetadataInfo.Empty;
                _detectedVideoMetadata.FileName = file?.Name ?? "";
                _detectedVideoMetadata.Notes = "Video metadata detection failed: " + ex.Message;
                _videoInfoStatus = FormatVideoMetadataInfo(_detectedVideoMetadata);
                UpdateVideoMetadataUI(_detectedVideoMetadata);
                LogVideoPlaybackPath("Video metadata detection failed. Continuing with native Windows playback and SDR fallback if required.");
            }
        }

        private async System.Threading.Tasks.Task<VideoMetadataInfo> DetectVideoMetadataAsync(WStorage.StorageFile file)
        {
            var info = VideoMetadataInfo.Empty;
            info.FileName = file?.Name ?? "";
            info.DetectionSource = "Windows display APIs";

            ApplyDisplayHdrInfo(info);

            string ffprobePath = FindFfprobePath();
            if (file == null || string.IsNullOrWhiteSpace(file.Path))
            {
                info.Notes = "No local video file path was available for metadata probing.";
                ChoosePlaybackPath(info);
                return info;
            }

            if (string.IsNullOrWhiteSpace(ffprobePath) || !File.Exists(ffprobePath))
            {
                info.Notes = "ffprobe.exe was not found, so only Windows display HDR status is available.";
                ChoosePlaybackPath(info);
                return info;
            }

            string json = await RunFfprobeForVideoJsonAsync(ffprobePath, file.Path);
            if (string.IsNullOrWhiteSpace(json))
            {
                info.Notes = "ffprobe returned no metadata. Native playback will still be used.";
                ChoosePlaybackPath(info);
                return info;
            }

            info.DetectionSource = "ffprobe metadata + Windows display APIs";

            using var document = JsonDocument.Parse(json);
            if (document.RootElement.TryGetProperty("streams", out var streams) &&
                streams.ValueKind == JsonValueKind.Array)
            {
                foreach (var stream in streams.EnumerateArray())
                {
                    string type = TryGetJsonString(stream, "codec_type");
                    if (!string.Equals(type, "video", StringComparison.OrdinalIgnoreCase))
                        continue;

                    ApplyVideoStreamMetadata(info, stream);
                    break;
                }
            }

            ChooseVideoBadge(info);
            ChoosePlaybackPath(info);
            return info;
        }

        private static string FindFfprobePath()
        {
            try
            {
                var candidates = new[]
                {
                    Path.Combine(AppContext.BaseDirectory, "Tools", "ffprobe.exe"),
                    Path.Combine(AppContext.BaseDirectory, "ffprobe.exe"),
                    Path.Combine(Environment.CurrentDirectory, "Tools", "ffprobe.exe"),
                    Path.Combine(Environment.CurrentDirectory, "ffprobe.exe")
                };

                foreach (var candidate in candidates)
                {
                    if (File.Exists(candidate))
                        return candidate;
                }
            }
            catch { }

            return null;
        }

        private static async System.Threading.Tasks.Task<string> RunFfprobeForVideoJsonAsync(string ffprobePath, string videoPath)
        {
            try
            {
                using var process = new Process();
                process.StartInfo.FileName = ffprobePath;
                process.StartInfo.UseShellExecute = false;
                process.StartInfo.CreateNoWindow = true;
                process.StartInfo.RedirectStandardOutput = true;
                process.StartInfo.RedirectStandardError = true;
                process.StartInfo.ArgumentList.Add("-v");
                process.StartInfo.ArgumentList.Add("error");
                process.StartInfo.ArgumentList.Add("-print_format");
                process.StartInfo.ArgumentList.Add("json");
                process.StartInfo.ArgumentList.Add("-show_streams");
                process.StartInfo.ArgumentList.Add("-show_format");
                process.StartInfo.ArgumentList.Add(videoPath);

                if (!process.Start())
                    return null;

                var outputTask = process.StandardOutput.ReadToEndAsync();
                var errorTask = process.StandardError.ReadToEndAsync();

                await process.WaitForExitAsync();
                string output = await outputTask;
                string error = await errorTask;

                if (process.ExitCode != 0)
                {
                    DiagnosticLogService.WriteLine($"Video metadata probe failed: ffprobe exit {process.ExitCode}: {error}");
                    return null;
                }

                return output;
            }
            catch (Exception ex)
            {
                DiagnosticLogService.WriteLine("Video metadata probe failed: " + ex.Message);
                return null;
            }
        }

        private static void ApplyVideoStreamMetadata(VideoMetadataInfo info, JsonElement stream)
        {
            info.Codec = TryGetJsonString(stream, "codec_name");
            info.CodecLongName = TryGetJsonString(stream, "codec_long_name");
            info.Profile = TryGetJsonString(stream, "profile");
            info.PixelFormat = TryGetJsonString(stream, "pix_fmt");
            info.ColorSpace = TryGetJsonString(stream, "color_space");
            info.ColorTransfer = TryGetJsonString(stream, "color_transfer");
            info.ColorPrimaries = TryGetJsonString(stream, "color_primaries");
            info.BitDepth = DetectBitDepth(stream, info.PixelFormat, info.Profile);
            info.ChromaSubsampling = DetectChromaSubsampling(info.PixelFormat);

            string codec = (info.Codec ?? "").Trim().ToLowerInvariant();
            string codecLong = (info.CodecLongName ?? "").Trim().ToLowerInvariant();
            info.IsAvc = codec is "h264" or "avc" || codecLong.Contains("h.264") || codecLong.Contains("avc");
            info.IsHevc = codec is "hevc" or "h265" || codecLong.Contains("h.265") || codecLong.Contains("hevc");
            info.NativeCodecPath = info.IsHevc
                ? "H.265/HEVC through Windows Media Foundation"
                : info.IsAvc
                    ? "H.264/AVC through Windows Media Foundation"
                    : "Windows Media Foundation native playback";

            string combined = $"{info.Codec} {info.CodecLongName} {info.Profile} {info.PixelFormat} {info.ColorTransfer} {info.ColorPrimaries} {info.ColorSpace}".ToLowerInvariant();

            if (stream.TryGetProperty("side_data_list", out var sideDataList) &&
                sideDataList.ValueKind == JsonValueKind.Array)
            {
                foreach (var sideData in sideDataList.EnumerateArray())
                {
                    string sideType = TryGetJsonString(sideData, "side_data_type") ?? "";
                    string sideTypeLower = sideType.ToLowerInvariant();
                    combined += " " + sideTypeLower;

                    if (sideTypeLower.Contains("dovi") || sideTypeLower.Contains("dolby vision"))
                    {
                        info.IsDolbyVision = true;
                        int profile = TryGetJsonInt(sideData, "dv_profile");
                        if (profile > 0)
                            info.DolbyVisionProfile = profile.ToString();
                    }

                    if (sideTypeLower.Contains("smpte2094-40") || sideTypeLower.Contains("hdr10+"))
                        info.IsHdr10Plus = true;

                    if (sideTypeLower.Contains("mastering display") || sideTypeLower.Contains("content light"))
                        info.IsHdr10 = true;
                }
            }

            if (combined.Contains("dovi") || combined.Contains("dolby vision"))
                info.IsDolbyVision = true;

            if (string.IsNullOrWhiteSpace(info.DolbyVisionProfile))
                info.DolbyVisionProfile = DetectDolbyVisionProfileFromText(combined);

            if (combined.Contains("smpte2094-40") || combined.Contains("hdr10+"))
                info.IsHdr10Plus = true;

            if (string.Equals(info.ColorTransfer, "arib-std-b67", StringComparison.OrdinalIgnoreCase) ||
                combined.Contains("hlg"))
            {
                info.IsHlg = true;
            }

            if (string.Equals(info.ColorTransfer, "smpte2084", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(info.ColorTransfer, "pq", StringComparison.OrdinalIgnoreCase) ||
                combined.Contains("mastering display") ||
                combined.Contains("content light"))
            {
                info.IsHdr10 = true;
            }

            ChooseVideoBadge(info);
        }

        private static int DetectBitDepth(JsonElement stream, string pixelFormat, string profile)
        {
            int bits = TryGetJsonInt(stream, "bits_per_raw_sample");
            if (bits > 0)
                return bits;

            string text = $"{pixelFormat} {profile}".ToLowerInvariant();
            if (text.Contains("12"))
                return 12;
            if (text.Contains("10"))
                return 10;
            if (text.Contains("16"))
                return 16;

            return 8;
        }

        private static string DetectChromaSubsampling(string pixelFormat)
        {
            string pix = (pixelFormat ?? "").ToLowerInvariant();
            if (pix.Contains("yuv420") || pix.Contains("p010") || pix.Contains("nv12"))
                return "4:2:0";
            if (pix.Contains("yuv422"))
                return "4:2:2";
            if (pix.Contains("yuv444"))
                return "4:4:4";
            if (pix.Contains("rgb") || pix.Contains("gbr"))
                return "RGB";

            return string.IsNullOrWhiteSpace(pixelFormat) ? "Unknown" : "Available as " + pixelFormat;
        }

        private static string DetectDolbyVisionProfileFromText(string text)
        {
            try
            {
                var match = Regex.Match(text ?? "", @"(?:dv|dovi|dolby\s*vision)[^\d]{0,12}(?<profile>\d{1,2})", RegexOptions.IgnoreCase);
                if (match.Success)
                    return match.Groups["profile"].Value;
            }
            catch { }

            return null;
        }

        private static void ChooseVideoBadge(VideoMetadataInfo info)
        {
            if (info == null)
                return;

            if (info.IsDolbyVision)
                info.Badge = "Dolby Vision";
            else if (info.IsHdr10Plus)
                info.Badge = "HDR10+";
            else if (info.IsHlg)
                info.Badge = "HLG";
            else if (info.IsHdr10)
                info.Badge = "HDR10";
            else
                info.Badge = "SDR";

            info.DynamicRange = info.Badge;
        }

        private void ApplyDisplayHdrInfo(VideoMetadataInfo info)
        {
            try
            {
                var displayInfo = global::Windows.Graphics.Display.DisplayInformation.GetForCurrentView();
                var advanced = displayInfo.GetAdvancedColorInfo();
                info.DisplayAdvancedColorKind = advanced.CurrentAdvancedColorKind.ToString();
                info.WindowsHdrEnabled = advanced.CurrentAdvancedColorKind == global::Windows.Graphics.Display.AdvancedColorKind.HighDynamicRange;
                info.DisplayHdrSupported = advanced.IsAdvancedColorKindAvailable(global::Windows.Graphics.Display.AdvancedColorKind.HighDynamicRange);
            }
            catch (Exception ex)
            {
                info.DisplayAdvancedColorKind = "Unavailable";
                info.WindowsHdrEnabled = false;
                info.DisplayHdrSupported = false;
                info.Notes = "Windows HDR/display capability check unavailable: " + ex.Message;
            }
        }

        private static void ChoosePlaybackPath(VideoMetadataInfo info)
        {
            if (info == null)
                return;

            ChooseVideoBadge(info);

            if (!info.IsDolbyVision && !info.IsHdr10Plus && !info.IsHdr10 && !info.IsHlg)
            {
                info.PlaybackPath = $"SDR video detected. Using {GetNativeCodecPath(info)}.";
                return;
            }

            if (info.WindowsHdrEnabled && info.DisplayHdrSupported)
            {
                info.PlaybackPath = $"{info.Badge} video detected. Windows HDR is enabled and the display reports HDR support; using {GetNativeCodecPath(info)}.";
                return;
            }

            if (info.IsDolbyVision)
            {
                if (info.IsHdr10 || info.IsHdr10Plus || string.Equals(info.ColorTransfer, "smpte2084", StringComparison.OrdinalIgnoreCase))
                {
                    info.PlaybackPath = $"Dolby Vision detected, but native Dolby Vision HDR rendering is not confirmed. Using {GetNativeCodecPath(info)} and falling back to the HDR10/PQ base layer where Windows exposes it; otherwise Windows will tone-map or play SDR.";
                    return;
                }

                info.PlaybackPath = $"Dolby Vision detected, but HDR rendering is not available. Using {GetNativeCodecPath(info)} and allowing Windows to tone-map or play SDR.";
                return;
            }

            info.PlaybackPath = $"{info.Badge} video detected, but Windows HDR is disabled or the display does not report HDR support. Using {GetNativeCodecPath(info)} with Windows tone-mapping or SDR presentation.";
        }

        private static string GetNativeCodecPath(VideoMetadataInfo info)
        {
            if (info == null)
                return "native Windows Media Foundation playback";

            if (!string.IsNullOrWhiteSpace(info.NativeCodecPath))
                return info.NativeCodecPath;

            if (info.IsHevc)
                return "H.265/HEVC through Windows Media Foundation";

            if (info.IsAvc)
                return "H.264/AVC through Windows Media Foundation";

            return "native Windows Media Foundation playback";
        }

        private async System.Threading.Tasks.Task ApplyNativeVideoCodecCapabilityAsync(WStorage.StorageFile file, VideoMetadataInfo info)
        {
            try
            {
                if (info == null)
                    return;

                if (info.IsHevc)
                {
                    info.HevcExtensionInstalled = await IsAnyCodecExtensionInstalledAsync(
                        MicrosoftHevcVideoExtensionPrefix,
                        MicrosoftHevcVideoExtensionsPrefix,
                        MicrosoftHevcDeviceExtensionPrefix);
                }

                if (info.IsDolbyVision)
                {
                    info.DolbyAccessInstalled = await IsCodecExtensionInstalledAsync(DolbyAccessPrefix);
                }

                ChoosePlaybackPath(info);

                var notes = new List<string>();
                if (!string.IsNullOrWhiteSpace(info.Notes))
                    notes.Add(info.Notes);

                if (info.IsAvc)
                    notes.Add("H.264/AVC is enabled through Windows Media Foundation native playback.");

                if (info.IsHevc)
                {
                    notes.Add(info.HevcExtensionInstalled
                        ? "HEVC/H.265 support appears installed, so Zink will use Windows' native HEVC decoder."
                        : "HEVC/H.265 support was not detected. Dolby Vision and HEVC playback may require the Windows HEVC Video Extensions or OEM HEVC support.");
                }

                if (info.IsDolbyVision)
                {
                    notes.Add(info.DolbyAccessInstalled
                        ? "Dolby Access appears installed. Dolby Vision still requires a Dolby Vision capable display, GPU driver, and Windows HDR path."
                        : "Dolby Vision metadata was detected. Zink enables the native Windows path, but full Dolby Vision rendering depends on Dolby/OEM Windows support.");
                }

                info.Notes = string.Join("\n", notes);
                LogVideoPlaybackPath($"{GetNativeCodecPath(info)} selected. HEVC extension installed: {info.HevcExtensionInstalled}. Dolby Access installed: {info.DolbyAccessInstalled}. Dolby Vision detected: {info.IsDolbyVision}.");

                await PromptForNativeVideoSupportIfNeededAsync(file, info);
            }
            catch (Exception ex)
            {
                DiagnosticLogService.WriteLine("Video native codec capability check failed: " + ex.Message);
            }
        }

        private async System.Threading.Tasks.Task PromptForNativeVideoSupportIfNeededAsync(WStorage.StorageFile file, VideoMetadataInfo info)
        {
            try
            {
                if (file == null || info == null || XamlRoot == null)
                    return;

                if (!info.IsHevc && !info.IsDolbyVision)
                    return;

                if (info.IsHevc && info.HevcExtensionInstalled && (!info.IsDolbyVision || info.DolbyAccessInstalled))
                    return;

                var path = file.Path ?? "";
                if (string.IsNullOrWhiteSpace(path))
                    return;

                if (_videoSupportPromptAlreadyShownForCurrentFile &&
                    string.Equals(_lastVideoSupportPromptedPath, path, StringComparison.OrdinalIgnoreCase))
                    return;

                _videoSupportPromptAlreadyShownForCurrentFile = true;
                _lastVideoSupportPromptedPath = path;

                string title = info.IsDolbyVision
                    ? "Enable Dolby Vision playback"
                    : "Enable HEVC playback";

                string content = info.IsDolbyVision
                    ? "This film has Dolby Vision metadata. Zink will use Windows' native H.265/HEVC Media Foundation path, but Dolby Vision rendering also needs Windows HEVC support, Dolby/OEM support, Windows HDR, and a Dolby Vision capable display.\n\nOpen Microsoft Store to install or enable the missing Windows video support?"
                    : "This film uses H.265/HEVC. Zink will use Windows' native Media Foundation path, but this PC may need the Windows HEVC Video Extensions or OEM HEVC support.\n\nOpen Microsoft Store to install HEVC support?";

                var dialog = new ContentDialog
                {
                    Title = title,
                    Content = content,
                    PrimaryButtonText = info.IsDolbyVision ? "Open Dolby/HEVC support" : "Open HEVC support",
                    CloseButtonText = "Not now",
                    DefaultButton = ContentDialogButton.Primary,
                    XamlRoot = XamlRoot
                };

                var result = await dialog.ShowAsync();
                if (result != ContentDialogResult.Primary)
                    return;

                string query = info.IsDolbyVision
                    ? "Dolby Access HEVC Video Extensions"
                    : "HEVC Video Extensions";

                _waitingForCodecInstallReturn = true;
                _pendingReloadVideoPath = _currentFile?.Path;
                _pendingReloadResumeSeconds = 0;

                await WSystem.Launcher.LaunchUriAsync(new Uri("ms-windows-store://search/?query=" + Uri.EscapeDataString(query)));
            }
            catch (Exception ex)
            {
                DiagnosticLogService.WriteLine("Video native codec support prompt failed: " + ex.Message);
            }
        }

        private async System.Threading.Tasks.Task<bool> IsAnyCodecExtensionInstalledAsync(params string[] packagePrefixes)
        {
            try
            {
                if (packagePrefixes == null || packagePrefixes.Length == 0)
                    return false;

                foreach (var prefix in packagePrefixes)
                {
                    if (await IsCodecExtensionInstalledAsync(prefix))
                        return true;
                }
            }
            catch { }

            return false;
        }

        private void UpdateVideoMetadataUI(VideoMetadataInfo info, bool showBadge = true)
        {
            try
            {
                if (info == null)
                    info = VideoMetadataInfo.Empty;

                if (VideoFormatBadgeText != null)
                    VideoFormatBadgeText.Text = string.IsNullOrWhiteSpace(info.Badge) ? "SDR" : info.Badge;

                if (VideoFormatBadge != null && showBadge)
                {
                    VideoFormatBadge.Visibility = Visibility.Visible;
                    _videoBadgeHideTimer?.Stop();
                    _videoBadgeHideTimer?.Start();
                }

                if (VideoInfoTextBlock != null)
                    VideoInfoTextBlock.Text = FormatVideoMetadataInfo(info);
            }
            catch { }
        }

        private static string FormatVideoMetadataInfo(VideoMetadataInfo info)
        {
            if (info == null)
                info = VideoMetadataInfo.Empty;

            var builder = new StringBuilder();
            AppendInfoLine(builder, "File", info.FileName);
            AppendInfoLine(builder, "Badge", info.Badge);
            AppendInfoLine(builder, "Dynamic range", info.DynamicRange);
            AppendInfoLine(builder, "Codec", FormatVideoCodec(info));
            AppendInfoLine(builder, "H.264/AVC", info.IsAvc ? "Yes" : "No");
            AppendInfoLine(builder, "HEVC/H.265", info.IsHevc ? "Yes" : "No");
            AppendInfoLine(builder, "Native codec path", GetNativeCodecPath(info));
            AppendInfoLine(builder, "Bit depth", info.BitDepth > 0 ? info.BitDepth + "-bit" : "Unknown");
            AppendInfoLine(builder, "Colour space", info.ColorSpace);
            AppendInfoLine(builder, "Colour transfer", info.ColorTransfer);
            AppendInfoLine(builder, "Colour primaries", info.ColorPrimaries);
            AppendInfoLine(builder, "Chroma subsampling", info.ChromaSubsampling);
            AppendInfoLine(builder, "Dolby Vision", info.IsDolbyVision ? "Yes" : "No");
            AppendInfoLine(builder, "Dolby Vision profile", info.DolbyVisionProfile);
            AppendInfoLine(builder, "HDR10", info.IsHdr10 ? "Yes" : "No");
            AppendInfoLine(builder, "HDR10+", info.IsHdr10Plus ? "Yes" : "No");
            AppendInfoLine(builder, "HLG", info.IsHlg ? "Yes" : "No");
            AppendInfoLine(builder, "Windows HDR enabled", info.WindowsHdrEnabled ? "Yes" : "No");
            AppendInfoLine(builder, "Display HDR support", info.DisplayHdrSupported ? "Yes" : "No");
            AppendInfoLine(builder, "Display advanced colour", info.DisplayAdvancedColorKind);
            AppendInfoLine(builder, "HEVC extension installed", info.HevcExtensionInstalled ? "Yes" : "No");
            AppendInfoLine(builder, "Dolby Access installed", info.DolbyAccessInstalled ? "Yes" : "No");
            AppendInfoLine(builder, "Playback path", info.PlaybackPath);
            AppendInfoLine(builder, "Detection source", info.DetectionSource);
            AppendInfoLine(builder, "Notes", info.Notes);
            return builder.ToString().Trim();
        }

        private static string FormatVideoCodec(VideoMetadataInfo info)
        {
            string codec = string.IsNullOrWhiteSpace(info.Codec) ? "Unknown" : info.Codec.ToUpperInvariant();
            string profile = string.IsNullOrWhiteSpace(info.Profile) ? "" : " / " + info.Profile;
            string longName = string.IsNullOrWhiteSpace(info.CodecLongName) ? "" : " (" + info.CodecLongName + ")";
            return codec + profile + longName;
        }

        private static void AppendInfoLine(StringBuilder builder, string label, string value)
        {
            if (builder == null)
                return;

            builder.Append(label);
            builder.Append(": ");
            builder.AppendLine(string.IsNullOrWhiteSpace(value) ? "Unknown" : value);
        }

        private void LogVideoPlaybackPath(string message)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(message))
                    return;

                DiagnosticLogService.WriteLine("Video Player HDR path: " + message);
            }
            catch { }
        }

        private static AudioStreamInfo GetPreferredAudioStream(IReadOnlyList<AudioStreamInfo> streams, string preferredMode)
        {
            try
            {
                if (streams == null || streams.Count == 0)
                    return null;

                string mode = NormalizeSurroundMode(preferredMode);

                if (!string.Equals(mode, SurroundModeAuto, StringComparison.OrdinalIgnoreCase))
                {
                    AudioStreamInfo preferred = null;
                    int preferredScore = int.MinValue;

                    foreach (var stream in streams)
                    {
                        if (!StreamMatchesSurroundMode(stream, mode))
                            continue;

                        int score = GetAudioStreamScore(stream);
                        if (score > preferredScore)
                        {
                            preferred = stream;
                            preferredScore = score;
                        }
                    }

                    if (preferred != null)
                        return preferred;
                }

                return GetBestAudioStream(streams);
            }
            catch
            {
                return GetBestAudioStream(streams);
            }
        }

        private static string TryGetJsonString(JsonElement element, string propertyName)
        {
            try
            {
                if (element.TryGetProperty(propertyName, out var value) &&
                    value.ValueKind == JsonValueKind.String)
                {
                    return value.GetString();
                }
            }
            catch { }

            return null;
        }

        private static int TryGetJsonInt(JsonElement element, string propertyName)
        {
            try
            {
                if (element.TryGetProperty(propertyName, out var value))
                {
                    if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number))
                        return number;

                    if (value.ValueKind == JsonValueKind.String &&
                        int.TryParse(value.GetString(), out number))
                    {
                        return number;
                    }
                }
            }
            catch { }

            return 0;
        }

        private static AudioStreamInfo GetBestAudioStream(IReadOnlyList<AudioStreamInfo> streams)
        {
            try
            {
                AudioStreamInfo best = null;
                int bestScore = int.MinValue;

                if (streams == null)
                    return null;

                foreach (var stream in streams)
                {
                    int score = GetAudioStreamScore(stream);
                    if (score > bestScore)
                    {
                        best = stream;
                        bestScore = score;
                    }
                }

                return best;
            }
            catch
            {
                return null;
            }
        }

        private static int GetAudioStreamScore(AudioStreamInfo stream)
        {
            if (stream == null)
                return int.MinValue;

            int score = Math.Max(0, stream.Channels) * 100;
            string codec = (stream.Codec ?? "").Trim().ToLowerInvariant();
            string profile = (stream.Profile ?? "").Trim().ToLowerInvariant();
            string layout = (stream.ChannelLayout ?? "").Trim().ToLowerInvariant();
            string title = (stream.Title ?? "").Trim().ToLowerInvariant();

            if (codec == "truehd")
                score += 90;
            else if (codec == "eac3" || codec == "ac4")
                score += 70;
            else if (codec == "ac3")
                score += 60;
            else if (codec == "dts" || codec == "dca")
                score += 65;

            if (stream.IsDolbyAtmos)
                score += 140;

            if (string.Equals(stream.SurroundLayout, SurroundMode24110, StringComparison.OrdinalIgnoreCase))
                score += 100;
            else if (string.Equals(stream.SurroundLayout, SurroundMode916, StringComparison.OrdinalIgnoreCase))
                score += 70;
            else if (string.Equals(stream.SurroundLayout, SurroundMode914, StringComparison.OrdinalIgnoreCase))
                score += 65;
            else if (string.Equals(stream.SurroundLayout, SurroundMode714, StringComparison.OrdinalIgnoreCase))
                score += 60;
            else if (string.Equals(stream.SurroundLayout, SurroundMode712, StringComparison.OrdinalIgnoreCase))
                score += 55;
            else if (string.Equals(stream.SurroundLayout, SurroundMode512, StringComparison.OrdinalIgnoreCase))
                score += 52;
            else if (string.Equals(stream.SurroundLayout, SurroundMode72, StringComparison.OrdinalIgnoreCase))
                score += 50;

            if (profile.Contains("ma") || profile.Contains("hd") ||
                layout.Contains("7.1") || title.Contains("7.1"))
            {
                score += 20;
            }

            return score;
        }

        private static string FormatAudioStreamSummary(AudioStreamInfo stream)
        {
            if (stream == null)
                return "Unknown audio";

            string channelText = FormatChannelText(stream.Channels, stream.ChannelLayout, stream.SurroundLayout);
            string codecText = FormatCodecText(stream.Codec, stream.Profile);
            string atmosText = stream.IsDolbyAtmos ? " Dolby Atmos" : "";
            string languageText = string.IsNullOrWhiteSpace(stream.Language)
                ? ""
                : $" - {stream.Language.ToUpperInvariant()}";
            string titleText = string.IsNullOrWhiteSpace(stream.Title)
                ? ""
                : $" - {stream.Title}";

            return $"{channelText} {codecText}{atmosText}{languageText}{titleText}".Trim();
        }

        private static string FormatChannelText(int channels, string layout, string detectedLayout = null)
        {
            if (!string.IsNullOrWhiteSpace(detectedLayout))
                return detectedLayout;

            var layoutText = layout ?? "";

            if (ContainsLayoutToken(layoutText, SurroundMode24110))
                return SurroundMode24110;

            if (ContainsLayoutToken(layoutText, SurroundMode916))
                return "9.1.6";

            if (ContainsLayoutToken(layoutText, SurroundMode914))
                return "9.1.4";

            if (ContainsLayoutToken(layoutText, SurroundMode714))
                return "7.1.4";

            if (ContainsLayoutToken(layoutText, SurroundMode712))
                return "7.1.2";

            if (ContainsLayoutToken(layoutText, SurroundMode512))
                return "5.1.2";

            if (ContainsLayoutToken(layoutText, SurroundMode72))
                return "7.2";

            if (channels >= 8 || ContainsLayoutToken(layoutText, SurroundMode71))
                return "7.1";

            if (channels >= 6 || ContainsLayoutToken(layoutText, SurroundMode51))
                return "5.1";

            if (channels == 3 || ContainsLayoutToken(layoutText, SurroundMode21))
                return "2.1";

            if (channels == 2)
                return "Stereo";

            if (channels == 1)
                return "Mono";

            if (channels > 0)
                return $"{channels} channel";

            return "Unknown channels";
        }

        private static string FormatCodecText(string codec, string profile)
        {
            string normalized = (codec ?? "").Trim().ToLowerInvariant();
            string profileText = string.IsNullOrWhiteSpace(profile) ? "" : $" {profile}";

            return normalized switch
            {
                "truehd" => "Dolby TrueHD",
                "eac3" => "Dolby Digital Plus",
                "ac3" => "Dolby Digital",
                "ac4" => "Dolby AC-4",
                "dts" => $"DTS{profileText}",
                "dca" => $"DTS{profileText}",
                "aac" => "AAC",
                "opus" => "Opus",
                "mp3" => "MP3",
                _ => string.IsNullOrWhiteSpace(codec) ? "Unknown codec" : codec.ToUpperInvariant()
            };
        }

        private static string DetectSurroundLayout(int channels, string layout, string codecLongName, string profile, string title)
        {
            string combined = $"{layout} {codecLongName} {profile} {title}".ToLowerInvariant();

            if (ContainsLayoutToken(combined, SurroundMode24110) || channels >= 35)
                return SurroundMode24110;

            if (ContainsLayoutToken(combined, SurroundMode916))
                return SurroundMode916;

            if (ContainsLayoutToken(combined, SurroundMode914))
                return SurroundMode914;

            if (ContainsLayoutToken(combined, SurroundMode714))
                return SurroundMode714;

            if (ContainsLayoutToken(combined, SurroundMode712))
                return SurroundMode712;

            if (ContainsLayoutToken(combined, SurroundMode512))
                return SurroundMode512;

            if (ContainsLayoutToken(combined, SurroundMode72))
                return SurroundMode72;

            if (ContainsLayoutToken(combined, SurroundMode71) || channels == 8)
                return SurroundMode71;

            if (ContainsLayoutToken(combined, SurroundMode51) || channels == 6)
                return SurroundMode51;

            if (ContainsLayoutToken(combined, SurroundMode21) || channels == 3)
                return SurroundMode21;

            if (channels >= 16)
                return SurroundMode916;

            if (channels >= 14)
                return SurroundMode914;

            if (channels >= 12)
                return SurroundMode714;

            if (channels >= 10)
                return SurroundMode712;

            if (channels >= 9)
                return SurroundMode72;

            return null;
        }

        private static bool IsDolbyAtmosStream(string codec, string codecLongName, string profile, string layout, string title)
        {
            string normalizedCodec = (codec ?? "").Trim().ToLowerInvariant();
            string combined = $"{codecLongName} {profile} {layout} {title}".ToLowerInvariant();

            if (combined.Contains("atmos") ||
                combined.Contains("joc") ||
                combined.Contains("joint object coding") ||
                combined.Contains("truehd atmos") ||
                combined.Contains("object") ||
                combined.Contains("immersive"))
            {
                return normalizedCodec is "truehd" or "eac3" or "ac4" or "mlp";
            }

            return false;
        }

        private static bool StreamMatchesSurroundMode(AudioStreamInfo stream, string mode)
        {
            if (stream == null)
                return false;

            mode = NormalizeSurroundMode(mode);

            if (string.Equals(mode, SurroundModeAtmos, StringComparison.OrdinalIgnoreCase))
                return stream.IsDolbyAtmos;

            if (string.Equals(stream.SurroundLayout, mode, StringComparison.OrdinalIgnoreCase))
                return true;

            string summary = $"{stream.ChannelLayout} {stream.CodecLongName} {stream.Profile} {stream.Title}".ToLowerInvariant();
            return ContainsLayoutToken(summary, mode);
        }

        private static bool ContainsLayoutToken(string text, string layout)
        {
            if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(layout))
                return false;

            string escapedLayout = Regex.Escape(layout).Replace("\\.", "[._]");
            return Regex.IsMatch(
                text,
                $@"(?<!\d){escapedLayout}(?!(?:[._]\d)|\d)",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        private static string NormalizeSurroundMode(string mode)
        {
            if (string.IsNullOrWhiteSpace(mode))
                return SurroundModeAuto;

            mode = mode.Trim().ToLowerInvariant();

            return mode switch
            {
                SurroundModeAtmos => SurroundModeAtmos,
                SurroundMode21 => SurroundMode21,
                SurroundMode51 => SurroundMode51,
                SurroundMode71 => SurroundMode71,
                SurroundMode72 => SurroundMode72,
                SurroundMode512 => SurroundMode512,
                SurroundMode712 => SurroundMode712,
                SurroundMode714 => SurroundMode714,
                SurroundMode914 => SurroundMode914,
                SurroundMode916 => SurroundMode916,
                SurroundMode24110 => SurroundMode24110,
                _ => SurroundModeAuto
            };
        }

        private static string GetSurroundModeSelectionPrefix(string mode)
        {
            mode = NormalizeSurroundMode(mode);

            return mode switch
            {
                SurroundModeAtmos => "Dolby Atmos",
                SurroundMode21 => "Surround 2.1",
                SurroundMode51 => "Surround 5.1",
                SurroundMode71 => "Surround 7.1",
                SurroundMode72 => "Surround 7.2",
                SurroundMode512 => "Dolby Atmos 5.1.2",
                SurroundMode712 => "Dolby Atmos 7.1.2",
                SurroundMode714 => "Surround 7.1.4",
                SurroundMode914 => "Surround 9.1.4",
                SurroundMode916 => "Surround 9.1.6",
                SurroundMode24110 => "Dolby Atmos 24.1.10",
                _ => "Auto"
            };
        }

        private async System.Threading.Tasks.Task TryLaunchCodecInstallerByPrefixAsync(string filePrefix, string friendlyName)
        {
            try
            {
                var installFolder = await WAppModel.Package.Current.InstalledLocation.GetFolderAsync(CodecInstallerFolderName);
                var files = await installFolder.GetFilesAsync();

                WStorage.StorageFile found = null;

                foreach (var file in files)
                {
                    if (file.Name.StartsWith(filePrefix, StringComparison.OrdinalIgnoreCase) &&
                        file.FileType.Equals(".AppxBundle", StringComparison.OrdinalIgnoreCase))
                    {
                        found = file;
                        break;
                    }
                }

                if (found != null)
                {
                    _waitingForCodecInstallReturn = true;
                    _pendingReloadVideoPath = _currentFile?.Path;
                    _pendingReloadResumeSeconds = 0;

                    await WSystem.Launcher.LaunchFileAsync(found);
                    return;
                }
            }
            catch { }

            try
            {
                if (XamlRoot == null)
                    return;

                var notFoundDialog = new ContentDialog
                {
                    Title = "Installer not found",
                    Content =
                        $"{friendlyName} was not found inside the app package.\n\n" +
                        $"Place the .AppxBundle file inside a folder named '{CodecInstallerFolderName}' in the app package, then try again.",
                    CloseButtonText = "OK",
                    XamlRoot = XamlRoot
                };

                await notFoundDialog.ShowAsync();
            }
            catch { }
        }

        private async System.Threading.Tasks.Task AutoReloadVideoAfterCodecInstallAsync()
        {
            try
            {
                if (!_waitingForCodecInstallReturn)
                    return;

                if (string.IsNullOrWhiteSpace(_pendingReloadVideoPath))
                {
                    _waitingForCodecInstallReturn = false;
                    return;
                }

                await System.Threading.Tasks.Task.Delay(1200);

                string path = _pendingReloadVideoPath;

                _waitingForCodecInstallReturn = false;
                _pendingReloadVideoPath = null;
                _pendingReloadResumeSeconds = 0;

                var file = await WStorage.StorageFile.GetFileFromPathAsync(path);

                var savedState = GetSavedCodecState(path);
                if (string.Equals(savedState, CodecStatePendingDdp, StringComparison.OrdinalIgnoreCase))
                {
                    if (await IsCodecExtensionInstalledAsync(DolbyDigitalPlusPrefix))
                        SaveCodecState(path, CodecStateInstalledDdp);
                }
                else if (string.Equals(savedState, CodecStatePendingAc4, StringComparison.OrdinalIgnoreCase))
                {
                    if (await IsCodecExtensionInstalledAsync(DolbyAC4Prefix))
                        SaveCodecState(path, CodecStateInstalledAc4);
                }

                _suppressCodecPromptOnce = true;
                _pendingResumeSeconds = 0;

                try { SavePositionSeconds(path, 0); } catch { }

                await LoadAndPlayAsync(file);
            }
            catch
            {
                _waitingForCodecInstallReturn = false;
            }
        }

        private void TryPushNowPlaying(bool isPlaying)
        {
            try
            {
                var name = _currentFile?.Name ?? "";
                if (string.IsNullOrWhiteSpace(name)) name = "Video";

                AppPlaybackService.Instance.SetGenericNowPlaying(
                    AppPlaybackService.MediaKind.Video,
                    primary: name,
                    secondary: "Video",
                    artworkUri: null,
                    isPlaying: isPlaying
                );
            }
            catch { }
        }

        private string GetDiscordVideoTitle()
        {
            try
            {
                if (_currentFile == null)
                    return "Video";

                var title = Path.GetFileNameWithoutExtension(_currentFile.Name);
                if (string.IsNullOrWhiteSpace(title))
                    title = _currentFile.Name;

                return string.IsNullOrWhiteSpace(title) ? "Video" : title;
            }
            catch
            {
                return "Video";
            }
        }

        private void RefreshDiscordVideoPresence(bool forcePlaying = false, bool forcePush = false)
        {
            try
            {
                if (_suppressDiscordPresenceRefresh)
                    return;

                var player = mediaPlayerElement?.MediaPlayer;
                var session = player?.PlaybackSession;
                if (session == null)
                    return;

                var title = GetDiscordVideoTitle();
                var state = session.PlaybackState;

                if (_discordPlaybackDuration.TotalSeconds <= 0)
                {
                    var sessionDuration = session.NaturalDuration;
                    if (sessionDuration.TotalSeconds > 0)
                        _discordPlaybackDuration = sessionDuration;
                }

                if (_discordPlaybackDuration.TotalSeconds <= 0)
                    return;

                if (!(forcePlaying ||
                    state == MediaPlaybackState.Playing ||
                    state == MediaPlaybackState.Opening ||
                    state == MediaPlaybackState.Buffering))
                    return;

                if (!_discordClockReady)
                    SyncDiscordPlaybackClockFromSession(force: true);

                var nowUtc = DateTime.UtcNow;
                if (!forcePush && (nowUtc - _lastDiscordPresencePushUtc).TotalSeconds < DiscordPresencePushIntervalSeconds)
                    return;

                var position = GetDiscordLiveElapsed();

                DiscordPresenceService.Instance.SetVideoPresence(
                    title,
                    position,
                    _discordPlaybackDuration,
                    "zink_1024",
                    title);

                _lastDiscordPresencePushUtc = nowUtc;
            }
            catch { }
        }

        private void RefreshDiscordPausedPresence(bool forcePush = false)
        {
            try
            {
                var player = mediaPlayerElement?.MediaPlayer;
                var session = player?.PlaybackSession;
                if (session == null)
                    return;

                var duration = session.NaturalDuration;
                if (duration.TotalSeconds > 0)
                    _discordPlaybackDuration = duration;

                if (_discordPlaybackDuration.TotalSeconds <= 0)
                    return;

                var nowUtc = DateTime.UtcNow;
                if (!forcePush && (nowUtc - _lastDiscordPresencePushUtc).TotalSeconds < 1.0)
                    return;

                var position = session.Position;
                if (position < TimeSpan.Zero)
                    position = TimeSpan.Zero;
                if (position > _discordPlaybackDuration)
                    position = _discordPlaybackDuration;

                var title = GetDiscordVideoTitle();

                DiscordPresenceService.Instance.SetVideoPausedPresence(
                    title,
                    position,
                    _discordPlaybackDuration,
                    "zink_1024",
                    title);

                _lastDiscordPresencePushUtc = nowUtc;
            }
            catch { }
        }

        private void ClearDiscordVideoPresence()
        {
            try
            {
                DiscordPresenceService.Instance.Clear();
            }
            catch { }
        }

        private async System.Threading.Tasks.Task<MediaPlaybackItem> BuildPlaybackItemWithNativeSubtitlesAsync(WStorage.StorageFile videoFile)
        {
            var mediaSource = MediaSource.CreateFromStorageFile(videoFile);

            try
            {
                var sidecar = await FindSidecarSubtitleAsync(videoFile);
                if (sidecar != null)
                {
                    var uri = new Uri(sidecar.Path);
                    var tts = TimedTextSource.CreateFromUri(uri);
                    mediaSource.ExternalTimedTextSources.Add(tts);
                }
            }
            catch { }

            var item = new MediaPlaybackItem(mediaSource);

            item.TimedMetadataTracksChanged += (_, __) =>
            {
                try { ApplyNativeSubtitleTrackState(_nativeSubtitlesEnabled); } catch { }
            };

            item.AudioTracksChanged += (_, __) =>
            {
                try
                {
                    DispatcherQueue.TryEnqueue(() => TryAutoSelectBestAudioTrack());
                }
                catch { }
            };

            return item;
        }

        private void TryAutoSelectBestAudioTrack()
        {
            try
            {
                var item = _currentPlaybackItem;
                var selected = _selectedAudioStream;

                if (item == null || selected == null)
                    return;

                var tracks = item.AudioTracks;
                if (tracks == null || tracks.Count == 0)
                    return;

                int trackNumber = Math.Max(0, selected.AudioTrackNumber);
                if (trackNumber < tracks.Count)
                {
                    tracks.SelectedIndex = trackNumber;
                    _audioInfoStatus =
                        $"{GetSurroundModeSelectionPrefix(_preferredSurroundMode)} selected: {FormatAudioStreamSummary(selected)}";
                    UpdateSurroundModeStatusText();
                }
            }
            catch { }
        }

        private async System.Threading.Tasks.Task<WStorage.StorageFile> FindSidecarSubtitleAsync(WStorage.StorageFile videoFile)
        {
            try
            {
                var folder = await videoFile.GetParentAsync();
                if (folder == null) return null;

                var baseName = Path.GetFileNameWithoutExtension(videoFile.Name);
                var exts = new[] { ".srt", ".vtt", ".ttml", ".dfxp" };

                foreach (var ext in exts)
                {
                    var candidateName = baseName + ext;
                    var item = await folder.TryGetItemAsync(candidateName);
                    if (item is WStorage.StorageFile sf) return sf;
                }

                return null;
            }
            catch
            {
                return null;
            }
        }

        private void ApplyNativeSubtitleTrackState(bool enabled)
        {
            try
            {
                var item = _currentPlaybackItem;
                if (item == null) return;

                var tracks = item.TimedMetadataTracks;
                if (tracks == null || tracks.Count == 0) return;

                for (uint i = 0; i < tracks.Count; i++)
                {
                    tracks.SetPresentationMode(
                        i,
                        enabled
                            ? TimedMetadataTrackPresentationMode.PlatformPresented
                            : TimedMetadataTrackPresentationMode.Disabled);
                }
            }
            catch { }
        }

        private async void SubtitlesButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (XamlRoot == null)
                    return;

                var dialog = new ContentDialog
                {
                    Title = "Subtitles",
                    Content = "Would you like to turn on subtitles for this video?",
                    PrimaryButtonText = "Enable subtitles",
                    CloseButtonText = "Don't enable",
                    DefaultButton = ContentDialogButton.Primary,
                    XamlRoot = XamlRoot
                };

                var result = await dialog.ShowAsync();

                if (result == ContentDialogResult.Primary)
                {
                    _nativeSubtitlesEnabled = true;
                    ApplyNativeSubtitleTrackState(true);
                    SubtitleOverlay.Visibility = Visibility.Collapsed;
                }
                else
                {
                    _nativeSubtitlesEnabled = false;
                    ApplyNativeSubtitleTrackState(false);
                }
            }
            catch { }
        }

        private async void AudioInfoButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (XamlRoot == null)
                    return;

                var content = new StackPanel
                {
                    Spacing = 8,
                    Width = 420
                };

                content.Children.Add(new TextBlock
                {
                    Text = _audioInfoStatus,
                    TextWrapping = TextWrapping.Wrap
                });

                content.Children.Add(new TextBlock
                {
                    Text = "Video",
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Margin = new Thickness(0, 10, 0, 0)
                });

                content.Children.Add(new TextBlock
                {
                    Text = _videoInfoStatus,
                    TextWrapping = TextWrapping.Wrap,
                    IsTextSelectionEnabled = true
                });

                if (_detectedAudioStreams != null && _detectedAudioStreams.Count > 0)
                {
                    foreach (var stream in _detectedAudioStreams)
                    {
                        content.Children.Add(new TextBlock
                        {
                            Text = $"Track {stream.AudioTrackNumber + 1}: {FormatAudioStreamSummary(stream)}",
                            TextWrapping = TextWrapping.Wrap
                        });
                    }
                }
                else
                {
                    content.Children.Add(new TextBlock
                    {
                        Text = "Zink will show 5.1, 7.1, Dolby, and DTS details here when they are detected in the film.",
                        TextWrapping = TextWrapping.Wrap
                    });
                }

                var dialog = new ContentDialog
                {
                    Title = "Audio info",
                    Content = content,
                    CloseButtonText = "OK",
                    XamlRoot = XamlRoot
                };

                await dialog.ShowAsync();
            }
            catch { }
        }

        private void VideoInfoButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (VideoInfoTextBlock != null)
                    VideoInfoTextBlock.Text = _videoInfoStatus;

                if (VideoInfoPanel != null)
                    VideoInfoPanel.Visibility = Visibility.Visible;

                ControlPanel.Visibility = Visibility.Visible;
                hideControlsTimer?.Stop();
            }
            catch { }
        }

        private void CloseVideoInfoButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (VideoInfoPanel != null)
                    VideoInfoPanel.Visibility = Visibility.Collapsed;

                hideControlsTimer?.Start();
            }
            catch { }
        }

        private void MediaPlayer_MediaOpened(MediaPlayer sender, object args)
        {
            LogVideoPlaybackPath("Media opened successfully. " + (_detectedVideoMetadata?.PlaybackPath ?? "Native Windows playback path active."));

            DispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    var session = sender.PlaybackSession;
                    var dur = session.NaturalDuration;

                    if (dur.TotalSeconds > 0)
                    {
                        _mediaReadyForSeek = true;
                        SeekSlider.IsEnabled = true;

                        if (_pendingResumeSeconds > 1 && (dur.TotalSeconds - _pendingResumeSeconds) > 2)
                        {
                            try { session.Position = TimeSpan.FromSeconds(_pendingResumeSeconds); } catch { }
                            _pendingResumeSeconds = 0;
                        }

                        _ignoreSliderChange = true;
                        SeekSlider.Minimum = 0;
                        SeekSlider.Maximum = dur.TotalSeconds;
                        SeekSlider.Value = Math.Max(0, Math.Min(session.Position.TotalSeconds, dur.TotalSeconds));
                        _ignoreSliderChange = false;

                        TotalTimeText.Text = FormatTime(dur);
                        CurrentTimeText.Text = FormatTime(session.Position);

                        try
                        {
                            if (mediaPlayerElement?.MediaPlayer != null)
                            {
                                ApplySavedVolume();
                            }
                        }
                        catch { }

                        SyncDiscordPlaybackClockFromSession(force: true);
                        TryAutoSelectBestAudioTrack();
                        ResetDiscordSecondPushTracking();
                        try { _discordPresenceTimer?.Start(); } catch { }
                        RefreshDiscordVideoPresence(forcePlaying: true, forcePush: true);
                    }
                }
                catch { }
            });
        }

        private void MediaPlayer_MediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
        {
            try
            {
                string error = args == null
                    ? "Unknown media failure."
                    : $"{args.Error} {args.ErrorMessage}".Trim();

                LogVideoPlaybackPath("Native Windows playback failed: " + error + " Safe fallback is unavailable inside the app without a supported Windows decoder; try enabling Windows HDR, installing the HEVC Video Extensions, or playing the HDR10/SDR base layer if the file provides one.");
            }
            catch { }
        }

        private void MediaPlayer_MediaEnded(MediaPlayer sender, object args)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    _userPausedDiscordPresence = false;
                    _discordPresenceTimer?.Stop();
                    ResetDiscordPlaybackClock();
                    ClearDiscordVideoPresence();
                }
                catch { }
            });
        }

        private void PlaybackSession_PlaybackStateChanged(MediaPlaybackSession sender, object args)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    if (_suppressDiscordPresenceRefresh)
                        return;

                    var state = sender.PlaybackState;

                    if (state == MediaPlaybackState.Playing)
                    {
                        _userPausedDiscordPresence = false;

                        if (!_discordClockReady)
                            SyncDiscordPlaybackClockFromSession(force: true);
                        else
                            SyncDiscordPlaybackClockFromSession(force: false);

                        try { _discordPresenceTimer?.Start(); } catch { }
                        RefreshDiscordVideoPresence(forcePlaying: true, forcePush: true);
                    }
                    else if (state == MediaPlaybackState.Opening || state == MediaPlaybackState.Buffering)
                    {
                        if (!_userPausedDiscordPresence)
                            RefreshDiscordVideoPresence(forcePlaying: true, forcePush: false);
                    }
                    else if (state == MediaPlaybackState.Paused)
                    {
                        if (_userPausedDiscordPresence)
                        {
                            try { _discordPresenceTimer?.Stop(); } catch { }
                            RefreshDiscordPausedPresence(forcePush: true);
                        }
                    }
                }
                catch { }
            });
        }

        private void SeekSlider_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (!_mediaReadyForSeek) return;

            _isUserSeeking = true;
            SeekSlider.CapturePointer(e.Pointer);

            SetSliderFromPointer(e);
            ApplySeekFromSlider(preservePlaybackState: true);
        }

        private void SeekSlider_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!_mediaReadyForSeek) return;
            if (!_isUserSeeking) return;

            SetSliderFromPointer(e);
            ApplySeekFromSlider(preservePlaybackState: true);
        }

        private void SeekSlider_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (!_mediaReadyForSeek) return;

            if (_isUserSeeking)
            {
                _isUserSeeking = false;
                ApplySeekFromSlider(preservePlaybackState: true);
            }

            SeekSlider.ReleasePointerCaptures();
        }

        private void SeekSlider_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
        {
            if (_isUserSeeking)
            {
                _isUserSeeking = false;
                ApplySeekFromSlider(preservePlaybackState: true);
            }
        }

        private void SeekSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (_ignoreSliderChange) return;

            if (_isUserSeeking)
                CurrentTimeText.Text = FormatTime(TimeSpan.FromSeconds(SeekSlider.Value));
        }

        private void SetSliderFromPointer(PointerRoutedEventArgs e)
        {
            try
            {
                var width = SeekSlider.ActualWidth;
                if (width <= 0) return;

                var p = e.GetCurrentPoint(SeekSlider).Position.X;
                var ratio = p / width;
                ratio = Math.Max(0, Math.Min(1, ratio));

                var value = SeekSlider.Minimum + ratio * (SeekSlider.Maximum - SeekSlider.Minimum);

                _ignoreSliderChange = true;
                SeekSlider.Value = value;
                _ignoreSliderChange = false;
            }
            catch { }
        }

        private void ApplySeekFromSlider(bool preservePlaybackState = false)
        {
            try
            {
                var player = mediaPlayerElement.MediaPlayer;
                var session = player.PlaybackSession;

                if (!session.CanSeek) return;

                var dur = session.NaturalDuration;
                if (dur.TotalSeconds <= 0) return;

                var seconds = Math.Max(0, Math.Min(SeekSlider.Value, dur.TotalSeconds));
                var wasPlaying = session.PlaybackState == MediaPlaybackState.Playing;

                _suppressDiscordPresenceRefresh = true;

                session.Position = TimeSpan.FromSeconds(seconds);
                CurrentTimeText.Text = FormatTime(session.Position);

                if (preservePlaybackState && wasPlaying)
                {
                    _userPausedDiscordPresence = false;
                    player.Play();
                }

                _suppressDiscordPresenceRefresh = false;
                SyncDiscordPlaybackClockFromSession(force: true);
                ResetDiscordSecondPushTracking();
                RefreshDiscordVideoPresence(forcePlaying: wasPlaying, forcePush: true);
            }
            catch
            {
                _suppressDiscordPresenceRefresh = false;
            }
        }

        private void UpdateSeekUI()
        {
            try
            {
                if (_isUserSeeking) return;

                var session = mediaPlayerElement.MediaPlayer.PlaybackSession;
                var duration = session.NaturalDuration;

                if (duration.TotalSeconds <= 0) return;

                var pos = session.Position;
                if (pos < TimeSpan.Zero) pos = TimeSpan.Zero;
                if (pos > duration) pos = duration;

                _ignoreSliderChange = true;
                SeekSlider.Maximum = duration.TotalSeconds;
                SeekSlider.Value = pos.TotalSeconds;
                _ignoreSliderChange = false;

                CurrentTimeText.Text = FormatTime(pos);
                TotalTimeText.Text = FormatTime(duration);

                MaybeSaveResumePosition(duration.TotalSeconds, pos.TotalSeconds);
            }
            catch { }
        }

        private static string FormatTime(TimeSpan t)
            => t.TotalHours >= 1 ? t.ToString(@"hh\:mm\:ss") : t.ToString(@"mm\:ss");

        private void FullScreenButton_Click(object sender, RoutedEventArgs e)
        {
            var hwnd = WindowNative.GetWindowHandle(App.MainWindow);
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            var appWindow = AppWindow.GetFromWindowId(windowId);

            var mainWindow = App.MainWindow as MainWindow;
            var sidebarColumnDef = mainWindow?.SidebarColumnReference;

            if (!isFullScreen)
            {
                StartNvidiaOverlaySuppression();
                appWindow.SetPresenter(AppWindowPresenterKind.FullScreen);

                if (sidebarColumnDef != null)
                    sidebarColumnDef.Width = new GridLength(0);

                isFullScreen = true;
                FullScreenLabel.Text = "Exit Fullscreen";
            }
            else
            {
                appWindow.SetPresenter(AppWindowPresenterKind.Overlapped);
                StopNvidiaOverlaySuppression();

                if (sidebarColumnDef != null)
                    sidebarColumnDef.Width = new GridLength(250);

                isFullScreen = false;
                FullScreenLabel.Text = "Fullscreen";
            }
        }

        private void StartNvidiaOverlaySuppression()
        {
            try
            {
                SuppressNvidiaOverlayWindows();
                _nvidiaOverlaySuppressTimer?.Stop();
                _nvidiaOverlaySuppressTimer?.Start();
            }
            catch { }
        }

        private void StopNvidiaOverlaySuppression()
        {
            try
            {
                _nvidiaOverlaySuppressTimer?.Stop();
            }
            catch { }
        }

        private static void SuppressNvidiaOverlayWindows()
        {
            try
            {
                EnumWindows((hwnd, _) =>
                {
                    try
                    {
                        if (!IsWindowVisible(hwnd))
                            return true;

                        GetWindowThreadProcessId(hwnd, out var processId);
                        if (processId == 0)
                            return true;

                        using var process = Process.GetProcessById((int)processId);
                        var processName = process.ProcessName ?? "";
                        var windowText = GetWindowText(hwnd);
                        var className = GetWindowClassName(hwnd);

                        if (IsNvidiaOverlayProcess(processName) ||
                            IsNvidiaOverlayWindow(processName, windowText, className))
                            ShowWindow(hwnd, SW_HIDE);
                    }
                    catch { }

                    return true;
                }, IntPtr.Zero);

                foreach (var processName in GetNvidiaOverlayProcessNames())
                {
                    try
                    {
                        foreach (var process in Process.GetProcessesByName(processName))
                        {
                            try
                            {
                                if (!process.HasExited)
                                    process.Kill();
                            }
                            catch { }
                            finally
                            {
                                try { process.Dispose(); } catch { }
                            }
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }

        private static bool IsNvidiaOverlayProcess(string processName)
        {
            return string.Equals(processName, "NVIDIA Overlay", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(processName, "NVIDIA Share", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(processName, "NVIDIA Web Helper", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(processName, "nvsphelper64", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsNvidiaOverlayWindow(string processName, string windowText, string className)
        {
            var combined = $"{processName} {windowText} {className}";

            return combined.IndexOf("NVIDIA Overlay", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   combined.IndexOf("NVIDIA Share", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   combined.IndexOf("GeForce Overlay", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   combined.IndexOf("NVIDIA GeForce", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   combined.IndexOf("ShadowPlay", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   combined.IndexOf("NvCamera", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   combined.IndexOf("NvOverlay", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   combined.IndexOf("NVIDIA Notification", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static IEnumerable<string> GetNvidiaOverlayProcessNames()
        {
            yield return "NVIDIA Overlay";
            yield return "NVIDIA Share";
            yield return "NVIDIA Web Helper";
            yield return "nvsphelper64";
        }

        private static string GetWindowText(IntPtr hwnd)
        {
            try
            {
                var builder = new StringBuilder(512);
                GetWindowText(hwnd, builder, builder.Capacity);
                return builder.ToString();
            }
            catch
            {
                return "";
            }
        }

        private static string GetWindowClassName(IntPtr hwnd)
        {
            try
            {
                var builder = new StringBuilder(512);
                GetClassName(hwnd, builder, builder.Capacity);
                return builder.ToString();
            }
            catch
            {
                return "";
            }
        }

        private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

        private const int SW_HIDE = 0;

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        private void FlyoutVolumeSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            try
            {
                if (!_volumeUiReady)
                    return;

                if (mediaPlayerElement?.MediaPlayer == null)
                    return;

                if (_flyoutVolumeSlider == null)
                    return;

                double volume = Math.Max(0, Math.Min(100, _flyoutVolumeSlider.Value)) / 100.0;
                mediaPlayerElement.MediaPlayer.Volume = volume;

                if (volume > 0)
                    _lastNonZeroVolume = volume;

                UpdateVolumeUI(volume);
                SaveVolume(volume);
            }
            catch { }
        }

        private void FlyoutMuteButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (mediaPlayerElement?.MediaPlayer == null)
                    return;

                double currentVolume = mediaPlayerElement.MediaPlayer.Volume;

                if (currentVolume > 0)
                {
                    _lastNonZeroVolume = currentVolume;

                    if (_flyoutVolumeSlider != null)
                        _flyoutVolumeSlider.Value = 0;
                }
                else
                {
                    double restoreVolume = _lastNonZeroVolume > 0 ? _lastNonZeroVolume : 1.0;

                    if (_flyoutVolumeSlider != null)
                        _flyoutVolumeSlider.Value = Math.Max(0, Math.Min(100, restoreVolume * 100.0));
                }
            }
            catch { }
        }

        private void ApplySavedVolume()
        {
            try
            {
                double savedVolume = 1.0;

                if (WStorage.ApplicationData.Current.LocalSettings.Values.TryGetValue(VIDEO_VOLUME_KEY, out object value))
                {
                    if (value is double d)
                        savedVolume = d;
                    else if (value is float f)
                        savedVolume = f;
                    else if (value is string s && double.TryParse(s, out var parsed))
                        savedVolume = parsed;
                }

                savedVolume = Math.Max(0, Math.Min(1, savedVolume));

                if (savedVolume > 0)
                    _lastNonZeroVolume = savedVolume;

                if (mediaPlayerElement?.MediaPlayer != null)
                    mediaPlayerElement.MediaPlayer.Volume = savedVolume;

                if (_flyoutVolumeSlider != null)
                    _flyoutVolumeSlider.Value = savedVolume * 100.0;

                UpdateVolumeUI(savedVolume);
            }
            catch { }
        }

        private void SaveVolume(double volume)
        {
            try
            {
                WStorage.ApplicationData.Current.LocalSettings.Values[VIDEO_VOLUME_KEY] = volume;
            }
            catch { }
        }

        private string LoadSavedSurroundMode()
        {
            try
            {
                if (WStorage.ApplicationData.Current.LocalSettings.Values.TryGetValue(VIDEO_SURROUND_MODE_KEY, out object value) &&
                    value is string savedMode)
                {
                    return NormalizeSurroundMode(savedMode);
                }
            }
            catch { }

            return SurroundModeAuto;
        }

        private void SaveSurroundMode(string mode)
        {
            try
            {
                WStorage.ApplicationData.Current.LocalSettings.Values[VIDEO_SURROUND_MODE_KEY] = NormalizeSurroundMode(mode);
            }
            catch { }
        }

        private void SurroundModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            try
            {
                string selectedMode = NormalizeSurroundMode(_surroundModeComboBox?.SelectedValue as string);
                _preferredSurroundMode = selectedMode;
                SaveSurroundMode(selectedMode);

                _selectedAudioStream = GetPreferredAudioStream(_detectedAudioStreams, _preferredSurroundMode);
                if (_selectedAudioStream != null)
                {
                    _audioInfoStatus =
                        $"{GetSurroundModeSelectionPrefix(_preferredSurroundMode)} selected: {FormatAudioStreamSummary(_selectedAudioStream)}";
                }
                else
                {
                    _audioInfoStatus =
                        $"{GetSurroundModeSelectionPrefix(_preferredSurroundMode)} is selected, but this video does not expose a matching audio track.";
                }

                TryAutoSelectBestAudioTrack();
                UpdateSurroundModeStatusText();
            }
            catch { }
        }

        private void UpdateSurroundModeStatusText()
        {
            try
            {
                if (_surroundModeStatusText == null)
                    return;

                if (_selectedAudioStream != null)
                {
                    _surroundModeStatusText.Text =
                        $"{GetSurroundModeSelectionPrefix(_preferredSurroundMode)}: {FormatAudioStreamSummary(_selectedAudioStream)}";
                    return;
                }

                _surroundModeStatusText.Text = string.IsNullOrWhiteSpace(_audioInfoStatus)
                    ? $"{GetSurroundModeSelectionPrefix(_preferredSurroundMode)}: no audio track detected yet."
                    : _audioInfoStatus;
            }
            catch { }
        }

        private void UpdateVolumeUI(double volume)
        {
            try
            {
                int percent = (int)Math.Round(volume * 100.0);

                if (_flyoutVolumeText != null)
                    _flyoutVolumeText.Text = percent + "%";

                if (_flyoutMuteButton != null)
                    _flyoutMuteButton.Content = percent == 0 ? "Unmute" : "Mute";

                if (SoundButton != null)
                    SoundButton.Content = percent == 0 ? "Sound" : $"Sound {percent}%";
            }
            catch { }
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            try
            {
                try
                {
                    if (mediaPlayerElement?.MediaPlayer != null)
                    {
                        ApplySavedVolume();
                    }
                }
                catch { }

                if (e?.Parameter is WStorage.StorageFile file)
                {
                    await LoadAndPlayAsync(file);
                }
                else if (e?.Parameter is string path && !string.IsNullOrWhiteSpace(path))
                {
                    var fileFromPath = await WStorage.StorageFile.GetFileFromPathAsync(path);
                    await LoadAndPlayAsync(fileFromPath);
                }
            }
            catch { }

            base.OnNavigatedTo(e);
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            try
            {
                if (isFullScreen)
                {
                    var hwnd = WindowNative.GetWindowHandle(App.MainWindow);
                    var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
                    var appWindow = AppWindow.GetFromWindowId(windowId);
                    appWindow.SetPresenter(AppWindowPresenterKind.Overlapped);
                    StopNvidiaOverlaySuppression();

                    var mainWindow = App.MainWindow as MainWindow;
                    if (mainWindow?.SidebarColumnReference != null)
                        mainWindow.SidebarColumnReference.Width = new GridLength(250);

                    isFullScreen = false;
                }

                ForceSaveResumePositionNow_Video();

                _positionTimer?.Stop();
                hideControlsTimer?.Stop();
                _videoBadgeHideTimer?.Stop();
                _discordPresenceTimer?.Stop();

                var mp = mediaPlayerElement?.MediaPlayer;
                if (mp != null)
                {
                    try { SaveVolume(mp.Volume); } catch { }
                    try { mp.Pause(); } catch { }
                    try { mp.Source = null; } catch { }
                    try { mp.PlaybackSession.Position = TimeSpan.Zero; } catch { }
                }

                try { mediaPlayerElement.Source = null; } catch { }

                _currentPlaybackItem = null;
                try { VideoFormatBadge.Visibility = Visibility.Collapsed; } catch { }
                _mediaReadyForSeek = false;
                _codecPromptAlreadyShownForCurrentFile = false;
                _lastCodecPromptedPath = null;
                _videoSupportPromptAlreadyShownForCurrentFile = false;
                _lastVideoSupportPromptedPath = null;
                _userPausedDiscordPresence = false;
                _forceStartFromBeginningOnNextLoad = false;
                ResetDiscordPlaybackClock();

                try { AppPlaybackService.Instance.ClearIfKind(AppPlaybackService.MediaKind.Video); } catch { }
                ClearDiscordVideoPresence();
            }
            catch { }

            base.OnNavigatedFrom(e);
        }

        private static string MakeKey(string path)
        {
            try
            {
                using var sha1 = SHA1.Create();
                var bytes = sha1.ComputeHash(Encoding.UTF8.GetBytes(path ?? ""));
                return VIDEO_POS_PREFIX + Convert.ToHexString(bytes);
            }
            catch
            {
                return VIDEO_POS_PREFIX + (path ?? "").GetHashCode().ToString();
            }
        }

        private static double GetSavedPositionSeconds(string path)
        {
            try
            {
                var key = MakeKey(path);
                if (WStorage.ApplicationData.Current.LocalSettings.Values.TryGetValue(key, out object val))
                {
                    if (val is double d) return d;
                    if (val is float f) return f;
                    if (val is string s && double.TryParse(s, out var p)) return p;
                }
            }
            catch { }
            return 0;
        }

        private static void SavePositionSeconds(string path, double seconds)
        {
            try
            {
                var key = MakeKey(path);
                WStorage.ApplicationData.Current.LocalSettings.Values[key] = seconds;
            }
            catch { }
        }

        private static string MakeCodecStateKey(string path)
        {
            try
            {
                using var sha1 = SHA1.Create();
                var bytes = sha1.ComputeHash(Encoding.UTF8.GetBytes(path ?? ""));
                return VIDEO_CODEC_STATE_PREFIX + Convert.ToHexString(bytes);
            }
            catch
            {
                return VIDEO_CODEC_STATE_PREFIX + (path ?? "").GetHashCode().ToString();
            }
        }

        private static string GetSavedCodecState(string path)
        {
            try
            {
                var key = MakeCodecStateKey(path);
                if (WStorage.ApplicationData.Current.LocalSettings.Values.TryGetValue(key, out object val))
                {
                    if (val is string s)
                        return s;
                }
            }
            catch { }

            return null;
        }

        private static void SaveCodecState(string path, string state)
        {
            try
            {
                var key = MakeCodecStateKey(path);
                WStorage.ApplicationData.Current.LocalSettings.Values[key] = state ?? "";
            }
            catch { }
        }

        private static void ClearSavedCodecState(string path)
        {
            try
            {
                var key = MakeCodecStateKey(path);
                WStorage.ApplicationData.Current.LocalSettings.Values.Remove(key);
            }
            catch { }
        }

        private static string NormalizePackagePrefix(string prefix)
        {
            try
            {
                return (prefix ?? "").Trim().TrimEnd('_');
            }
            catch
            {
                return prefix ?? "";
            }
        }

        private async System.Threading.Tasks.Task<bool> IsCodecExtensionInstalledAsync(string packagePrefix)
        {
            try
            {
                return await System.Threading.Tasks.Task.Run(() =>
                {
                    try
                    {
                        var normalizedPrefix = NormalizePackagePrefix(packagePrefix);
                        if (string.IsNullOrWhiteSpace(normalizedPrefix))
                            return false;

                        var manager = new WDeployment.PackageManager();

                        foreach (var pkg in manager.FindPackagesForUser(string.Empty))
                        {
                            try
                            {
                                var name = pkg?.Id?.Name ?? "";
                                var familyName = pkg?.Id?.FamilyName ?? "";
                                var fullName = pkg?.Id?.FullName ?? "";

                                if (name.StartsWith(normalizedPrefix, StringComparison.OrdinalIgnoreCase) ||
                                    familyName.StartsWith(normalizedPrefix, StringComparison.OrdinalIgnoreCase) ||
                                    fullName.StartsWith(normalizedPrefix, StringComparison.OrdinalIgnoreCase))
                                {
                                    return true;
                                }
                            }
                            catch { }
                        }
                    }
                    catch { }

                    return false;
                });
            }
            catch
            {
                return false;
            }
        }

        private void MaybeSaveResumePosition(double durationSeconds, double posSeconds)
        {
            try
            {
                if (_currentFile == null || string.IsNullOrWhiteSpace(_currentFile.Path)) return;

                var state = mediaPlayerElement?.MediaPlayer?.PlaybackSession?.PlaybackState ?? MediaPlaybackState.None;
                if (state != MediaPlaybackState.Playing && state != MediaPlaybackState.Paused)
                    return;

                if (durationSeconds <= 0) return;
                if (posSeconds < 1) return;

                if ((durationSeconds - posSeconds) < 2.0)
                    return;

                var now = DateTime.UtcNow;
                if ((now - _lastPosSaveUtc).TotalSeconds < 1.5) return;
                if (_lastSavedPosSeconds >= 0 && Math.Abs(posSeconds - _lastSavedPosSeconds) < 1.0) return;

                _lastPosSaveUtc = now;
                _lastSavedPosSeconds = posSeconds;

                SavePositionSeconds(_currentFile.Path, posSeconds);
            }
            catch { }
        }
    }
}
