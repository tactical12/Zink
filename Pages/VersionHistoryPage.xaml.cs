using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.Generic;

namespace Zink.Pages
{
    public sealed partial class VersionHistoryPage : Page
    {
        private readonly Dictionary<string, (string Title, DateTime? Released, string Notes)> _changelog =
            new(StringComparer.OrdinalIgnoreCase)
            {
                // 3.0.0.0 (NEW)
                ["3.0.0.0"] = (
                    Title: "Version 3.0.0.0",
                    Released: new DateTime(2026, 05, 03),
                    Notes:
@"Version 3.0.0.0
- Rebranded the app to Zink across the visible product experience.
- Revamped the home dashboard around Zink's media, calling, screen sharing, gaming and diagnostics identity.
- Stabilised native calling and screen sharing with richer diagnostics and report upload support.
- Added stronger Discord Rich Presence, Spotify now playing surfaces, health checks, FPS tools, and support-ready logs.
- Refreshed the package versioning and Fluent glass branding assets for the Zink 3 milestone."
                ),

                // 2.4.1.0
                ["2.4.1.0"] = (
    Title: "Version 2.4.1.0",
    Released: new DateTime(2026, 02, 11),   // <-- set build date here
    Notes:
@"Version 2.4.1.0
- Added Twitch.tv to the social tab.
- Added twitch to the power tools section on the homedashboard page.
- Added a new customisation page.
- Added a new home dashboard customisation page where you can edit all of the things on the dashboard to your own liking
- Added an app customisation theme control page where you can change the theme of Zink from dark to light or from light to dark or even set it to your windows theme.
- Updated the leave a review page.
- Added a new pop up message for the video library page.
- Added a new pop up message for the music library page.
- Added a search button to the sidebar
- Added a search page after you click the search button to search on Zink.
- Added a photo viewer page to zink."
),

                // 2.3.5 (NEW)
                ["2.3.5.0"] = (
                    Title: "Version 2.3.5.0",
                    Released: new DateTime(2026, 01, 16),
                    Notes:
@"Added a new home dashboard page which is the new landing page when opening the zink app. Which should help new users get started with the zink app.
Added power tools with quick access so you can find the most used tools in one place. Which contains Youtube, spotify, radio, discord, video player, music player, settings, equalizer, visualizer, video library, music library, and version history.
Added insights to what you use the most in zink.
Added recent activity to the dashboard.
Added a card at the top of the home dashboard so you can open a music or video file.
Added a card a the top of the home dashboard which lets you resume your last played music or video file.
Added a card at the top of the home dashboard so you can open a folder containing music or video files.
Fixed a bug with the navigation of the sidebar where sometimes the selected item wouldn't match the current page.
Added artist pictures to the now palying card on the home dashboard."
                ),

                // 2.2.8 (NEW)
                ["2.2.8.0"] = (
                    Title: "Version 2.2.8.0",
                    Released: new DateTime(2026, 01, 12),
                    Notes:
@"Added support for ARM Systems.
Added subtitles to the video player so when the subtitles button is clicked you can turn on subtitles for videos/films.
Added a progress bar to the video player so you can move the time of a video to any point to pick where you want to watch.
Added time progression in the video player so you can see how much time has passed and how much time is left in a video.
Added new button designs to the video player for a better user experience.
Added the .net 8 runtime so it won't be asked for new installs from the microsoft store.
Moved the video player and library buttons from the films section to its own video section in the sidebar for easier access."
                ),

                // 2.1.6
                ["2.1.6.0"] = (
                    Title: "Version 2.1.6.0",
                    Released: new DateTime(2025, 11, 25), // today
                    Notes:
@"Fixed a bug where the app wouldn't launch on Windows 10 for x32 and x64 bit systems."
                ),

                // 2.1.5
                ["2.1.5.0"] = (
                    Title: "Version 2.1.5.0",
                    Released: new DateTime(2025, 11, 25), // today
                    Notes:
@"Added support for Windows 10 for both x32 & x64 systems.
Updated the apps runtime version from 1.7 to 1.8 for better performance and stability.
Added a like song button to the radio player page where it lets you like songs while listening to a radio station.
Added a radio songs page where you can see all the songs you've liked when you've listened to a radio station and liked the songs."
                ),

                // 2.0
                ["2.0.0.0"] = (
                    Title: "Version 2.0.0.0",
                    Released: new DateTime(2025, 9, 5),
                    Notes:
@"The Bugs in the Zink app that have been fixed in the new update. --
(Sorry for the delay in fixing this bug, it was a big one!) 
- Finally fixed the bug where the BBC radio stations would close before the player opened. The BBC radio stations will now open for sign-in and playback without closing early.
- Fixed a bug where audio would continue to play on any audio page after Zink was shut. Audio will now stop playing when Zink is closed.
- Fixed a bug where YouTube wasn't going into fullscreen mode when clicking the fullscreen button. YouTube videos will now go fullscreen correctly.
- Fixed a bug where TikTok videos were muted by default. Videos will now play with sound by default.
- Fixed a bug where starting audio (like a video on the YouTube page) and navigating to another page would keep playing the audio. Now it stops correctly.
- Fixed a bug where any radio playing on the Radio page stopped incorrectly when navigating away.
- Fixed a bug where audio playback didn't stop after leaving the Music Player page.
- Fixed a bug where playing a song and then going to the Music Library page to play another song wouldn't overwrite the previous track. The new song will now replace the old one.
- Fixed a bug where Zink wasn't shutting down properly when the close button was clicked. Zink will now shut down cleanly.
+ Scroll down further to see the new features and changes in this version of Zink.

The New features that have been added to the Zink App. --
- Added a new simple sidebar.
- Added the Video Library page. You can now view your videos when you import them into the Video Library page. 
- Added thumbnails to the Video Library page. You can now see thumbnails for your videos.
- Added support for clicking to play videos in the Video Library page. You can now click a thumbnail or the 'Play with Video Player' button.
- Added MP4 file support to the Music Player page. You can now play MP4 files.
- Added M4A file support to the Music Player page.
- Added Greatest Hits Radio to the Radio page.
- Added BBC Radio 1 Xtra to the Radio page.
- Added a loading section for YouTube on the YouTube page to help while waiting.
- Added the Hits Radio image to the Radio page.
- Added notifications to the Music Player page when a new station starts playing.
- Added a new Radio Widget page. You can now listen to the radio in a small window that shows the current station, song, and artist live with images.
- Added GEM 106, Premier Christian Radio, BBC Radio Derby, Jazz FM, MKFM, Capital Xtra, Radio Essex, Magic Radio, and talkSPORT to the Radio page.
- Added YouTube Music and Amazon Music to the Music section.
- Added BBC iPlayer and My5 to the TV section.
- Added Netflix, Amazon Prime Video, Disney Plus, Paramount Plus, and Now TV to the Films section.
- Added GeForce Now, Amazon Luna, Boosteroid, and Shadow PC to the Gaming section.
- Added X (formerly Twitter), Facebook, Telegram Web, WhatsApp Web, Messenger, LinkedIn, Threads, Bluesky, Mastodon (any instance), Pinterest, Tumblr, and Reddit to the Social section.

The Changed features in the Zink App --
- Removed the music library section from the Music Player page as it has been replaced with the new Music Library page."
                ),

                // 1.0 (set to 24/07/2025)
                ["1.0.0.0"] = (
                    Title: "Version 1.0.0.0",
                    Released: new DateTime(2025, 7, 24),
                    Notes:
@"Initial release of Zink.
• Core music, radio, and video foundations.
• First sidebar layout and navigation.
• Basic player controls and page structure."
                ),
            };

