using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.Storage;
using Zink.Services;

namespace Zink.Pages
{
    public sealed partial class EqualizerPage : Page
    {
        private readonly ApplicationDataContainer _settings =
            ApplicationData.Current.LocalSettings;

        // True while we are programmatically loading values from settings
        // so that PresetComboBox_SelectionChanged can ignore those events.
        private bool _isLoadingSettings;

        // True while we are applying a preset or reset so slider changes
        // don’t mark the preset as "Custom" or spam SaveSettings.
        private bool _isApplyingPreset;

        // True only after the page is fully loaded
        private bool _isUiReady;

        public EqualizerPage()
        {
            this.InitializeComponent();

            // keep this page instance alive between navigations
            this.NavigationCacheMode = NavigationCacheMode.Required;

            // Ensure the Loaded handler is wired up
            this.Loaded += EqualizerPage_Loaded;

            // Load previously saved EQ settings into the UI + engine
            LoadSettings();
        }

        // Popup describing hardware EQ support (with exact bands)
        // - Shows every time by default
        // - User can tick "Do not show again"
        // - OK button is Primary (blue)
        private async void EqualizerPage_Loaded(object sender, RoutedEventArgs e)
        {
            // Mark that the visual tree is now ready; sliders exist
            _isUiReady = true;

            const string flagKey = "EQ_DeviceInfoHidden";

            // If user previously chose "Do not show again", skip dialog
            if (_settings.Values[flagKey] is bool hidden && hidden)
                return;

            var engine = AudioGraphMusicEngine.Instance;

            // Ask the engine for the real hardware bands
            double[] freqs = await engine.GetHardwareBandFrequenciesAsync();
            int bandCount = freqs.Length;

            // If we couldn't get band info, don't show anything
            if (bandCount <= 0)
            {
                return;
            }

            // Build a nice "100 Hz, 300 Hz, 1 kHz" style string
            var parts = new List<string>();
            for (int i = 0; i < bandCount; i++)
            {
                double f = freqs[i];
                string s = f >= 1000
                    ? $"{f / 1000.0:0.#} kHz"
                    : $"{f:0} Hz";
                parts.Add(s);
            }

            string freqText = string.Join(", ", parts);

            string message;
            if (bandCount == 1)
            {
                message =
                    "Your device’s equalizer has 1 hardware band.\n\n" +
                    $"Band: {freqText}.\n\n" +
                    "Zink’s 10 EQ sliders are automatically mapped to this band, " +
                    "so all sliders share the same underlying effect.";
            }
            else
            {
                message =
                    $"Your device’s equalizer has {bandCount} hardware bands.\n\n" +
                    $"Bands: {freqText}.\n\n" +
                    "Zink automatically maps the 10 EQ sliders to these hardware bands, " +
                    "so some sliders may share the same underlying control depending on the device.";
            }

            // Build dialog content: text + "Do not show again" checkbox
            var textBlock = new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.WrapWholeWords,
                Margin = new Thickness(0, 0, 0, 12)
            };

            var doNotShowCheckBox = new CheckBox
            {
                Content = "Do not show this message again",
                Margin = new Thickness(0, 0, 0, 0)
            };

            var stack = new StackPanel();
            stack.Children.Add(textBlock);
            stack.Children.Add(doNotShowCheckBox);

            var dialog = new ContentDialog
            {
                Title = "Equalizer hardware support",
                Content = stack,
                // Make OK the primary (blue) button
                PrimaryButtonText = "OK",
                DefaultButton = ContentDialogButton.Primary
            };

            // Attach XamlRoot safely
            if (Content is FrameworkElement root)
            {
                dialog.XamlRoot = root.XamlRoot;
            }

            dialog.PrimaryButtonClick += (_, __) =>
            {
                // If user checked "Do not show again", remember it
                if (doNotShowCheckBox.IsChecked == true)
                {
                    _settings.Values[flagKey] = true;
                }
            };

            try
            {
                await dialog.ShowAsync();
            }
            catch
            {
                // If the dialog can't show, just skip it
            }
        }

