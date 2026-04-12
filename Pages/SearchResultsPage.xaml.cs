using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Zink.Services;
using static Zink.Pages.MusicLibraryPage;

namespace Zink.Pages
{
    public sealed partial class SearchResultsPage : Page
    {
        public ObservableCollection<SongResult> Songs { get; } = new();
        public ObservableCollection<AlbumResult> Albums { get; } = new();
        public ObservableCollection<ArtistResult> Artists { get; } = new();
        public ObservableCollection<VideoResult> Videos { get; } = new();
        public ObservableCollection<StationResult> Stations { get; } = new();

        private string _query = "";
        private CancellationTokenSource? _typingCts;

        public SearchResultsPage()
        {
            InitializeComponent();
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            _query = (e.Parameter as string) ?? "";
            _query = _query.Trim();

            // ? If opened from sidebar "Search" button, there is no query -> do NOT search ""
            if (string.IsNullOrWhiteSpace(_query))
            {
                QueryTextBlock.Text = "Search across Zink";
                try { SearchBox.Text = ""; } catch { }

                ClearResultsAndHideSections();
                EmptyState.Visibility = Visibility.Collapsed;
                return;
            }

            try { SearchBox.Text = _query; } catch { }

            QueryTextBlock.Text = $"Results for: \"{_query}\"";
            await RunSearchAsync(_query);
        }

        private void ClearResultsAndHideSections()
        {
            try
            {
                Songs.Clear();
                Albums.Clear();
                Artists.Clear();
                Videos.Clear();
                Stations.Clear();

                SongsSection.Visibility = Visibility.Collapsed;
                AlbumsSection.Visibility = Visibility.Collapsed;
                ArtistsSection.Visibility = Visibility.Collapsed;
                VideosSection.Visibility = Visibility.Collapsed;
                RadioSection.Visibility = Visibility.Collapsed;
            }
            catch { }
        }

        private void ShowEmptyState(string message)
        {
            try
            {
                if (EmptyStateText != null) EmptyStateText.Text = message;
                EmptyState.Visibility = Visibility.Visible;
            }
            catch { }
        }

        private async Task RunSearchAsync(string query)
        {
            query = (query ?? "").Trim();
            if (string.IsNullOrWhiteSpace(query))
            {
                ClearResultsAndHideSections();
                EmptyState.Visibility = Visibility.Collapsed;
                return;
            }

            try
            {
                Songs.Clear();
                Albums.Clear();
                Artists.Clear();
                Videos.Clear();
                Stations.Clear();

                SongsSection.Visibility = Visibility.Collapsed;
                AlbumsSection.Visibility = Visibility.Collapsed;
                ArtistsSection.Visibility = Visibility.Collapsed;
                VideosSection.Visibility = Visibility.Collapsed;
                RadioSection.Visibility = Visibility.Collapsed;
                EmptyState.Visibility = Visibility.Collapsed;

                var results = await SearchService.Current.SearchAsync(query);

                foreach (var s in results.Songs) Songs.Add(s);
                foreach (var a in results.Albums) Albums.Add(a);
                foreach (var a in results.Artists) Artists.Add(a);
                foreach (var v in results.Videos) Videos.Add(v);
                foreach (var r in results.Stations) Stations.Add(r);

                if (Songs.Count > 0) SongsSection.Visibility = Visibility.Visible;
                if (Albums.Count > 0) AlbumsSection.Visibility = Visibility.Visible;
                if (Artists.Count > 0) ArtistsSection.Visibility = Visibility.Visible;
                if (Videos.Count > 0) VideosSection.Visibility = Visibility.Visible;
                if (Stations.Count > 0) RadioSection.Visibility = Visibility.Visible;

                if (Songs.Count == 0 && Albums.Count == 0 && Artists.Count == 0 && Videos.Count == 0 && Stations.Count == 0)
                    ShowEmptyState("No results found.");
            }
            catch
            {
                ShowEmptyState("No results found.");
            }
        }

        // ================= Search box =================

        private void SearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
        {
            try
            {
                var q = (args.QueryText ?? "").Trim();
                if (string.IsNullOrWhiteSpace(q))
                {
                    QueryTextBlock.Text = "Search across Zink";
                    ClearResultsAndHideSections();
                    EmptyState.Visibility = Visibility.Collapsed;
                    return;
                }

                _query = q;
                QueryTextBlock.Text = $"Results for: \"{_query}\"";

                _ = DispatcherQueue.TryEnqueue(async () => await RunSearchAsync(_query));
            }
            catch { }
        }

