using Windows.Media.Playback;

namespace Zink.Services
{
    public static class MediaPlayerSingleton
    {
        private static readonly MediaPlayer _instance = CreateConfiguredMediaPlayer();

        public static MediaPlayer Instance => _instance;

        private static MediaPlayer CreateConfiguredMediaPlayer()
        {
            var player = new MediaPlayer
            {
                AutoPlay = true,
                AudioCategory = MediaPlayerAudioCategory.Media
            };
            return player;
        }
    }
}
