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
using Zink.Services.NativeCalling;
using Zink.Services.Recording;

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
            DiagnosticLogService.InitializeFromSettings();
            DiagnosticLogService.WriteLine("Zink app constructor started.");
            HookDiagnosticCrashLogging();

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
                    DiagnosticLogService.WriteLine("Unhandled exception: " + e.Exception);
                    Directory.CreateDirectory(DiagnosticLogService.LogDirectoryPath);
                    var crashLogPath = Path.Combine(DiagnosticLogService.LogDirectoryPath, $"CrashLog-{DiagnosticLogService.DeviceName}-latest.txt");
                    File.WriteAllText(crashLogPath, e.Exception + Environment.NewLine);
                }
                catch { }
                finally
                {
                    DiagnosticLogService.Flush();
                }

                e.Handled = true;
            };
        }

        private static void HookDiagnosticCrashLogging()
        {
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            {
                try
                {
                    DiagnosticLogService.WriteLine("AppDomain unhandled exception: " + e.ExceptionObject);
                }
                catch
                {
                }
                finally
                {
                    DiagnosticLogService.Flush();
                }
            };

            AppDomain.CurrentDomain.FirstChanceException += (_, e) =>
            {
                try
                {
                    if (IsScreenShareDiagnosticException(e.Exception))
                        DiagnosticLogService.WriteLine("First-chance screen-share exception: " + e.Exception);
                }
                catch
                {
                }
            };

            TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                try
                {
                    DiagnosticLogService.WriteLine("Unobserved task exception: " + e.Exception);
                }
                catch
                {
                }
                finally
                {
                    DiagnosticLogService.Flush();
                }
            };

            AppDomain.CurrentDomain.ProcessExit += (_, __) =>
            {
                try
                {
                    DiagnosticLogService.WriteLine("Process exit.");
                }
                catch
                {
                }
                finally
                {
                    DiagnosticLogService.Flush();
                }
            };
        }

        private static bool IsScreenShareDiagnosticException(Exception exception)
        {
            var typeName = exception.GetType().FullName ?? "";
            var stack = exception.StackTrace ?? "";

            return typeName.Contains("SharpDX", StringComparison.OrdinalIgnoreCase) ||
                typeName.Contains("Vortice", StringComparison.OrdinalIgnoreCase) ||
                stack.Contains("ScreenShare", StringComparison.OrdinalIgnoreCase) ||
                stack.Contains("MediaFoundationH264Encoder", StringComparison.OrdinalIgnoreCase) ||
                stack.Contains("NativePeerConnectionService", StringComparison.OrdinalIgnoreCase) ||
                stack.Contains("SIPSorcery", StringComparison.OrdinalIgnoreCase);
        }

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(nint hWnd, int nCmdShow);

        private const int SW_MAXIMIZE = 3;

        protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            _ = InitializeAppServicesAsync();

            SetupTray();
            _ = ZinkBackgroundModeService.Instance.ApplyAsync();

            StorageFile fileToOpen = null;
            bool launchedFromStartupTask = IsStartupTaskLaunch();
            bool backgroundRunEnabled = BackgroundModePreferences.IsBackgroundRunEnabled;
            bool gamingReplayEnabled = RecordingPreferences.IsGamingBackgroundReplayEnabled;

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

                if (backgroundRunEnabled && gamingReplayEnabled)
                {
                    _replayStartupTask = DelayedReplayStartupAsync(TimeSpan.FromSeconds(2));
                }

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

                if (backgroundRunEnabled && gamingReplayEnabled)
                {
                    _replayStartupTask = DelayedReplayStartupAsync(TimeSpan.FromSeconds(25));
                }

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

                    if (backgroundRunEnabled && gamingReplayEnabled)
                    {
                        _replayStartupTask = DelayedReplayStartupAsync(TimeSpan.FromSeconds(2));
                    }

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

                if (!RecordingPreferences.IsGamingBackgroundReplayEnabled)
                {
                    _trayService?.ShowBalloonTip(
                        "Zink Replay",
                        "Background replay for gaming is turned off in Settings.");
                    return;
                }

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
                    MainWindow.AllowClose = true;
                    MainWindow.Close();
                }
                else
                {
                    NotifyActiveCallEndedBeforeExit();

                    try
                    {
                        ZinkBackgroundModeService.Instance.Dispose();
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
                if (!RecordingPreferences.IsGamingBackgroundReplayEnabled)
                {
                    _replayStartupError = "Background replay for gaming is turned off in Settings.";
                    _replayReady = false;
                    return;
                }

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
            var hasActiveCall = IsActiveCallState(NativeCallCoordinator.Instance.CurrentSession.State);
            DiagnosticLogService.WriteLine($"Main window closing; exitRequested={_exitRequested}; activeCall={hasActiveCall}.");
            DiagnosticLogService.Flush();

            if (!_exitRequested && !hasActiveCall)
            {
                try
                {
                    MainWindow?.HideToTray();
                    DiagnosticLogService.WriteLine("Main window hidden to tray.");
                    DiagnosticLogService.Flush();
                    return;
                }
                catch
                {
                }
            }

            if (!_exitRequested && hasActiveCall)
            {
                DiagnosticLogService.WriteLine("Main window close requested during an active call; leaving the call instead of hiding to tray.");
                DiagnosticLogService.Flush();
            }

            NotifyActiveCallEndedBeforeExit();

            try { VideoLibraryService.Current.Stop(); } catch { }
            try { DiscordPresenceService.Instance.Clear(); } catch { }
            try { DiscordPresenceService.Instance.Shutdown(); } catch { }
            try { ZinkBackgroundModeService.Instance.Dispose(); } catch { }

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

            DiagnosticLogService.WriteLine("Main window closed; exiting process.");
            DiagnosticLogService.Flush();
            Environment.Exit(0);
        }

        private void NotifyActiveCallEndedBeforeExit()
        {
            try
            {
                var session = NativeCallCoordinator.Instance.CurrentSession;
                if (!IsActiveCallState(session.State) || string.IsNullOrWhiteSpace(session.CallId))
                    return;

                var endTask = Task.Run(async () =>
                {
                    try
                    {
                        DiagnosticLogService.WriteLine("Stopping screen share during app exit.");
                        await NativeScreenShareStreamingService.Instance.StopAsync();
                    }
                    catch
                    {
                        DiagnosticLogService.WriteLine("Stopping screen share during app exit failed.");
                    }

                    await NativeCallCoordinator.Instance.EndAsync("closed-app");
                });

                if (!endTask.Wait(TimeSpan.FromMilliseconds(650)))
                {
                    DiagnosticLogService.WriteLine("Timed out quickly while notifying call end during app exit; continuing shutdown.");
                }
                else
                {
                    DiagnosticLogService.WriteLine("Call end notification completed during app exit.");
                }
            }
            catch (Exception ex)
            {
                DiagnosticLogService.WriteLine("Failed to notify call end during app exit: " + ex);
            }
        }

        private static bool IsActiveCallState(NativeCallState state)
        {
            return state == NativeCallState.Calling ||
                state == NativeCallState.Incoming ||
                state == NativeCallState.Accepted ||
                state == NativeCallState.Negotiating ||
                state == NativeCallState.Connected;
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

