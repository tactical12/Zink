using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.Foundation;
using Windows.Storage;
using Windows.System;
using WinRT;
using WinRT.Interop;
using Zink.Pages;
using Zink.Pages.Social;
using Zink.Services;
using Zink.Services.Calling;
using Zink.Services.NativeCalling;
using Zink.Services.Social;

namespace Zink
{
    public sealed partial class MainWindow : Window
    {
        private bool _hasShownUpdateDialog;

        private bool _savedSidebarStateExists = false;
        private bool _savedSidebarVisible;
        private bool _savedPaneOpen;
        private NavigationViewPaneDisplayMode _savedPaneDisplayMode;
        private GridLength _savedSidebarWidth;

        private bool _weAreInFullscreenMode = false;

        private Timer? _fullscreenMonitorTimer;
        private readonly object _fullscreenMonitorLock = new();

        private DesktopAcrylicController? _acrylicController;
        private SystemBackdropConfiguration? _backdropConfig;

        private bool _hasCheckedWhatsNewDialog = false;

        private bool _windowIsActivated = false;

        private bool _contentFrameLoaded = false;
        private readonly TaskCompletionSource<bool> _activatedTcs =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource<bool> _loadedTcs =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private static readonly SemaphoreSlim _dialogGate = new(1, 1);

        private bool _incomingCallDialogShowing = false;
        private bool _realtimeConnectAttempted = false;

        public MainWindow()
        {
            this.InitializeComponent();

            ApplySavedThemeOnStartup();

            this.Activated += MainWindow_Activated;
            this.Closed += MainWindow_Closed;
            this.Activated += MainWindow_Activated_EnsureRealtime;

            TrySetDesktopAcrylicBackdrop();

            MaximizeWindow();
            SetWindowIcon();

            SidebarNav.PaneDisplayMode = NavigationViewPaneDisplayMode.Left;
            SidebarNav.IsPaneOpen = true;

            ContentFrame.Loaded += ContentFrame_Loaded;

            ContentFrame.Navigate(typeof(HomeDashboardPage));

            TrySelectHomeItem();

            BeginWhatsNewDialogCheck();

            ContentFrame.Navigated += ContentFrame_Navigated;

            SocialManager.Instance.Realtime.IncomingCall -= Realtime_IncomingCall_Global;
            SocialManager.Instance.Realtime.IncomingCall += Realtime_IncomingCall_Global;

            _ = EnsureRealtimeConnectedIfLoggedInAsync();
        }

        private async void MainWindow_Activated_EnsureRealtime(object sender, WindowActivatedEventArgs e)
        {
            await EnsureRealtimeConnectedIfLoggedInAsync();
        }

        private async Task EnsureRealtimeConnectedIfLoggedInAsync()
        {
            try
            {
                var token = await TokenStore.Instance.GetTokenAsync();
                if (string.IsNullOrWhiteSpace(token))
                    return;

                if (_realtimeConnectAttempted && SocialManager.Instance.Realtime.IsConnected)
                    return;

                _realtimeConnectAttempted = true;

                if (!SocialManager.Instance.Realtime.IsConnected)
                {
                    await SocialManager.Instance.Realtime.ConnectAsync();
                }
            }
            catch
            {
            }
        }

        private void Realtime_IncomingCall_Global(object? sender, IncomingCallEventArgs e)
        {
            DispatcherQueue.TryEnqueue(async () =>
            {
                await ShowIncomingCallDialogAsync(e);
            });
        }

