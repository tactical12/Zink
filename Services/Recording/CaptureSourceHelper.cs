using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Windows.Graphics.Capture;
using WinRT;
using WinRT.Interop;

namespace Zink.Services.Recording
{
    internal static class CaptureSourceHelper
    {
        [ComImport]
        [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IGraphicsCaptureItemInterop
        {
            [PreserveSig]
            int CreateForWindow(IntPtr window, ref Guid iid, out IntPtr result);

            [PreserveSig]
            int CreateForMonitor(IntPtr monitor, ref Guid iid, out IntPtr result);
        }

        private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, ref RECT rect, IntPtr data);
        private delegate bool WindowEnumProc(IntPtr hwnd, IntPtr lParam);

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int DWMWA_CLOAKED = 14;
        private const uint MONITOR_DEFAULTTONEAREST = 2;
        private static readonly Guid GraphicsCaptureItemInterfaceGuid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
        private static readonly Guid GraphicsCaptureItemInteropGuid = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");

        public static int LastSelectedProcessId { get; private set; }
        public static string? LastSelectedProcessName { get; private set; }
        private static CaptureSourceOption? _lastSelectedOption;

        public static void ClearCachedSelection()
        {
            _lastSelectedOption = null;
            LastSelectedProcessId = 0;
            LastSelectedProcessName = null;
        }

        public static async Task<GraphicsCaptureItem?> GetOrCreateAsync(IntPtr hwnd, bool preferCachedSelection = false)
        {
            if (!GraphicsCaptureSession.IsSupported())
                return null;

            if (preferCachedSelection && _lastSelectedOption != null)
            {
                var cachedItem = CreateCaptureItem(_lastSelectedOption);
                if (cachedItem != null)
                {
                    LastSelectedProcessId = _lastSelectedOption.ProcessId;
                    LastSelectedProcessName = _lastSelectedOption.ProcessName;
                    Debug.WriteLine($"[ScreenShare:WGC] Reusing selected {_lastSelectedOption.Kind}: {_lastSelectedOption.Name}.");
                    return cachedItem;
                }

                Debug.WriteLine("[ScreenShare:WGC] Cached capture source could not be recreated; opening source picker.");
                ClearCachedSelection();
            }

            var selection = await PickWithZinkDialogAsync(hwnd);
            if (selection == null)
            {
                Debug.WriteLine("[ScreenShare:WGC] No capture source was selected.");
                return null;
            }

            LastSelectedProcessId = selection.ProcessId;
            LastSelectedProcessName = selection.ProcessName;
            _lastSelectedOption = selection;

            var item = CreateCaptureItem(selection);

            if (item == null)
                Debug.WriteLine($"[ScreenShare:WGC] Failed to create capture item for selected {selection.Kind}: {selection.Name}.");

            return item;
        }

        public static async Task<GraphicsCaptureItem?> GetPrimaryScreenOrPromptAsync(IntPtr hwnd)
        {
            if (!GraphicsCaptureSession.IsSupported())
                return null;

            var options = EnumerateCaptureSources(hwnd);
            if (options.Count == 0)
                return null;

            var selection = options.Find(option => option.Kind == CaptureSourceKind.Screen) ?? options[0];
            var item = selection.Kind == CaptureSourceKind.Screen || selection.Kind == CaptureSourceKind.Game
                ? TryCreateForMonitor(selection.Handle)
                : TryCreateForWindow(selection.Handle);

            if (item != null)
            {
                Debug.WriteLine($"[ScreenShare:WGC] Auto-selected {selection.Kind}: {selection.Name} ({selection.Details}).");
                return item;
            }

            Debug.WriteLine("[ScreenShare:WGC] Auto-select failed; falling back to source picker.");
            return await GetOrCreateAsync(hwnd);
        }

        private static GraphicsCaptureItem? CreateCaptureItem(CaptureSourceOption selection)
        {
            return selection.Kind == CaptureSourceKind.Screen || selection.Kind == CaptureSourceKind.Game
                ? TryCreateForMonitor(selection.Handle)
                : TryCreateForWindow(selection.Handle);
        }

