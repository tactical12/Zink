using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using Zink.Models;
using Zink.Services;

namespace Zink.Pages
{
    public sealed partial class LikedRadioSongsPage : Page
    {
        public ObservableCollection<LikedRadioSong> Likes => LikedRadioLikesService.Instance.Liked;

        public LikedRadioSongsPage()
        {
            this.InitializeComponent();
            this.Loaded += LikedRadioSongsPage_Loaded;
            Likes.CollectionChanged += (_, __) => UpdateEmptyHint();
            UpdateEmptyHint();
        }

        private async void LikedRadioSongsPage_Loaded(object sender, RoutedEventArgs e)
        {
            await LikedRadioLikesService.Instance.EnsureLoadedAsync();
            RefreshList();
            UpdateEmptyHint();
        }

        private void UpdateEmptyHint()
        {
            EmptyHint.Visibility = Likes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void RefreshList()
        {
            List.ItemsSource = null;
            List.ItemsSource = Likes;
        }

        private async void Remove_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is Guid id)
            {
                await LikedRadioLikesService.Instance.RemoveAsync(id);
                RefreshList();
            }
        }

        private async void AddToSpotify_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not Guid id)
                return;

            var song = Likes.FirstOrDefault(x => x.Id == id);
            if (song == null)
                return;

            if (song.AddedToSpotifyLikedSongs)
            {
                RefreshList();
                return;
            }

            try
            {
                btn.IsEnabled = false;

                if (!await SpotifyAuthHelper.EnsureAccessTokenAsync())
                {
                    await new ContentDialog
                    {
                        Title = "Spotify is not connected",
                        Content = "Please sign in to Spotify in Zink first, then try again.",
                        CloseButtonText = "OK",
                        XamlRoot = this.Content.XamlRoot
                    }.ShowAsync();

                    RefreshList();
                    return;
                }

                var result = await SpotifyAuthHelper.SearchBestTrackAsync(song.Artist, song.Title, song.Album);

                if (result == null || string.IsNullOrWhiteSpace(result.Value.TrackId))
                {
                    await new ContentDialog
                    {
                        Title = "Track not found",
                        Content = "Zink could not find a matching song on Spotify for this liked radio track.",
                        CloseButtonText = "OK",
                        XamlRoot = this.Content.XamlRoot
                    }.ShowAsync();

                    RefreshList();
                    return;
                }

                var added = await SpotifyAuthHelper.AddTrackToLikedSongsAsync(result.Value.TrackId);
                if (!added)
                {
                    await new ContentDialog
                    {
                        Title = "Spotify save failed",
                        Content = "Zink could not add this track to your Spotify Liked Songs.",
                        CloseButtonText = "OK",
                        XamlRoot = this.Content.XamlRoot
                    }.ShowAsync();

                    RefreshList();
                    return;
                }

                await LikedRadioLikesService.Instance.MarkAsAddedToSpotifyAsync(
                    song.Id,
                    result.Value.TrackId,
                    result.Value.TrackUrl);

                RefreshList();

                try
                {
                    NotificationService.Instance.Show("Spotify", $"Added to Spotify: {song.Artist} - {song.Title}");
                }
                catch
                {
                    await new ContentDialog
                    {
                        Title = "Added to Spotify",
                        Content = $"{song.Artist} - {song.Title} was added to your Spotify Liked Songs.",
                        CloseButtonText = "OK",
                        XamlRoot = this.Content.XamlRoot
                    }.ShowAsync();
                }
            }
            catch
            {
                RefreshList();

                await new ContentDialog
                {
                    Title = "Error",
                    Content = "Something went wrong while adding this song to Spotify.",
                    CloseButtonText = "OK",
                    XamlRoot = this.Content.XamlRoot
                }.ShowAsync();
            }
            finally
            {
                btn.IsEnabled = true;
            }
        }

        private async void PlayInSpotify_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not Guid id)
                return;

            var song = Likes.FirstOrDefault(x => x.Id == id);
            if (song == null)
                return;

            try
            {
                btn.IsEnabled = false;

                if (!await SpotifyAuthHelper.EnsureAccessTokenAsync())
                {
                    await new ContentDialog
                    {
                        Title = "Spotify is not connected",
                        Content = "Please sign in to Spotify in Zink first, then try again.",
                        CloseButtonText = "OK",
                        XamlRoot = this.Content.XamlRoot
                    }.ShowAsync();
                    return;
                }

                var launched = await SpotifyAuthHelper.OpenTrackInSpotifyAsync(song.Artist, song.Title, song.Album);

                if (!launched)
                {
                    await new ContentDialog
                    {
                        Title = "Track not found",
                        Content = "Zink could not find a matching track to open in Spotify.",
                        CloseButtonText = "OK",
                        XamlRoot = this.Content.XamlRoot
                    }.ShowAsync();
                }
            }
            catch
            {
                await new ContentDialog
                {
                    Title = "Error",
                    Content = "Something went wrong while opening this song in Spotify.",
                    CloseButtonText = "OK",
                    XamlRoot = this.Content.XamlRoot
                }.ShowAsync();
            }
            finally
            {
                btn.IsEnabled = true;
            }
        }

        private async void PlayWithYouTube_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not Guid id)
                return;

            var song = Likes.FirstOrDefault(x => x.Id == id);
            if (song == null)
                return;

            try
            {
                btn.IsEnabled = false;
                App.MainWindow?.MainFrame?.Navigate(typeof(YouTubePage), song);
            }
            catch
            {
                await new ContentDialog
                {
                    Title = "Error",
                    Content = "Something went wrong while opening this song with YouTube.",
                    CloseButtonText = "OK",
                    XamlRoot = this.Content.XamlRoot
                }.ShowAsync();
            }
            finally
            {
                btn.IsEnabled = true;
            }
        }
    }
}