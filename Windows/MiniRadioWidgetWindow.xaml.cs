using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Runtime.InteropServices;
using Windows.Foundation;
using Windows.UI;
using Zink.Services;
using Zink.ViewModels;

namespace Zink
{
    public sealed class MiniRadioWidgetWindow : Window
    {
        private const int WidgetWidth = 760;
        private const int WidgetHeight = 360;
        private const int WmNcButtonDown = 0x00A1;
        private const int HtCaption = 2;

        private static MiniRadioWidgetWindow? _singleton;

        private readonly TextBlock _txtStation = new()
        {
            FontSize = 36,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        private readonly TextBlock _txtArtist = new()
        {
            FontSize = 22,
            Opacity = 0.95,
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        private readonly TextBlock _txtTitle = new()
        {
            FontSize = 22,
            Opacity = 0.9,
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        private readonly TextBlock _txtElapsed = new() { FontSize = 24 };
        private readonly TextBlock _txtDuration = new() { FontSize = 24, Opacity = 0.9 };
        private readonly Image _img = new() { Stretch = Stretch.UniformToFill };
        private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };

        private IntPtr _hwnd;
        private Grid? _dragRegion;

        public RadioWidgetViewModel ViewModel { get; } = new();
        public static MiniRadioWidgetWindow? Current => _singleton;

        public MiniRadioWidgetWindow()
        {
            Title = "Radio Widget";
            Content = BuildUI();
            TryUseCustomChrome();

            RefreshAll();
            ViewModel.PropertyChanged += (_, e) => RefreshChanged(e?.PropertyName);
            _tick.Tick += (_, __) => _txtElapsed.Text = FormatClock(ViewModel.Elapsed);
            _tick.Start();
        }

        public static void ShowSingleton()
        {
            if (_singleton == null)
            {
                _singleton = new MiniRadioWidgetWindow();
                _singleton.Closed += (_, __) => _singleton = null;
                _singleton.CenterAndShow();
            }
            else
            {
                _singleton.Activate();
            }
        }

        private UIElement BuildUI()
        {
            var root = new Grid
            {
                Width = WidgetWidth,
                Height = WidgetHeight
            };

            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(56) });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.Background = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(1, 1),
                GradientStops =
                {
                    new GradientStop { Color = Color.FromArgb(255, 9, 15, 22), Offset = 0 },
                    new GradientStop { Color = Color.FromArgb(255, 19, 42, 55), Offset = 0.48 },
                    new GradientStop { Color = Color.FromArgb(255, 5, 8, 13), Offset = 1 }
                }
            };

            _dragRegion = CreateHeader();
            root.Children.Add(_dragRegion);

            var shell = new Border
            {
                Margin = new Thickness(18, 0, 18, 18),
                Padding = new Thickness(26),
                CornerRadius = new CornerRadius(34),
                Background = GlassBrush(0xC8, 0x12, 0x1B, 0x24),
                BorderBrush = GlassBrush(0x4D, 0xFF, 0xFF, 0xFF),
                BorderThickness = new Thickness(1.25)
            };
            Grid.SetRow(shell, 1);
            root.Children.Add(shell);

