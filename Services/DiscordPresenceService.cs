using System;
using DiscordRPC;

namespace Zink.Services
{
    public sealed class DiscordPresenceService
    {
        private static DiscordPresenceService? _instance;
        public static DiscordPresenceService Instance => _instance ??= new DiscordPresenceService();

        private DiscordRpcClient? _client;
        private bool _initialized;
        private DateTime _activityStartUtc;

        private const string DiscordApplicationId = "1487472795767279857";

        private DiscordPresenceService()
        {
        }

        public bool IsEnabled { get; set; } = true;

        public void Initialize()
        {
            if (_initialized || !IsEnabled)
                return;

            try
            {
                System.Diagnostics.Debug.WriteLine("Discord RPC: Initialize() starting");

                _client = new DiscordRpcClient(DiscordApplicationId);

                _client.OnReady += (_, e) =>
                {
                    try
                    {
                        System.Diagnostics.Debug.WriteLine($"Discord RPC ready: {e.User.Username}#{e.User.Discriminator}");
                    }
                    catch
                    {
                        System.Diagnostics.Debug.WriteLine("Discord RPC ready.");
                    }
                };

                _client.OnPresenceUpdate += (_, e) =>
                {
                    try
                    {
                        System.Diagnostics.Debug.WriteLine("Discord RPC presence update acknowledged.");
                    }
                    catch { }
                };

                _client.OnError += (_, e) =>
                {
                    System.Diagnostics.Debug.WriteLine($"Discord RPC error: {e.Code} - {e.Message}");
                };

                _client.Initialize();
                _initialized = true;

                System.Diagnostics.Debug.WriteLine("Discord RPC: Initialize() called successfully.");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Discord RPC init failed: " + ex);
                _initialized = false;
                _client = null;
            }
        }

        public void Shutdown()
        {
            try
            {
                System.Diagnostics.Debug.WriteLine("Discord RPC: Shutdown()");
                _client?.ClearPresence();
                _client?.Dispose();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Discord RPC shutdown failed: " + ex);
            }
            finally
            {
                _client = null;
                _initialized = false;
            }
        }

        public void Clear()
        {
            if (!_initialized || _client == null)
            {
                System.Diagnostics.Debug.WriteLine("Discord RPC: Clear() skipped - not initialized.");
                return;
            }

            try
            {
                System.Diagnostics.Debug.WriteLine("Discord RPC: Clearing presence.");
                _client.ClearPresence();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Discord RPC clear failed: " + ex);
            }
        }

        public void SetRadioPresence(
            string stationName,
            string? songTitle,
            string? artistName,
            string? stationAssetKey = null,
            string? buttonUrl = null)
        {
            if (!IsEnabled)
            {
                System.Diagnostics.Debug.WriteLine("Discord RPC: SetRadioPresence skipped - disabled.");
                return;
            }

            Initialize();

            if (!_initialized || _client == null)
            {
                System.Diagnostics.Debug.WriteLine("Discord RPC: SetRadioPresence skipped - client not initialized.");
                return;
            }

            _activityStartUtc = DateTime.UtcNow;

            string details = $"Listening to {stationName}";

            string state;
            if (!string.IsNullOrWhiteSpace(artistName) && !string.IsNullOrWhiteSpace(songTitle))
            {
                state = $"Now playing: {artistName} - {songTitle}";
            }
            else if (!string.IsNullOrWhiteSpace(songTitle))
            {
                state = $"Now playing: {songTitle}";
            }
            else
            {
                state = "Live radio playback";
            }

            var presence = new RichPresence
            {
                Details = Trim(details, 128),
                State = Trim(state, 128),
                Timestamps = new Timestamps(_activityStartUtc),
                Assets = new Assets
                {
                    LargeImageKey = string.IsNullOrWhiteSpace(stationAssetKey) ? "zink_1024" : stationAssetKey,
                    LargeImageText = Trim(stationName, 128)
                }
            };

            if (!string.IsNullOrWhiteSpace(buttonUrl))
            {
                presence.Buttons = new[]
                {
                    new DiscordRPC.Button
                    {
                        Label = "Open Zink",
                        Url = buttonUrl
                    }
                };
            }

            try
            {
                System.Diagnostics.Debug.WriteLine(
                    $"Discord RPC: Setting station presence | Station='{stationName}' | Asset='{presence.Assets?.LargeImageKey}'");

                _client.SetPresence(presence);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Discord RPC set presence failed: " + ex);
            }
        }

        public void UpdateRadioTrack(
            string stationName,
            string? songTitle,
            string? artistName,
            string? stationAssetKey = null,
            string? buttonUrl = null)
        {
            if (!IsEnabled)
            {
                System.Diagnostics.Debug.WriteLine("Discord RPC: UpdateRadioTrack skipped - disabled.");
                return;
            }

            if (!_initialized || _client == null)
            {
                System.Diagnostics.Debug.WriteLine("Discord RPC: UpdateRadioTrack client not initialized, falling back to SetRadioPresence().");
                SetRadioPresence(stationName, songTitle, artistName, stationAssetKey, buttonUrl);
                return;
            }

            string details = $"Listening to {stationName}";

            string state;
            if (!string.IsNullOrWhiteSpace(artistName) && !string.IsNullOrWhiteSpace(songTitle))
            {
                state = $"Now playing: {artistName} - {songTitle}";
            }
            else if (!string.IsNullOrWhiteSpace(songTitle))
            {
                state = $"Now playing: {songTitle}";
            }
            else
            {
                state = "Live radio playback";
            }

            var presence = new RichPresence
            {
                Details = Trim(details, 128),
                State = Trim(state, 128),
                Timestamps = new Timestamps(_activityStartUtc == default ? DateTime.UtcNow : _activityStartUtc),
                Assets = new Assets
                {
                    LargeImageKey = string.IsNullOrWhiteSpace(stationAssetKey) ? "zink_1024" : stationAssetKey,
                    LargeImageText = Trim(stationName, 128)
                }
            };

            if (!string.IsNullOrWhiteSpace(buttonUrl))
            {
                presence.Buttons = new[]
                {
                    new DiscordRPC.Button
                    {
                        Label = "Open Zink",
                        Url = buttonUrl
                    }
                };
            }

            try
            {
                System.Diagnostics.Debug.WriteLine(
                    $"Discord RPC: Updating track presence | Station='{stationName}' | Artist='{artistName}' | Title='{songTitle}' | Asset='{presence.Assets?.LargeImageKey}'");

                _client.SetPresence(presence);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Discord RPC update presence failed: " + ex);
            }
        }