        private async Task ShowIncomingCallDialogAsync(IncomingCallEventArgs e)
        {
            if (_incomingCallDialogShowing)
                return;

            if (ContentFrame?.XamlRoot == null)
                return;

            _incomingCallDialogShowing = true;

            try
            {
                var callerName = !string.IsNullOrWhiteSpace(e.FromDisplayName)
                    ? e.FromDisplayName
                    : (!string.IsNullOrWhiteSpace(e.FromUsername) ? e.FromUsername : $"User {e.FromUserId}");

                var body = new StackPanel
                {
                    Spacing = 10
                };

                body.Children.Add(new TextBlock
                {
                    Text = "Incoming Call",
                    FontSize = 20,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
                });

                body.Children.Add(new TextBlock
                {
                    Text = $"From: {callerName}",
                    TextWrapping = TextWrapping.Wrap
                });

                body.Children.Add(new TextBlock
                {
                    Text = "Accept to open the call page and join the call, or decline to reject it.",
                    TextWrapping = TextWrapping.Wrap
                });

                var dialog = new ContentDialog
                {
                    Title = "Zink Call",
                    Content = body,
                    PrimaryButtonText = "Accept",
                    CloseButtonText = "Decline",
                    DefaultButton = ContentDialogButton.Primary,
                    XamlRoot = ContentFrame.XamlRoot
                };

                var result = await dialog.ShowAsync();

                if (result == ContentDialogResult.Primary)
                {
                    try
                    {
                        await NativeCallCoordinator.Instance.AcceptIncomingAsync(e.FromUserId, e.CallId);

                        ContentFrame.Navigate(typeof(CallPage), new CallPageArgs
                        {
                            TargetUserId = e.FromUserId,
                            IsScreenShare = false
                        });
                    }
                    catch
                    {
                    }
                }
                else
                {
                    await SocialManager.Instance.Realtime.RejectCallAsync(e.FromUserId, e.CallId);
                }
            }
            catch
            {
            }
            finally
            {
                _incomingCallDialogShowing = false;
            }
        }

        public void ApplyAppTheme(ElementTheme theme)
        {
            try
            {
                RootGrid.RequestedTheme = theme;
            }
            catch { }
        }

        public IntPtr GetWindowHandle()
        {
            return WindowNative.GetWindowHandle(this);
        }

        public void HideToTray()
        {
            try
            {
                var hwnd = GetWindowHandle();
                ShowWindow(hwnd, SW_HIDE);
            }
            catch
            {
            }
        }

        public void ShowFromTray()
        {
            try
            {
                var hwnd = GetWindowHandle();
                ShowWindow(hwnd, SW_RESTORE);
                ShowWindow(hwnd, SW_SHOW);
                SetForegroundWindow(hwnd);
                this.Activate();
            }
            catch
            {
            }
        }

        private void ApplySavedThemeOnStartup()
        {
            try
            {
                var value = ApplicationData.Current.LocalSettings.Values["Zink.Theme"] as string ?? "Default";

                var theme = value switch
                {
                    "Light" => ElementTheme.Light,
                    "Dark" => ElementTheme.Dark,
                    _ => ElementTheme.Default
                };

                ApplyAppTheme(theme);
            }
            catch
            {
            }
        }

        private void TrySelectHomeItem()
        {
            try
            {
                var homeItem = FindNavItemByTag(SidebarNav.MenuItems, "Home");
                if (homeItem != null)
                    SidebarNav.SelectedItem = homeItem;
            }
            catch { }
        }

        private void ContentFrame_Loaded(object sender, RoutedEventArgs e)
        {
            _contentFrameLoaded = true;
            _loadedTcs.TrySetResult(true);
        }

