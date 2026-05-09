using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Globalization;
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
        private const double SidebarOpenWidth = 260;
        private const double SidebarCompactWidth = 104;
        private const double SidebarOpenPaneLength = 232;
        private const double SidebarCompactPaneLength = 76;

        private bool _isSidebarCompact;

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
        private static readonly global::Windows.UI.Color DefaultGlassTint =
            global::Windows.UI.Color.FromArgb(255, 63, 111, 127);
        private global::Windows.UI.Color _currentGlassTint = DefaultGlassTint;
        private ElementTheme _currentAppTheme = ElementTheme.Default;

        private bool _windowIsActivated = false;

        private bool _incomingCallDialogShowing = false;
        private bool _realtimeConnectAttempted = false;

        public bool AllowClose { get; set; }

        public MainWindow()
        {
            this.InitializeComponent();

            ApplySavedThemeOnStartup();
            ApplySavedGlassTintOnStartup();

            this.Activated += MainWindow_Activated;
            this.Closed += MainWindow_Closed;
            this.Activated += MainWindow_Activated_EnsureRealtime;

            TrySetDesktopAcrylicBackdrop();

            MaximizeWindow();
            SetWindowIcon();
            RegisterWindowClosingHandler();

            SidebarNav.PaneDisplayMode = NavigationViewPaneDisplayMode.Left;
            SidebarNav.IsPaneOpen = true;
            SetSidebarColumnForPaneState(true);

            ContentFrame.Navigated += ContentFrame_Navigated;
            ContentFrame.Navigate(typeof(HomeDashboardPage));

            TrySelectHomeItem();
            DispatcherQueue.TryEnqueue(ApplyGlassTintToCurrentPage);

            SocialManager.Instance.Realtime.IncomingCall -= Realtime_IncomingCall_Global;
            SocialManager.Instance.Realtime.IncomingCall += Realtime_IncomingCall_Global;

            _ = EnsureRealtimeConnectedIfLoggedInAsync();
        }

        private void RegisterWindowClosingHandler()
        {
            try
            {
                var hwnd = WindowNative.GetWindowHandle(this);
                var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
                var appWindow = AppWindow.GetFromWindowId(windowId);
                appWindow.Closing += MainWindow_AppWindowClosing;
            }
            catch
            {
            }
        }

        private void MainWindow_AppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
        {
            if (AllowClose)
                return;

            if (!BackgroundModePreferences.AreBackgroundNotificationsEnabled)
            {
                return;
            }

            if (IsActiveCallState(NativeCallCoordinator.Instance.CurrentSession.State))
                return;

            args.Cancel = true;
            HideToTray();
            _ = ZinkBackgroundModeService.Instance.ApplyAsync();
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
            IncomingCallRingtoneService.TryStart();
            DispatcherQueue.TryEnqueue(async () =>
            {
                await ShowIncomingCallDialogAsync(e);
            });
        }

        private async Task ShowIncomingCallDialogAsync(IncomingCallEventArgs e)
        {
            if (_incomingCallDialogShowing)
                return;

            if (RootGrid == null)
                return;

            _incomingCallDialogShowing = true;
            IncomingCallRingtoneService.TryStart();

            try
            {
                var callerName = !string.IsNullOrWhiteSpace(e.FromDisplayName)
                    ? e.FromDisplayName
                    : (!string.IsNullOrWhiteSpace(e.FromUsername) ? e.FromUsername : $"User {e.FromUserId}");

                var completion = new TaskCompletionSource<bool>();
                var body = CreateIncomingCallGlassContent(
                    callerName,
                    () =>
                    {
                        completion.TrySetResult(true);
                    },
                    () =>
                    {
                        completion.TrySetResult(false);
                    });

                var overlay = CreateIncomingCallOverlay(body);
                RootGrid.Children.Add(overlay);

                bool acceptedCall;
                try
                {
                    acceptedCall = await completion.Task;
                }
                finally
                {
                    RootGrid.Children.Remove(overlay);
                }

                IncomingCallRingtoneService.TryStop();

                if (acceptedCall)
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
                IncomingCallRingtoneService.TryStop();
                _incomingCallDialogShowing = false;
            }
        }

        private static Grid CreateIncomingCallOverlay(FrameworkElement body)
        {
            var overlay = new Grid
            {
                Background = new SolidColorBrush(global::Windows.UI.Color.FromArgb(92, 0, 0, 0)),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                Padding = new Thickness(24)
            };

            Grid.SetColumnSpan(overlay, 2);
            Canvas.SetZIndex(overlay, 10000);

            body.HorizontalAlignment = HorizontalAlignment.Center;
            body.VerticalAlignment = VerticalAlignment.Center;
            overlay.Children.Add(body);

            return overlay;
        }

        private static FrameworkElement CreateIncomingCallGlassContent(
            string callerName,
            Action accept,
            Action decline)
        {
            var callerInitial = string.IsNullOrWhiteSpace(callerName)
                ? "Z"
                : callerName.Trim()[0].ToString().ToUpperInvariant();

            var acrylicBrush = new AcrylicBrush
            {
                TintColor = global::Windows.UI.Color.FromArgb(255, 15, 23, 31),
                TintOpacity = 0.78,
                TintLuminosityOpacity = 0.58,
                FallbackColor = global::Windows.UI.Color.FromArgb(245, 15, 23, 31)
            };

            var card = new Border
            {
                Width = 420,
                MaxWidth = 460,
                Padding = new Thickness(24),
                CornerRadius = new CornerRadius(28),
                Background = acrylicBrush,
                BorderBrush = new SolidColorBrush(global::Windows.UI.Color.FromArgb(24, 255, 255, 255)),
                BorderThickness = new Thickness(1)
            };

            var root = new Grid
            {
                RowSpacing = 16
            };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var header = new Grid
            {
                ColumnSpacing = 16
            };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var avatar = new Grid
            {
                Width = 58,
                Height = 58
            };

            avatar.Children.Add(new Microsoft.UI.Xaml.Shapes.Ellipse
            {
                Fill = new LinearGradientBrush
                {
                    StartPoint = new Point(0, 0),
                    EndPoint = new Point(1, 1),
                    GradientStops =
                    {
                        new GradientStop { Color = global::Windows.UI.Color.FromArgb(255, 34, 211, 238), Offset = 0 },
                        new GradientStop { Color = global::Windows.UI.Color.FromArgb(255, 45, 212, 191), Offset = 0.52 },
                        new GradientStop { Color = global::Windows.UI.Color.FromArgb(255, 99, 102, 241), Offset = 1 }
                    }
                },
                Stroke = new SolidColorBrush(global::Windows.UI.Color.FromArgb(90, 255, 255, 255)),
                StrokeThickness = 1
            });

            avatar.Children.Add(new TextBlock
            {
                Text = callerInitial,
                FontSize = 24,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 255, 255, 255)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            });

            var titleStack = new StackPanel
            {
                Spacing = 6,
                VerticalAlignment = VerticalAlignment.Center
            };

            titleStack.Children.Add(new TextBlock
            {
                Text = "Incoming call",
                FontSize = 13,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(210, 188, 245, 255))
            });

            titleStack.Children.Add(new TextBlock
            {
                Text = callerName,
                FontSize = 26,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 255, 255, 255))
            });

            Grid.SetColumn(titleStack, 1);
            header.Children.Add(avatar);
            header.Children.Add(titleStack);

            var statusPill = new Border
            {
                Padding = new Thickness(14, 9, 14, 9),
                CornerRadius = new CornerRadius(16),
                Background = new SolidColorBrush(global::Windows.UI.Color.FromArgb(28, 255, 255, 255)),
                BorderBrush = new SolidColorBrush(global::Windows.UI.Color.FromArgb(30, 255, 255, 255)),
                BorderThickness = new Thickness(1)
            };

            var statusRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 10,
                VerticalAlignment = VerticalAlignment.Center
            };

            statusRow.Children.Add(new FontIcon
            {
                Glyph = "\uE717",
                FontFamily = new FontFamily("Segoe Fluent Icons"),
                FontSize = 18,
                Width = 22,
                Height = 22,
                Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 125, 249, 196))
            });

            statusRow.Children.Add(new TextBlock
            {
                Text = "Ringing now on Zink",
                FontSize = 15,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(235, 255, 255, 255)),
                VerticalAlignment = VerticalAlignment.Center
            });

            statusPill.Child = statusRow;

            var actionGrid = new Grid
            {
                ColumnSpacing = 12
            };
            actionGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            actionGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var answerButton = CreateIncomingCallActionButton(
                "\uE717",
                "Answer",
                new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 21, 132, 151)),
                new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 255, 255, 255)));
            answerButton.Click += (_, _) => accept();

            var declineButton = CreateIncomingCallActionButton(
                "\uE711",
                "Decline",
                new SolidColorBrush(global::Windows.UI.Color.FromArgb(48, 255, 255, 255)),
                new SolidColorBrush(global::Windows.UI.Color.FromArgb(238, 255, 255, 255)));
            declineButton.Click += (_, _) => decline();

            Grid.SetColumn(declineButton, 1);
            actionGrid.Children.Add(answerButton);
            actionGrid.Children.Add(declineButton);

            Grid.SetRow(header, 0);
            Grid.SetRow(statusPill, 1);
            Grid.SetRow(actionGrid, 2);

            root.Children.Add(header);
            root.Children.Add(statusPill);
            root.Children.Add(actionGrid);
            card.Child = root;

            return card;
        }

        private static Button CreateIncomingCallActionButton(
            string glyph,
            string text,
            Brush background,
            Brush foreground)
        {
            var content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            content.Children.Add(new FontIcon
            {
                Glyph = glyph,
                FontFamily = new FontFamily("Segoe Fluent Icons"),
                FontSize = 17,
                Foreground = foreground
            });

            content.Children.Add(new TextBlock
            {
                Text = text,
                FontSize = 15,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = foreground
            });

            return new Button
            {
                Height = 48,
                Padding = new Thickness(16, 0, 16, 0),
                CornerRadius = new CornerRadius(16),
                Background = background,
                BorderBrush = new SolidColorBrush(global::Windows.UI.Color.FromArgb(28, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                Foreground = foreground,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Content = content
            };
        }

        public void ApplyAppTheme(ElementTheme theme)
        {
            _currentAppTheme = theme;

            try
            {
                ApplicationData.Current.LocalSettings.Values["Zink.Theme"] = theme switch
                {
                    ElementTheme.Light => "Light",
                    ElementTheme.Dark => "Dark",
                    _ => "Default"
                };
            }
            catch { }

            try
            {
                if (RootGrid != null)
                    RootGrid.RequestedTheme = theme;

                if (ContentFrame != null)
                    ContentFrame.RequestedTheme = theme;

                if (ContentFrame?.Content is FrameworkElement content)
                    content.RequestedTheme = theme;
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

                _currentAppTheme = theme;
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
                    SidebarColumn.Width = _savedSidebarVisible
                        ? (_savedSidebarWidth.Value > 0 ? _savedSidebarWidth : new GridLength(_savedPaneOpen ? SidebarOpenWidth : SidebarCompactWidth))
                        : new GridLength(0);
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
            SidebarNav.PaneDisplayMode = NavigationViewPaneDisplayMode.Left;
            SidebarNav.IsPaneOpen = true;
            SidebarNav.IsPaneVisible = true;
            SidebarNav.Visibility = Visibility.Visible;
            _isSidebarCompact = false;
            SetSidebarColumnForPaneState(true);
            _savedSidebarStateExists = false;
            _weAreInFullscreenMode = false;
            StopFullscreenMonitor();
        }

        public void SetSidebarVisibility(bool visible)
        {
            SidebarNav.IsPaneVisible = visible;
            SidebarNav.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            SidebarColumn.Width = visible
                ? new GridLength(SidebarNav.IsPaneOpen ? SidebarOpenWidth : SidebarCompactWidth)
                : new GridLength(0);
        }

        private void SidebarNav_PaneOpening(NavigationView sender, object args)
        {
            SidebarNav.IsPaneOpen = true;
            SetSidebarColumnForPaneState(!_isSidebarCompact);
        }

        private void SidebarNav_PaneOpened(NavigationView sender, object args)
        {
            SidebarNav.IsPaneOpen = true;
            SetSidebarColumnForPaneState(!_isSidebarCompact);
        }

        private void SidebarNav_PaneClosing(NavigationView sender, NavigationViewPaneClosingEventArgs args)
        {
            args.Cancel = true;
            SidebarNav.IsPaneOpen = true;
            SetSidebarColumnForPaneState(!_isSidebarCompact);
        }

        private void SidebarNav_PaneClosed(NavigationView sender, object args)
        {
            SidebarNav.IsPaneOpen = true;
            SetSidebarColumnForPaneState(!_isSidebarCompact);
        }

        private void SidebarToggleButton_Click(object sender, RoutedEventArgs e)
        {
            _isSidebarCompact = !_isSidebarCompact;
            SidebarNav.IsPaneOpen = true;
            SetSidebarColumnForPaneState(!_isSidebarCompact);
        }

        private void SetSidebarColumnForPaneState(bool isOpen)
        {
            try
            {
                if (SidebarNav.Visibility != Visibility.Visible || !SidebarNav.IsPaneVisible)
                {
                    SidebarColumn.Width = new GridLength(0);
                    return;
                }

                SidebarNav.IsPaneOpen = true;
                SidebarNav.OpenPaneLength = isOpen ? SidebarOpenPaneLength : SidebarCompactPaneLength;
                SidebarColumn.Width = new GridLength(isOpen ? SidebarOpenWidth : SidebarCompactWidth);
            }
            catch { }
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
                "ZinkConnect" => typeof(ZinkConnectPage),
                "MusicPlayer" => typeof(MusicPlayerPage),
                "MusicLibrary" => typeof(MusicLibraryPage),
                "YouTubeMusic" => typeof(YouTubeMusicPage),
                "AmazonMusic" => typeof(AmazonMusicPage),
                "Equalizer" => typeof(EqualizerPage),
                "Visualizer" => typeof(VisualizerPage),
                "VideoPlayer" => typeof(VideoPlayerPage),
                "VideoLibrary" => typeof(VideoLibraryPage),
                "ScreenRecorder" => typeof(RecorderPage),
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
                "SocialLogin" => typeof(LoginPage),
                "SocialRegister" => typeof(RegisterPage),
                "SocialDeveloperSettings" => typeof(DeveloperSettingsPage),
                "SocialFriends" => typeof(FriendsPage),
                "SocialMessages" => typeof(MessagesPage),
                "SocialFriendRequests" => typeof(FriendRequestsPage),
                "SocialProfile" => typeof(ProfilePage),
                "SocialCall" => typeof(CallPage),
                "Xbox" => typeof(XboxPage),
                "FpsRecorder" => typeof(FpsRecorderPage),
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

        public void ApplyGlassTint(global::Windows.UI.Color tint)
        {
            ApplyGlassTint(tint, true);
        }

        public void ApplyGlassTint(global::Windows.UI.Color tint, bool tintCurrentPage)
        {
            _currentGlassTint = tint;

            try
            {
                ApplicationData.Current.LocalSettings.Values["Zink.GlassTint"] = ColorToHex(tint);
            }
            catch { }

            ApplyGlassTintToResources(tint, tintCurrentPage);
        }

        private void ApplySavedGlassTintOnStartup()
        {
            try
            {
                var saved = ApplicationData.Current.LocalSettings.Values["Zink.GlassTint"] as string;
                var tint = TryParseHexColor(saved, out var savedTint)
                    ? savedTint
                    : DefaultGlassTint;

                _currentGlassTint = tint;
                ApplyGlassTintToResources(tint, true);
            }
            catch { }
        }

        private void ApplyGlassTintToResources(global::Windows.UI.Color tint, bool tintCurrentPage)
        {
            var panel = WithAlpha(tint, 170);
            var card = WithAlpha(tint, 130);
            var hover = WithAlpha(Lighten(tint, 55), 44);
            var selected = WithAlpha(Lighten(tint, 72), 68);
            var pressed = WithAlpha(Lighten(tint, 48), 56);
            var primaryText = GetTintedTextColor(tint, TextBrushRole.Primary);
            var mutedText = GetTintedTextColor(tint, TextBrushRole.Muted);

            SetBrushColor("ZinkGlassPanelBrush", panel);
            SetBrushColor("ZinkGlassCardBrush", card);
            SetBrushColor("ZinkGlassBorderBrush", WithAlpha(Lighten(tint, 90), 72));
            SetBrushColor("ZinkGlassHoverBrush", hover);
            SetBrushColor("ZinkGlassSelectedBrush", selected);
            SetBrushColor("NavigationViewItemBackgroundPointerOver", hover);
            SetBrushColor("NavigationViewItemBackgroundSelected", selected);
            SetBrushColor("NavigationViewItemBackgroundPressed", pressed);
            SetBrushColor("NavigationViewItemForeground", mutedText);
            SetBrushColor("NavigationViewItemForegroundPointerOver", primaryText);
            SetBrushColor("NavigationViewItemForegroundSelected", primaryText);
            SetBrushColor("NavigationViewItemIconForeground", mutedText);
            SetBrushColor("NavigationViewItemIconForegroundPointerOver", primaryText);
            SetBrushColor("NavigationViewItemIconForegroundSelected", primaryText);

            ShellGradientStart.Color = Darken(tint, 110);
            ShellGradientMiddle.Color = WithAlpha(Darken(tint, 52), 255);

            if (tintCurrentPage)
            {
                ApplyGlassTintToCurrentPage();
            }
        }

        private void ApplyGlassTintToCurrentPage()
        {
            try
            {
                if (ContentFrame?.Content is FrameworkElement content)
                {
                    content.RequestedTheme = _currentAppTheme;
                    content.Loaded -= CurrentPage_Loaded;
                    content.Loaded += CurrentPage_Loaded;

                    ApplyGlassTintToElementTree(content, _currentGlassTint);
                    QueueGlassTintPass(content, 3);
                }
            }
            catch { }
        }

        private void CurrentPage_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                if (sender is FrameworkElement content)
                {
                    content.RequestedTheme = _currentAppTheme;
                    ApplyGlassTintToElementTree(content, _currentGlassTint);
                    QueueGlassTintPass(content, 3);
                }
            }
            catch { }
        }

        private void QueueGlassTintPass(FrameworkElement content, int remainingPasses)
        {
            if (remainingPasses <= 0)
                return;

            DispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    ApplyGlassTintToElementTree(content, _currentGlassTint);
                    QueueGlassTintPass(content, remainingPasses - 1);
                }
                catch { }
            });
        }

        private void ApplyGlassTintToElementTree(DependencyObject element, global::Windows.UI.Color tint)
        {
            if (IsWebViewElement(element))
                return;

            if (element is FrameworkElement frameworkElement)
            {
                frameworkElement.RequestedTheme = _currentAppTheme;
                ApplyGlassTintToResourceDictionary(frameworkElement.Resources, tint);
                TintElementBrushes(frameworkElement, tint);
            }

            var childCount = VisualTreeHelper.GetChildrenCount(element);
            for (var i = 0; i < childCount; i++)
            {
                ApplyGlassTintToElementTree(VisualTreeHelper.GetChild(element, i), tint);
            }
        }

        private void ApplyGlassTintToResourceDictionary(ResourceDictionary resources, global::Windows.UI.Color tint)
        {
            SetResourceBrush(resources, "GlassPanelBrush", WithAlpha(tint, 176), tint);
            SetResourceBrush(resources, "GlassPanelStrongBrush", WithAlpha(Darken(tint, 12), 206), Darken(tint, 12));
            SetResourceBrush(resources, "GlassPanelBorderBrush", WithAlpha(Lighten(tint, 98), 74), Lighten(tint, 98));
            SetResourceBrush(resources, "GlassStrongBrush", WithAlpha(Darken(tint, 28), 235), Darken(tint, 28));
            SetResourceBrush(resources, "GlassCardBrush", WithAlpha(tint, 138), tint);
            SetResourceBrush(resources, "GlassTileBrush", WithAlpha(tint, 108), tint);
            SetResourceBrush(resources, "GlassTileHoverBrush", WithAlpha(Lighten(tint, 42), 144), Lighten(tint, 42));
            SetResourceBrush(resources, "GlassBorderBrush", WithAlpha(Lighten(tint, 96), 72), Lighten(tint, 96));
            SetResourceBrush(resources, "GlassBorderStrongBrush", WithAlpha(Lighten(tint, 112), 108), Lighten(tint, 112));
            SetResourceBrush(resources, "ThemePanelBrush", WithAlpha(tint, 176), tint);
            SetResourceBrush(resources, "ThemeCardBrush", WithAlpha(tint, 130), tint);
            SetResourceTextBrush(resources, "MutedTextBrush", GetTintedTextColor(tint, TextBrushRole.Muted));
            SetResourceTextBrush(resources, "DimTextBrush", GetTintedTextColor(tint, TextBrushRole.Dim));
            SetResourceTextBrush(resources, "SubtleTextBrush", GetTintedTextColor(tint, TextBrushRole.Dim));
            SetResourceTextBrush(resources, "PrimaryTextBrush", GetTintedTextColor(tint, TextBrushRole.Primary));
        }

        private void TintElementBrushes(FrameworkElement element, global::Windows.UI.Color tint)
        {
            try
            {
                switch (element)
                {
                    case TextBlock textBlock:
                        textBlock.Foreground = TintTextBrush(textBlock.Foreground, tint, TextBrushRole.Primary);
                        break;
                    case RichTextBlock richTextBlock:
                        richTextBlock.Foreground = TintTextBrush(richTextBlock.Foreground, tint, TextBrushRole.Primary);
                        break;
                    case FontIcon fontIcon:
                        fontIcon.Foreground = TintTextBrush(fontIcon.Foreground, tint, TextBrushRole.Primary);
                        break;
                    case Grid grid:
                        grid.Background = TintBrush(grid.Background, tint, GlassBrushRole.Surface);
                        break;
                    case StackPanel stackPanel:
                        stackPanel.Background = TintBrush(stackPanel.Background, tint, GlassBrushRole.Surface);
                        break;
                    case Panel panel:
                        panel.Background = TintBrush(panel.Background, tint, GlassBrushRole.Surface);
                        break;
                    case Border border:
                        border.Background = TintBrush(border.Background, tint, GlassBrushRole.Surface);
                        border.BorderBrush = TintBrush(border.BorderBrush, tint, GlassBrushRole.Border);
                        break;
                    case Control control:
                        control.Background = TintBrush(control.Background, tint, GlassBrushRole.Control);
                        control.BorderBrush = TintBrush(control.BorderBrush, tint, GlassBrushRole.Border);
                        control.Foreground = TintTextBrush(control.Foreground, tint, TextBrushRole.Primary);
                        break;
                    case Microsoft.UI.Xaml.Shapes.Shape shape:
                        shape.Fill = TintBrush(shape.Fill, tint, GlassBrushRole.Surface);
                        shape.Stroke = TintBrush(shape.Stroke, tint, GlassBrushRole.Border);
                        break;
                }
            }
            catch { }
        }

        private enum GlassBrushRole
        {
            Surface,
            Control,
            Border
        }

        private enum TextBrushRole
        {
            Primary,
            Muted,
            Dim
        }

        private static Brush TintBrush(Brush brush, global::Windows.UI.Color tint, GlassBrushRole role)
        {
            try
            {
                switch (brush)
                {
                    case null:
                        return brush;
                    case SolidColorBrush solidBrush when solidBrush.Color.A == 0:
                        return brush;
                    case SolidColorBrush solidBrush when IsGlassLikeColor(solidBrush.Color):
                        solidBrush.Color = role switch
                        {
                            GlassBrushRole.Border => WithAlpha(Lighten(tint, 100), Math.Max((byte)48, solidBrush.Color.A)),
                            GlassBrushRole.Control => WithAlpha(Lighten(tint, 34), Math.Max((byte)45, solidBrush.Color.A)),
                            _ => WithAlpha(tint, Math.Max((byte)70, solidBrush.Color.A))
                        };
                        return brush;
                    case AcrylicBrush acrylicBrush:
                        acrylicBrush.TintColor = tint;
                        acrylicBrush.FallbackColor = WithAlpha(tint, Math.Max((byte)160, acrylicBrush.FallbackColor.A));
                        return brush;
                    case LinearGradientBrush gradientBrush:
                        TintGradientStops(gradientBrush, tint);
                        return brush;
                }
            }
            catch { }

            return brush;
        }

        private static Brush TintTextBrush(Brush brush, global::Windows.UI.Color tint, TextBrushRole fallbackRole)
        {
            try
            {
                if (brush is not SolidColorBrush solidBrush)
                    return new SolidColorBrush(GetTintedTextColor(tint, fallbackRole));

                if (solidBrush.Color.A == 0)
                    return brush;

                var role = IsTextLikeColor(solidBrush.Color)
                    ? GetTextRole(solidBrush.Color)
                    : fallbackRole;

                return new SolidColorBrush(GetTintedTextColor(tint, role));
            }
            catch { }

            return brush;
        }

        private static void TintGradientStops(LinearGradientBrush gradientBrush, global::Windows.UI.Color tint)
        {
            try
            {
                for (var i = 0; i < gradientBrush.GradientStops.Count; i++)
                {
                    var stop = gradientBrush.GradientStops[i];
                    if (!IsGlassLikeColor(stop.Color))
                        continue;

                    stop.Color = i switch
                    {
                        0 => WithAlpha(Darken(tint, 112), stop.Color.A),
                        1 => WithAlpha(Darken(tint, 52), stop.Color.A),
                        _ => WithAlpha(Darken(tint, 126), stop.Color.A)
                    };
                }
            }
            catch { }
        }

        private static bool IsGlassLikeColor(global::Windows.UI.Color color)
        {
            if (color.A == 0)
                return false;

            if (color.A < 255)
                return true;

            var max = Math.Max(color.R, Math.Max(color.G, color.B));
            var min = Math.Min(color.R, Math.Min(color.G, color.B));

            return max <= 64 || (max - min <= 24 && max >= 210);
        }

        private static bool IsTextLikeColor(global::Windows.UI.Color color)
        {
            if (color.A == 0)
                return false;

            var max = Math.Max(color.R, Math.Max(color.G, color.B));
            var min = Math.Min(color.R, Math.Min(color.G, color.B));

            return max >= 120 && max - min <= 70;
        }

        private static TextBrushRole GetTextRole(global::Windows.UI.Color color)
        {
            if (color.A < 170)
                return TextBrushRole.Dim;

            var max = Math.Max(color.R, Math.Max(color.G, color.B));
            if (max < 215 || color.A < 225)
                return TextBrushRole.Muted;

            return TextBrushRole.Primary;
        }

        private static global::Windows.UI.Color GetTintedTextColor(global::Windows.UI.Color tint, TextBrushRole role)
        {
            return role switch
            {
                TextBrushRole.Dim => WithAlpha(MixWithWhite(tint, 112), 165),
                TextBrushRole.Muted => WithAlpha(MixWithWhite(tint, 136), 220),
                _ => WithAlpha(MixWithWhite(tint, 168), 255)
            };
        }

        private static global::Windows.UI.Color MixWithWhite(global::Windows.UI.Color color, byte whiteAmount)
        {
            var keep = 255 - whiteAmount;
            return global::Windows.UI.Color.FromArgb(
                color.A,
                (byte)Math.Min(255, ((color.R * keep) + (255 * whiteAmount)) / 255),
                (byte)Math.Min(255, ((color.G * keep) + (255 * whiteAmount)) / 255),
                (byte)Math.Min(255, ((color.B * keep) + (255 * whiteAmount)) / 255));
        }

        private static void SetResourceBrush(
            ResourceDictionary resources,
            string key,
            global::Windows.UI.Color color,
            global::Windows.UI.Color tint)
        {
            try
            {
                if (!resources.TryGetValue(key, out var value))
                    return;

                if (value is SolidColorBrush solidBrush)
                {
                    solidBrush.Color = color;
                    return;
                }

                if (value is AcrylicBrush acrylicBrush)
                {
                    acrylicBrush.TintColor = tint;
                    acrylicBrush.FallbackColor = color;
                }
            }
            catch { }
        }

        private static void SetResourceTextBrush(
            ResourceDictionary resources,
            string key,
            global::Windows.UI.Color color)
        {
            try
            {
                if (resources.TryGetValue(key, out var value) &&
                    value is SolidColorBrush solidBrush)
                {
                    solidBrush.Color = color;
                }
            }
            catch { }
        }

        private static bool IsWebViewElement(DependencyObject root)
        {
            try
            {
                var typeName = root.GetType().FullName ?? root.GetType().Name;
                if (typeName.Contains("WebView", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            catch { }

            return false;
        }

        private void SetBrushColor(string resourceKey, global::Windows.UI.Color color)
        {
            try
            {
                if (Application.Current.Resources.TryGetValue(resourceKey, out var appValue) &&
                    appValue is SolidColorBrush appBrush)
                {
                    appBrush.Color = color;
                    return;
                }

                if (RootGrid.Resources.TryGetValue(resourceKey, out var shellValue) &&
                    shellValue is SolidColorBrush shellBrush)
                {
                    shellBrush.Color = color;
                }
            }
            catch { }
        }

        private static global::Windows.UI.Color WithAlpha(global::Windows.UI.Color color, byte alpha)
        {
            return global::Windows.UI.Color.FromArgb(alpha, color.R, color.G, color.B);
        }

        private static global::Windows.UI.Color Lighten(global::Windows.UI.Color color, byte amount)
        {
            return global::Windows.UI.Color.FromArgb(
                color.A,
                (byte)Math.Min(255, color.R + amount),
                (byte)Math.Min(255, color.G + amount),
                (byte)Math.Min(255, color.B + amount));
        }

        private static global::Windows.UI.Color Darken(global::Windows.UI.Color color, byte amount)
        {
            return global::Windows.UI.Color.FromArgb(
                color.A,
                (byte)Math.Max(0, color.R - amount),
                (byte)Math.Max(0, color.G - amount),
                (byte)Math.Max(0, color.B - amount));
        }

        private static string ColorToHex(global::Windows.UI.Color color)
        {
            return $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        }

        private static bool TryParseHexColor(string? hex, out global::Windows.UI.Color color)
        {
            color = DefaultGlassTint;

            if (string.IsNullOrWhiteSpace(hex))
                return false;

            var value = hex.Trim().TrimStart('#');
            if (value.Length == 8)
                value = value.Substring(2);

            if (value.Length != 6)
                return false;

            if (!byte.TryParse(value.Substring(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var r) ||
                !byte.TryParse(value.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var g) ||
                !byte.TryParse(value.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
            {
                return false;
            }

            color = global::Windows.UI.Color.FromArgb(255, r, g, b);
            return true;
        }

        private void ContentFrame_Navigated(object sender, NavigationEventArgs e)
        {
            try
            {
                var t = e?.SourcePageType;
                if (t == null) return;

                if (ContentFrame.Content is FrameworkElement content)
                {
                    content.RequestedTheme = _currentAppTheme;
                }

                ApplyGlassTintToCurrentPage();

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

                UpdateDiscordPresenceForPage(t, title);
            }
            catch { }
        }

        private void UpdateDiscordPresenceForPage(Type pageType, string fallbackTitle)
        {
            try
            {
                if (IsActiveCallState(NativeCallCoordinator.Instance.CurrentSession.State))
                    return;

                var tag = GetTagForPageType(pageType);
                var displayName = GetDiscordPresenceDisplayName(tag, fallbackTitle);

                if (string.Equals(tag, "Home", StringComparison.OrdinalIgnoreCase))
                {
                    DiscordPresenceService.Instance.SetAppPresence("Home dashboard");
                    return;
                }

                if (IsStreamingPresenceTag(tag))
                {
                    DiscordPresenceService.Instance.SetWebPresence(displayName, "Streaming", $"Browsing {displayName}");
                    return;
                }

                if (IsMusicPresenceTag(tag))
                {
                    DiscordPresenceService.Instance.SetPagePresence(displayName, "Music", "Browsing");
                    return;
                }

                if (IsSocialPresenceTag(tag))
                {
                    DiscordPresenceService.Instance.SetWebPresence(displayName, "Social", $"Browsing {displayName}");
                    return;
                }

                if (IsGamingPresenceTag(tag))
                {
                    DiscordPresenceService.Instance.SetWebPresence(displayName, "Cloud gaming", $"Launching {displayName}");
                    return;
                }

                if (string.Equals(tag, "SocialCall", StringComparison.OrdinalIgnoreCase))
                {
                    DiscordPresenceService.Instance.SetPagePresence("Zink Call", "Calls", "Opening");
                    return;
                }

                var category = tag switch
                {
                    "ScreenRecorder" or "FpsRecorder" or "Equalizer" or "Visualizer" or "ZinkConnect" => "Tools",
                    "Search" => "Search",
                    "Notifications" or "Feedback" or "PrivacyPolicy" or "LeaveReview" or "AppCustomization" or "Settings" or "About" => "App",
                    _ => "Zink"
                };

                DiscordPresenceService.Instance.SetPagePresence(displayName, category, "Using");
            }
            catch { }
        }

        private static bool IsActiveCallState(NativeCallState state)
        {
            return state == NativeCallState.Calling ||
                   state == NativeCallState.Incoming ||
                   state == NativeCallState.Accepted ||
                   state == NativeCallState.Negotiating ||
                   state == NativeCallState.Connected;
        }

        private static bool IsStreamingPresenceTag(string tag)
        {
            return tag is "Netflix" or "PrimeVideo" or "DisneyPlus" or "ParamountPlus" or "NowTV" or "BBCiPlayer" or "My5" or "YouTube" or "Twitch";
        }

        private static bool IsMusicPresenceTag(string tag)
        {
            return tag is "MusicPlayer" or "MusicLibrary" or "YouTubeMusic" or "AmazonMusic" or "Radio" or "Spotify" or "SpotifyWidget" or "LikedRadioSongs" or "RadioWidget";
        }

        private static bool IsSocialPresenceTag(string tag)
        {
            return tag is "Discord" or "TikTok" or "Instagram" or "X" or "Facebook" or "Telegram" or "WhatsApp" or "Messenger" or "LinkedIn" or "Threads" or "Bluesky" or "Mastodon" or "Pinterest" or "Tumblr" or "Reddit" or "SocialLogin" or "SocialRegister" or "SocialDeveloperSettings" or "SocialFriends" or "SocialMessages" or "SocialFriendRequests" or "SocialProfile";
        }

        private static bool IsGamingPresenceTag(string tag)
        {
            return tag is "Xbox" or "FpsRecorder" or "GeForceNow" or "AmazonLuna" or "Boosteroid" or "ShadowPC";
        }

        private static string GetDiscordPresenceDisplayName(string tag, string fallbackTitle)
        {
            if (string.IsNullOrWhiteSpace(tag))
                return MakeFriendlyTitle(fallbackTitle);

            return tag switch
            {
                "Home" => "Home dashboard",
                "ZinkConnect" => "Zink Connect",
                "MusicPlayer" => "Music player",
                "MusicLibrary" => "Music library",
                "YouTubeMusic" => "YouTube Music",
                "AmazonMusic" => "Amazon Music",
                "VideoPlayer" => "Video player",
                "VideoLibrary" => "Video library",
                "ScreenRecorder" => "Screen recorder",
                "FpsRecorder" => "FPS recorder",
                "PrimeVideo" => "Prime Video",
                "DisneyPlus" => "Disney+",
                "ParamountPlus" => "Paramount+",
                "NowTV" => "NOW",
                "BBCiPlayer" => "BBC iPlayer",
                "SpotifyWidget" => "Spotify widget",
                "LikedRadioSongs" => "Liked radio songs",
                "RadioWidget" => "Radio widget",
                "SocialLogin" => "Zink Social login",
                "SocialRegister" => "Zink Social signup",
                "SocialDeveloperSettings" => "Developer settings",
                "SocialFriends" => "Friends",
                "SocialMessages" => "Messages",
                "SocialFriendRequests" => "Friend requests",
                "SocialProfile" => "Profile",
                "SocialCall" => "Zink Call",
                "GeForceNow" => "GeForce NOW",
                "AmazonLuna" => "Amazon Luna",
                "ShadowPC" => "Shadow PC",
                "PrivacyPolicy" => "Privacy policy",
                "LeaveReview" => "Leave a review",
                "AppCustomization" => "App customization",
                _ => MakeFriendlyTitle(tag)
            };
        }

        private static string MakeFriendlyTitle(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "Zink";

            var clean = value.EndsWith("Page", StringComparison.OrdinalIgnoreCase)
                ? value.Substring(0, value.Length - 4)
                : value;

            var builder = new System.Text.StringBuilder(clean.Length + 8);
            for (var i = 0; i < clean.Length; i++)
            {
                var current = clean[i];
                if (i > 0 &&
                    char.IsUpper(current) &&
                    !char.IsWhiteSpace(clean[i - 1]) &&
                    !char.IsUpper(clean[i - 1]))
                {
                    builder.Append(' ');
                }

                builder.Append(current);
            }

            return builder.ToString();
        }

        private string GetTagForPageType(Type t)
        {
            try
            {
                if (t == typeof(SearchResultsPage)) return "Search";
                if (t == typeof(HomeDashboardPage)) return "Home";
                if (t == typeof(ZinkConnectPage)) return "ZinkConnect";
                if (t == typeof(MusicPlayerPage)) return "MusicPlayer";
                if (t == typeof(MusicLibraryPage)) return "MusicLibrary";
                if (t == typeof(YouTubeMusicPage)) return "YouTubeMusic";
                if (t == typeof(AmazonMusicPage)) return "AmazonMusic";
                if (t == typeof(EqualizerPage)) return "Equalizer";
                if (t == typeof(VisualizerPage)) return "Visualizer";
                if (t == typeof(VideoPlayerPage)) return "VideoPlayer";
                if (t == typeof(VideoLibraryPage)) return "VideoLibrary";
                if (t == typeof(RecorderPage)) return "ScreenRecorder";
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
                if (t == typeof(LoginPage)) return "SocialLogin";
                if (t == typeof(RegisterPage)) return "SocialRegister";
                if (t == typeof(DeveloperSettingsPage)) return "SocialDeveloperSettings";
                if (t == typeof(FriendsPage)) return "SocialFriends";
                if (t == typeof(MessagesPage)) return "SocialMessages";
                if (t == typeof(FriendRequestsPage)) return "SocialFriendRequests";
                if (t == typeof(ProfilePage)) return "SocialProfile";
                if (t == typeof(CallPage)) return "SocialCall";
                if (t == typeof(XboxPage)) return "Xbox";
                if (t == typeof(FpsRecorderPage)) return "FpsRecorder";
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
