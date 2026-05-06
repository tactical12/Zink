using System;
using System.Linq;
using DiscordRPC;
using Windows.Storage;

namespace Zink.Services
{
    public sealed class DiscordPresenceService
    {
        private static DiscordPresenceService? _instance;
        public static DiscordPresenceService Instance => _instance ??= new DiscordPresenceService();

        private DiscordRpcClient? _client;
        private bool _initialized;
        private readonly DateTime _appStartUtc = DateTime.UtcNow;
        private DateTime _activityStartUtc;
        private DateTime _callStartUtc;
        private string? _lastPresenceKey;
        private string? _lastPresenceContext;
        private DateTime _lastPresenceSentUtc;

        private const string DiscordApplicationId = "1487472795767279857";
        private const string DefaultLargeImageKey = "zink_1024";
        private const string DefaultLargeImageText = "Zink";
        private const string EnabledSettingKey = "ZinkDiscordRichPresenceEnabled";
        private static readonly TimeSpan DuplicatePresenceRefreshInterval = TimeSpan.FromSeconds(30);

        private DiscordPresenceService()
        {
        }

        public bool IsEnabled => GetEnabledSetting();

        public static bool GetEnabledSetting()
        {
            try
            {
                object value = ApplicationData.Current.LocalSettings.Values[EnabledSettingKey];
                if (value is bool boolValue)
                    return boolValue;
            }
            catch
            {
            }

            return true;
        }