        private void BeginWhatsNewDialogCheck()
        {
            DispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    await EnsureAndShowWhatsNewDialogAsync();
                }
                catch
                {
                }
            });
        }

        private async Task EnsureAndShowWhatsNewDialogAsync()
        {
            if (_hasCheckedWhatsNewDialog)
                return;

            await Task.Yield();

            const int maxWaitMs = 15000;

            var readyTask = Task.WhenAll(_activatedTcs.Task, _loadedTcs.Task);
            var completed = await Task.WhenAny(readyTask, Task.Delay(maxWaitMs));
            if (completed != readyTask)
                return;

            if (!_windowIsActivated || !_contentFrameLoaded || ContentFrame?.XamlRoot == null)
                return;

            const string LastVersionKey = "LastAppVersion";
            const string SuppressWhatsNewKey = "SuppressWhatsNewVersion";

            ApplicationDataContainer settings;
            try
            {
                settings = ApplicationData.Current.LocalSettings;
            }
            catch
            {
                return;
            }

            var vid = Package.Current.Id.Version;
            var currentVersion = $"{vid.Major}.{vid.Minor}.{vid.Build}.{vid.Revision}";

            var suppressedVersion = settings.Values[SuppressWhatsNewKey] as string;
            if (suppressedVersion == currentVersion)
            {
                settings.Values[LastVersionKey] = currentVersion;
                _hasCheckedWhatsNewDialog = true;
                return;
            }

            var shown = await ShowWhatsNewDialogAsync();
            if (shown)
            {
                settings.Values[LastVersionKey] = currentVersion;
                _hasCheckedWhatsNewDialog = true;
            }
        }

        private async Task<bool> ShowWhatsNewDialogAsync()
        {
            await _dialogGate.WaitAsync();
            try
            {
                ApplicationDataContainer settings;
                try
                {
                    settings = ApplicationData.Current.LocalSettings;
                }
                catch
                {
                    return false;
                }

                var vid = Package.Current.Id.Version;
                var currentVersion = $"{vid.Major}.{vid.Minor}.{vid.Build}.{vid.Revision}";

                var changelog = @"
Version 2.4.1.0 
- Added Twitch.tv to the social tab.
- Added twitch to the power tools section on the homedashboard page.
- Added a new customisation page.
- Added a new home dashboard customisation page where you can edit all of the things on the dashboard to your own liking
- Added an app customisation theme control page where you can change the theme of Zink from dark to light or from light to dark or even set it to your windows theme.
- Updated the leave a review page.
- Added a new pop up message for the video library page.
- Added a new pop up message for the music library page.
- Added a search button to the sidebar
- Added a search page after you click the search button to search on Zink.
- Added a photo viewer page to zink.";

                var textBlock = new TextBlock
                {
                    Text = changelog,
                    TextWrapping = TextWrapping.WrapWholeWords,
                    Margin = new Thickness(12, 0, 12, 12)
                };

                var scrollViewer = new ScrollViewer
                {
                    Content = textBlock,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollMode = ScrollMode.Disabled,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    Width = 440,
                    Height = 580
                };

                var dialogHost = new Grid
                {
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                dialogHost.Children.Add(scrollViewer);

                if (ContentFrame?.XamlRoot == null)
                    return false;

                var dialog = new ContentDialog
                {
                    Title = "Version Notes",
                    Content = dialogHost,
                    CloseButtonText = "OK",
                    PrimaryButtonText = "Don't show this message again",
                    DefaultButton = ContentDialogButton.Close,
                    XamlRoot = ContentFrame.XamlRoot
                };

                if (Microsoft.UI.Xaml.Application.Current.Resources.TryGetValue("AccentButtonStyle", out var styleObj) &&
                    styleObj is Style foundAccentStyle)
                {
                    dialog.CloseButtonStyle = foundAccentStyle;
                }

                var result = await dialog.ShowAsync();
                if (result == ContentDialogResult.Primary)
                {
                    settings.Values["SuppressWhatsNewVersion"] = currentVersion;
                }

                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                try { _dialogGate.Release(); } catch { }
            }
        }

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(nint hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(nint hWnd);

        private const int SW_HIDE = 0;
        private const int SW_SHOW = 5;
        private const int SW_RESTORE = 9;
        private const int SW_MAXIMIZE = 3;

        private void MaximizeWindow()
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            ShowWindow(hwnd, SW_MAXIMIZE);
        }

        private void SetWindowIcon()
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            var appWindow = AppWindow.GetFromWindowId(windowId);
            var iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "Zink.ico");
            if (File.Exists(iconPath))
                appWindow.SetIcon(iconPath);
        }

        public NavigationView SidebarNavReference => SidebarNav;
        public ColumnDefinition SidebarColumnReference => SidebarColumn;
        public Frame MainFrame => ContentFrame;

        public void SaveAndHideSidebar()
        {
            if (_savedSidebarStateExists) return;

            _savedSidebarVisible = SidebarNav.IsPaneVisible;
            _savedPaneOpen = SidebarNav.IsPaneOpen;
            _savedPaneDisplayMode = SidebarNav.PaneDisplayMode;
            _savedSidebarWidth = SidebarColumn.Width;

            _savedSidebarStateExists = true;

            DispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    SidebarNav.IsPaneVisible = false;
                    SidebarNav.Visibility = Visibility.Collapsed;
                    SidebarColumn.Width = new GridLength(0);
                }
                catch { }
            });
        }

        public void RestoreSavedSidebar()
        {
            if (!_savedSidebarStateExists) return;

            DispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    SidebarNav.IsPaneVisible = _savedSidebarVisible;
                    SidebarNav.IsPaneOpen = _savedPaneOpen;
                    SidebarNav.PaneDisplayMode = _savedPaneDisplayMode;
                    SidebarColumn.Width = _savedSidebarWidth;
                    SidebarNav.Visibility = _savedSidebarVisible ? Visibility.Visible : Visibility.Collapsed;
                }
                catch { }
                finally
                {
                    _savedSidebarStateExists = false;
                }
            });
        }

        public void EnterFullscreenMode()
        {
            if (_weAreInFullscreenMode) return;

            SaveAndHideSidebar();

            DispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                    var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
                    var appWindow = AppWindow.GetFromWindowId(windowId);
                    appWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
                    _weAreInFullscreenMode = true;

                    StartFullscreenMonitor(appWindow);
                }
                catch { }
            });
        }

        public void ExitFullscreenMode()
        {
            if (!_weAreInFullscreenMode)
            {
                RestoreSavedSidebar();
                return;
            }

            DispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                    var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
                    var appWindow = AppWindow.GetFromWindowId(windowId);
                    appWindow.SetPresenter(AppWindowPresenterKind.Overlapped);
                    _weAreInFullscreenMode = false;

                    StopFullscreenMonitor();
                }
                catch { }
                finally
                {
                    RestoreSavedSidebar();
                }
            });
        }

        public void RestoreSidebar()
        {
            SidebarColumn.Width = new GridLength(200);
            SidebarNav.PaneDisplayMode = NavigationViewPaneDisplayMode.Left;
            SidebarNav.IsPaneOpen = true;
            SidebarNav.IsPaneVisible = true;
            SidebarNav.Visibility = Visibility.Visible;
            _savedSidebarStateExists = false;
            _weAreInFullscreenMode = false;
            StopFullscreenMonitor();
        }

        public void SetSidebarVisibility(bool visible)
        {
            SidebarNav.IsPaneVisible = visible;
            SidebarNav.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            SidebarColumn.Width = new GridLength(visible ? 200 : 0);
        }

        private void StartFullscreenMonitor(AppWindow appWindow)
        {
            lock (_fullscreenMonitorLock)
            {
                StopFullscreenMonitor();

                _fullscreenMonitorTimer = new Timer(_ =>
                {
                    try
                    {
                        DispatcherQueue.TryEnqueue(() =>
                        {
                            try
                            {
                                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                                var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
                                var currentAppWindow = AppWindow.GetFromWindowId(windowId);
                                if (currentAppWindow is null) return;

                                var presenterKind = currentAppWindow.Presenter?.Kind;
                                if (_weAreInFullscreenMode && presenterKind != AppWindowPresenterKind.FullScreen)
                                {
                                    _weAreInFullscreenMode = false;
                                    StopFullscreenMonitor();
                                    RestoreSavedSidebar();
                                }
                            }
                            catch { }
                        });
                    }
                    catch { }
                }, null, 250, 250);
            }
        }

        private void StopFullscreenMonitor()
        {
            lock (_fullscreenMonitorLock)
            {
                try
                {
                    _fullscreenMonitorTimer?.Dispose();
                }
                catch { }
                finally
                {
                    _fullscreenMonitorTimer = null;
                }
            }
        }

        private void SidebarNav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
        {
            if (args.SelectedItem is not NavigationViewItem item)
                return;

            var targetPage = item.Tag switch
            {
                "Search" => typeof(SearchResultsPage),
                "Home" => typeof(HomeDashboardPage),
                "MusicPlayer" => typeof(MusicPlayerPage),
                "MusicLibrary" => typeof(MusicLibraryPage),
                "YouTubeMusic" => typeof(YouTubeMusicPage),
                "AmazonMusic" => typeof(AmazonMusicPage),
                "Equalizer" => typeof(EqualizerPage),
                "Visualizer" => typeof(VisualizerPage),
                "VideoPlayer" => typeof(VideoPlayerPage),
                "VideoLibrary" => typeof(VideoLibraryPage),
                "ScreenRecorder" => typeof(RecorderPage),
                "PhotoViewer" => typeof(PhotoViewerPage),
                "Netflix" => typeof(NetflixPage),
                "PrimeVideo" => typeof(PrimeVideoPage),
                "DisneyPlus" => typeof(DisneyPlusPage),
                "ParamountPlus" => typeof(ParamountPlusPage),
                "NowTV" => typeof(NowTVPage),
                "BBCiPlayer" => typeof(BBCiPlayerPage),
                "My5" => typeof(My5Page),
                "Radio" => typeof(RadioPage),
                "Spotify" => typeof(SpotifyLoginPage),
                "SpotifyWidget" => typeof(SpotifyWidgetPage),
                "LikedRadioSongs" => typeof(LikedRadioSongsPage),
                "RadioWidget" => typeof(RadioWidgetPage),
                "Twitch" => typeof(TwitchPage),
                "Discord" => typeof(DiscordPage),
                "YouTube" => typeof(YouTubePage),
                "TikTok" => typeof(TikTokPage),
                "Instagram" => typeof(InstagramPage),
                "X" => typeof(XPage),
                "Facebook" => typeof(FacebookPage),
                "Telegram" => typeof(TelegramPage),
                "WhatsApp" => typeof(WhatsAppPage),
                "Messenger" => typeof(MessengerPage),
                "LinkedIn" => typeof(LinkedInPage),
                "Threads" => typeof(ThreadsPage),
                "Bluesky" => typeof(BlueskyPage),
                "Mastodon" => typeof(MastodonPage),
                "Pinterest" => typeof(PinterestPage),
                "Tumblr" => typeof(TumblrPage),
                "Reddit" => typeof(RedditPage),
                "nativevoiceshare" => typeof(NativeVoiceSharePage),
                "SocialLogin" => typeof(LoginPage),
                "SocialRegister" => typeof(RegisterPage),
                "SocialFriends" => typeof(FriendsPage),
                "SocialFriendRequests" => typeof(FriendRequestsPage),
                "SocialProfile" => typeof(ProfilePage),
                "SocialCall" => typeof(CallPage),
                "Xbox" => typeof(XboxPage),
                "GeForceNow" => typeof(GeForceNowPage),
                "AmazonLuna" => typeof(AmazonLunaPage),
                "Boosteroid" => typeof(BoosteroidPage),
                "ShadowPC" => typeof(ShadowPCPage),
                "Notifications" => typeof(NotificationsPage),
                "Feedback" => typeof(FeedbackPage),
                "PrivacyPolicy" => typeof(PrivacyPolicyPage),
                "LeaveReview" => typeof(ReviewPage),
                "AppCustomization" => typeof(AppCustomizationPage),
                "Settings" => typeof(SettingsPage),
                "About" => typeof(AboutPage),
                _ => null
            };

            if (targetPage is not null && ContentFrame.CurrentSourcePageType != targetPage)
            {
                ContentFrame.Navigate(targetPage);
            }
        }

        private bool TrySetDesktopAcrylicBackdrop()
        {
            if (!DesktopAcrylicController.IsSupported())
                return false;

            _backdropConfig = new SystemBackdropConfiguration
            {
                IsInputActive = true
            };

            if (Content is FrameworkElement rootElement)
            {
                rootElement.ActualThemeChanged += RootElement_ActualThemeChanged;
                SetBackdropThemeFromRoot(rootElement);
            }

            _acrylicController = new DesktopAcrylicController();
            _acrylicController.AddSystemBackdropTarget(
                this.As<Microsoft.UI.Composition.ICompositionSupportsSystemBackdrop>());
            _acrylicController.SetSystemBackdropConfiguration(_backdropConfig);

            return true;
        }

        private void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
        {
            _windowIsActivated = args.WindowActivationState != WindowActivationState.Deactivated;

            if (_windowIsActivated)
                _activatedTcs.TrySetResult(true);

            if (_backdropConfig == null) return;

            _backdropConfig.IsInputActive =
                args.WindowActivationState != WindowActivationState.Deactivated;
        }

        private void MainWindow_Closed(object sender, WindowEventArgs args)
        {
            SocialManager.Instance.Realtime.IncomingCall -= Realtime_IncomingCall_Global;

            if (_acrylicController != null)
            {
                _acrylicController.Dispose();
                _acrylicController = null;
            }

            _backdropConfig = null;
        }

        private void RootElement_ActualThemeChanged(FrameworkElement sender, object args)
        {
            SetBackdropThemeFromRoot(sender);
        }

        private void SetBackdropThemeFromRoot(FrameworkElement root)
        {
            if (_backdropConfig == null) return;

            _backdropConfig.Theme = root.ActualTheme switch
            {
                ElementTheme.Dark => SystemBackdropTheme.Dark,
                ElementTheme.Light => SystemBackdropTheme.Light,
                _ => SystemBackdropTheme.Default
            };
        }

        private void ContentFrame_Navigated(object sender, NavigationEventArgs e)
        {
            try
            {
                var t = e?.SourcePageType;
                if (t == null) return;

                try
                {
                    var tag = GetTagForPageType(t);
                    if (!string.IsNullOrWhiteSpace(tag))
                    {
                        var match = FindNavItemByTag(SidebarNav.MenuItems, tag)
                                 ?? FindNavItemByTag(SidebarNav.FooterMenuItems, tag);

                        if (match != null)
                            SidebarNav.SelectedItem = match;
                    }
                }
                catch { }

                var fullName = t.FullName ?? t.Name;
                var shortName = t.Name ?? fullName;

                var title = shortName.EndsWith("Page", StringComparison.OrdinalIgnoreCase)
                    ? shortName.Substring(0, shortName.Length - 4)
                    : shortName;

                if (!string.IsNullOrWhiteSpace(fullName) &&
                    (fullName.StartsWith("Zink.Pages.", StringComparison.Ordinal) || fullName.StartsWith("Zink.", StringComparison.Ordinal)))
                {
                    ActivityHub.Record(
                        ActivityHub.ActivityKind.PageVisit,
                        title: $"Visited {title}",
                        subtitle: fullName,
                        payload: fullName,
                        watchedSeconds: 0,
                        listenedSeconds: 0,
                        imageUri: ""
                    );
                }
            }
            catch { }
        }

        private string GetTagForPageType(Type t)
        {
            try
            {
                if (t == typeof(SearchResultsPage)) return "Search";
                if (t == typeof(HomeDashboardPage)) return "Home";
                if (t == typeof(MusicPlayerPage)) return "MusicPlayer";
                if (t == typeof(MusicLibraryPage)) return "MusicLibrary";
                if (t == typeof(YouTubeMusicPage)) return "YouTubeMusic";
                if (t == typeof(AmazonMusicPage)) return "AmazonMusic";
                if (t == typeof(EqualizerPage)) return "Equalizer";
                if (t == typeof(VisualizerPage)) return "Visualizer";
                if (t == typeof(VideoPlayerPage)) return "VideoPlayer";
                if (t == typeof(VideoLibraryPage)) return "VideoLibrary";
                if (t == typeof(RecorderPage)) return "ScreenRecorder";
                if (t == typeof(PhotoViewerPage)) return "PhotoViewer";
                if (t == typeof(NetflixPage)) return "Netflix";
                if (t == typeof(PrimeVideoPage)) return "PrimeVideo";
                if (t == typeof(DisneyPlusPage)) return "DisneyPlus";
                if (t == typeof(ParamountPlusPage)) return "ParamountPlus";
                if (t == typeof(NowTVPage)) return "NowTV";
                if (t == typeof(BBCiPlayerPage)) return "BBCiPlayer";
                if (t == typeof(My5Page)) return "My5";
                if (t == typeof(RadioPage)) return "Radio";
                if (t == typeof(SpotifyLoginPage)) return "Spotify";
                if (t == typeof(SpotifyWidgetPage)) return "SpotifyWidget";
                if (t == typeof(LikedRadioSongsPage)) return "LikedRadioSongs";
                if (t == typeof(RadioWidgetPage)) return "RadioWidget";
                if (t == typeof(TwitchPage)) return "Twitch";
                if (t == typeof(DiscordPage)) return "Discord";
                if (t == typeof(YouTubePage)) return "YouTube";
                if (t == typeof(TikTokPage)) return "TikTok";
                if (t == typeof(InstagramPage)) return "Instagram";
                if (t == typeof(XPage)) return "X";
                if (t == typeof(FacebookPage)) return "Facebook";
                if (t == typeof(TelegramPage)) return "Telegram";
                if (t == typeof(WhatsAppPage)) return "WhatsApp";
                if (t == typeof(MessengerPage)) return "Messenger";
                if (t == typeof(LinkedInPage)) return "LinkedIn";
                if (t == typeof(ThreadsPage)) return "Threads";
                if (t == typeof(BlueskyPage)) return "Bluesky";
                if (t == typeof(MastodonPage)) return "Mastodon";
                if (t == typeof(PinterestPage)) return "Pinterest";
                if (t == typeof(TumblrPage)) return "Tumblr";
                if (t == typeof(RedditPage)) return "Reddit";
                if (t == typeof(NativeVoiceSharePage)) return "nativevoiceshare";
                if (t == typeof(LoginPage)) return "SocialLogin";
                if (t == typeof(RegisterPage)) return "SocialRegister";
                if (t == typeof(FriendsPage)) return "SocialFriends";
                if (t == typeof(FriendRequestsPage)) return "SocialFriendRequests";
                if (t == typeof(ProfilePage)) return "SocialProfile";
                if (t == typeof(CallPage)) return "SocialCall";
                if (t == typeof(XboxPage)) return "Xbox";
                if (t == typeof(GeForceNowPage)) return "GeForceNow";
                if (t == typeof(AmazonLunaPage)) return "AmazonLuna";
                if (t == typeof(BoosteroidPage)) return "Boosteroid";
                if (t == typeof(ShadowPCPage)) return "ShadowPC";
                if (t == typeof(NotificationsPage)) return "Notifications";
                if (t == typeof(FeedbackPage)) return "Feedback";
                if (t == typeof(PrivacyPolicyPage)) return "PrivacyPolicy";
                if (t == typeof(ReviewPage)) return "LeaveReview";
                if (t == typeof(AppCustomizationPage)) return "AppCustomization";
                if (t == typeof(SettingsPage)) return "Settings";
                if (t == typeof(AboutPage)) return "About";
                return "";
            }
            catch { return ""; }
        }

        private NavigationViewItem? FindNavItemByTag(System.Collections.Generic.IList<object> items, string tag)
        {
            try
            {
                foreach (var obj in items)
                {
                    if (obj is NavigationViewItem nvi)
                    {
                        if (string.Equals(nvi.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase))
                            return nvi;

                        if (nvi.MenuItems != null && nvi.MenuItems.Count > 0)
                        {
                            var nested = FindNavItemByTag(nvi.MenuItems, tag);
                            if (nested != null) return nested;
                        }
                    }
                }
            }
            catch { }

            return null;
        }
    }
}