        private static async Task<CaptureSourceOption?> PickWithZinkDialogAsync(IntPtr appHwnd)
        {
            var options = EnumerateCaptureSources(appHwnd);
            if (options.Count == 0)
                return null;

            if (App.MainWindow?.Content is not FrameworkElement root || root.XamlRoot == null)
                return options[0];

            var screens = options.FindAll(option => option.Kind == CaptureSourceKind.Screen);
            var windows = options.FindAll(option => option.Kind == CaptureSourceKind.Window && !IsWindowsGamingSystemSource(option));
            var games = options.FindAll(option => option.Kind == CaptureSourceKind.Game);
            var gameWindows = windows.FindAll(option => IsKnownGameProcess(option.ProcessName));
            var selected = gameWindows.Count > 0
                ? gameWindows[0]
                : games.Count > 0
                    ? games[0]
                    : windows.Count > 0
                        ? windows[0]
                        : screens[0];

            var screensList = CreateSourceList(screens, selected);
            var windowsList = CreateSourceList(windows, selected);
            var gamesList = CreateSourceList(games, selected);

            screensList.SelectionChanged += (_, _) =>
            {
                if (screensList.SelectedItem is ListViewItem { Tag: CaptureSourceOption option })
                {
                    selected = option;
                    windowsList.SelectedIndex = -1;
                    gamesList.SelectedIndex = -1;
                }
            };

            windowsList.SelectionChanged += (_, _) =>
            {
                if (windowsList.SelectedItem is ListViewItem { Tag: CaptureSourceOption option })
                {
                    selected = option;
                    screensList.SelectedIndex = -1;
                    gamesList.SelectedIndex = -1;
                }
            };

            gamesList.SelectionChanged += (_, _) =>
            {
                if (gamesList.SelectedItem is ListViewItem { Tag: CaptureSourceOption option })
                {
                    selected = option;
                    screensList.SelectedIndex = -1;
                    windowsList.SelectedIndex = -1;
                }
            };

            var title = new TextBlock
            {
                Text = "ZINK Capture Source Picker",
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
                FontSize = 25,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 2)
            };