        public void SetEnabled(bool enabled)
        {
            ApplicationData.Current.LocalSettings.Values[EnabledSettingKey] = enabled;

            if (enabled)
            {
                Initialize();
                return;
            }

            Shutdown();
        }

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
                SetAppPresence();

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
                _client?.Invoke();
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
                _lastPresenceKey = null;
                _lastPresenceContext = null;
                _lastPresenceSentUtc = default;
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
                System.Diagnostics.Debug.WriteLine("Discord RPC: Restoring app presence.");
                SetAppPresence();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Discord RPC clear failed: " + ex);
            }
        }

        public void SetAppPresence(string? state = null)
        {
            if (!TryEnsureClient(nameof(SetAppPresence)))
                return;

            _callStartUtc = default;

            var presence = new RichPresence
            {
                Details = "Using Zink",
                State = Trim(string.IsNullOrWhiteSpace(state) ? "Music, movies, calls, and web apps" : state.Trim(), 128),
                Timestamps = new Timestamps(_appStartUtc),
                Assets = new Assets
                {
                    LargeImageKey = DefaultLargeImageKey,
                    LargeImageText = DefaultLargeImageText
                }
            };

            SetPresence(presence, "app");
        }

        public void SetPagePresence(string pageName, string? category = null, string? action = null)
        {
            if (!TryEnsureClient(nameof(SetPagePresence)))
                return;

            var cleanPage = NormalizePresencePart(pageName, "Zink");
            var cleanCategory = category?.Trim();
            var cleanAction = string.IsNullOrWhiteSpace(action) ? "Using" : action.Trim();

            var presence = new RichPresence
            {
                Details = Trim($"{cleanAction} {cleanPage}", 128),
                State = Trim(string.IsNullOrWhiteSpace(cleanCategory) ? "Exploring Zink" : $"{cleanCategory} in Zink", 128),
                Timestamps = new Timestamps(_appStartUtc),
                Assets = new Assets
                {
                    LargeImageKey = DefaultLargeImageKey,
                    LargeImageText = Trim($"{DefaultLargeImageText} - {cleanPage}", 128)
                }
            };

            SetPresence(presence, "page");
        }

        public void SetWebPresence(string siteName, string? category = null, string? pageTitle = null)
        {
            if (!TryEnsureClient(nameof(SetWebPresence)))
                return;

            var cleanSite = NormalizePresencePart(siteName, "Web");
            var cleanCategory = string.IsNullOrWhiteSpace(category) ? "Web app" : category.Trim();

            var presence = new RichPresence
            {
                Details = Trim(string.IsNullOrWhiteSpace(pageTitle) ? $"Browsing {cleanSite}" : pageTitle.Trim(), 128),
                State = Trim($"{cleanCategory} in Zink", 128),
                Timestamps = new Timestamps(_appStartUtc),
                Assets = new Assets
                {
                    LargeImageKey = DefaultLargeImageKey,
                    LargeImageText = Trim($"{DefaultLargeImageText} - {cleanSite}", 128)
                }
            };

            SetPresence(presence, "web page");
        }

        public void SetMusicPresence(
            string songTitle,
            string? artistName,
            string? sourceName = null,
            string? largeImageKey = null,
            string? largeImageText = null,
            string? buttonUrl = null,
            bool isPlaying = true)
        {
            if (!TryEnsureClient(nameof(SetMusicPresence)))
                return;

            var cleanSource = NormalizePresencePart(sourceName, "Zink");
            var cleanTitle = NormalizePresencePart(songTitle, "Music");
            var trackText = BuildRadioState(cleanTitle, artistName);
            var state = isPlaying ? trackText : $"Paused - {trackText}";

            _activityStartUtc = DateTime.UtcNow;

            var presence = new RichPresence
            {
                Details = Trim(isPlaying ? $"Listening on {cleanSource}" : $"Paused on {cleanSource}", 128),
                State = Trim(state, 128),
                Timestamps = new Timestamps(_activityStartUtc),
                Assets = new Assets
                {
                    LargeImageKey = string.IsNullOrWhiteSpace(largeImageKey) ? DefaultLargeImageKey : largeImageKey,
                    LargeImageText = Trim(string.IsNullOrWhiteSpace(largeImageText) ? $"{DefaultLargeImageText} - {cleanSource}" : largeImageText, 128)
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

            SetPresence(presence, "music");
        }

        public void SetCallPresence(
            string status,
            int participantCount,
            bool isScreenSharing,
            bool isMuted,
            bool isDeafened,
            TimeSpan? connectedFor = null)
        {
            if (!TryEnsureClient(nameof(SetCallPresence)))
                return;

            if (participantCount < 1)
                participantCount = 1;

            var cleanStatus = NormalizePresencePart(status, isScreenSharing ? "Screen sharing in Zink" : "In a Zink call");
            var participantLabel = participantCount == 1 ? "1 participant" : $"{participantCount} participants";
            var mediaLabel = isScreenSharing ? "Screen share on" : "Voice call";
            var audioLabel = isMuted && isDeafened
                ? "Muted and deafened"
                : (isMuted ? "Muted" : (isDeafened ? "Deafened" : "Voice active"));

            var startUtc = connectedFor.HasValue && connectedFor.Value > TimeSpan.Zero
                ? DateTime.UtcNow - connectedFor.Value
                : (_callStartUtc == default ? DateTime.UtcNow : _callStartUtc);

            _callStartUtc = startUtc;

            var presence = new RichPresence
            {
                Details = Trim(cleanStatus, 128),
                State = Trim($"{participantLabel} - {mediaLabel} - {audioLabel}", 128),
                Timestamps = new Timestamps(startUtc),
                Assets = new Assets
                {
                    LargeImageKey = DefaultLargeImageKey,
                    LargeImageText = isScreenSharing ? "Zink screen share" : "Zink call"
                }
            };

            SetPresence(presence, "call");
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

            var normalizedStation = NormalizeStationName(stationName);
            string details = BuildRadioDetails(normalizedStation);
            string state = BuildRadioState(songTitle, artistName);

            var presence = new RichPresence
            {
                Details = Trim(details, 128),
                State = Trim(state, 128),
                Timestamps = new Timestamps(_activityStartUtc),
                Assets = new Assets
                {
                    LargeImageKey = string.IsNullOrWhiteSpace(stationAssetKey) ? DefaultLargeImageKey : stationAssetKey,
                    LargeImageText = BuildRadioImageText(normalizedStation)
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
                    $"Discord RPC: Setting station presence | Station='{normalizedStation}' | Asset='{presence.Assets?.LargeImageKey}'");

                _client.SetPresence(presence);
                _client.Invoke();
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

            var normalizedStation = NormalizeStationName(stationName);
            string details = BuildRadioDetails(normalizedStation);
            string state = BuildRadioState(songTitle, artistName);

            var presence = new RichPresence
            {
                Details = Trim(details, 128),
                State = Trim(state, 128),
                Timestamps = new Timestamps(_activityStartUtc == default ? DateTime.UtcNow : _activityStartUtc),
                Assets = new Assets
                {
                    LargeImageKey = string.IsNullOrWhiteSpace(stationAssetKey) ? DefaultLargeImageKey : stationAssetKey,
                    LargeImageText = BuildRadioImageText(normalizedStation)
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
                    $"Discord RPC: Updating track presence | Station='{normalizedStation}' | Artist='{artistName}' | Title='{songTitle}' | Asset='{presence.Assets?.LargeImageKey}'");

                _client.SetPresence(presence);
                _client.Invoke();
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
                    LargeImageKey = string.IsNullOrWhiteSpace(largeImageKey) ? DefaultLargeImageKey : largeImageKey,
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
                _client.Invoke();
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
                    LargeImageKey = string.IsNullOrWhiteSpace(largeImageKey) ? DefaultLargeImageKey : largeImageKey,
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
                _client.Invoke();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Discord RPC set paused video presence failed: " + ex);
            }
        }

        private bool TryEnsureClient(string caller)
        {
            if (!IsEnabled)
            {
                System.Diagnostics.Debug.WriteLine($"Discord RPC: {caller} skipped - disabled.");
                return false;
            }

            Initialize();

            if (!_initialized || _client == null)
            {
                System.Diagnostics.Debug.WriteLine($"Discord RPC: {caller} skipped - client not initialized.");
                return false;
            }

            return true;
        }

        private void SetPresence(RichPresence presence, string context)
        {
            if (!_initialized || _client == null)
                return;

            try
            {
                var presenceKey = BuildPresenceKey(presence);
                var nowUtc = DateTime.UtcNow;
                if (string.Equals(_lastPresenceKey, presenceKey, StringComparison.Ordinal) &&
                    string.Equals(_lastPresenceContext, context, StringComparison.Ordinal) &&
                    nowUtc - _lastPresenceSentUtc < DuplicatePresenceRefreshInterval)
                {
                    return;
                }

                System.Diagnostics.Debug.WriteLine($"Discord RPC: Setting {context} presence | Details='{presence.Details}' | State='{presence.State}'");
                _client.SetPresence(presence);
                _client.Invoke();
                _lastPresenceKey = presenceKey;
                _lastPresenceContext = context;
                _lastPresenceSentUtc = nowUtc;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Discord RPC set {context} presence failed: " + ex);
            }
        }

        private static string BuildPresenceKey(RichPresence presence)
        {
            var assets = presence.Assets;
            var buttons = presence.Buttons == null
                ? string.Empty
                : string.Join("|", presence.Buttons.Select(button => $"{button.Label}:{button.Url}"));

            return string.Join(
                "\u001F",
                presence.Details ?? string.Empty,
                presence.State ?? string.Empty,
                assets?.LargeImageKey ?? string.Empty,
                assets?.LargeImageText ?? string.Empty,
                assets?.SmallImageKey ?? string.Empty,
                assets?.SmallImageText ?? string.Empty,
                buttons);
        }

        private static string NormalizePresencePart(string? value, string fallback)
        {
            return string.IsNullOrWhiteSpace(value)
                ? fallback
                : value.Trim();
        }

        private static string NormalizeStationName(string stationName)
        {
            return string.IsNullOrWhiteSpace(stationName)
                ? "Live radio"
                : stationName.Trim();
        }

        private static string BuildRadioDetails(string stationName)
        {
            if (string.Equals(stationName, "Live radio", StringComparison.OrdinalIgnoreCase))
                return "Live radio on Zink";

            return $"{stationName} on Zink";
        }

        private static string BuildRadioState(string? songTitle, string? artistName)
        {
            var title = songTitle?.Trim();
            var artist = artistName?.Trim();

            if (!string.IsNullOrWhiteSpace(artist) && !string.IsNullOrWhiteSpace(title))
                return $"{artist} - {title}";

            if (!string.IsNullOrWhiteSpace(title))
                return title;

            return "Live radio";
        }

        private static string BuildRadioImageText(string stationName)
        {
            return Trim($"{DefaultLargeImageText} - {stationName}", 128);
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
