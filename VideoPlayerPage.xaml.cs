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
using System.Diagnostics;

using DispatcherTimer = Microsoft.UI.Xaml.DispatcherTimer;
using Zink.Services;

namespace Zink
{
    public sealed class SecondsToTimeConverter : IValueConverter
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

        private bool _waitingForCodecInstallReturn = false;
        private bool _isHandlingCodecReturnReload = false;
        private bool _suppressCodecPromptOnce = false;
        private string _pendingReloadVideoPath = null;
        private double _pendingReloadResumeSeconds = 0;

        private const string CodecInstallerFolderName = "CodecInstallers";
        private const string ToolsFolderName = "Tools";
        private const string FfprobeExeName = "ffprobe.exe";

        private const string DolbyDigitalPlusPrefix = "DolbyLaboratories.DolbyDigitalPlusDecoderOEM_";
        private const string DolbyAC4Prefix = "DolbyLaboratories.DolbyAC4DecoderOEM_";

        private const string CodecStateNotNeeded = "not_needed";
        private const string CodecStateInstalledDdp = "installed_ddp";
        private const string CodecStateInstalledAc4 = "installed_ac4";
        private const string CodecStatePendingDdp = "pending_ddp";
        private const string CodecStatePendingAc4 = "pending_ac4";

        private const string VIDEO_VOLUME_KEY = "Zink_VideoPlayer_Volume";
        private double _lastNonZeroVolume = 1.0;
        private bool _volumeUiReady = false;

        private Flyout _soundFlyout;
        private Slider _flyoutVolumeSlider;
        private TextBlock _flyoutVolumeText;
        private Button _flyoutMuteButton;

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

            _positionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _positionTimer.Tick += (_, _) => UpdateSeekUI();
            _positionTimer.Start();

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
            _userPausedDiscordPresence = false;
            ResetDiscordPlaybackClock();

            if (_suppressCodecPromptOnce)
            {
                _suppressCodecPromptOnce = false;
            }
            else
            {
                await PromptForMissingCodecIfNeededAsync(file);
            }

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
            try
            {
                if (file == null || string.IsNullOrWhiteSpace(file.Path))
                    return null;

                var ffprobePath = await GetBundledFfprobePathAsync();
                if (string.IsNullOrWhiteSpace(ffprobePath) || !File.Exists(ffprobePath))
                    return null;

                string args =
                    $"-v error -select_streams a:0 -show_entries stream=codec_name " +
                    $"-of default=noprint_wrappers=1:nokey=1 \"{file.Path}\"";

                var startInfo = new ProcessStartInfo
                {
                    FileName = ffprobePath,
                    Arguments = args,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using var process = new Process();
                process.StartInfo = startInfo;

                process.Start();

                string stdout = await process.StandardOutput.ReadToEndAsync();
                _ = await process.StandardError.ReadToEndAsync();

                await System.Threading.Tasks.Task.Run(() => process.WaitForExit(8000));

                if (!process.HasExited)
                {
                    try { process.Kill(true); } catch { }
                    return null;
                }

                if (!string.IsNullOrWhiteSpace(stdout))
                    return stdout.Trim();
            }
            catch { }

            return null;
        }

        private async System.Threading.Tasks.Task<string> GetBundledFfprobePathAsync()
        {
            try
            {
                var installed = WAppModel.Package.Current.InstalledLocation;

                try
                {
                    var toolsFolder = await installed.GetFolderAsync(ToolsFolderName);
                    var probeFile = await toolsFolder.GetFileAsync(FfprobeExeName);
                    return probeFile.Path;
                }
                catch { }

                try
                {
                    var rootProbeFile = await installed.GetFileAsync(FfprobeExeName);
                    return rootProbeFile.Path;
                }
                catch { }
            }
            catch { }

            return null;
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

            return item;
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

        private void MediaPlayer_MediaOpened(MediaPlayer sender, object args)
        {
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
                        ResetDiscordSecondPushTracking();
                        try { _discordPresenceTimer?.Start(); } catch { }
                        RefreshDiscordVideoPresence(forcePlaying: true, forcePush: true);
                    }
                }
                catch { }
            });
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
            ApplySeekFromSlider();
        }

        private void SeekSlider_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!_mediaReadyForSeek) return;
            if (!_isUserSeeking) return;

            SetSliderFromPointer(e);
            CurrentTimeText.Text = FormatTime(TimeSpan.FromSeconds(SeekSlider.Value));
        }

        private void SeekSlider_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (!_mediaReadyForSeek) return;

            if (_isUserSeeking)
            {
                _isUserSeeking = false;
                ApplySeekFromSlider();
            }

            SeekSlider.ReleasePointerCaptures();
        }

        private void SeekSlider_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
        {
            if (_isUserSeeking)
            {
                _isUserSeeking = false;
                ApplySeekFromSlider();
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

        private void ApplySeekFromSlider()
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

                player.Pause();
                session.Position = TimeSpan.FromSeconds(seconds);
                CurrentTimeText.Text = FormatTime(session.Position);

                if (wasPlaying)
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
                appWindow.SetPresenter(AppWindowPresenterKind.FullScreen);

                if (sidebarColumnDef != null)
                    sidebarColumnDef.Width = new GridLength(0);

                isFullScreen = true;
                FullScreenLabel.Text = "Exit Fullscreen";
            }
            else
            {
                appWindow.SetPresenter(AppWindowPresenterKind.Overlapped);

                if (sidebarColumnDef != null)
                    sidebarColumnDef.Width = new GridLength(250);

                isFullScreen = false;
                FullScreenLabel.Text = "Fullscreen";
            }
        }

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
                ForceSaveResumePositionNow_Video();

                _positionTimer?.Stop();
                hideControlsTimer?.Stop();
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
                _mediaReadyForSeek = false;
                _codecPromptAlreadyShownForCurrentFile = false;
                _lastCodecPromptedPath = null;
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