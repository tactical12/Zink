using System;
using System.Threading.Tasks;
using Windows.Storage;

namespace Zink.Services.Recording
{
    public static class RecordingOutputService
    {
        public static async Task<string> CreateNewOutputPathAsync(string prefix)
        {
            var videosFolder = KnownFolders.VideosLibrary;
            var capturesFolder = await videosFolder.CreateFolderAsync(
                "Zink Captures",
                CreationCollisionOption.OpenIfExists);

            var file = await capturesFolder.CreateFileAsync(
                $"{prefix} {DateTime.Now:yyyy-MM-dd HH-mm-ss}.mp4",
                CreationCollisionOption.GenerateUniqueName);

            return file.Path;
        }
    }
}