            var hint = new TextBlock
            {
                Text = "Choose a running game, app window, or display for preview and streaming.",
                Foreground = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(190, 255, 255, 255)),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10)
            };

            var header = new Grid
            {
                ColumnSpacing = 12,
                Children =
                {
                    new Border
                    {
                        Width = 48,
                        Height = 48,
                        CornerRadius = new CornerRadius(16),
                        Background = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(48, 81, 214, 255)),
                        Child = new FontIcon
                        {
                            Glyph = "\uE7F4",
                            FontSize = 22,
                            Foreground = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 81, 214, 255))
                        }
                    }
                }
            };

            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var titleStack = new StackPanel
            {
                Spacing = 2,
                Children =
                {
                    title,
                    hint
                }
            };
            Grid.SetColumn(titleStack, 1);
            header.Children.Add(titleStack);

            var availableWidth = root.ActualWidth > 0 ? root.ActualWidth : 1280;
            var availableHeight = root.ActualHeight > 0 ? root.ActualHeight : 800;
            var dialogWidth = Math.Clamp(availableWidth - 320, 760, 980);
            var dialogMaxHeight = Math.Clamp(availableHeight - 170, 440, 720);

            var content = new Border
            {
                Width = dialogWidth,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(24),
                CornerRadius = new CornerRadius(24, 24, 0, 0),
                Background = new LinearGradientBrush
                {
                    StartPoint = new global::Windows.Foundation.Point(0, 0),
                    EndPoint = new global::Windows.Foundation.Point(1, 1),
                    GradientStops =
                    {
                        new GradientStop { Color = Microsoft.UI.ColorHelper.FromArgb(232, 8, 13, 20), Offset = 0 },
                        new GradientStop { Color = Microsoft.UI.ColorHelper.FromArgb(220, 12, 24, 34), Offset = 0.55 },
                        new GradientStop { Color = Microsoft.UI.ColorHelper.FromArgb(232, 7, 11, 18), Offset = 1 }
                    }
                },
                BorderBrush = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(96, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                Child = new StackPanel
                {
                    Spacing = 12,
                    Children =
                    {
                        header
                    }
                }
            };

            var stack = (StackPanel)content.Child;

            if (games.Count > 0)
            {
                stack.Children.Add(CreateSectionHeader("Running Games", games.Count));
                stack.Children.Add(gamesList);
            }

            if (screens.Count > 0)
            {
                stack.Children.Add(CreateSectionHeader("Displays", screens.Count));
                stack.Children.Add(screensList);
            }

            if (windows.Count > 0)
            {
                stack.Children.Add(CreateSectionHeader("Windows", windows.Count));
                stack.Children.Add(windowsList);
            }

            stack.Children.Add(new TextBlock
            {
                Text = "Exclusive fullscreen games use display capture because Windows does not expose them like normal app windows.",
                Foreground = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(155, 255, 255, 255)),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 0)
            });

            var useButton = new Button
            {
                Content = "Use source",
                MinHeight = 38,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Background = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 94, 211, 222)),
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(6)
            };

            var cancelButton = new Button
            {
                Content = "Cancel",
                MinHeight = 38,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Background = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 47, 69, 76)),
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(6)
            };

            var footer = new Grid
            {
                Width = dialogWidth,
                Padding = new Thickness(24, 18, 24, 24),
                ColumnSpacing = 10,
                Background = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 32, 32, 32))
            };
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(useButton, 0);
            Grid.SetColumn(cancelButton, 1);
            footer.Children.Add(useButton);
            footer.Children.Add(cancelButton);

            var dialogPanel = new Border
            {
                Width = dialogWidth,
                MaxHeight = dialogMaxHeight + 92,
                CornerRadius = new CornerRadius(24),
                BorderBrush = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(96, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                Background = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 32, 32, 32)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Child = new Grid
                {
                    RowDefinitions =
                    {
                        new RowDefinition { Height = new GridLength(1, GridUnitType.Star) },
                        new RowDefinition { Height = GridLength.Auto }
                    },
                    Children =
                    {
                        new ScrollViewer
                        {
                            MaxHeight = dialogMaxHeight,
                            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                            Content = content
                        }
                    }
                }
            };

            Grid.SetRow(footer, 1);
            ((Grid)dialogPanel.Child).Children.Add(footer);

            var overlay = new Grid
            {
                Width = availableWidth,
                Height = availableHeight,
                Padding = new Thickness(24),
                Background = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(80, 0, 0, 0)),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                Children =
                {
                    dialogPanel
                }
            };

            var popup = new Popup
            {
                XamlRoot = root.XamlRoot,
                Child = overlay,
                IsLightDismissEnabled = true,
                ShouldConstrainToRootBounds = true
            };

            var completion = new TaskCompletionSource<CaptureSourceOption?>();
            useButton.Click += (_, _) =>
            {
                completion.TrySetResult(selected);
                popup.IsOpen = false;
            };
            cancelButton.Click += (_, _) =>
            {
                completion.TrySetResult(null);
                popup.IsOpen = false;
            };
            popup.Closed += (_, _) => completion.TrySetResult(null);
            popup.IsOpen = true;

            return await completion.Task;
        }

        private static ListView CreateSourceList(IReadOnlyList<CaptureSourceOption> options, CaptureSourceOption selected)
        {
            var list = new ListView
            {
                SelectionMode = ListViewSelectionMode.Single,
                Padding = new Thickness(0),
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch
            };
            ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
            ScrollViewer.SetHorizontalScrollMode(list, ScrollMode.Disabled);
            ScrollViewer.SetVerticalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
            ScrollViewer.SetVerticalScrollMode(list, ScrollMode.Disabled);

            foreach (var option in options)
            {
                var item = new ListViewItem
                {
                    Tag = option,
                    Padding = new Thickness(0),
                    Margin = new Thickness(0, 0, 0, 8),
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    Content = CreateSourceRow(option)
                };

                list.Items.Add(item);

                if (ReferenceEquals(option, selected))
                    list.SelectedItem = item;
            }

            return list;
        }

        private static UIElement CreateSourceRow(CaptureSourceOption option)
        {
            var preview = CreateSourcePreview(option);

            var name = new TextBlock
            {
                Text = option.Name,
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxLines = 1
            };

            var details = new TextBlock
            {
                Text = option.Details,
                Foreground = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(170, 255, 255, 255)),
                FontSize = 12,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxLines = 1
            };

            var textStack = new StackPanel
            {
                Spacing = 2,
                MinWidth = 0,
                Children =
                {
                    name,
                    details
                }
            };

            var grid = new Grid
            {
                ColumnSpacing = 12,
                Padding = new Thickness(12),
                Background = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(28, 255, 255, 255)),
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 0 });

            Grid.SetColumn(preview, 0);
            Grid.SetColumn(textStack, 1);

            grid.Children.Add(preview);
            grid.Children.Add(textStack);

            return new Border
            {
                CornerRadius = new CornerRadius(14),
                BorderBrush = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(38, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                Child = grid
            };
        }

        private static Border CreateSourcePreview(CaptureSourceOption option)
        {
            var previewUri = TryCreatePreviewThumbnail(option);
            UIElement child = previewUri is null
                ? new FontIcon
                {
                    Glyph = option.Kind == CaptureSourceKind.Screen ? "\uE7F4" : option.Kind == CaptureSourceKind.Game ? "\uE7FC" : "\uE8A7",
                    FontSize = 26,
                    Foreground = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(210, 255, 255, 255))
                }
                : new Image
                {
                    Source = new BitmapImage(previewUri),
                    Stretch = Stretch.UniformToFill
                };

            return new Border
            {
                Width = 132,
                Height = 74,
                CornerRadius = new CornerRadius(10),
                Background = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(42, 255, 255, 255)),
                BorderBrush = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(54, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                Child = child
            };
        }

        private static Uri? TryCreatePreviewThumbnail(CaptureSourceOption option)
        {
            try
            {
                var rect = option.Kind == CaptureSourceKind.Window
                    ? GetWindowPreviewRect(option.Handle)
                    : GetMonitorPreviewRect(option.Handle);
                var width = Math.Max(1, rect.Right - rect.Left);
                var height = Math.Max(1, rect.Bottom - rect.Top);
                if (width < 16 || height < 16)
                    return null;

                using var source = new global::System.Drawing.Bitmap(width, height);
                using (var graphics = global::System.Drawing.Graphics.FromImage(source))
                {
                    graphics.CopyFromScreen(rect.Left, rect.Top, 0, 0, new global::System.Drawing.Size(width, height));
                }

                using var thumbnail = new global::System.Drawing.Bitmap(336, 188);
                using (var graphics = global::System.Drawing.Graphics.FromImage(thumbnail))
                {
                    graphics.Clear(global::System.Drawing.Color.FromArgb(8, 13, 20));
                    graphics.InterpolationMode = global::System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    graphics.DrawImage(source, new global::System.Drawing.Rectangle(0, 0, thumbnail.Width, thumbnail.Height));
                }

                var fileName = $"zink-capture-preview-{Math.Abs(option.Handle.ToInt64())}-{Environment.TickCount64}.png";
                var filePath = Path.Combine(Path.GetTempPath(), fileName);
                thumbnail.Save(filePath, ImageFormat.Png);
                return new Uri(filePath);
            }
            catch
            {
                return null;
            }
        }

        private static RECT GetMonitorPreviewRect(IntPtr monitor)
        {
            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            return GetMonitorInfo(monitor, ref info) ? info.rcMonitor : default;
        }

        private static RECT GetWindowPreviewRect(IntPtr window)
        {
            return GetWindowRect(window, out var rect) ? rect : default;
        }

        private static TextBlock CreateSectionHeader(string text, int count)
        {
            return new TextBlock
            {
                Text = $"{text} ({count})",
                Foreground = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(235, 255, 255, 255)),
                FontSize = 14,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Margin = new Thickness(0, 8, 0, 0)
            };
        }

        private static List<CaptureSourceOption> EnumerateCaptureSources(IntPtr appHwnd)
        {
            var options = new List<CaptureSourceOption>();
            var addedWindows = new HashSet<IntPtr>();
            var addedGameProcesses = new HashSet<int>();
            var screenNumber = 1;
            var primaryMonitor = IntPtr.Zero;

            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr monitor, IntPtr hdc, ref RECT rect, IntPtr data) =>
            {
                if (primaryMonitor == IntPtr.Zero)
                    primaryMonitor = monitor;

                var width = Math.Max(0, rect.Right - rect.Left);
                var height = Math.Max(0, rect.Bottom - rect.Top);
                if (width > 0 && height > 0)
                {
                    options.Add(new CaptureSourceOption(
                        CaptureSourceKind.Screen,
                        monitor,
                        $"Screen {screenNumber}",
                        $"{width} x {height}",
                        0,
                        null));
                    screenNumber++;
                }

                return true;
            }, IntPtr.Zero);

            EnumWindows((window, lParam) =>
            {
                if (window == appHwnd)
                    return true;

                TryAddWindowSource(options, addedWindows, window, allowGameProcessFallback: true);

                return true;
            }, IntPtr.Zero);

            AddProcessMainWindowSources(options, addedWindows, appHwnd);
            AddExclusiveFullscreenGameSources(options, addedGameProcesses, primaryMonitor, appHwnd);

            return options;
        }

        private static void AddExclusiveFullscreenGameSources(
            List<CaptureSourceOption> options,
            HashSet<int> addedGameProcesses,
            IntPtr primaryMonitor,
            IntPtr appHwnd)
        {
            foreach (var process in Process.GetProcesses())
            {
                try
                {
                    if (!IsKnownGameProcess(process.ProcessName) || !addedGameProcesses.Add(process.Id))
                        continue;

                    var window = process.MainWindowHandle;
                    if (window == appHwnd)
                        continue;

                    var monitor = window != IntPtr.Zero
                        ? MonitorFromWindow(window, MONITOR_DEFAULTTONEAREST)
                        : primaryMonitor;

                    if (monitor == IntPtr.Zero)
                        monitor = primaryMonitor;

                    if (monitor == IntPtr.Zero)
                        continue;

                    var displayName = !string.IsNullOrWhiteSpace(process.MainWindowTitle)
                        ? process.MainWindowTitle.Trim()
                        : process.ProcessName;

                    options.Add(new CaptureSourceOption(
                        CaptureSourceKind.Game,
                        monitor,
                        $"{displayName} (exclusive fullscreen)",
                        "Display capture - running fullscreen game",
                        process.Id,
                        process.ProcessName));
                }
                catch
                {
                }
                finally
                {
                    process.Dispose();
                }
            }
        }

        private static void AddProcessMainWindowSources(List<CaptureSourceOption> options, HashSet<IntPtr> addedWindows, IntPtr appHwnd)
        {
            foreach (var process in Process.GetProcesses())
            {
                try
                {
                    var window = process.MainWindowHandle;
                    if (window == IntPtr.Zero || window == appHwnd)
                        continue;

                    TryAddWindowSource(options, addedWindows, window, allowGameProcessFallback: true, process.ProcessName);
                }
                catch
                {
                }
                finally
                {
                    process.Dispose();
                }
            }
        }

        private static bool TryAddWindowSource(
            List<CaptureSourceOption> options,
            HashSet<IntPtr> addedWindows,
            IntPtr window,
            bool allowGameProcessFallback,
            string? fallbackProcessName = null)
        {
            if (window == IntPtr.Zero || addedWindows.Contains(window))
                return false;

            var exStyle = GetWindowLong(window, GWL_EXSTYLE);
            if ((exStyle & WS_EX_TOOLWINDOW) != 0)
                return false;

            if (!GetWindowRect(window, out var rect))
                return false;

            var width = Math.Max(0, rect.Right - rect.Left);
            var height = Math.Max(0, rect.Bottom - rect.Top);
            if (width < 160 || height < 120)
                return false;

            var title = GetWindowTitle(window);
            var processId = GetWindowProcessId(window);
            var processName = GetWindowProcessName(window);
            if (string.IsNullOrWhiteSpace(processName))
                processName = fallbackProcessName ?? string.Empty;

            if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(processName))
                return false;

            var isLikelyFullscreen = IsLikelyFullscreenWindow(window, rect);
            var isKnownGame = IsKnownGameProcess(processName);
            var isVisible = IsWindowVisible(window);
            var isCloaked = IsWindowCloaked(window);
            if ((!isVisible || isCloaked) && !isLikelyFullscreen && !(allowGameProcessFallback && isKnownGame))
                return false;

            var name = !string.IsNullOrWhiteSpace(title)
                ? title
                : $"{processName} (fullscreen)";
            var details = isLikelyFullscreen
                ? $"{width} x {height} - fullscreen"
                : $"{width} x {height}";
            if (isKnownGame && !details.Contains("game", StringComparison.OrdinalIgnoreCase))
                details += " - game";

            options.Add(new CaptureSourceOption(
                CaptureSourceKind.Window,
                window,
                name,
                details,
                processId,
                processName));
            addedWindows.Add(window);
            return true;
        }

        private static GraphicsCaptureItem? TryCreateForMonitor(IntPtr monitor)
        {
            try
            {
                using var factory = CaptureItemInteropFactory.Create();
                var iid = GraphicsCaptureItemInterfaceGuid;
                var hr = factory.Interop.CreateForMonitor(monitor, ref iid, out var itemPtr);
                if (hr < 0)
                    Marshal.ThrowExceptionForHR(hr);

                return itemPtr == IntPtr.Zero
                    ? null
                    : MarshalInterface<GraphicsCaptureItem>.FromAbi(itemPtr);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ScreenShare:WGC] CreateForMonitor failed: {ex}");
                return null;
            }
        }

        private static GraphicsCaptureItem? TryCreateForWindow(IntPtr window)
        {
            try
            {
                using var factory = CaptureItemInteropFactory.Create();
                var iid = GraphicsCaptureItemInterfaceGuid;
                var hr = factory.Interop.CreateForWindow(window, ref iid, out var itemPtr);
                if (hr < 0)
                    Marshal.ThrowExceptionForHR(hr);

                return itemPtr == IntPtr.Zero
                    ? null
                    : MarshalInterface<GraphicsCaptureItem>.FromAbi(itemPtr);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ScreenShare:WGC] CreateForWindow failed: {ex}");
                return null;
            }
        }

        private static string GetWindowTitle(IntPtr hwnd)
        {
            var length = GetWindowTextLength(hwnd);
            if (length <= 0)
                return string.Empty;

            var builder = new StringBuilder(length + 1);
            GetWindowText(hwnd, builder, builder.Capacity);
            return builder.ToString().Trim();
        }

        private static bool IsWindowCloaked(IntPtr hwnd)
        {
            try
            {
                var cloaked = 0;
                var result = DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, ref cloaked, sizeof(int));
                return result == 0 && cloaked != 0;
            }
            catch
            {
                return false;
            }
        }

        private static string GetWindowProcessName(IntPtr hwnd)
        {
            try
            {
                var processId = GetWindowProcessId(hwnd);
                if (processId == 0)
                    return string.Empty;

                using var process = Process.GetProcessById(processId);
                return string.IsNullOrWhiteSpace(process.MainWindowTitle)
                    ? process.ProcessName
                    : process.MainWindowTitle.Trim();
            }
            catch
            {
                return string.Empty;
            }
        }

        private static int GetWindowProcessId(IntPtr hwnd)
        {
            GetWindowThreadProcessId(hwnd, out var processId);
            return processId > int.MaxValue ? 0 : (int)processId;
        }

        private static bool IsKnownGameProcess(string processName)
        {
            if (string.IsNullOrWhiteSpace(processName))
                return false;

            if (IsWindowsGamingSystemText(processName))
                return false;

            return processName.Equals("Overwatch", StringComparison.OrdinalIgnoreCase) ||
                   processName.Equals("Overwatch Launcher", StringComparison.OrdinalIgnoreCase) ||
                   processName.Contains("Overwatch", StringComparison.OrdinalIgnoreCase) ||
                   processName.Contains("Game", StringComparison.OrdinalIgnoreCase) ||
                   processName.Contains("Shipping", StringComparison.OrdinalIgnoreCase) ||
                   processName.Contains("Win64", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsWindowsGamingSystemSource(CaptureSourceOption option)
        {
            return IsWindowsGamingSystemText(option.Name) ||
                   IsWindowsGamingSystemText(option.Details) ||
                   IsWindowsGamingSystemText(option.ProcessName);
        }

        private static bool IsWindowsGamingSystemText(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return false;

            return text.Contains("Xbox Game Bar", StringComparison.OrdinalIgnoreCase) ||
                   text.Contains("Game Bar", StringComparison.OrdinalIgnoreCase) ||
                   text.Contains("GameBar", StringComparison.OrdinalIgnoreCase) ||
                   text.Contains("GameInput", StringComparison.OrdinalIgnoreCase) ||
                   text.Contains("Game Input", StringComparison.OrdinalIgnoreCase) ||
                   text.Contains("GameInputSvc", StringComparison.OrdinalIgnoreCase) ||
                   text.Contains("GameBarFTServer", StringComparison.OrdinalIgnoreCase) ||
                   text.Contains("GameBarPresenceWriter", StringComparison.OrdinalIgnoreCase) ||
                   text.Contains("GamingServices", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsLikelyFullscreenWindow(IntPtr hwnd, RECT windowRect)
        {
            try
            {
                var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
                if (monitor == IntPtr.Zero || !TryGetMonitorInfo(monitor, out var info))
                    return false;

                var monitorRect = info.rcMonitor;
                return Math.Abs(windowRect.Left - monitorRect.Left) <= 2 &&
                       Math.Abs(windowRect.Top - monitorRect.Top) <= 2 &&
                       Math.Abs(windowRect.Right - monitorRect.Right) <= 2 &&
                       Math.Abs(windowRect.Bottom - monitorRect.Bottom) <= 2;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryGetMonitorInfo(IntPtr monitor, out MONITORINFO info)
        {
            info = new MONITORINFO
            {
                cbSize = Marshal.SizeOf<MONITORINFO>()
            };
            return GetMonitorInfo(monitor, ref info);
        }

        [DllImport("user32.dll")]
        private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(WindowEnumProc enumProc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hwnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int maxCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowTextLength(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hwnd, int index);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

        [DllImport("dwmapi.dll")]
        private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        [DllImport("combase.dll", ExactSpelling = true)]
        private static extern int RoGetActivationFactory(IntPtr activatableClassId, ref Guid iid, out IntPtr factory);

        [DllImport("combase.dll", ExactSpelling = true)]
        private static extern int WindowsCreateString(
            [MarshalAs(UnmanagedType.LPWStr)] string sourceString,
            int length,
            out IntPtr hstring);

        [DllImport("combase.dll", ExactSpelling = true)]
        private static extern int WindowsDeleteString(IntPtr hstring);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        private enum CaptureSourceKind
        {
            Screen,
            Window,
            Game
        }

        private sealed class CaptureSourceOption
        {
            public CaptureSourceOption(CaptureSourceKind kind, IntPtr handle, string name, string details, int processId, string? processName)
            {
                Kind = kind;
                Handle = handle;
                Name = name;
                Details = details;
                ProcessId = processId;
                ProcessName = processName;
            }

            public CaptureSourceKind Kind { get; }
            public IntPtr Handle { get; }
            public string Name { get; }
            public string Details { get; }
            public int ProcessId { get; }
            public string? ProcessName { get; }

            public override string ToString()
            {
                return $"{(Kind == CaptureSourceKind.Screen ? "Screen" : Kind == CaptureSourceKind.Game ? "Game" : "Window")} - {Name} ({Details})";
            }
        }

        private sealed class CaptureItemInteropFactory : IDisposable
        {
            private IntPtr _factoryPtr;

            private CaptureItemInteropFactory(IntPtr factoryPtr)
            {
                _factoryPtr = factoryPtr;
                Interop = (IGraphicsCaptureItemInterop)Marshal.GetObjectForIUnknown(factoryPtr);
            }

            public IGraphicsCaptureItemInterop Interop { get; }

            public static CaptureItemInteropFactory Create()
            {
                var hstring = IntPtr.Zero;
                var factoryPtr = IntPtr.Zero;

                try
                {
                    var hr = WindowsCreateString(
                        "Windows.Graphics.Capture.GraphicsCaptureItem",
                        "Windows.Graphics.Capture.GraphicsCaptureItem".Length,
                        out hstring);
                    if (hr < 0)
                        Marshal.ThrowExceptionForHR(hr);

                    var iid = GraphicsCaptureItemInteropGuid;
                    hr = RoGetActivationFactory(hstring, ref iid, out factoryPtr);
                    if (hr < 0)
                        Marshal.ThrowExceptionForHR(hr);

                    return new CaptureItemInteropFactory(factoryPtr);
                }
                catch
                {
                    if (factoryPtr != IntPtr.Zero)
                        Marshal.Release(factoryPtr);
                    throw;
                }
                finally
                {
                    if (hstring != IntPtr.Zero)
                        WindowsDeleteString(hstring);
                }
            }

            public void Dispose()
            {
                if (_factoryPtr == IntPtr.Zero)
                    return;

                Marshal.Release(_factoryPtr);
                _factoryPtr = IntPtr.Zero;
            }
        }
    }
}
