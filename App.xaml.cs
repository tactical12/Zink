using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Controls;
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Windows.Media.Playback;
using Windows.Storage;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Microsoft.Windows.AppNotifications;
using AppLifecycleInstance = Microsoft.Windows.AppLifecycle.AppInstance;

using Zink.Services;

namespace Zink
{
    public partial class App : Application
    {
        public static MainWindow MainWindow { get; private set; }

        private TrayIconService _trayService;
        private bool _exitRequested;

        private Task? _replayStartupTask;
        private bool _replayReady;
        private string? _replayStartupError;

        private DateTimeOffset? _replayBufferStartedAtUtc;
        private static readonly TimeSpan RequiredReplayBufferDuration = TimeSpan.FromSeconds(45);

        public App()
        {
            this.InitializeComponent();

            try
            {
                AppNotificationManager.Default.Register();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("AppNotification Register FAILED: " + ex);
            }

            this.UnhandledException += (s, e) =>
            {
                try
                {
                    File.WriteAllText("CrashLog.txt", e.Exception.Message + "\n" + e.Exception.StackTrace);
                }
                catch { }
                e.Handled = true;
            };
        }

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(nint hWnd, int nCmdShow);

        private const int SW_MAXIMIZE = 3;

        protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            _ = InitializeAppServicesAsync();
            _ = EnsureStartupTaskEnabledAsync();

            SetupTray();

            StorageFile fileToOpen = null;
            bool launchedFromStartupTask = IsStartupTaskLaunch();

            try
            {
                var activatedArgs = AppLifecycleInstance.GetCurrent().GetActivatedEventArgs();
                if (activatedArgs != null && activatedArgs.Kind == Microsoft.Windows.AppLifecycle.ExtendedActivationKind.File)
                {
                    if (activatedArgs.Data is IFileActivatedEventArgs fileArgs &&
                        fileArgs.Files != null &&
                        fileArgs.Files.Count > 0 &&
                        fileArgs.Files[0] is StorageFile sf)
                    {
                        fileToOpen = sf;
                    }
                }
            }
            catch { }

            if (fileToOpen != null)
            {
                MainWindow = new MainWindow();
                MainWindow.Closed += OnMainWindowClosed;
                MainWindow.Activate();

                _ = VideoLibraryService.Current.StartAsync(TimeSpan.FromSeconds(30));
                _replayStartupTask = DelayedReplayStartupAsync(TimeSpan.FromSeconds(2));

                var frame = GetRootFrame();
                frame?.Navigate(typeof(VideoPlayerPage), fileToOpen);
                return;
            }

            if (launchedFromStartupTask)
            {
                MainWindow = new MainWindow();
                MainWindow.Closed += OnMainWindowClosed;
                MainWindow.Activate();

                MainWindow.DispatcherQueue.TryEnqueue(() =>
                {
                    try
                    {
                        MainWindow.HideToTray();
                    }
                    catch { }
                });

                _ = VideoLibraryService.Current.StartAsync(TimeSpan.FromSeconds(30));
                _replayStartupTask = DelayedReplayStartupAsync(TimeSpan.FromSeconds(25));
                return;
            }

            var splashWindow = new Window();
            splashWindow.Content = new SplashPage();
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(splashWindow);
            ShowWindow(hwnd, SW_MAXIMIZE);
            splashWindow.Activate();

            Task.Run(async () =>
            {
                await Task.Delay(2000);
                splashWindow.DispatcherQueue.TryEnqueue(() =>
                {
                    MainWindow = new MainWindow();
                    MainWindow.Closed += OnMainWindowClosed;
                    MainWindow.Activate();

                    _ = VideoLibraryService.Current.StartAsync(TimeSpan.FromSeconds(30));
                    _replayStartupTask = DelayedReplayStartupAsync(TimeSpan.FromSeconds(2));

                    splashWindow.Close();
                });
            });
        }

