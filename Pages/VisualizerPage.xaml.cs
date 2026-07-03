using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Windows.Storage;
using Zink.Services.Recording;

namespace Zink.Pages
{
    public sealed partial class VisualizerPage : Page
    {
        private readonly DispatcherTimer _timer;
        private readonly object _audioGate = new();
        private readonly SystemLoopbackCaptureService _audioCapture = new();
        private readonly double[] _levels = new double[64];
        private readonly double[] _waveSamples = new double[128];
        private const string SettingsStyleKey = "Visualizer_Style";
        private const string SettingsColorThemeKey = "Visualizer_ColorTheme";
        private const string SettingsSensitivityKey = "Visualizer_SensitivityPercent";
        private const string SettingsSmoothingKey = "Visualizer_SmoothingPercent";
        private string _style = "Bars";
        private string _colorTheme = "Sky";
        private double _sensitivityPercent = 100.0;
        private double _sensitivity = 1.0;
        private double _smoothingPercent = 82.0;
        private double _smoothing = 0.82;
        private double _beatLevel;
        private double _phase;
        private bool _captureStartAttempted;
        private bool _isNavigatedFrom;
        private bool _isLoadingSettings;

        public VisualizerPage()
        {
            this.InitializeComponent();
            RestoreSettings();

            // Create timer but don't start it until the page is ready
            _timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(33) // ~30 FPS
            };
            _timer.Tick += Timer_Tick;

            // Make sure we only start drawing once everything is loaded
            this.Loaded += VisualizerPage_Loaded;
            this.Unloaded += VisualizerPage_Unloaded;
            _audioCapture.AudioPacketArrived += AudioCapture_AudioPacketArrived;
        }

        private async void VisualizerPage_Loaded(object sender, RoutedEventArgs e)
        {
            // Now the Canvas is guaranteed to be created
            _isNavigatedFrom = false;
            await StartAudioCaptureAsync();
            _timer.Start();
            DrawFrame();
        }

        private void Timer_Tick(object? sender, object e)
        {
            if (PauseToggle.IsChecked == true)
                return;

            DrawFrame();
        }

        private async Task StartAudioCaptureAsync()
        {
            if (_captureStartAttempted || _audioCapture.IsRunning)
                return;

            _captureStartAttempted = true;

            try
            {
                await _audioCapture.StartAsync();
                HintText.Text = "Live audio visualizer is listening to your default output.";
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Visualizer loopback capture failed: " + ex.Message);
                HintText.Text = "Play audio in Zink or on this PC to drive the visualizer. If it stays still, check Windows audio permissions.";
            }
        }

        private async Task StopAudioCaptureAsync()
        {
            _captureStartAttempted = false;

            try
            {
                await _audioCapture.StopAsync();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Visualizer loopback capture stop failed: " + ex.Message);
            }
        }

        private void AudioCapture_AudioPacketArrived(object? sender, AudioPacket packet)
        {
            if (packet.PcmData.Length == 0 || packet.Channels <= 0)
                return;

            var frameCount = GetFrameCount(packet);
            if (frameCount <= 0)
                return;

            var levelCount = _levels.Length;
            var waveCount = _waveSamples.Length;
            var nextLevels = new double[levelCount];
            var nextWave = new double[waveCount];
            var levelFramesPerBucket = Math.Max(1, frameCount / levelCount);
            var waveFramesPerBucket = Math.Max(1, frameCount / waveCount);

            for (int bucket = 0; bucket < levelCount; bucket++)
            {
                var start = bucket * levelFramesPerBucket;
                var end = bucket == levelCount - 1
                    ? frameCount
                    : Math.Min(frameCount, start + levelFramesPerBucket);

                double sum = 0;
                var samples = 0;

                for (int frame = start; frame < end; frame++)
                {
                    var mono = ReadMonoSample(packet, frame);
                    sum += mono * mono;
                    samples++;
                }

                nextLevels[bucket] = samples == 0 ? 0 : Math.Sqrt(sum / samples);
            }

            for (int bucket = 0; bucket < waveCount; bucket++)
            {
                var frame = Math.Min(frameCount - 1, bucket * waveFramesPerBucket);
                nextWave[bucket] = ReadMonoSample(packet, frame);
            }

            lock (_audioGate)
            {
                for (int i = 0; i < levelCount; i++)
                {
                    var boosted = Math.Clamp(nextLevels[i] * 3.5 * _sensitivity, 0, 1);
                    _levels[i] = boosted > _levels[i]
                        ? boosted
                        : (_levels[i] * _smoothing) + (boosted * (1 - _smoothing));
                }

                for (int i = 0; i < waveCount; i++)
                {
                    _waveSamples[i] = (_waveSamples[i] * 0.45) + (nextWave[i] * 0.55);
                }

                double total = 0;
                for (int i = 0; i < levelCount; i++)
                    total += _levels[i];

                var average = total / levelCount;
                _beatLevel = average > _beatLevel
                    ? average
                    : (_beatLevel * _smoothing) + (average * (1 - _smoothing));
            }
        }

