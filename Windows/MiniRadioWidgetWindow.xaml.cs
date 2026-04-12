using Microsoft.UI.Text;            // <-- WinUI 3 font weights
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using Zink.Services;
using Zink.ViewModels;

namespace Zink
{
    public sealed class MiniRadioWidgetWindow : Window
    {
        public RadioWidgetViewModel ViewModel { get; } = new();

        private const int WidgetWidth = 560;
        private const int WidgetHeight = 260;

        private static MiniRadioWidgetWindow? _singleton;
        public static MiniRadioWidgetWindow? Current => _singleton;
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

        private readonly TextBlock _txtStation = new()
        {
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,   // WinUI 3
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        private readonly TextBlock _txtArtist = new() { Opacity = 0.95, TextTrimming = TextTrimming.CharacterEllipsis };
        private readonly TextBlock _txtTitle = new() { Opacity = 0.85, TextTrimming = TextTrimming.CharacterEllipsis };
        private readonly TextBlock _txtElapsed = new() { FontSize = 18 };
        private readonly TextBlock _txtDuration = new() { FontSize = 18, Opacity = 0.9 };
        private readonly Image _img = new() { Stretch = Stretch.UniformToFill };

        private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };

        public MiniRadioWidgetWindow()
        {
            Title = "Radio Widget";
            Content = BuildUI();

            RefreshAll();
            ViewModel.PropertyChanged += (_, e) => RefreshChanged(e?.PropertyName);
            _tick.Tick += (_, __) => _txtElapsed.Text = FormatClock(ViewModel.Elapsed);
            _tick.Start();
        }

        private UIElement BuildUI()
        {
            var root = new Grid
            {
                Background = (Brush)Application.Current.Resources["LayerFillColorDefaultBrush"],
                Padding = new Thickness(16),
                Width = WidgetWidth,
                Height = WidgetHeight
            };

            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // Artwork
            var imgBorder = new Border
            {
                CornerRadius = new CornerRadius(16),
                Height = 180,
                Width = 180,
                HorizontalAlignment = HorizontalAlignment.Left,
                Child = _img
            };
            root.Children.Add(imgBorder);

            // Right stack
            var stack = new StackPanel
            {
                Margin = new Thickness(16, 0, 0, 0),
                Spacing = 8,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(stack, 1);
            root.Children.Add(stack);

            stack.Children.Add(_txtStation);
            stack.Children.Add(_txtArtist);
            stack.Children.Add(_txtTitle);

            var timeRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 8, 0, 0) };
            timeRow.Children.Add(_txtElapsed);
            timeRow.Children.Add(new TextBlock { Text = " / ", Opacity = 0.6, FontSize = 18 });
            timeRow.Children.Add(_txtDuration);
            stack.Children.Add(timeRow);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(0, 12, 0, 0) };
            buttons.Children.Add(new Button { Width = 48, Height = 36, Content = new SymbolIcon(Symbol.Play), Command = new RelayAction(() => AppPlaybackService.Instance.RequestPlay()) });
            buttons.Children.Add(new Button { Width = 48, Height = 36, Content = new SymbolIcon(Symbol.Pause), Command = new RelayAction(() => AppPlaybackService.Instance.RequestPause()) });
            buttons.Children.Add(new Button { Width = 48, Height = 36, Content = new SymbolIcon(Symbol.Stop), Command = new RelayAction(() => AppPlaybackService.Instance.RequestStop()) });
            stack.Children.Add(buttons);

            return root;
        }

        private void CenterAndShow()
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var id = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            var appWindow = AppWindow.GetFromWindowId(id);

            try
            {
                appWindow?.SetPresenter(AppWindowPresenterKind.Overlapped);
                appWindow?.Resize(new global::Windows.Graphics.SizeInt32(WidgetWidth, WidgetHeight));

                var da = DisplayArea.GetFromWindowId(id, DisplayAreaFallback.Primary);
                var wa = da.WorkArea;
                int x = wa.X + (wa.Width - WidgetWidth) / 2;
                int y = wa.Y + (wa.Height - WidgetHeight) / 2;
                appWindow?.Move(new global::Windows.Graphics.PointInt32(x, y));
            }
            catch { }

            Activate();
        }

        private void RefreshAll()
        {
            _txtStation.Text = string.IsNullOrWhiteSpace(ViewModel.StationTitle) ? "—" : ViewModel.StationTitle;
            _txtArtist.Text = string.IsNullOrWhiteSpace(ViewModel.ArtistName) ? "—" : ViewModel.ArtistName;
            _txtTitle.Text = string.IsNullOrWhiteSpace(ViewModel.SongTitle) ? "—" : ViewModel.SongTitle;
            _txtElapsed.Text = FormatClock(ViewModel.Elapsed);
            _txtDuration.Text = ViewModel.Duration is TimeSpan d ? FormatClock(d) : "Live";
            _img.Source = ViewModel.ArtworkOrLogo != null ? new BitmapImage(ViewModel.ArtworkOrLogo) : null;
        }

        private void RefreshChanged(string? name)
        {
            switch (name)
            {
                case nameof(AppPlaybackService.StationTitle): _txtStation.Text = string.IsNullOrWhiteSpace(ViewModel.StationTitle) ? "—" : ViewModel.StationTitle; break;
                case nameof(AppPlaybackService.ArtistName): _txtArtist.Text = string.IsNullOrWhiteSpace(ViewModel.ArtistName) ? "—" : ViewModel.ArtistName; break;
                case nameof(AppPlaybackService.SongTitle): _txtTitle.Text = string.IsNullOrWhiteSpace(ViewModel.SongTitle) ? "—" : ViewModel.SongTitle; break;
                case nameof(AppPlaybackService.Duration): _txtDuration.Text = ViewModel.Duration is TimeSpan d ? FormatClock(d) : "Live"; break;
                case nameof(AppPlaybackService.ArtworkOrLogo):
                    _img.Source = ViewModel.ArtworkOrLogo != null ? new BitmapImage(ViewModel.ArtworkOrLogo) : null;
                    break;
            }
        }

        private static string FormatClock(TimeSpan ts) => $"{(int)ts.TotalMinutes:00}:{ts.Seconds:00}";

        private sealed class RelayAction : System.Windows.Input.ICommand
        {
            private readonly Action _action;
            public RelayAction(Action action) => _action = action;
            public bool CanExecute(object parameter) => true;
            public event EventHandler CanExecuteChanged { add { } remove { } }
            public void Execute(object parameter) => _action();
        }
    }
}
