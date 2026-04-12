using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using System.Collections.ObjectModel;
using System.ComponentModel;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.AccessCache;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using TagLib;
using System.IO;
using System;

namespace Zink.Pages
{
    public sealed partial class MusicLibraryPage : Page, INotifyPropertyChanged
    {
        public ObservableCollection<SongInfo> Songs { get; set; } = new();

        public string PickedFolderPath
        {
            get => _pickedFolderPath;
            set
            {
                _pickedFolderPath = value;
                OnPropertyChanged(nameof(PickedFolderPath));
            }
        }

        private string _pickedFolderToken;
        private string _pickedFolderPath;
        private StorageFolder _pickedFolder;
        private DispatcherTimer _watcherTimer;

        // Prevents multiple prompts in the same page instance
        private bool _emptyLibraryPromptShown;

        // NEW: if we need to show the dialog but XamlRoot isn't ready yet, show it after Loaded
        private bool _pendingShowEmptyDialog;

        public event PropertyChangedEventHandler PropertyChanged;

        public MusicLibraryPage()
        {
            this.InitializeComponent();
            this.DataContext = this;

            // NEW: ensure we can show the dialog after the page is actually in the tree
            this.Loaded += MusicLibraryPage_Loaded;
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            bool loadedAnySongs = false;

            if (ApplicationData.Current.LocalSettings.Values.TryGetValue("MusicLibraryFolderToken", out object tokenObj))
            {
                _pickedFolderToken = tokenObj as string;
                if (!string.IsNullOrEmpty(_pickedFolderToken) &&
                    StorageApplicationPermissions.FutureAccessList.ContainsItem(_pickedFolderToken))
                {
                    _pickedFolder = await StorageApplicationPermissions.FutureAccessList.GetFolderAsync(_pickedFolderToken);
                    if (_pickedFolder != null)
                    {
                        PickedFolderPath = _pickedFolder.Path;
                        await LoadSongsFromFolder(_pickedFolder);
                        StartWatcher();

                        loadedAnySongs = Songs.Count > 0;
                    }
                }
            }

            // If nothing loaded (no folder yet OR folder has no music), show the dialog once
            if (!loadedAnySongs && !_emptyLibraryPromptShown)
            {
                _emptyLibraryPromptShown = true;

                // NEW: If XamlRoot isn't ready yet, defer until Loaded event
                if (GetDialogXamlRoot() == null)
                {
                    _pendingShowEmptyDialog = true;
                }
                else
                {
                    await ShowEmptyLibraryDialogAsync();
                }
            }
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            StopWatcher();
            base.OnNavigatedFrom(e);
        }

        // NEW: Loaded handler to show dialog when XamlRoot becomes available
        private async void MusicLibraryPage_Loaded(object sender, RoutedEventArgs e)
        {
            if (_pendingShowEmptyDialog)
            {
                _pendingShowEmptyDialog = false;

                // Now the page is in the tree, XamlRoot should exist
                await ShowEmptyLibraryDialogAsync();
            }
        }

        // NEW: Safely resolve a XamlRoot for dialogs
        private Microsoft.UI.Xaml.XamlRoot GetDialogXamlRoot()
        {
            // Prefer the page XamlRoot if present
            if (this.XamlRoot != null)
                return this.XamlRoot;

            // Fallback to window content XamlRoot
            if (App.MainWindow?.Content is FrameworkElement fe && fe.XamlRoot != null)
                return fe.XamlRoot;

            return null;
        }

        // Dialog shown on page load when library is empty
        private async Task ShowEmptyLibraryDialogAsync()
        {
            var xamlRoot = GetDialogXamlRoot();
            if (xamlRoot == null)
            {
                // Should be rare now, but safe-guard: just don't show if still not available
                return;
            }

            var dialog = new ContentDialog
            {
                Title = "No music found",
                Content = "You don't have any music here. Would you like to add the folder where you have your music saved and add it to Zink so you can store it here to see, and play?",
                CloseButtonText = "No thanks",          // left
                PrimaryButtonText = "Yes, okay",        // right (blue)
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = xamlRoot
            };

            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                await PickFolderAndLoadAsync(showSelectedDialog: false);
            }
        }

        private async void PickFolderButton_Click(object sender, RoutedEventArgs e)
        {
            await PickFolderAndLoadAsync(showSelectedDialog: true);
        }

        // Shared picker logic (so the page-load dialog can trigger the same flow)
        private async Task PickFolderAndLoadAsync(bool showSelectedDialog)
        {
            Songs.Clear();
            StopWatcher();

            var picker = new FolderPicker();
            picker.SuggestedStartLocation = PickerLocationId.MusicLibrary;
            picker.FileTypeFilter.Add("*");

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            var folder = await picker.PickSingleFolderAsync();
            if (folder != null)
            {
                _pickedFolder = folder;

                _pickedFolderToken = StorageApplicationPermissions.FutureAccessList.Add(folder);
                ApplicationData.Current.LocalSettings.Values["MusicLibraryFolderToken"] = _pickedFolderToken;

                PickedFolderPath = folder.Path;

                if (showSelectedDialog)
                {
                    var xamlRoot = GetDialogXamlRoot();

                    // If for some reason it's still null, don't crash—just skip the dialog
                    if (xamlRoot != null)
                    {
                        var dialog = new ContentDialog
                        {
                            Title = "Music Folder Selected",
                            Content = $"You've selected:\n{folder.Path}\n\nYour music will now be added.",
                            CloseButtonText = "OK",
                            XamlRoot = xamlRoot
                        };
                        await dialog.ShowAsync();
                    }
                }

                await LoadSongsFromFolder(folder);
                StartWatcher();
            }
        }

