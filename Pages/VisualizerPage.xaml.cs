using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Microsoft.UI.Xaml.Navigation;
using System;

namespace Zink.Pages
{
    public sealed partial class VisualizerPage : Page
    {
        private readonly DispatcherTimer _timer;
        private readonly Random _random = new();
        private string _style = "Bars";

        public VisualizerPage()
        {
            this.InitializeComponent();

            // Create timer but don't start it until the page is ready
            _timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(33) // ~30 FPS
            };
            _timer.Tick += Timer_Tick;

            // Make sure we only start drawing once everything is loaded
            this.Loaded += VisualizerPage_Loaded;
        }

        private void VisualizerPage_Loaded(object sender, RoutedEventArgs e)
        {
            // Now the Canvas is guaranteed to be created
            _timer.Start();
            DrawFrame();
        }

        private void Timer_Tick(object? sender, object e)
        {
            if (PauseToggle.IsChecked == true)
                return;

            DrawFrame();
        }

        private void DrawFrame()
        {
            // Extra safety: if XAML name hasn't been wired yet, just skip
            if (VisualizerCanvas == null)
                return;

            double width = VisualizerCanvas.ActualWidth;
            double height = VisualizerCanvas.ActualHeight;

            if (width <= 0 || height <= 0)
                return;

            VisualizerCanvas.Children.Clear();

            switch (_style)
            {
                case "Bars":
                    DrawBars(width, height);
                    break;
                case "Wave":
                    DrawWave(width, height);
                    break;
                case "Circle":
                    DrawCircle(width, height);
                    break;
            }
        }

        private void DrawBars(double width, double height)
        {
            int barCount = 48;
            double barWidth = width / barCount;

            var brush = new SolidColorBrush(Microsoft.UI.Colors.DeepSkyBlue);

            for (int i = 0; i < barCount; i++)
            {
                double magnitude = _random.NextDouble(); // placeholder data
                double barHeight = magnitude * height * 0.9;
                double x = i * barWidth;
                double y = height - barHeight;

                var rect = new Rectangle
                {
                    Width = barWidth * 0.8,
                    Height = barHeight,
                    RadiusX = 2,
                    RadiusY = 2,
                    Fill = brush
                };

                Canvas.SetLeft(rect, x + (barWidth - rect.Width) / 2);
                Canvas.SetTop(rect, y);
                VisualizerCanvas.Children.Add(rect);
            }
        }

        private void DrawWave(double width, double height)
        {
            int pointCount = 128;
            double step = width / (pointCount - 1);
            double baseLine = height / 2;

            var polyline = new Polyline
            {
                StrokeThickness = 2,
                Stroke = new SolidColorBrush(Microsoft.UI.Colors.DeepSkyBlue)
            };

            for (int i = 0; i < pointCount; i++)
            {
                double t = (double)i / (pointCount - 1);
                double noise = (_random.NextDouble() - 0.5) * height * 0.15;
                double y = baseLine +
                           Math.Sin(t * Math.PI * 4 + DateTime.Now.Millisecond / 200.0) * height * 0.25 +
                           noise;
                double x = i * step;

                polyline.Points.Add(new global::Windows.Foundation.Point(x, y));
            }

            VisualizerCanvas.Children.Add(polyline);
        }

        private void DrawCircle(double width, double height)
        {
            double radius = Math.Min(width, height) / 3;
            var center = new global::Windows.Foundation.Point(width / 2, height / 2);

            int segmentCount = 64;

            var polyline = new Polyline
            {
                StrokeThickness = 3,
                Stroke = new SolidColorBrush(Microsoft.UI.Colors.DeepSkyBlue)
            };

            for (int i = 0; i <= segmentCount; i++)
            {
                double t = (double)i / segmentCount;
                double angle = t * Math.PI * 2;

                double magnitude = 0.8 + _random.NextDouble() * 0.4; // placeholder
                double r = radius * magnitude;

                double x = center.X + Math.Cos(angle) * r;
                double y = center.Y + Math.Sin(angle) * r;

                polyline.Points.Add(new global::Windows.Foundation.Point(x, y));
            }

            VisualizerCanvas.Children.Add(polyline);
        }

        private void StyleComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (StyleComboBox.SelectedItem is ComboBoxItem item &&
                item.Tag is string style)
            {
                _style = style;
                DrawFrame();
            }
        }

        private void PauseToggle_Checked(object sender, RoutedEventArgs e)
        {
            // Just stops updating frames while checked
        }

        private void PauseToggle_Unchecked(object sender, RoutedEventArgs e)
        {
            DrawFrame();
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            _timer.Stop();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            _timer.Start();
        }
    }
}