        private async Task DelayedReplayStartupAsync(TimeSpan delay)
        {
            try
            {
                await Task.Delay(delay);
                await StartReplayBufferAsync();
            }
            catch (Exception ex)
            {
                _replayStartupError = ex.Message;
                _replayReady = false;
                System.Diagnostics.Debug.WriteLine("Delayed replay startup failed: " + ex);
            }
        }

        public void NotifyReplayBufferStarted()
        {
            _replayReady = true;
            _replayStartupError = null;
            _replayBufferStartedAtUtc = DateTimeOffset.UtcNow;
        }

        public void NotifyReplayBufferStopped()
        {
            _replayReady = false;
            _replayBufferStartedAtUtc = null;
        }

        private async Task EnsureStartupTaskEnabledAsync()
        {
            try
            {
                var startupTask = await StartupTask.GetAsync("ZinkStartupTask");

                if (startupTask.State == StartupTaskState.Disabled)
                {
                    await startupTask.RequestEnableAsync();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Startup task enable failed: " + ex);
            }
        }

        private bool IsStartupTaskLaunch()
        {
            try
            {
                var activatedArgs = AppLifecycleInstance.GetCurrent().GetActivatedEventArgs();
                if (activatedArgs == null)
                    return false;

                return string.Equals(
                    activatedArgs.Kind.ToString(),
                    "StartupTask",
                    StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private void SetupTray()
        {
            try
            {
                _trayService = new TrayIconService();
                _trayService.Create();

                _trayService.OpenClicked += TrayService_OpenClicked;
                _trayService.SaveLast45SecondsClicked += TrayService_SaveLast45SecondsClicked;
                _trayService.ExitClicked += TrayService_ExitClicked;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Tray init failed: " + ex);
            }
        }

        private void TrayService_OpenClicked(object sender, EventArgs e)
        {
            try
            {
                if (MainWindow == null)
                {
                    MainWindow = new MainWindow();
                    MainWindow.Closed += OnMainWindowClosed;
                    MainWindow.Activate();
                }

                MainWindow.ShowFromTray();
            }
            catch { }
        }

        private void TrayService_SaveLast45SecondsClicked(object sender, EventArgs e)
        {
            try
            {
                if (MainWindow != null)
                {
                    MainWindow.DispatcherQueue.TryEnqueue(async () =>
                    {
                        await SaveReplayFromTrayAsync();
                    });
                }
                else
                {
                    _ = Task.Run(async () => await SaveReplayFromTrayAsync());
                }
            }
            catch (Exception ex)
            {
                try
                {
                    _trayService?.ShowBalloonTip("Zink Replay Error", ex.Message);
                }
                catch
                {
                }
            }
        }

        private async Task SaveReplayFromTrayAsync()
        {
            try
            {
                if (_replayStartupTask != null)
                {
                    await _replayStartupTask;
                }

                var service = Zink.Services.Recording.ManualRecordingService.Instance;

                if (!service.IsReplayBufferRunning)
                {
                    _trayService?.ShowBalloonTip(
                        "Zink Replay Error",
                        "Replay buffer is not running.");
                    return;
                }

                TimeSpan actualBuffered = service.GetReplayBufferedDuration();
                if (actualBuffered < RequiredReplayBufferDuration)
                {
                    int secondsReady = Math.Max(0, (int)actualBuffered.TotalSeconds);

                    _trayService?.ShowBalloonTip(
                        "Zink Replay",
                        $"Replay buffer is still filling: {secondsReady}/45 seconds.");
                    return;
                }

                string savedPath = await service.SaveReplayAsync();

                _trayService?.ShowBalloonTip(
                    "Zink Replay",
                    $"Saved: {Path.GetFileName(savedPath)}");
            }
            catch (Exception ex)
            {
                try
                {
                    _trayService?.ShowBalloonTip(
                        "Zink Replay Error",
                        ex.Message);
                }
                catch
                {
                }
            }
        }

        private void TrayService_ExitClicked(object sender, EventArgs e)
        {
            try
            {
                _exitRequested = true;

                if (MainWindow != null)
                {
                    MainWindow.Close();
                }
                else
                {
                    try
                    {
                        _trayService?.Dispose();
                    }
                    catch { }

                    Environment.Exit(0);
                }
            }
            catch
            {
                Environment.Exit(0);
            }
        }

        private async Task StartReplayBufferAsync()
        {
            _replayReady = false;
            _replayStartupError = null;

            try
            {
                var service = Zink.Services.Recording.ManualRecordingService.Instance;

                if (service.IsReplayBufferRunning)
                {
                    NotifyReplayBufferStarted();
                    return;
                }

                if (service.IsManualRecording)
                {
                    _replayStartupError = "Manual recording is active.";
                    _replayReady = false;
                    return;
                }

                await Task.Delay(1000);

                var options = new Zink.Models.RecordingOptions
                {
                    IncludeSystemAudio = true,
                    IncludeMicrophone = true
                };

                await service.StartReplayAsync(options);

                NotifyReplayBufferStarted();

                _trayService?.ShowBalloonTip("Zink", "Replay buffer started");
            }
            catch (Exception ex)
            {
                _replayStartupError = ex.Message;
                _replayReady = false;
                _replayBufferStartedAtUtc = null;

                _trayService?.ShowBalloonTip("Zink Replay Error", ex.Message);
            }
        }

        private async Task InitializeAppServicesAsync()
        {
            try
            {
                await LikedRadioLikesService.Instance.LoadAsync();
            }
            catch { }

            try
            {
                await SpotifyAuthHelper.LoadStoredTokenAsync();
            }
            catch { }

            try
            {
                DiscordPresenceService.Instance.Initialize();
            }
            catch { }
        }

        private Frame GetRootFrame()
        {
            if (MainWindow?.Content is Frame directFrame)
                return directFrame;

            var prop = MainWindow?.GetType().GetProperty("MainFrame");
            if (prop != null)
                return prop.GetValue(MainWindow) as Frame;

            return null;
        }

        private void OnMainWindowClosed(object sender, Microsoft.UI.Xaml.WindowEventArgs e)
        {
            if (!_exitRequested)
            {
                try
                {
                    MainWindow?.HideToTray();
                    return;
                }
                catch
                {
                }
            }

            try { VideoLibraryService.Current.Stop(); } catch { }
            try { DiscordPresenceService.Instance.Clear(); } catch { }
            try { DiscordPresenceService.Instance.Shutdown(); } catch { }

            try
            {
                Zink.Services.Recording.ManualRecordingService.Instance.StopAsync().GetAwaiter().GetResult();
            }
            catch { }

            StopAllMediaPlayers(MainWindow);

            try
            {
                if (_trayService != null)
                {
                    _trayService.OpenClicked -= TrayService_OpenClicked;
                    _trayService.SaveLast45SecondsClicked -= TrayService_SaveLast45SecondsClicked;
                    _trayService.ExitClicked -= TrayService_ExitClicked;
                    _trayService.Dispose();
                }
            }
            catch { }

            Environment.Exit(0);
        }

        private void StopAllMediaPlayers(Window window)
        {
            if (window.Content is FrameworkElement root)
            {
                StopMediaInElement(root);
            }
        }

        private void StopMediaInElement(FrameworkElement element)
        {
            if (element is MediaPlayerElement mpe && mpe.MediaPlayer != null)
            {
                mpe.MediaPlayer.Pause();
                mpe.MediaPlayer.Dispose();
            }

            int childrenCount = VisualTreeHelper.GetChildrenCount(element);
            for (int i = 0; i < childrenCount; i++)
            {
                if (VisualTreeHelper.GetChild(element, i) is FrameworkElement child)
                {
                    StopMediaInElement(child);
                }
            }
        }
    }
}