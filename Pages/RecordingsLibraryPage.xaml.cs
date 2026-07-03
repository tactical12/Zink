using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using Windows.Storage;
using Windows.System;

namespace Zink.Pages
{
    public sealed partial class RecordingsLibraryPage : Page
    {
        public sealed class RecordingItem
        {
            public string Name { get; set; } = "";
            public string Details { get; set; } = "";
            public string Path { get; set; } = "";
        }

        private readonly ObservableCollection<RecordingItem> _items = new();
        private string? _folderPath;

        public RecordingsLibraryPage()
        {
            InitializeComponent();
            RecordingsList.ItemsSource = _items;
            Loaded += RecordingsLibraryPage_Loaded;
        }

        private async void RecordingsLibraryPage_Loaded(object sender, RoutedEventArgs e)
        {
            await LoadAsync();
        }

        private async System.Threading.Tasks.Task LoadAsync()
        {
            _items.Clear();

            try
            {
                var folder = await KnownFolders.VideosLibrary.CreateFolderAsync("Zink Captures", CreationCollisionOption.OpenIfExists);
                _folderPath = folder.Path;

                var dir = new DirectoryInfo(folder.Path);
                foreach (var file in dir.EnumerateFiles("*.mp4").OrderByDescending(f => f.LastWriteTimeUtc))
                {
                    _items.Add(new RecordingItem
                    {
                        Name = file.Name,
                        Details = $"{FormatBytes(file.Length)} - {file.LastWriteTime:dd MMM yyyy HH:mm}",
                        Path = file.FullName
                    });
                }

                if (_items.Count == 0)
                    ShowStatus("No recordings yet. Save a replay clip or manual recording and it will appear here.", InfoBarSeverity.Informational);
                else
                    StatusBar.IsOpen = false;
            }
            catch (Exception ex)
            {
                ShowStatus("Could not load recordings: " + ex.Message, InfoBarSeverity.Error);
            }
        }

        private async void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            await LoadAsync();
        }

        private async void OpenFolderButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var folder = await KnownFolders.VideosLibrary.CreateFolderAsync("Zink Captures", CreationCollisionOption.OpenIfExists);
                await Launcher.LaunchFolderAsync(folder);
            }
            catch (Exception ex)
            {
                ShowStatus("Could not open folder: " + ex.Message, InfoBarSeverity.Error);
            }
        }

        private async void OpenRecordingButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is string path)
                await OpenPathAsync(path);
        }

        private async void RecordingsList_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is RecordingItem item)
                await OpenPathAsync(item.Path);
        }

        private async System.Threading.Tasks.Task OpenPathAsync(string path)
        {
            try
            {
                if (!File.Exists(path))
                {
                    ShowStatus("That recording no longer exists. Refreshing the library.", InfoBarSeverity.Warning);
                    await LoadAsync();
                    return;
                }

                var file = await StorageFile.GetFileFromPathAsync(path);
                await Launcher.LaunchFileAsync(file);
            }
            catch (Exception ex)
            {
                ShowStatus("Could not open recording: " + ex.Message, InfoBarSeverity.Error);
            }
        }

        private void ShowStatus(string message, InfoBarSeverity severity)
        {
            StatusBar.Message = message;
            StatusBar.Severity = severity;
            StatusBar.IsOpen = true;
        }

        private static string FormatBytes(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB" };
            double value = bytes;
            int unit = 0;
            while (value >= 1024 && unit < units.Length - 1)
            {
                value /= 1024;
                unit++;
            }

            return $"{value:0.#} {units[unit]}";
        }
    }
}