        public VersionHistoryPage()
        {
            this.InitializeComponent();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            var requested = e?.Parameter as string;

            if (string.IsNullOrWhiteSpace(requested) || !_changelog.ContainsKey(requested))
                requested = PickLatestVersionFallback();

            RenderVersion(requested!);
        }

        private string PickLatestVersionFallback()
        {
            string latest = null;
            Version best = null;

            foreach (var key in _changelog.Keys)
            {
                if (Version.TryParse(key, out var v))
                {
                    if (best == null || v > best)
                    {
                        best = v;
                        latest = key;
                    }
                }
                else if (latest == null)
                {
                    latest = key;
                }
            }

            // ? FIX: must return a key that actually exists in _changelog
            return latest ?? "2.2.8.0";
        }

        private void RenderVersion(string versionKey)
        {
            var data = _changelog[versionKey];

            PageTitle.Text = "Version History";
            VersionHeader.Text = data.Title;

            if (data.Released.HasValue)
            {
                ReleaseDateText.Text = $"Released: {data.Released.Value:dd MMM yyyy}";
                ReleaseDateText.Visibility = Visibility.Visible;
            }
            else
            {
                ReleaseDateText.Text = "";
                ReleaseDateText.Visibility = Visibility.Collapsed;
            }

            NotesText.Text = string.IsNullOrWhiteSpace(data.Notes)
                ? "No notes available yet."
                : data.Notes;
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            if (Frame.CanGoBack)
                Frame.GoBack();
            else
                Frame.Navigate(typeof(AboutPage));
        }
    }
}