        private async Task LoadSongsFromFolder(StorageFolder folder)
        {
            var files = await GetAllMusicFilesAsync(folder);
            Songs.Clear();
            foreach (var file in files)
            {
                var song = await LoadSongInfoAsync(file);
                if (song != null)
                    Songs.Add(song);
            }
        }

        private async Task<List<StorageFile>> GetAllMusicFilesAsync(StorageFolder folder)
        {
            var supportedTypes = new[] { ".mp3", ".flac", ".wav", ".m4a", ".aac", ".wma" };
            var files = new List<StorageFile>();
            var queue = new Queue<StorageFolder>();
            queue.Enqueue(folder);

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                try
                {
                    var fileList = await current.GetFilesAsync();
                    files.AddRange(fileList.Where(f => supportedTypes.Contains(Path.GetExtension(f.Name).ToLower())));
                    var subfolders = await current.GetFoldersAsync();
                    foreach (var sub in subfolders)
                        queue.Enqueue(sub);
                }
                catch { }
            }
            return files;
        }

        private async Task<SongInfo> LoadSongInfoAsync(StorageFile file)
        {
            try
            {
                using (var stream = await file.OpenStreamForReadAsync())
                {
                    var tagFile = TagLib.File.Create(new StreamFileAbstraction(file.Name, stream, stream));
                    var tag = tagFile.Tag;
                    var props = tagFile.Properties;

                    return new SongInfo
                    {
                        Title = string.IsNullOrEmpty(tag.Title) ? file.DisplayName : tag.Title,
                        Artist = tag.FirstPerformer ?? "",
                        Album = tag.Album ?? "",
                        Duration = props.Duration.ToString(@"mm\:ss"),
                        FilePath = file.Path
                    };
                }
            }
            catch
            {
                return null;
            }
        }

        private void StartWatcher()
        {
            StopWatcher();
            if (_pickedFolder == null) return;

            _watcherTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(10)
            };
            _watcherTimer.Tick += async (s, e) =>
            {
                await LoadSongsFromFolder(_pickedFolder);
            };
            _watcherTimer.Start();
        }

        private void StopWatcher()
        {
            if (_watcherTimer != null)
            {
                _watcherTimer.Stop();
                _watcherTimer = null;
            }
        }

        private void PlayAllButton_Click(object sender, RoutedEventArgs e)
        {
            if (Songs.Count == 0) return;

            LibraryData.PlaySongList(Songs.ToList());

            var main = (MainWindow)App.MainWindow;
            main.ContentFrame.Navigate(typeof(MusicPlayerPage), "autoplay");

            var playerItem = main.SidebarNav.MenuItems
                .OfType<NavigationViewItem>()
                .FirstOrDefault(i => (string)i.Tag == "MusicPlayer");
            if (playerItem != null)
                main.SidebarNav.SelectedItem = playerItem;
        }

        private void PlaySongButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.DataContext is SongInfo song)
            {
                LibraryData.PlaySongList(new List<SongInfo> { song });

                var main = (MainWindow)App.MainWindow;
                main.ContentFrame.Navigate(typeof(MusicPlayerPage), "autoplay");

                var playerItem = main.SidebarNav.MenuItems
                    .OfType<NavigationViewItem>()
                    .FirstOrDefault(i => (string)i.Tag == "MusicPlayer");
                if (playerItem != null)
                    main.SidebarNav.SelectedItem = playerItem;
            }
        }

        private void OnPropertyChanged(string name) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        public class SongInfo
        {
            public string Title { get; set; }
            public string Artist { get; set; }
            public string Album { get; set; }
            public string Duration { get; set; }
            public string FilePath { get; set; }
        }
    }

    public class StreamFileAbstraction : TagLib.File.IFileAbstraction
    {
        public StreamFileAbstraction(string name, Stream readStream, Stream writeStream)
        {
            Name = name;
            _readStream = readStream;
            _writeStream = writeStream;
        }

        public string Name { get; }

        private readonly Stream _readStream;
        private readonly Stream _writeStream;

        public Stream ReadStream => _readStream;
        public Stream WriteStream => _writeStream;

        public void CloseStream(Stream stream)
        {
            // Handled externally
        }
    }

    public static class LibraryData
    {
        public static List<MusicLibraryPage.SongInfo> CurrentQueue { get; private set; } = new();

        public static event EventHandler<List<MusicLibraryPage.SongInfo>> QueueOverrideRequested;

        public static void PlaySongList(List<MusicLibraryPage.SongInfo> songs)
        {
            CurrentQueue = songs;
            QueueOverrideRequested?.Invoke(null, songs);
        }

        public static bool HasSongs()
        {
            return CurrentQueue?.Any() == true;
        }
    }
}
