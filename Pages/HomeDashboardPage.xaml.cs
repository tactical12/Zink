using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using WStorage = global::Windows.Storage;
using WPickers = global::Windows.Storage.Pickers;
using WFileProps = global::Windows.Storage.FileProperties;
using WPerms = global::Windows.Storage.AccessCache;

using WinRT.Interop;
using Zink.Services;
using DispatcherTimer = Microsoft.UI.Xaml.DispatcherTimer;

namespace Zink.Pages
{
    public sealed partial class HomeDashboardPage : Page
    {
        public sealed class RecentItem
        {
            public string Title { get; set; } = "";
            public string Subtitle { get; set; } = "";
            public string RightText { get; set; } = "";
            public string? PayloadPath { get; set; }
            public string? PayloadToken { get; set; }
            public string? Kind { get; set; }
        }

        private readonly ObservableCollection<RecentItem> _recent = new();
        private RecentItem? _lastPlayable;
        private readonly DispatcherTimer _nowPlayingTimer = new();
        private string _lastThumbIdentity = "";

        private static readonly HashSet<string> MusicExt = new(StringComparer.OrdinalIgnoreCase)
        {
            ".mp3", ".m4a", ".aac", ".wav", ".flac", ".ogg", ".wma", ".alac", ".aiff", ".opus"
        };

        private static readonly HashSet<string> VideoExt = new(StringComparer.OrdinalIgnoreCase)
        {
            ".mp4", ".mkv", ".mov", ".avi", ".wmv", ".webm", ".m4v", ".ts", ".m2ts", ".3gp"
        };

        private static WStorage.ApplicationDataContainer Settings => WStorage.ApplicationData.Current.LocalSettings;

        private const string LS_LastKind = "HomeDash_LastKind";
        private const string LS_LastPath = "HomeDash_LastPath";
        private const string LS_LastTitle = "HomeDash_LastTitle";
        private const string LS_LastSubtitle = "HomeDash_LastSubtitle";
        private const string LS_LastToken = "HomeDash_LastToken";

        private const string K_ShowHeroInsights = "Dash_ShowHeroInsights";
        private const string K_ShowPowerTools = "Dash_ShowPowerTools";
        private const string K_ShowRecentActivity = "Dash_ShowRecentActivity";
        private static string K_Tile(string id) => $"Dash_Tile_{id}";

        public HomeDashboardPage()
        {
            InitializeComponent();

            RecentList.ItemsSource = _recent;

            ActivityHub.EnsureLoaded();
            ActivityHub.Changed -= ActivityHub_Changed;
            ActivityHub.Changed += ActivityHub_Changed;

            AppPlaybackService.Instance.NowPlayingChanged -= AppPlaybackService_NowPlayingChanged;
            AppPlaybackService.Instance.NowPlayingChanged += AppPlaybackService_NowPlayingChanged;

            AppPlaybackService.Instance.IsPlayingChanged -= AppPlaybackService_IsPlayingChanged;
            AppPlaybackService.Instance.IsPlayingChanged += AppPlaybackService_IsPlayingChanged;

            Loaded += HomeDashboardPage_Loaded;
            Unloaded += HomeDashboardPage_Unloaded;

            _nowPlayingTimer.Interval = TimeSpan.FromSeconds(1);
            _nowPlayingTimer.Tick += NowPlayingTimer_Tick;

            ApplyDashboardCustomisation();

            SetHeroEmptyState();
            RestoreHeroFromSettings();

            RefreshFromActivityHub();
            RefreshNowPlayingFromServices();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            ApplyDashboardCustomisation();
            RestoreHeroFromSettings();
            RefreshFromActivityHub();
            RefreshNowPlayingFromServices();
        }

