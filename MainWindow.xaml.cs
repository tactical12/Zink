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
        private const double SidebarOpenWidth = 260;
        private const double SidebarCompactWidth = 104;
        private const double SidebarOpenPaneLength = 232;
        private const double SidebarCompactPaneLength = 76;

        private bool _hasShownUpdateDialog;
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
            SetSidebarColumnForPaneState(true);

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
                    "ScreenRecorder" or "FpsRecorder" or "Equalizer" or "Visualizer" => "Tools",
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
            return tag is "Discord" or "TikTok" or "Instagram" or "X" or "Facebook" or "Telegram" or "WhatsApp" or "Messenger" or "LinkedIn" or "Threads" or "Bluesky" or "Mastodon" or "Pinterest" or "Tumblr" or "Reddit" or "SocialLogin" or "SocialRegister" or "SocialFriends" or "SocialMessages" or "SocialFriendRequests" or "SocialProfile";
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