        private void LoadSettings()
        {
            _isLoadingSettings = true;
            try
            {
                // Master gain
                double masterGain = ReadDouble("EQ_MasterGain", 0);
                MasterGainSlider.Value = masterGain;
                MasterGainLabel.Text = $"{masterGain:0} dB";

                // Bands
                SetSliderFromSetting(Band31Slider, "EQ_31", Band31Label);
                SetSliderFromSetting(Band62Slider, "EQ_62", Band62Label);
                SetSliderFromSetting(Band125Slider, "EQ_125", Band125Label);
                SetSliderFromSetting(Band250Slider, "EQ_250", Band250Label);
                SetSliderFromSetting(Band500Slider, "EQ_500", Band500Label);
                SetSliderFromSetting(Band1kSlider, "EQ_1k", Band1kLabel);
                SetSliderFromSetting(Band2kSlider, "EQ_2k", Band2kLabel);
                SetSliderFromSetting(Band4kSlider, "EQ_4k", Band4kLabel);
                SetSliderFromSetting(Band8kSlider, "EQ_8k", Band8kLabel);
                SetSliderFromSetting(Band16kSlider, "EQ_16k", Band16kLabel);

                // Preset (store name only)
                string preset = _settings.Values["EQ_Preset"] as string ?? "Flat";

                // Select the matching preset item (if present)
                foreach (ComboBoxItem item in PresetComboBox.Items)
                {
                    if ((item.Tag as string) == preset)
                    {
                        PresetComboBox.SelectedItem = item;
                        break;
                    }
                }

                // Apply initial EQ to engine (whatever gains we just loaded)
                ApplyEqualizer();
            }
            finally
            {
                _isLoadingSettings = false;
            }
        }

        private void SetSliderFromSetting(Slider slider, string key, TextBlock label)
        {
            double value = ReadDouble(key, 0);
            slider.Value = value;
            label.Text = $"{value:0} dB";
        }

        private double ReadDouble(string key, double fallback)
        {
            if (_settings.Values[key] is double d)
                return d;

            if (_settings.Values[key] is int i)
                return i;

            // (In case a future version writes a long)
            if (_settings.Values[key] is long l)
                return l;

            return fallback;
        }

        private void SaveSettings()
        {
            _settings.Values["EQ_MasterGain"] = MasterGainSlider.Value;
            _settings.Values["EQ_31"] = Band31Slider.Value;
            _settings.Values["EQ_62"] = Band62Slider.Value;
            _settings.Values["EQ_125"] = Band125Slider.Value;
            _settings.Values["EQ_250"] = Band250Slider.Value;
            _settings.Values["EQ_500"] = Band500Slider.Value;
            _settings.Values["EQ_1k"] = Band1kSlider.Value;
            _settings.Values["EQ_2k"] = Band2kSlider.Value;
            _settings.Values["EQ_4k"] = Band4kSlider.Value;
            _settings.Values["EQ_8k"] = Band8kSlider.Value;
            _settings.Values["EQ_16k"] = Band16kSlider.Value;

            if (PresetComboBox.SelectedItem is ComboBoxItem item &&
                item.Tag is string presetName)
            {
                _settings.Values["EQ_Preset"] = presetName;
            }

            ApplyEqualizer();
        }

        private void ApplyEqualizer()
        {
            // Send EQ values to the AudioGraph engine
            var engine = AudioGraphMusicEngine.Instance;

            // Master gain
            engine.SetMasterGain(MasterGainSlider.Value);

            // Bands (10 sliders)
            var gains = new double[]
            {
                Band31Slider.Value,
                Band62Slider.Value,
                Band125Slider.Value,
                Band250Slider.Value,
                Band500Slider.Value,
                Band1kSlider.Value,
                Band2kSlider.Value,
                Band4kSlider.Value,
                Band8kSlider.Value,
                Band16kSlider.Value
            };

            engine.SetBandGains(gains);
        }

        // Small helper to show info dialogs
        private async Task ShowInfoDialogAsync(string title, string message)
        {
            var dialog = new ContentDialog
            {
                Title = title,
                Content = message,
                PrimaryButtonText = "OK",
                DefaultButton = ContentDialogButton.Primary
            };

            if (Content is FrameworkElement root)
                dialog.XamlRoot = root.XamlRoot;

            try
            {
                await dialog.ShowAsync();
            }
            catch
            {
                // ignore dialog failures
            }
        }

        // SAVE button – manual save (auto save still works too)
        private async void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            SaveSettings();