        private void HomeDashboardPage_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                _nowPlayingTimer.Start();
                RefreshNowPlayingFromServices();
            }
            catch { }
        }

        private void HomeDashboardPage_Unloaded(object sender, RoutedEventArgs e)
        {
            try
            {
                _nowPlayingTimer.Stop();
            }
            catch { }
        }

        private void NowPlayingTimer_Tick(object? sender, object e)
        {
            RefreshNowPlayingFromServices();
        }

        private void ActivityHub_Changed(object? sender, EventArgs e)
        {
            RefreshFromActivityHub();
            RefreshNowPlayingFromServices();
        }

        private void AppPlaybackService_NowPlayingChanged(object? sender, EventArgs e)
        {
            try
            {
                DispatcherQueue?.TryEnqueue(() =>
                {
                    RefreshNowPlayingFromServices();
                });
            }
            catch { }
        }

        private void AppPlaybackService_IsPlayingChanged(object? sender, EventArgs e)
        {
            try
            {
                DispatcherQueue?.TryEnqueue(() =>
                {
                    RefreshNowPlayingFromServices();
                });
            }
            catch { }
        }

        private void RefreshFromActivityHub()
        {
            try
            {
                _recent.Clear();

                foreach (var a in ActivityHub.Recent)
                {
                    var kind = ToDashKind(a.Type, a.Payload);

                    _recent.Add(new RecentItem
                    {
                        Title = a.Title ?? "",
                        Subtitle = a.Subtitle ?? "",
                        PayloadPath = a.Payload,
                        Kind = kind,
                        RightText = FormatWhen(a.When)
                    });
                }

                if (!HasLiveNowPlaying())
                {
                    var latestPlayable = _recent.FirstOrDefault(IsPlayableForHero);

                    if (latestPlayable != null)
                    {
                        var changed =
                            _lastPlayable == null ||
                            !string.Equals(_lastPlayable.Kind, latestPlayable.Kind, StringComparison.OrdinalIgnoreCase) ||
                            !string.Equals(_lastPlayable.Title, latestPlayable.Title, StringComparison.Ordinal) ||
                            !string.Equals(_lastPlayable.Subtitle, latestPlayable.Subtitle, StringComparison.Ordinal) ||
                            !string.Equals(_lastPlayable.PayloadPath, latestPlayable.PayloadPath, StringComparison.Ordinal);

                        if (changed)
                            SetHeroPlayableState(latestPlayable, persist: true);
                    }
                }

                try
                {
                    var w = ActivityHub.GetTotalWatchedTime();
                    var l = ActivityHub.GetTotalListenedTime();

                    TimeWatchedText.Text = FormatShortTime(w);
                    TimeListenedText.Text = FormatShortTime(l);
                    MostUsedText.Text = ActivityHub.GetMostUsedType();
                }
                catch { }
            }
            catch
            {
            }
        }

        private void RefreshNowPlayingFromServices()
        {
            try
            {
                if (TryRefreshFromAppPlaybackService())
                    return;

                if (_lastPlayable != null)
                {
                    if (string.Equals(_lastPlayable.Kind, "radio", StringComparison.OrdinalIgnoreCase))
                    {
                        HeroKindText.Text = "Radio";
                        HeroTypeValue.Text = "Radio";
                        HeroPlaybackValue.Text = "Live";
                        HeroDurationValue.Text = "Live";
                        HeroMetaText.Text = "Live stream";
                    }
                    else
                    {
                        HeroPlaybackValue.Text = ResumeButton.IsEnabled ? "Ready" : "Stopped";
                    }
                }
            }
            catch { }
        }

        private bool TryRefreshFromAppPlaybackService()
        {
            try
            {
                var appPlayback = AppPlaybackService.Instance;
                var player = MediaPlayerSingleton.Instance;

                if (appPlayback == null)
                    return false;

                if (!HasLiveNowPlaying())
                    return false;

                var kind = appPlayback.CurrentKind switch
                {
                    AppPlaybackService.MediaKind.Music => "music",
                    AppPlaybackService.MediaKind.Video => "video",
                    AppPlaybackService.MediaKind.Radio => "radio",
                    _ => "page"
                };

                var primary = (appPlayback.PrimaryText ?? "").Trim();
                var secondary = (appPlayback.SecondaryText ?? "").Trim();

                if (string.IsNullOrWhiteSpace(primary))
                {
                    primary = kind switch
                    {
                        "music" => "Music",
                        "video" => "Video",
                        "radio" => appPlayback.CurrentStationTitle ?? "Radio",
                        _ => "Nothing playing yet"
                    };
                }

                if (kind == "radio")
                {
                    if (string.IsNullOrWhiteSpace(primary))
                        primary = appPlayback.CurrentStationTitle ?? "Radio";

                    if (string.IsNullOrWhiteSpace(secondary))
                    {
                        var artist = appPlayback.CurrentArtist ?? "";
                        var title = appPlayback.CurrentTitle ?? "";

                        if (!string.IsNullOrWhiteSpace(artist) && !string.IsNullOrWhiteSpace(title))
                            secondary = $"{artist} - {title}";
                        else if (!string.IsNullOrWhiteSpace(title))
                            secondary = title;
                        else if (!string.IsNullOrWhiteSpace(artist))
                            secondary = artist;
                    }
                }

                HeroTitle.Text = primary;
                HeroSubtitle.Text = string.IsNullOrWhiteSpace(secondary)
                    ? GetDefaultSubtitleForKind(kind)
                    : secondary;

                HeroKindText.Text = GetKindDisplay(kind);
                HeroTypeValue.Text = GetKindDisplay(kind);

                if (kind == "radio")
                {
                    HeroPlaybackValue.Text = appPlayback.IsPlaying ? "Live" : "Stopped";
                    HeroDurationValue.Text = "Live";

                    var elapsed = appPlayback.Elapsed;
                    if (elapsed > TimeSpan.Zero)
                        HeroMetaText.Text = $"{FormatClock(elapsed)} elapsed";
                    else
                        HeroMetaText.Text = "Live stream";
                }
                else
                {
                    var session = player?.PlaybackSession;
                    var playbackState = session?.PlaybackState;
                    HeroPlaybackValue.Text = playbackState switch
                    {
                        global::Windows.Media.Playback.MediaPlaybackState.Playing => "Playing",
                        global::Windows.Media.Playback.MediaPlaybackState.Paused => "Paused",
                        global::Windows.Media.Playback.MediaPlaybackState.Buffering => "Buffering",
                        global::Windows.Media.Playback.MediaPlaybackState.Opening => "Opening",
                        _ => (appPlayback.IsPlaying ? "Playing" : "Stopped")
                    };

                    var duration = session?.NaturalDuration ?? TimeSpan.Zero;
                    var position = session?.Position ?? appPlayback.Elapsed;

                    if (duration > TimeSpan.Zero)
                    {
                        HeroMetaText.Text = $"{FormatClock(position)} - {FormatClock(duration)}";
                        HeroDurationValue.Text = FormatClock(duration);
                    }
                    else if (position > TimeSpan.Zero)
                    {
                        HeroMetaText.Text = FormatClock(position);
                        HeroDurationValue.Text = "-";
                    }
                    else
                    {
                        HeroMetaText.Text = GetHeroMetaText(kind);
                        HeroDurationValue.Text = "-";
                    }
                }

                ResumeButton.IsEnabled = true;

                var artUri = kind == "radio"
                    ? (appPlayback.CurrentArtworkUri ?? appPlayback.CurrentStationLogoUri)
                    : (appPlayback.GenericArtworkUri ?? appPlayback.CurrentArtworkUri);

                _ = RefreshHeroArtworkFromServicesAsync(kind, artUri);

                _lastPlayable = BuildRecentItemFromLiveState(kind, primary, HeroSubtitle.Text);

                if (_lastPlayable != null)
                    SaveHeroToSettings(_lastPlayable);

                return true;
            }
            catch
            {
                return false;
            }
        }

        private async Task RefreshHeroArtworkFromServicesAsync(string? kind, Uri? artworkUri)
        {
            try
            {
                var identity = $"{kind}|uri|{artworkUri}";
                if (string.Equals(identity, _lastThumbIdentity, StringComparison.Ordinal))
                    return;

                if (artworkUri != null)
                {
                    try
                    {
                        var bmp = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
                        bmp.UriSource = artworkUri;
                        HeroThumb.Source = bmp;
                        HeroThumb.Visibility = Visibility.Visible;
                        HeroThumbFallback.Visibility = Visibility.Collapsed;
                        _lastThumbIdentity = identity;
                        return;
                    }
                    catch
                    {
                    }
                }

                if (_lastPlayable != null)
                {
                    await RefreshHeroThumbnailIfNeededAsync(_lastPlayable.Kind, _lastPlayable.PayloadPath, _lastPlayable.PayloadToken);
                    return;
                }

                HeroThumb.Visibility = Visibility.Collapsed;
                HeroThumbFallback.Visibility = Visibility.Visible;
                _lastThumbIdentity = identity;
            }
            catch
            {
                HeroThumb.Visibility = Visibility.Collapsed;
                HeroThumbFallback.Visibility = Visibility.Visible;
            }
        }

        private RecentItem? BuildRecentItemFromLiveState(string kind, string primary, string secondary)
        {
            try
            {
                if (kind == "radio")
                {
                    return new RecentItem
                    {
                        Title = primary,
                        Subtitle = secondary,
                        RightText = "Live",
                        Kind = "radio",
                        PayloadPath = null,
                        PayloadToken = null
                    };
                }

                var candidate = _recent.FirstOrDefault(r =>
                    string.Equals(r.Kind, kind, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(r.Title, primary, StringComparison.Ordinal));

                if (candidate != null)
                {
                    return new RecentItem
                    {
                        Title = candidate.Title,
                        Subtitle = string.IsNullOrWhiteSpace(secondary) ? candidate.Subtitle : secondary,
                        RightText = candidate.RightText,
                        Kind = candidate.Kind,
                        PayloadPath = candidate.PayloadPath,
                        PayloadToken = candidate.PayloadToken
                    };
                }

                var anyPlayable = _recent.FirstOrDefault(r => string.Equals(r.Kind, kind, StringComparison.OrdinalIgnoreCase));
                if (anyPlayable != null)
                {
                    return new RecentItem
                    {
                        Title = primary,
                        Subtitle = string.IsNullOrWhiteSpace(secondary) ? anyPlayable.Subtitle : secondary,
                        RightText = anyPlayable.RightText,
                        Kind = anyPlayable.Kind,
                        PayloadPath = anyPlayable.PayloadPath,
                        PayloadToken = anyPlayable.PayloadToken
                    };
                }

                return new RecentItem
                {
                    Title = primary,
                    Subtitle = secondary,
                    RightText = "Now",
                    Kind = kind
                };
            }
            catch
            {
                return null;
            }
        }

        private static bool ReadBool(string key, bool defaultValue)
        {
            try
            {
                if (Settings.Values.TryGetValue(key, out var v) && v is bool b)
                    return b;
            }
            catch { }
            return defaultValue;
        }

        private void ApplyDashboardCustomisation()
        {
            try
            {
                HeroInsightsSection.Visibility = ReadBool(K_ShowHeroInsights, true) ? Visibility.Visible : Visibility.Collapsed;
                PowerToolsSection.Visibility = ReadBool(K_ShowPowerTools, true) ? Visibility.Visible : Visibility.Collapsed;
                RecentActivitySection.Visibility = ReadBool(K_ShowRecentActivity, true) ? Visibility.Visible : Visibility.Collapsed;

                SetTileVisibility(TileBtn_MusicPlayer, "MusicPlayer");
                SetTileVisibility(TileBtn_VideoPlayer, "VideoPlayer");
                SetTileVisibility(TileBtn_Radio, "Radio");

                SetTileVisibility(TileBtn_VideoLibrary, "VideoLibrary");
                SetTileVisibility(TileBtn_MusicLibrary, "MusicLibrary");

                SetTileVisibility(TileBtn_Equalizer, "Equalizer");
                SetTileVisibility(TileBtn_Visualizer, "Visualizer");

                SetTileVisibility(TileBtn_Settings, "Settings");
                SetTileVisibility(TileBtn_VersionHistory, "VersionHistory");

                SetTileVisibility(TileBtn_Spotify, "Spotify");
                SetTileVisibility(TileBtn_Twitch, "Twitch");
                SetTileVisibility(TileBtn_Xbox, "Xbox");
                SetTileVisibility(TileBtn_Discord, "Discord");
                SetTileVisibility(TileBtn_YouTube, "YouTube");

                SetTileVisibility(TileBtn_Customise, "Customise");
            }
            catch { }
        }

        private void SetTileVisibility(Button btn, string id)
        {
            if (btn == null) return;
            btn.Visibility = ReadBool(K_Tile(id), true) ? Visibility.Visible : Visibility.Collapsed;
        }

        private void SetHeroEmptyState()
        {
            try
            {
                HeroTitle.Text = "Nothing playing yet";
                HeroSubtitle.Text = "Play music, radio or a film and it will appear here";
                HeroKindText.Text = "Idle";
                HeroMetaText.Text = "Nothing active";
                HeroTypeValue.Text = "Nothing";
                HeroPlaybackValue.Text = "Stopped";
                HeroDurationValue.Text = "-";
                ResumeButton.IsEnabled = false;
                HeroThumb.Visibility = Visibility.Collapsed;
                HeroThumbFallback.Visibility = Visibility.Visible;
                _lastPlayable = null;
                _lastThumbIdentity = "";
            }
            catch { }
        }

        private void SaveHeroToSettings(RecentItem item)
        {
            try
            {
                Settings.Values[LS_LastKind] = item.Kind ?? "";
                Settings.Values[LS_LastPath] = item.PayloadPath ?? "";
                Settings.Values[LS_LastTitle] = item.Title ?? "";
                Settings.Values[LS_LastSubtitle] = item.Subtitle ?? "";
                Settings.Values[LS_LastToken] = item.PayloadToken ?? "";
            }
            catch { }
        }

        private void ClearHeroFromSettings()
        {
            try
            {
                Settings.Values.Remove(LS_LastKind);
                Settings.Values.Remove(LS_LastPath);
                Settings.Values.Remove(LS_LastTitle);
                Settings.Values.Remove(LS_LastSubtitle);
                Settings.Values.Remove(LS_LastToken);
            }
            catch { }
        }

        private void RestoreHeroFromSettings()
        {
            try
            {
                var kind = Settings.Values[LS_LastKind] as string;
                var path = Settings.Values[LS_LastPath] as string;
                var title = Settings.Values[LS_LastTitle] as string;
                var subtitle = Settings.Values[LS_LastSubtitle] as string;
                var token = Settings.Values[LS_LastToken] as string;

                if (string.IsNullOrWhiteSpace(kind))
                    return;

                if ((kind == "music" || kind == "video") && string.IsNullOrWhiteSpace(path))
                    return;

                if (kind == "music" || kind == "video")
                {
                    var hasToken = !string.IsNullOrWhiteSpace(token);
                    var hasPathAccess = !string.IsNullOrWhiteSpace(path) && File.Exists(path);

                    if (!hasToken && !hasPathAccess)
                    {
                        ClearHeroFromSettings();
                        return;
                    }
                }

                var item = new RecentItem
                {
                    Kind = kind,
                    PayloadPath = path,
                    PayloadToken = token,
                    Title = string.IsNullOrWhiteSpace(title) && !string.IsNullOrWhiteSpace(path)
                        ? Path.GetFileNameWithoutExtension(path)
                        : (title ?? ""),
                    Subtitle = string.IsNullOrWhiteSpace(subtitle) ? "Resume playback" : subtitle!,
                    RightText = "Resume"
                };

                SetHeroPlayableState(item, persist: false);
            }
            catch { }
        }

        private void SetHeroPlayableState(RecentItem item, bool persist = true)
        {
            HeroTitle.Text = item.Title;
            HeroSubtitle.Text = string.IsNullOrWhiteSpace(item.Subtitle) ? "Ready to resume" : item.Subtitle;
            HeroKindText.Text = GetKindDisplay(item.Kind);
            HeroMetaText.Text = GetHeroMetaText(item.Kind);
            HeroTypeValue.Text = GetKindDisplay(item.Kind);
            HeroPlaybackValue.Text = "Ready";
            HeroDurationValue.Text = string.Equals(item.Kind, "radio", StringComparison.OrdinalIgnoreCase) ? "Live" : "-";
            ResumeButton.IsEnabled = true;
            _lastPlayable = item;

            if (persist)
                SaveHeroToSettings(item);

            _ = RefreshHeroThumbnailIfNeededAsync(item.Kind, item.PayloadPath, item.PayloadToken);
        }

        private async Task RefreshHeroThumbnailIfNeededAsync(string? kind, string? path, string? token)
        {
            var identity = $"{kind}|file|{path}|{token}";
            if (string.Equals(identity, _lastThumbIdentity, StringComparison.Ordinal))
                return;

            _lastThumbIdentity = identity;
            await TrySetHeroThumbnailAsync(kind, path, token);
        }

        private async Task TrySetHeroThumbnailAsync(string? kind, string? path, string? token)
        {
            try
            {
                HeroThumb.Visibility = Visibility.Collapsed;
                HeroThumbFallback.Visibility = Visibility.Visible;

                if (string.Equals(kind, "radio", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                if (kind != "music" && kind != "video")
                    return;

                global::Windows.Storage.StorageFile? file = null;

                if (!string.IsNullOrWhiteSpace(token))
                {
                    try
                    {
                        file = await WPerms.StorageApplicationPermissions.FutureAccessList.GetFileAsync(token);
                    }
                    catch
                    {
                        file = null;
                    }
                }

                if (file == null && !string.IsNullOrWhiteSpace(path) && File.Exists(path))
                {
                    file = await WStorage.StorageFile.GetFileFromPathAsync(path);
                }

                if (file == null)
                    return;

                var thumbMode = kind == "music"
                    ? WFileProps.ThumbnailMode.MusicView
                    : WFileProps.ThumbnailMode.VideosView;

                var thumb = await file.GetThumbnailAsync(thumbMode, 512);
                if (thumb != null)
                {
                    var bmp = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
                    await bmp.SetSourceAsync(thumb);
                    HeroThumb.Source = bmp;
                    HeroThumb.Visibility = Visibility.Visible;
                    HeroThumbFallback.Visibility = Visibility.Collapsed;
                }

                if (kind == "music")
                {
                    try
                    {
                        var props = await file.Properties.GetMusicPropertiesAsync();
                        if (props.Duration > TimeSpan.Zero && HeroDurationValue.Text == "-")
                        {
                            HeroDurationValue.Text = FormatClock(props.Duration);
                        }
                    }
                    catch { }
                }
                else if (kind == "video")
                {
                    try
                    {
                        var props = await file.Properties.GetVideoPropertiesAsync();
                        if (props.Duration > TimeSpan.Zero && HeroDurationValue.Text == "-")
                        {
                            HeroDurationValue.Text = FormatClock(props.Duration);
                        }
                    }
                    catch { }
                }
            }
            catch
            {
                HeroThumb.Visibility = Visibility.Collapsed;
                HeroThumbFallback.Visibility = Visibility.Visible;
            }
        }

        private void NavigateFromRecent(RecentItem item)
        {
            var path = item.PayloadPath;

            switch (item.Kind)
            {
                case "music":
                    App.MainWindow.MainFrame.Navigate(typeof(MusicPlayerPage), path);
                    break;

                case "video":
                    App.MainWindow.MainFrame.Navigate(typeof(VideoPlayerPage), path);
                    break;

                case "radio":
                    App.MainWindow.MainFrame.Navigate(typeof(RadioPage));
                    break;
            }
        }

        private void ResumeButton_Click(object sender, RoutedEventArgs e)
        {
            if (_lastPlayable == null)
                RestoreHeroFromSettings();

            if (_lastPlayable == null)
                return;

            if (string.IsNullOrWhiteSpace(_lastPlayable.Kind) && !string.IsNullOrWhiteSpace(_lastPlayable.PayloadPath))
                _lastPlayable.Kind = DetectKindFromPath(_lastPlayable.PayloadPath);

            NavigateFromRecent(_lastPlayable);
        }

        private async void OpenFileButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var picker = new WPickers.FileOpenPicker();
                InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWindow));
                picker.FileTypeFilter.Add("*");

                var file = await picker.PickSingleFileAsync();
                if (file == null)
                    return;

                var kind = DetectKindFromPath(file.Path);

                string token = "";
                try { token = WPerms.StorageApplicationPermissions.FutureAccessList.Add(file); } catch { }

                var item = new RecentItem
                {
                    Title = file.DisplayName,
                    Subtitle = kind == "music" ? "Opened music file" :
                               kind == "video" ? "Opened video file" :
                               "Opened file",
                    RightText = "Now",
                    PayloadPath = file.Path,
                    PayloadToken = token,
                    Kind = kind
                };

                try
                {
                    ActivityHub.Record(
                        kind == "music" ? ActivityHub.ActivityKind.Music :
                        kind == "video" ? ActivityHub.ActivityKind.Video :
                        ActivityHub.ActivityKind.PageVisit,
                        title: item.Title,
                        subtitle: item.Subtitle,
                        payload: item.PayloadPath ?? "",
                        listenedSeconds: 0,
                        watchedSeconds: 0,
                        imageUri: ""
                    );
                }
                catch { }

                SetHeroPlayableState(item, persist: true);
                NavigateFromRecent(item);
            }
            catch { }
        }

        private async void OpenFolderButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var picker = new WPickers.FolderPicker();
                InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWindow));
                picker.FileTypeFilter.Add("*");

                var folder = await picker.PickSingleFolderAsync();
                if (folder == null)
                    return;

                // Original behavior unchanged
            }
            catch { }
        }

        private void ViewLibrary_Click(object sender, RoutedEventArgs e)
        {
            try { App.MainWindow.MainFrame.Navigate(typeof(MusicLibraryPage)); } catch { }
        }

        private void ClearRecent_Click(object sender, RoutedEventArgs e)
        {
            try { ActivityHub.Clear(); } catch { }

            ClearHeroFromSettings();
            SetHeroEmptyState();
            RefreshFromActivityHub();
        }

        private void RecentList_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is RecentItem item)
            {
                if (string.IsNullOrWhiteSpace(item.Kind) && !string.IsNullOrWhiteSpace(item.PayloadPath))
                    item.Kind = DetectKindFromPath(item.PayloadPath);

                if (item.Kind == "music" || item.Kind == "video" || item.Kind == "radio")
                    SetHeroPlayableState(item, persist: true);
                else
                    SetHeroPlayableState(item, persist: false);

                NavigateFromRecent(item);
            }
        }

        private void MusicPlayer_Click(object sender, RoutedEventArgs e) => App.MainWindow.MainFrame.Navigate(typeof(MusicPlayerPage));
        private void VideoPlayer_Click(object sender, RoutedEventArgs e) => App.MainWindow.MainFrame.Navigate(typeof(VideoPlayerPage));
        private void Radio_Click(object sender, RoutedEventArgs e) => App.MainWindow.MainFrame.Navigate(typeof(RadioPage));
        private void VideoLibrary_Click(object sender, RoutedEventArgs e) => App.MainWindow.MainFrame.Navigate(typeof(VideoLibraryPage));
        private void MusicLibrary_Click(object sender, RoutedEventArgs e) => App.MainWindow.MainFrame.Navigate(typeof(MusicLibraryPage));
        private void Equalizer_Click(object sender, RoutedEventArgs e) => App.MainWindow.MainFrame.Navigate(typeof(EqualizerPage));
        private void Visualizer_Click(object sender, RoutedEventArgs e) => App.MainWindow.MainFrame.Navigate(typeof(VisualizerPage));
        private void Settings_Click(object sender, RoutedEventArgs e) => App.MainWindow.MainFrame.Navigate(typeof(SettingsPage));
        private void VersionHistory_Click(object sender, RoutedEventArgs e) => App.MainWindow.MainFrame.Navigate(typeof(VersionHistoryPage));

        private void Spotify_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var candidates = new[]
                {
                    "Zink.Pages.SpotifyPage",
                    "Zink.Pages.SpotifyLoginPage",
                    "Zink.Pages.SpotifyHomePage",
                    "Zink.Pages.SpotifyBrowserPage"
                };

                foreach (var name in candidates)
                {
                    var t = Type.GetType(name);
                    if (t != null)
                    {
                        App.MainWindow.MainFrame.Navigate(t);
                        return;
                    }
                }
            }
            catch { }
        }

        private async void Xbox_Click(object sender, RoutedEventArgs e)
        {
            var ok = TryNavigateToAny(
                "Zink.Pages.XboxPage",
                "Zink.Pages.XboxHomePage"
            );

            if (!ok)
                await ShowMissingPageDialog("Xbox", "Zink.Pages.XboxPage");
        }

        private async void Discord_Click(object sender, RoutedEventArgs e)
        {
            var ok = TryNavigateToAny(
                "Zink.Pages.DiscordPage",
                "Zink.Pages.DiscordHomePage"
            );

            if (!ok)
                await ShowMissingPageDialog("Discord", "Zink.Pages.DiscordPage");
        }

        private async void YouTube_Click(object sender, RoutedEventArgs e)
        {
            var ok = TryNavigateToAny(
                "Zink.Pages.YouTubePage",
                "Zink.Pages.YouTubeHomePage",
                "Zink.Pages.YouTubeBrowserPage"
            );

            if (!ok)
                await ShowMissingPageDialog("YouTube", "Zink.Pages.YouTubePage");
        }

        private async void Customise_Click(object sender, RoutedEventArgs e)
        {
            App.MainWindow.MainFrame.Navigate(typeof(AppCustomizationPage));
            await Task.CompletedTask;
        }

        private void HomeSearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
        {
            try
            {
                var q = (args?.QueryText ?? "").Trim();
                if (string.IsNullOrWhiteSpace(q))
                    return;

                App.MainWindow.MainFrame.Navigate(typeof(SearchResultsPage), q);
            }
            catch { }
        }

        private static string FormatWhen(DateTimeOffset when)
        {
            try
            {
                var local = when.ToLocalTime();
                var age = DateTimeOffset.Now - local;

                if (age.TotalMinutes < 1) return "Now";
                if (age.TotalMinutes < 60) return $"{(int)age.TotalMinutes}m";
                if (age.TotalHours < 24) return $"{(int)age.TotalHours}h";
                return local.ToString("dd MMM");
            }
            catch
            {
                return "";
            }
        }

        private static string FormatShortTime(TimeSpan t)
        {
            if (t.TotalHours >= 1)
                return $"{(int)t.TotalHours}h {t.Minutes}m";
            return $"{Math.Max(0, t.Minutes)}m";
        }

        private static string ToDashKind(string? activityType, string? payload)
        {
            if (string.Equals(activityType, "Music", StringComparison.OrdinalIgnoreCase)) return "music";
            if (string.Equals(activityType, "Video", StringComparison.OrdinalIgnoreCase)) return "video";
            if (string.Equals(activityType, "Radio", StringComparison.OrdinalIgnoreCase)) return "radio";

            if (!string.IsNullOrWhiteSpace(payload))
                return DetectKindFromPath(payload);

            return "page";
        }

        private static string DetectKindFromPath(string path)
        {
            try
            {
                var ext = Path.GetExtension(path);
                if (MusicExt.Contains(ext)) return "music";
                if (VideoExt.Contains(ext)) return "video";
            }
            catch { }
            return "page";
        }

        private bool TryNavigateToAny(params string[] typeNames)
        {
            foreach (var name in typeNames)
            {
                try
                {
                    var t = Type.GetType(name);
                    if (t != null)
                    {
                        App.MainWindow.MainFrame.Navigate(t);
                        return true;
                    }
                }
                catch { }
            }
            return false;
        }

        private async Task ShowMissingPageDialog(string featureName, string expectedType)
        {
            try
            {
                var dlg = new ContentDialog
                {
                    Title = $"{featureName} page missing",
                    Content = $"Could not find the page type:\n{expectedType}\n\nMake sure the page exists in your project.",
                    CloseButtonText = "OK",
                    XamlRoot = this.XamlRoot
                };

                await dlg.ShowAsync();
            }
            catch { }
        }

        private async void Twitch_Click(object sender, RoutedEventArgs e)
        {
            var ok = TryNavigateToAny(
                "Zink.Pages.TwitchPage"
            );

            if (!ok)
                await ShowMissingPageDialog("Twitch", "Zink.Pages.TwitchPage");
        }

        private static bool IsPlayableForHero(RecentItem? item)
        {
            if (item == null)
                return false;

            if (string.Equals(item.Kind, "radio", StringComparison.OrdinalIgnoreCase))
                return true;

            if (string.Equals(item.Kind, "music", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(item.Kind, "video", StringComparison.OrdinalIgnoreCase))
            {
                return !string.IsNullOrWhiteSpace(item.PayloadToken) ||
                       !string.IsNullOrWhiteSpace(item.PayloadPath);
            }

            return false;
        }

        private static string GetKindDisplay(string? kind)
        {
            return kind?.ToLowerInvariant() switch
            {
                "music" => "Music",
                "video" => "Film / Video",
                "radio" => "Radio",
                _ => "Idle"
            };
        }

        private static string GetHeroMetaText(string? kind)
        {
            return kind?.ToLowerInvariant() switch
            {
                "music" => "Music playback",
                "video" => "Video playback",
                "radio" => "Live stream",
                _ => "Nothing active"
            };
        }

        private static string GetDefaultSubtitleForKind(string? kind)
        {
            return kind?.ToLowerInvariant() switch
            {
                "music" => "Music is active in Zink",
                "video" => "Video is active in Zink",
                "radio" => "Radio is active in Zink",
                _ => "Play music, radio or a film and it will appear here"
            };
        }

        private bool HasLiveNowPlaying()
        {
            try
            {
                var appPlayback = AppPlaybackService.Instance;
                if (appPlayback == null)
                    return false;

                if (appPlayback.CurrentKind == AppPlaybackService.MediaKind.None)
                    return false;

                if (!string.IsNullOrWhiteSpace(appPlayback.PrimaryText))
                    return true;

                if (appPlayback.CurrentKind == AppPlaybackService.MediaKind.Radio &&
                    !string.IsNullOrWhiteSpace(appPlayback.CurrentStationTitle))
                    return true;
            }
            catch { }

            return false;
        }

        private static string FormatClock(TimeSpan t)
        {
            if (t.TotalHours >= 1)
                return t.ToString(@"hh\:mm\:ss");

            return t.ToString(@"mm\:ss");
        }
    }
}