        private static int GetFrameCount(AudioPacket packet)
        {
            var bytesPerSample = Math.Max(1, packet.BitsPerSample / 8);
            var bytesPerFrame = bytesPerSample * packet.Channels;

            return bytesPerFrame <= 0 ? 0 : packet.PcmData.Length / bytesPerFrame;
        }

        private static double ReadMonoSample(AudioPacket packet, int frameIndex)
        {
            var bytesPerSample = Math.Max(1, packet.BitsPerSample / 8);
            var frameOffset = frameIndex * bytesPerSample * packet.Channels;
            double sum = 0;
            var channelsRead = 0;

            for (int channel = 0; channel < packet.Channels; channel++)
            {
                var offset = frameOffset + (channel * bytesPerSample);
                if (offset < 0 || offset + bytesPerSample > packet.PcmData.Length)
                    continue;

                sum += ReadSample(packet, offset, bytesPerSample);
                channelsRead++;
            }

            return channelsRead == 0 ? 0 : Math.Clamp(sum / channelsRead, -1, 1);
        }

        private static double ReadSample(AudioPacket packet, int offset, int bytesPerSample)
        {
            var data = packet.PcmData;

            if (packet.IsFloatFormat && bytesPerSample >= 4)
                return Math.Clamp(BitConverter.ToSingle(data, offset), -1f, 1f);

            return packet.BitsPerSample switch
            {
                8 => ((data[offset] - 128) / 128.0),
                16 => BitConverter.ToInt16(data, offset) / 32768.0,
                24 => ReadInt24(data, offset) / 8388608.0,
                32 => BitConverter.ToInt32(data, offset) / 2147483648.0,
                _ => 0
            };
        }

        private static int ReadInt24(byte[] data, int offset)
        {
            var value = data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16);
            if ((value & 0x800000) != 0)
                value |= unchecked((int)0xff000000);

            return value;
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

            _phase += 0.045;
            VisualizerCanvas.Children.Clear();
            DrawAmbientBackground(width, height);

