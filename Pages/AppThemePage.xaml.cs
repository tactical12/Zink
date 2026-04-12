using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage;

namespace Zink.Pages
{
    public sealed partial class AppThemePage : Page
    {
        private bool _loaded;

        public AppThemePage()
        {
            InitializeComponent();
            LoadThemeSelection();
        }

        private void LoadThemeSelection()
        {
            try
            {
                var value = ApplicationData.Current.LocalSettings.Values["Zink.Theme"] as string ?? "Default";
                ApplySelectionUI(value);
                _loaded = true;
            }
            catch
            {
                _loaded = true;
            }
        }

        private void ApplySelectionUI(string value)
        {
            // Update current label
            CurrentThemeText.Text = value switch
            {
                "Light" => "Current: Light",
                "Dark" => "Current: Dark",
                _ => "Current: Use system setting"
            };

            // Reset borders to default
            ResetTileBorders();

            // Highlight selected tile (accent border)
            var selected = value switch
            {
                "Light" => LightThemeButton,
                "Dark" => DarkThemeButton,
                _ => SystemThemeButton
            };

            try
            {
                selected.BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemControlForegroundAccentBrush"];
                selected.BorderThickness = new Thickness(2);
            }
            catch
            {
                // If accent resource isn't found, keep default border.
            }
        }

        private void ResetTileBorders()
        {
            LightThemeButton.BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"];
            DarkThemeButton.BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"];
            SystemThemeButton.BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"];

            LightThemeButton.BorderThickness = new Thickness(1);
            DarkThemeButton.BorderThickness = new Thickness(1);
            SystemThemeButton.BorderThickness = new Thickness(1);
        }

        private void SaveAndApply(string value)
        {
            if (!_loaded) return;

            try
            {
                ApplicationData.Current.LocalSettings.Values["Zink.Theme"] = value;
            }
            catch { }

            var theme = value switch
            {
                "Light" => ElementTheme.Light,
                "Dark" => ElementTheme.Dark,
                _ => ElementTheme.Default
            };

            try
            {
                if (App.MainWindow is MainWindow mw)
                {
                    mw.ApplyAppTheme(theme);
                }
            }
            catch { }

            ApplySelectionUI(value);
        }

        private void SystemThemeButton_Click(object sender, RoutedEventArgs e)
        {
            SaveAndApply("Default");
        }

        private void LightThemeButton_Click(object sender, RoutedEventArgs e)
        {
            SaveAndApply("Light");
        }

        private void DarkThemeButton_Click(object sender, RoutedEventArgs e)
        {
            SaveAndApply("Dark");
        }
    }
}