        public void SetVideoPresence(
            string videoTitle,
            TimeSpan position,
            TimeSpan duration,
            string? largeImageKey = null,
            string? largeImageText = null,
            string? buttonUrl = null)
        {
            if (!IsEnabled)
            {
                System.Diagnostics.Debug.WriteLine("Discord RPC: SetVideoPresence skipped - disabled.");
                return;
            }

            Initialize();

            if (!_initialized || _client == null)
            {
                System.Diagnostics.Debug.WriteLine("Discord RPC: SetVideoPresence skipped - client not initialized.");
                return;
            }

            if (string.IsNullOrWhiteSpace(videoTitle))
                videoTitle = "Video";

            if (position < TimeSpan.Zero)
                position = TimeSpan.Zero;

            if (duration <= TimeSpan.Zero)
                duration = TimeSpan.Zero;

            if (position > duration && duration > TimeSpan.Zero)
                position = duration;

            var nowUtc = DateTime.UtcNow;
            var startUtc = nowUtc - position;

            _activityStartUtc = startUtc;

            var details = $"Watching {videoTitle}";
            var state = $"Playing - {FormatTime(position)} / {FormatTime(duration)}";

            var presence = new RichPresence
            {
                Details = Trim(details, 128),
                State = Trim(state, 128),
                Timestamps = new Timestamps(startUtc),
                Assets = new Assets
                {
                    LargeImageKey = string.IsNullOrWhiteSpace(largeImageKey) ? "zink_1024" : largeImageKey,
                    LargeImageText = Trim(string.IsNullOrWhiteSpace(largeImageText) ? videoTitle : largeImageText, 128)
                }
            };

            if (!string.IsNullOrWhiteSpace(buttonUrl))
            {
                presence.Buttons = new[]
                {
                    new DiscordRPC.Button
                    {
                        Label = "Open Zink",
                        Url = buttonUrl
                    }
                };
            }

            try
            {
                System.Diagnostics.Debug.WriteLine(
                    $"Discord RPC: Setting video presence | Title='{videoTitle}' | Position='{position}' | Duration='{duration}'");

                _client.SetPresence(presence);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Discord RPC set video presence failed: " + ex);
            }
        }

        public void SetVideoPausedPresence(
            string videoTitle,
            TimeSpan position,
            TimeSpan duration,
            string? largeImageKey = null,
            string? largeImageText = null,
            string? buttonUrl = null)
        {
            if (!IsEnabled)
            {
                System.Diagnostics.Debug.WriteLine("Discord RPC: SetVideoPausedPresence skipped - disabled.");
                return;
            }

            Initialize();

            if (!_initialized || _client == null)
            {
                System.Diagnostics.Debug.WriteLine("Discord RPC: SetVideoPausedPresence skipped - client not initialized.");
                return;
            }

            if (string.IsNullOrWhiteSpace(videoTitle))
                videoTitle = "Video";

            if (position < TimeSpan.Zero)
                position = TimeSpan.Zero;

            if (duration <= TimeSpan.Zero)
                duration = TimeSpan.Zero;

            if (position > duration && duration > TimeSpan.Zero)
                position = duration;

            var details = $"Watching {videoTitle}";
            var state = $"Paused - {FormatTime(position)} / {FormatTime(duration)}";

            var presence = new RichPresence
            {
                Details = Trim(details, 128),
                State = Trim(state, 128),
                Assets = new Assets
                {
                    LargeImageKey = string.IsNullOrWhiteSpace(largeImageKey) ? "zink_1024" : largeImageKey,
                    LargeImageText = Trim(string.IsNullOrWhiteSpace(largeImageText) ? videoTitle : largeImageText, 128)
                }
            };

            if (!string.IsNullOrWhiteSpace(buttonUrl))
            {
                presence.Buttons = new[]
                {
                    new DiscordRPC.Button
                    {
                        Label = "Open Zink",
                        Url = buttonUrl
                    }
                };
            }

            try
            {
                System.Diagnostics.Debug.WriteLine(
                    $"Discord RPC: Setting paused video presence | Title='{videoTitle}' | Position='{position}' | Duration='{duration}'");

                _client.SetPresence(presence);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Discord RPC set paused video presence failed: " + ex);
            }
        }

        private static string FormatTime(TimeSpan time)
        {
            try
            {
                if (time.TotalHours >= 1)
                    return time.ToString(@"hh\:mm\:ss");

                return time.ToString(@"mm\:ss");
            }
            catch
            {
                return "00:00";
            }
        }

        private static string Trim(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;

            return value.Length <= maxLength ? value : value.Substring(0, maxLength);
        }
    }
}