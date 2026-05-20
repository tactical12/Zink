using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using Windows.Storage;

namespace Zink.Pages
{
    public sealed partial class CustomisePage : Page
    {
        private static ApplicationDataContainer Settings => ApplicationData.Current.LocalSettings;

        // Section keys
        private const string K_ShowHeroInsights = "Dash_ShowHeroInsights";
        private const string K_ShowPowerTools = "Dash_ShowPowerTools";
        private const string K_ShowRecentActivity = "Dash_ShowRecentActivity";

        // Tile keys (Power tools)
        private static string K_Tile(string id) => $"Dash_Tile_{id}";

        private bool _loading;

        public CustomisePage()
        {
            InitializeComponent();
            LoadSettingsToUI();
        }

        private void LoadSettingsToUI()
        {
            _loading = true;

            ToggleHeroInsights.IsOn = ReadBool(K_ShowHeroInsights, defaultValue: true);
            TogglePowerTools.IsOn = ReadBool(K_ShowPowerTools, defaultValue: true);
            ToggleRecentActivity.IsOn = ReadBool(K_ShowRecentActivity, defaultValue: true);

            // Tiles
            Tile_MusicPlayer.IsOn = ReadBool(K_Tile("MusicPlayer"), true);
            Tile_VideoPlayer.IsOn = ReadBool(K_Tile("VideoPlayer"), true);
            Tile_Radio.IsOn = ReadBool(K_Tile("Radio"), true);

            Tile_VideoLibrary.IsOn = ReadBool(K_Tile("VideoLibrary"), true);
            Tile_MusicLibrary.IsOn = ReadBool(K_Tile("MusicLibrary"), true);

            Tile_Equalizer.IsOn = ReadBool(K_Tile("Equalizer"), true);
            Tile_Visualizer.IsOn = ReadBool(K_Tile("Visualizer"), true);

            Tile_Settings.IsOn = ReadBool(K_Tile("Settings"), true);
            Tile_VersionHistory.IsOn = ReadBool(K_Tile("VersionHistory"), true);

            Tile_Spotify.IsOn = ReadBool(K_Tile("Spotify"), true);
            Tile_Twitch.IsOn = ReadBool(K_Tile("Twitch"), true); // ? requires Tile_Twitch in XAML
            Tile_Xbox.IsOn = ReadBool(K_Tile("Xbox"), true);
            Tile_Discord.IsOn = ReadBool(K_Tile("Discord"), true);
            Tile_YouTube.IsOn = ReadBool(K_Tile("YouTube"), true);

            Tile_Customise.IsOn = ReadBool(K_Tile("Customise"), true);

            _loading = false;
        }

        private static bool ReadBool(string key, bool defaultValue)
        {
            try
            {
                if (Settings.Values.TryGetValue(key, out var v) && v is bool b)
                    return b;
            }
            catch { }
            return defaultValue;
        }

        private static void WriteBool(string key, bool value)
        {
            try { Settings.Values[key] = value; } catch { }
        }

        private void ToggleHeroInsights_Toggled(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            WriteBool(K_ShowHeroInsights, ToggleHeroInsights.IsOn);
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (Frame.CanGoBack)
                {
                    Frame.GoBack();
                }
                else
                {
                    Frame.Navigate(typeof(AppCustomizationPage));
                }
            }
            catch { }
        }

        private void TogglePowerTools_Toggled(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            WriteBool(K_ShowPowerTools, TogglePowerTools.IsOn);
        }

        private void ToggleRecentActivity_Toggled(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            WriteBool(K_ShowRecentActivity, ToggleRecentActivity.IsOn);
        }

        private void Tile_Toggled(object sender, RoutedEventArgs e)
        {
            if (_loading) return;

            if (sender is ToggleSwitch ts && ts.Name != null)
            {
                var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Tile_MusicPlayer"] = "MusicPlayer",
                    ["Tile_VideoPlayer"] = "VideoPlayer",
                    ["Tile_Radio"] = "Radio",
                    ["Tile_VideoLibrary"] = "VideoLibrary",
                    ["Tile_MusicLibrary"] = "MusicLibrary",
                    ["Tile_Equalizer"] = "Equalizer",
                    ["Tile_Visualizer"] = "Visualizer",
                    ["Tile_Settings"] = "Settings",
                    ["Tile_VersionHistory"] = "VersionHistory",
                    ["Tile_Spotify"] = "Spotify",
                    ["Tile_Twitch"] = "Twitch", // ?
                    ["Tile_Xbox"] = "Xbox",
                    ["Tile_Discord"] = "Discord",
                    ["Tile_YouTube"] = "YouTube",
                    ["Tile_Customise"] = "Customise",
                };

                if (map.TryGetValue(ts.Name, out var id))
                    WriteBool(K_Tile(id), ts.IsOn);
            }
        }

        private void ResetDefaults_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Settings.Values[K_ShowHeroInsights] = true;
                Settings.Values[K_ShowPowerTools] = true;
                Settings.Values[K_ShowRecentActivity] = true;

                var ids = new[]
                {
                    "MusicPlayer","VideoPlayer","Radio",
                    "VideoLibrary","MusicLibrary",
                    "Equalizer","Visualizer",
                    "Settings","VersionHistory",
                    "Spotify","Twitch","Xbox","Discord","YouTube",
                    "Customise"
                };

                foreach (var id in ids)
                    Settings.Values[K_Tile(id)] = true;
            }
            catch { }

            LoadSettingsToUI();
        }
    }
}