            var layout = new Grid
            {
                ColumnSpacing = 30
            };
            layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(244) });
            layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            shell.Child = layout;

            _txtStation.Foreground = GlassBrush(0xFF, 0xFF, 0xFF, 0xFF);
            _txtArtist.Foreground = GlassBrush(0xDE, 0xC9, 0xD4, 0xDE);
            _txtTitle.Foreground = GlassBrush(0xF5, 0xF3, 0xFA, 0xFF);
            _txtElapsed.Foreground = GlassBrush(0xFF, 0xFF, 0xFF, 0xFF);
            _txtDuration.Foreground = GlassBrush(0xD6, 0xC9, 0xD4, 0xDE);

            var imgBorder = new Border
            {
                CornerRadius = new CornerRadius(30),
                Height = 244,
                Width = 244,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                Background = GlassBrush(0xFF, 0x25, 0x38, 0x46),
                BorderBrush = GlassBrush(0x34, 0xFF, 0xFF, 0xFF),
                BorderThickness = new Thickness(1),
                Child = _img
            };
            layout.Children.Add(imgBorder);

            var stack = new StackPanel
            {
                Spacing = 10,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(stack, 1);
            layout.Children.Add(stack);

            stack.Children.Add(_txtStation);
            stack.Children.Add(_txtArtist);
            stack.Children.Add(_txtTitle);
            stack.Children.Add(CreateTimeRow());
            stack.Children.Add(CreatePlaybackControls());

            return root;
        }

        private Grid CreateHeader()
        {
            var header = new Grid
            {
                Height = 56,
                Background = GlassBrush(0x01, 0, 0, 0),
                Margin = new Thickness(18, 10, 14, 8),
                ColumnSpacing = 10
            };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.PointerPressed += BeginDragMove;

            var badge = new Border
            {
                Width = 34,
                Height = 34,
                CornerRadius = new CornerRadius(11),
                Background = GlassBrush(0xD0, 0x16, 0x2D, 0x35),
                BorderBrush = GlassBrush(0x50, 0xB8, 0xFB, 0xFF),
                BorderThickness = new Thickness(1),
                Child = new TextBlock
                {
                    Text = "z",
                    FontSize = 18,
                    FontWeight = FontWeights.Bold,
                    Foreground = GlassBrush(0xFF, 0xDC, 0xFF, 0xF7),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
            header.Children.Add(badge);

            var title = new TextBlock
            {
                Text = "Radio Widget",
                FontSize = 18,
                FontWeight = FontWeights.SemiBold,
                Foreground = GlassBrush(0xE8, 0xF2, 0xF8, 0xFF),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(title, 1);
            header.Children.Add(title);

            var closeButton = CreateWindowButton("x", Close);
            Grid.SetColumn(closeButton, 2);
            header.Children.Add(closeButton);

            return header;
        }

        private UIElement CreateTimeRow()
        {
            var timeRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 10,
                Margin = new Thickness(0, 14, 0, 0)
            };

            timeRow.Children.Add(CreatePill(_txtElapsed, 0x3A, 0x2E, 0x8D, 0xFF, 0x70, 0x7C, 0xC7, 0xFF));
            timeRow.Children.Add(CreatePill(_txtDuration, 0x28, 0xFF, 0xFF, 0xFF, 0x26, 0xFF, 0xFF, 0xFF));
            return timeRow;
        }

        private UIElement CreatePlaybackControls()
        {
            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 14,
                Margin = new Thickness(0, 18, 0, 0)
            };

            buttons.Children.Add(CreateGlassButton(Symbol.Play, () => AppPlaybackService.Instance.RequestPlay(), "Play", primary: true, size: 64));
            buttons.Children.Add(CreateGlassButton(Symbol.Pause, () => AppPlaybackService.Instance.RequestPause(), "Pause", size: 58));
            buttons.Children.Add(CreateGlassButton(Symbol.Stop, () => AppPlaybackService.Instance.RequestStop(), "Stop", size: 58));
            return buttons;
        }

        private static Border CreatePill(
            UIElement child,
            byte backgroundAlpha,
            byte backgroundRed,
            byte backgroundGreen,
            byte backgroundBlue,
            byte borderAlpha,
            byte borderRed,
            byte borderGreen,
            byte borderBlue)
        {
            return new Border
            {
                CornerRadius = new CornerRadius(999),
                Padding = new Thickness(18, 8, 18, 9),
                Background = GlassBrush(backgroundAlpha, backgroundRed, backgroundGreen, backgroundBlue),
                BorderBrush = GlassBrush(borderAlpha, borderRed, borderGreen, borderBlue),
                BorderThickness = new Thickness(1),
                Child = child
            };
        }

        private static Button CreateGlassButton(Symbol symbol, Action action, string tooltip, bool primary = false, double size = 58)
        {
            var button = new Button
            {
                Width = size,
                Height = size,
                CornerRadius = new CornerRadius(size / 2),
                BorderThickness = new Thickness(1),
                BorderBrush = primary ? GlassBrush(0x8A, 0xD8, 0xF0, 0xFF) : GlassBrush(0x34, 0xFF, 0xFF, 0xFF),
                Background = primary ? GlassBrush(0xDC, 0x2E, 0x8D, 0xFF) : GlassBrush(0x34, 0xFF, 0xFF, 0xFF),
                Foreground = GlassBrush(0xFF, 0xFF, 0xFF, 0xFF),
                Content = new SymbolIcon(symbol),
                Command = new RelayAction(action)
            };
            ToolTipService.SetToolTip(button, tooltip);
            return button;
        }

        private static Button CreateWindowButton(string text, Action action)
        {
            var button = new Button
            {
                Width = 38,
                Height = 34,
                CornerRadius = new CornerRadius(17),
                Background = GlassBrush(0x24, 0xFF, 0xFF, 0xFF),
                BorderBrush = GlassBrush(0x28, 0xFF, 0xFF, 0xFF),
                BorderThickness = new Thickness(1),
                Foreground = GlassBrush(0xE8, 0xF2, 0xF8, 0xFF),
                Content = new TextBlock
                {
                    Text = text,
                    FontSize = 18,
                    FontWeight = FontWeights.SemiBold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                },
                Command = new RelayAction(action)
            };
            ToolTipService.SetToolTip(button, "Close");
            return button;
        }

        private void TryUseCustomChrome()
        {
            try
            {
                ExtendsContentIntoTitleBar = true;
                if (_dragRegion != null)
                    SetTitleBar(_dragRegion);

                _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                var id = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(_hwnd);
                var appWindow = AppWindow.GetFromWindowId(id);
                if (appWindow == null)
                    return;

                appWindow.Title = "Radio Widget";
                appWindow.Resize(new global::Windows.Graphics.SizeInt32(WidgetWidth, WidgetHeight));

                if (appWindow.Presenter is OverlappedPresenter presenter)
                {
                    presenter.IsResizable = false;
                    presenter.IsMaximizable = false;
                    presenter.IsMinimizable = true;
                    presenter.SetBorderAndTitleBar(false, false);
                }
            }
            catch
            {
            }
        }

        private void CenterAndShow()
        {
            var hwnd = _hwnd == IntPtr.Zero
                ? WinRT.Interop.WindowNative.GetWindowHandle(this)
                : _hwnd;
            var id = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            var appWindow = AppWindow.GetFromWindowId(id);

            try
            {
                appWindow?.SetPresenter(AppWindowPresenterKind.Overlapped);
                appWindow?.Resize(new global::Windows.Graphics.SizeInt32(WidgetWidth, WidgetHeight));

                if (appWindow?.Presenter is OverlappedPresenter presenter)
                {
                    presenter.IsResizable = false;
                    presenter.IsMaximizable = false;
                    presenter.SetBorderAndTitleBar(false, false);
                }

                var da = DisplayArea.GetFromWindowId(id, DisplayAreaFallback.Primary);
                var wa = da.WorkArea;
                int x = wa.X + (wa.Width - WidgetWidth) / 2;
                int y = wa.Y + (wa.Height - WidgetHeight) / 2;
                appWindow?.Move(new global::Windows.Graphics.PointInt32(x, y));
            }
            catch
            {
            }

            Activate();
        }

        private void BeginDragMove(object sender, PointerRoutedEventArgs e)
        {
            if (_hwnd == IntPtr.Zero)
                _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);

            if (_hwnd == IntPtr.Zero)
                return;

            try
            {
                ReleaseCapture();
                SendMessage(_hwnd, WmNcButtonDown, new IntPtr(HtCaption), IntPtr.Zero);
            }
            catch
            {
            }
        }

        private void RefreshAll()
        {
            _txtStation.Text = string.IsNullOrWhiteSpace(ViewModel.StationTitle) ? "Radio" : ViewModel.StationTitle;
            _txtArtist.Text = string.IsNullOrWhiteSpace(ViewModel.ArtistName) ? "-" : ViewModel.ArtistName;
            _txtTitle.Text = string.IsNullOrWhiteSpace(ViewModel.SongTitle) ? "-" : ViewModel.SongTitle;
            _txtElapsed.Text = FormatClock(ViewModel.Elapsed);
            _txtDuration.Text = ViewModel.Duration is TimeSpan d ? FormatClock(d) : "Live";
            _img.Source = ViewModel.ArtworkOrLogo != null ? new BitmapImage(ViewModel.ArtworkOrLogo) : null;
        }

        private void RefreshChanged(string? name)
        {
            switch (name)
            {
                case nameof(AppPlaybackService.StationTitle):
                    _txtStation.Text = string.IsNullOrWhiteSpace(ViewModel.StationTitle) ? "Radio" : ViewModel.StationTitle;
                    break;
                case nameof(AppPlaybackService.ArtistName):
                    _txtArtist.Text = string.IsNullOrWhiteSpace(ViewModel.ArtistName) ? "-" : ViewModel.ArtistName;
                    break;
                case nameof(AppPlaybackService.SongTitle):
                    _txtTitle.Text = string.IsNullOrWhiteSpace(ViewModel.SongTitle) ? "-" : ViewModel.SongTitle;
                    break;
                case nameof(AppPlaybackService.Duration):
                    _txtDuration.Text = ViewModel.Duration is TimeSpan d ? FormatClock(d) : "Live";
                    break;
                case nameof(AppPlaybackService.ArtworkOrLogo):
                    _img.Source = ViewModel.ArtworkOrLogo != null ? new BitmapImage(ViewModel.ArtworkOrLogo) : null;
                    break;
            }
        }

        private static SolidColorBrush GlassBrush(byte alpha, byte red, byte green, byte blue)
        {
            return new SolidColorBrush(Color.FromArgb(alpha, red, green, blue));
        }

        private static string FormatClock(TimeSpan ts) => $"{(int)ts.TotalMinutes:00}:{ts.Seconds:00}";

        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        private sealed class RelayAction : System.Windows.Input.ICommand
        {
            private readonly Action _action;

            public RelayAction(Action action) => _action = action;

            public bool CanExecute(object? parameter) => true;
            public event EventHandler? CanExecuteChanged { add { } remove { } }
            public void Execute(object? parameter) => _action();
        }
    }
}