            switch (_style)
            {
                case "Bars":
                    DrawBars(width, height);
                    break;
                case "MirrorBars":
                    DrawMirrorBars(width, height);
                    break;
                case "Wave":
                    DrawWave(width, height);
                    break;
                case "Circle":
                    DrawCircle(width, height);
                    break;
                case "Dots":
                    DrawDots(width, height);
                    break;
                case "Tunnel":
                    DrawTunnel(width, height);
                    break;
                case "Pulse":
                    DrawPulse(width, height);
                    break;
            }
        }

        private void DrawAmbientBackground(double width, double height)
        {
            double level;
            lock (_audioGate)
                level = _beatLevel;

            var glow = new Ellipse
            {
                Width = Math.Min(width, height) * (0.45 + level * 0.5),
                Height = Math.Min(width, height) * (0.45 + level * 0.5),
                StrokeThickness = 1,
                Stroke = CreateBrush(0, 4, 0.18 + level * 0.22),
                Fill = CreateBrush(1, 4, 0.04 + level * 0.08)
            };

            Canvas.SetLeft(glow, (width - glow.Width) / 2);
            Canvas.SetTop(glow, (height - glow.Height) / 2);
            VisualizerCanvas.Children.Add(glow);
        }

        private void DrawBars(double width, double height)
        {
            double[] levels;
            lock (_audioGate)
                levels = (double[])_levels.Clone();

            int barCount = levels.Length;
            double barWidth = width / barCount;

            for (int i = 0; i < barCount; i++)
            {
                double magnitude = levels[i];
                double barHeight = magnitude * height * 0.9;
                double x = i * barWidth;
                double y = height - barHeight;

                var rect = new Rectangle
                {
                    Width = barWidth * 0.8,
                    Height = barHeight,
                    RadiusX = 2,
                    RadiusY = 2,
                    Fill = CreateBrush(i, barCount)
                };

                Canvas.SetLeft(rect, x + (barWidth - rect.Width) / 2);
                Canvas.SetTop(rect, y);
                VisualizerCanvas.Children.Add(rect);
            }
        }

        private void DrawMirrorBars(double width, double height)
        {
            double[] levels;
            lock (_audioGate)
                levels = (double[])_levels.Clone();

            int barCount = levels.Length;
            double barWidth = width / barCount;
            double centerY = height / 2;

            for (int i = 0; i < barCount; i++)
            {
                double magnitude = levels[i];
                double barHeight = magnitude * height * 0.42;
                double x = i * barWidth;

                var rect = new Rectangle
                {
                    Width = barWidth * 0.78,
                    Height = Math.Max(1, barHeight * 2),
                    RadiusX = 2,
                    RadiusY = 2,
                    Fill = CreateBrush(i, barCount)
                };

                Canvas.SetLeft(rect, x + (barWidth - rect.Width) / 2);
                Canvas.SetTop(rect, centerY - barHeight);
                VisualizerCanvas.Children.Add(rect);
            }
        }

        private void DrawWave(double width, double height)
        {
            double[] samples;
            lock (_audioGate)
                samples = (double[])_waveSamples.Clone();

            int pointCount = samples.Length;
            double step = width / (pointCount - 1);
            double baseLine = height / 2;

            var polyline = new Polyline
            {
                StrokeThickness = 2,
                Stroke = CreateBrush(0, 1)
            };

            for (int i = 0; i < pointCount; i++)
            {
                double y = baseLine - (samples[i] * height * 0.45);
                double x = i * step;

                polyline.Points.Add(new global::Windows.Foundation.Point(x, y));
            }

            VisualizerCanvas.Children.Add(polyline);
        }

        private void DrawCircle(double width, double height)
        {
            double[] levels;
            lock (_audioGate)
                levels = (double[])_levels.Clone();

            double radius = Math.Min(width, height) / 3;
            var center = new global::Windows.Foundation.Point(width / 2, height / 2);

            int segmentCount = levels.Length;

            var polyline = new Polyline
            {
                StrokeThickness = 3,
                Stroke = CreateBrush(0, 1)
            };

            for (int i = 0; i <= segmentCount; i++)
            {
                double t = (double)i / segmentCount;
                double angle = t * Math.PI * 2;

                double magnitude = 0.8 + levels[i % levels.Length] * 0.7;
                double r = radius * magnitude;

                double x = center.X + Math.Cos(angle) * r;
                double y = center.Y + Math.Sin(angle) * r;

                polyline.Points.Add(new global::Windows.Foundation.Point(x, y));
            }

            VisualizerCanvas.Children.Add(polyline);
        }

        private void DrawDots(double width, double height)
        {
            double[] levels;
            lock (_audioGate)
                levels = (double[])_levels.Clone();

            int columns = 16;
            int rows = 8;
            double cellWidth = width / columns;
            double cellHeight = height / rows;

            for (int row = 0; row < rows; row++)
            {
                for (int column = 0; column < columns; column++)
                {
                    var levelIndex = Math.Min(levels.Length - 1, column * levels.Length / columns);
                    var rowThreshold = 1.0 - ((row + 1) / (double)rows);
                    var active = levels[levelIndex] >= rowThreshold;
                    var size = Math.Min(cellWidth, cellHeight) * (active ? 0.42 + levels[levelIndex] * 0.28 : 0.16);

                    var dot = new Ellipse
                    {
                        Width = size,
                        Height = size,
                        Fill = CreateBrush(column + row, columns + rows, active ? 0.95 : 0.2)
                    };

                    Canvas.SetLeft(dot, column * cellWidth + (cellWidth - size) / 2);
                    Canvas.SetTop(dot, row * cellHeight + (cellHeight - size) / 2);
                    VisualizerCanvas.Children.Add(dot);
                }
            }
        }

        private void DrawTunnel(double width, double height)
        {
            double beat;
            lock (_audioGate)
                beat = _beatLevel;

            var centerX = width / 2;
            var centerY = height / 2;
            var maxRadius = Math.Min(width, height) * 0.46;
            const int rings = 12;

            for (int i = rings - 1; i >= 0; i--)
            {
                var t = (i + ((_phase * 0.55) % 1)) / rings;
                var radius = maxRadius * t * (0.75 + beat * 0.6);
                var thickness = 1.0 + beat * 5.0 + (rings - i) * 0.08;

                var ring = new Ellipse
                {
                    Width = radius * 2,
                    Height = radius * 2,
                    StrokeThickness = thickness,
                    Stroke = CreateBrush(i, rings, 0.18 + t * 0.72),
                    Fill = new SolidColorBrush(Microsoft.UI.Colors.Transparent)
                };

                Canvas.SetLeft(ring, centerX - radius);
                Canvas.SetTop(ring, centerY - radius);
                VisualizerCanvas.Children.Add(ring);
            }
        }

        private void DrawPulse(double width, double height)
        {
            double[] levels;
            double beat;
            lock (_audioGate)
            {
                levels = (double[])_levels.Clone();
                beat = _beatLevel;
            }

            var center = new global::Windows.Foundation.Point(width / 2, height / 2);
            var radius = Math.Min(width, height) * (0.12 + beat * 0.34);
            var core = new Ellipse
            {
                Width = radius * 2,
                Height = radius * 2,
                Fill = CreateBrush(1, 3, 0.9),
                Stroke = CreateBrush(0, 3),
                StrokeThickness = 2 + beat * 8
            };

            Canvas.SetLeft(core, center.X - radius);
            Canvas.SetTop(core, center.Y - radius);
            VisualizerCanvas.Children.Add(core);

            for (int i = 0; i < levels.Length; i += 4)
            {
                var angle = (i / (double)levels.Length) * Math.PI * 2 + _phase;
                var length = Math.Min(width, height) * (0.22 + levels[i] * 0.36);
                var line = new Line
                {
                    X1 = center.X + Math.Cos(angle) * radius,
                    Y1 = center.Y + Math.Sin(angle) * radius,
                    X2 = center.X + Math.Cos(angle) * (radius + length),
                    Y2 = center.Y + Math.Sin(angle) * (radius + length),
                    Stroke = CreateBrush(i, levels.Length, 0.35 + levels[i] * 0.65),
                    StrokeThickness = 1 + levels[i] * 5
                };

                VisualizerCanvas.Children.Add(line);
            }
        }

        private SolidColorBrush CreateBrush(int index, int total, double opacity = 1.0)
        {
            var color = _colorTheme switch
            {
                "Fire" => index % 3 == 0 ? Microsoft.UI.Colors.OrangeRed : index % 3 == 1 ? Microsoft.UI.Colors.Gold : Microsoft.UI.Colors.HotPink,
                "Neon" => index % 3 == 0 ? Microsoft.UI.Colors.Lime : index % 3 == 1 ? Microsoft.UI.Colors.Fuchsia : Microsoft.UI.Colors.Cyan,
                "Ocean" => index % 3 == 0 ? Microsoft.UI.Colors.DeepSkyBlue : index % 3 == 1 ? Microsoft.UI.Colors.MediumSeaGreen : Microsoft.UI.Colors.Aquamarine,
                "Mono" => Microsoft.UI.Colors.White,
                _ => index % 3 == 0 ? Microsoft.UI.Colors.DeepSkyBlue : index % 3 == 1 ? Microsoft.UI.Colors.DodgerBlue : Microsoft.UI.Colors.LightSkyBlue
            };

            return new SolidColorBrush(color) { Opacity = Math.Clamp(opacity, 0, 1) };
        }

        private void StyleComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (StyleComboBox.SelectedItem is ComboBoxItem item &&
                item.Tag is string style)
            {
                _style = style;
                SaveSetting(SettingsStyleKey, style);
                DrawFrame();
            }
        }

        private void ColorComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ColorComboBox.SelectedItem is ComboBoxItem item &&
                item.Tag is string theme)
            {
                _colorTheme = theme;
                SaveSetting(SettingsColorThemeKey, theme);
                DrawFrame();
            }
        }

        private void SensitivitySlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            _sensitivityPercent = Math.Clamp(Math.Round(e.NewValue), 0, 100);
            _sensitivity = _sensitivityPercent / 100.0;

            if (SensitivityText != null)
                SensitivityText.Text = $"Sensitivity {(int)_sensitivityPercent}%";

            if (!_isLoadingSettings)
                SaveSetting(SettingsSensitivityKey, _sensitivityPercent);
        }

        private void SmoothingSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            _smoothingPercent = Math.Clamp(Math.Round(e.NewValue), 0, 100);
            _smoothing = _smoothingPercent / 100.0;

            if (SmoothingText != null)
                SmoothingText.Text = $"Smoothing {(int)_smoothingPercent}%";

            if (!_isLoadingSettings)
                SaveSetting(SettingsSmoothingKey, _smoothingPercent);
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            SaveCurrentSettings();
            HintText.Text = "Visualizer settings saved.";
        }

        private void ResetButton_Click(object sender, RoutedEventArgs e)
        {
            _isLoadingSettings = true;

            try
            {
                _style = "Bars";
                _colorTheme = "Sky";
                _sensitivityPercent = 100;
                _sensitivity = 1.0;
                _smoothingPercent = 82;
                _smoothing = 0.82;

                SelectComboBoxItemByTag(StyleComboBox, _style);
                SelectComboBoxItemByTag(ColorComboBox, _colorTheme);
                SensitivitySlider.Value = _sensitivityPercent;
                SmoothingSlider.Value = _smoothingPercent;
                SensitivityText.Text = "Sensitivity 100%";
                SmoothingText.Text = "Smoothing 82%";

                SaveCurrentSettings();
            }
            finally
            {
                _isLoadingSettings = false;
            }

            DrawFrame();
        }

        private void RestoreSettings()
        {
            _isLoadingSettings = true;

            try
            {
                var settings = ApplicationData.Current.LocalSettings;

                if (TryReadString(settings, SettingsStyleKey, out var style))
                {
                    _style = style;
                    SelectComboBoxItemByTag(StyleComboBox, style);
                }

                if (TryReadString(settings, SettingsColorThemeKey, out var colorTheme))
                {
                    _colorTheme = colorTheme;
                    SelectComboBoxItemByTag(ColorComboBox, colorTheme);
                }

                if (TryReadDouble(settings, SettingsSensitivityKey, out var sensitivityPercent))
                {
                    _sensitivityPercent = Math.Clamp(Math.Round(sensitivityPercent), 0, 100);
                    _sensitivity = _sensitivityPercent / 100.0;
                    SensitivitySlider.Value = _sensitivityPercent;
                    SensitivityText.Text = $"Sensitivity {(int)_sensitivityPercent}%";
                }

                if (TryReadDouble(settings, SettingsSmoothingKey, out var smoothingPercent))
                {
                    _smoothingPercent = Math.Clamp(Math.Round(smoothingPercent), 0, 100);
                    _smoothing = _smoothingPercent / 100.0;
                    SmoothingSlider.Value = _smoothingPercent;
                    SmoothingText.Text = $"Smoothing {(int)_smoothingPercent}%";
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Visualizer settings restore failed: " + ex.Message);
            }
            finally
            {
                _isLoadingSettings = false;
            }
        }

        private void SaveCurrentSettings()
        {
            SaveSetting(SettingsStyleKey, _style);
            SaveSetting(SettingsColorThemeKey, _colorTheme);
            SaveSetting(SettingsSensitivityKey, _sensitivityPercent);
            SaveSetting(SettingsSmoothingKey, _smoothingPercent);
        }

        private static void SelectComboBoxItemByTag(ComboBox comboBox, string tag)
        {
            foreach (var item in comboBox.Items)
            {
                if (item is ComboBoxItem comboBoxItem &&
                    comboBoxItem.Tag is string itemTag &&
                    string.Equals(itemTag, tag, StringComparison.Ordinal))
                {
                    comboBox.SelectedItem = comboBoxItem;
                    return;
                }
            }
        }

        private static bool TryReadString(ApplicationDataContainer settings, string key, out string value)
        {
            value = "";

            if (!settings.Values.TryGetValue(key, out var raw) || raw == null)
                return false;

            value = raw.ToString() ?? "";
            return !string.IsNullOrWhiteSpace(value);
        }

        private static bool TryReadDouble(ApplicationDataContainer settings, string key, out double value)
        {
            value = 0;

            if (!settings.Values.TryGetValue(key, out var raw) || raw == null)
                return false;

            if (raw is double d)
            {
                value = d;
                return true;
            }

            if (raw is int i)
            {
                value = i;
                return true;
            }

            return double.TryParse(raw.ToString(), out value);
        }

        private static void SaveSetting(string key, object value)
        {
            try
            {
                ApplicationData.Current.LocalSettings.Values[key] = value;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Visualizer setting save failed: " + ex.Message);
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
            _isNavigatedFrom = true;
            _timer.Stop();
            _ = StopAudioCaptureAsync();
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            _isNavigatedFrom = false;
            await StartAudioCaptureAsync();
            _timer.Start();
        }

        private void VisualizerPage_Unloaded(object sender, RoutedEventArgs e)
        {
            if (_isNavigatedFrom)
                return;

            _timer.Stop();
            _ = StopAudioCaptureAsync();
        }
    }
}
