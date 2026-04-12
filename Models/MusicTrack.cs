namespace Zink
{
    public class MusicTrack
    {
        public string Title { get; set; }
        public string Artist { get; set; }
        public string FilePath { get; set; }
        public string AlbumArtPath { get; set; }

        public MusicTrack(string title, string artist, string filePath, string albumArtPath)
        {
            Title = title;
            Artist = artist;
            FilePath = filePath;
            AlbumArtPath = albumArtPath;
        }

        public MusicTrack() { } // Required for XAML binding
    }
}