            // Show "changes saved" message
            await ShowInfoDialogAsync(
                "Changes saved",
                "Your equalizer changes have been saved. " +
                "When you navigate away from this page and come back, the settings will be kept.");
        }

        private void BandSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (sender is Slider slider)
            {
                if (slider == Band31Slider) Band31Label.Text = $"{slider.Value:0} dB";
                else if (slider == Band62Slider) Band62Label.Text = $"{slider.Value:0} dB";
                else if (slider == Band125Slider) Band125Label.Text = $"{slider.Value:0} dB";
                else if (slider == Band250Slider) Band250Label.Text = $"{slider.Value:0} dB";
                else if (slider == Band500Slider) Band500Label.Text = $"{slider.Value:0} dB";
                else if (slider == Band1kSlider) Band1kLabel.Text = $"{slider.Value:0} dB";
                else if (slider == Band2kSlider) Band2kLabel.Text = $"{slider.Value:0} dB";
                else if (slider == Band4kSlider) Band4kLabel.Text = $"{slider.Value:0} dB";
                else if (slider == Band8kSlider) Band8kLabel.Text = $"{slider.Value:0} dB";
                else if (slider == Band16kSlider) Band16kLabel.Text = $"{slider.Value:0} dB";
            }

            // If the user is manually moving sliders, mark preset as "Custom"
            if (!_isApplyingPreset)
            {
                _settings.Values["EQ_Preset"] = "Custom";

                // If you have a "Custom" item in the combo, select it:
                foreach (ComboBoxItem item in PresetComboBox.Items)
                {
                    if ((item.Tag as string) == "Custom")
                    {
                        PresetComboBox.SelectedItem = item;
                        break;
                    }
                }
            }

            SaveSettings();
        }

        private void MasterGainSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            MasterGainLabel.Text = $"{MasterGainSlider.Value:0} dB";
            SaveSettings();
        }

        // Reset to Default – flat curve + Flat preset
        private async void ResetButton_Click(object sender, RoutedEventArgs e)
        {
            _isApplyingPreset = true;
            try
            {
                MasterGainSlider.Value = 0;
                MasterGainLabel.Text = "0 dB";

                Band31Slider.Value = 0;
                Band62Slider.Value = 0;
                Band125Slider.Value = 0;
                Band250Slider.Value = 0;
                Band500Slider.Value = 0;
                Band1kSlider.Value = 0;
                Band2kSlider.Value = 0;
                Band4kSlider.Value = 0;
                Band8kSlider.Value = 0;
                Band16kSlider.Value = 0;

                foreach (ComboBoxItem item in PresetComboBox.Items)
                {
                    if ((item.Tag as string) == "Flat")
                    {
                        PresetComboBox.SelectedItem = item;
                        break;
                    }
                }
            }
            finally
            {
                _isApplyingPreset = false;
                SaveSettings();
            }

            // Show "reset" message
            await ShowInfoDialogAsync(
                "Changes reset",
                "Your equalizer changes have been reset back to the default flat settings.");
        }

        private void PresetComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Ignore events fired during XAML construction / before sliders are ready
            // or while we are loading settings programmatically.
            if (!_isUiReady || _isLoadingSettings)
                return;

            if (PresetComboBox.SelectedItem is not ComboBoxItem item ||
                item.Tag is not string presetName)
                return;

            _isApplyingPreset = true;
            try
            {
                ApplyPresetCurve(presetName);
            }
            finally
            {
                _isApplyingPreset = false;
                SaveSettings();
            }
        }

        private void ApplyPresetCurve(string preset)
        {
            // Safety: if for some reason sliders are still null, just bail out
            if (Band31Slider == null)
                return;

            double[] flat = { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
            double[] bassBoost = { 6, 5, 4, 2, 1, 0, -1, -2, -3, -4 };
            double[] trebleBoost = { -4, -3, -2, -1, 0, 1, 2, 4, 5, 6 };
            double[] rock = { 4, 3, 1, 0, -1, -1, 1, 3, 4, 5 };
            double[] pop = { -1, 2, 3, 2, 0, 1, 2, 3, 3, 2 };
            double[] classical = { 2, 1, 0, -1, -2, 0, 2, 3, 4, 4 };

            double[] curve = preset switch
            {
                "BassBoost" => bassBoost,
                "TrebleBoost" => trebleBoost,
                "Rock" => rock,
                "Pop" => pop,
                "Classical" => classical,
                _ => flat
            };

            Band31Slider.Value = curve[0];
            Band62Slider.Value = curve[1];
            Band125Slider.Value = curve[2];
            Band250Slider.Value = curve[3];
            Band500Slider.Value = curve[4];
            Band1kSlider.Value = curve[5];
            Band2kSlider.Value = curve[6];
            Band4kSlider.Value = curve[7];
            Band8kSlider.Value = curve[8];
            Band16kSlider.Value = curve[9];
        }
    }
}