        private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
        {
            try
            {
                if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput)
                    return;

                _typingCts?.Cancel();
                _typingCts = new CancellationTokenSource();
                var token = _typingCts.Token;

                var q = (sender.Text ?? "").Trim();

                if (string.IsNullOrWhiteSpace(q))
                {
                    QueryTextBlock.Text = "Search across Zink";
                    ClearResultsAndHideSections();
                    EmptyState.Visibility = Visibility.Collapsed;
                    return;
                }

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(250, token);
                        if (token.IsCancellationRequested) return;

                        _query = q;

                        await DispatcherQueue.EnqueueAsync(async () =>
                        {
                            if (token.IsCancellationRequested) return;
                            QueryTextBlock.Text = $"Results for: \"{_query}\"";
                            await RunSearchAsync(_query);
                        });
                    }
                    catch { }
                });
            }
            catch { }
        }

        // ================= Click handlers =================

        private void SongsList_ItemClick(object sender, ItemClickEventArgs e)
        {
            try
            {
                if (e.ClickedItem is not SongResult song) return;
                if (string.IsNullOrWhiteSpace(song.FilePath)) return;

                var songInfo = new SongInfo
                {
                    Title = song.Title,
                    Artist = song.Artist,
                    Album = song.Album,
                    Duration = "",
                    FilePath = song.FilePath
                };

                LibraryData.PlaySongList(new() { songInfo });

                var main = (MainWindow)App.MainWindow;
                main.MainFrame.Navigate(typeof(MusicPlayerPage), "autoplay");

                var playerItem = main.SidebarNavReference.MenuItems
                    .OfType<NavigationViewItem>()
                    .FirstOrDefault(i => (string)i.Tag == "MusicPlayer");
                if (playerItem != null)
                    main.SidebarNavReference.SelectedItem = playerItem;
            }
            catch { }
        }

        private void AlbumsList_ItemClick(object sender, ItemClickEventArgs e)
        {
            // keep as-is (album details wiring optional)
        }

        private void ArtistsList_ItemClick(object sender, ItemClickEventArgs e)
        {
            // keep as-is (artist details wiring optional)
        }

        private void VideosList_ItemClick(object sender, ItemClickEventArgs e)
        {
            try
            {
                if (e.ClickedItem is not VideoResult vid) return;
                if (string.IsNullOrWhiteSpace(vid.FullPath)) return;

                var main = (MainWindow)App.MainWindow;
                main.MainFrame.Navigate(typeof(VideoPlayerPage), vid.FullPath);

                var item = main.SidebarNavReference.MenuItems
                    .OfType<NavigationViewItem>()
                    .SelectMany(mi => mi.MenuItems.OfType<NavigationViewItem>())
                    .FirstOrDefault(i => (string)i.Tag == "VideoPlayer");
                if (item != null)
                    main.SidebarNavReference.SelectedItem = item;
            }
            catch { }
        }

        private void StationsList_ItemClick(object sender, ItemClickEventArgs e)
        {
            try
            {
                if (e.ClickedItem is not StationResult st) return;
                if (string.IsNullOrWhiteSpace(st.Title)) return;

                var main = (MainWindow)App.MainWindow;
                main.MainFrame.Navigate(typeof(RadioPage), st.Title);

                var item = main.SidebarNavReference.MenuItems
                    .OfType<NavigationViewItem>()
                    .SelectMany(mi => mi.MenuItems.OfType<NavigationViewItem>())
                    .FirstOrDefault(i => (string)i.Tag == "Radio");
                if (item != null)
                    main.SidebarNavReference.SelectedItem = item;
            }
            catch { }
        }
    }

    // Small helper for DispatcherQueue await without adding new dependencies
    internal static class DispatcherQueueExtensions
    {
        public static Task EnqueueAsync(this Microsoft.UI.Dispatching.DispatcherQueue queue, Func<Task> action)
        {
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            queue.TryEnqueue(async () =>
            {
                try { await action(); tcs.TrySetResult(true); }
                catch (Exception ex) { tcs.TrySetException(ex); }
            });
            return tcs.Task;
        }
    }
}
