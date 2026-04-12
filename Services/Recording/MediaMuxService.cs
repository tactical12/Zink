using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Windows.Media.Editing;
using Windows.Media.MediaProperties;
using Windows.Storage;

namespace Zink.Services.Recording
{
    public sealed class MediaMuxService
    {
        public async Task MuxAsync(string videoPath, string audioPath, string outputPath)
        {
            if (string.IsNullOrWhiteSpace(videoPath))
                throw new ArgumentException("Video path is required.", nameof(videoPath));

            if (string.IsNullOrWhiteSpace(audioPath))
                throw new ArgumentException("Audio path is required.", nameof(audioPath));

            if (string.IsNullOrWhiteSpace(outputPath))
                throw new ArgumentException("Output path is required.", nameof(outputPath));

            if (!File.Exists(videoPath))
                throw new FileNotFoundException("Video file to mux was not found.", videoPath);

            if (!File.Exists(audioPath))
                throw new FileNotFoundException("Audio file to mux was not found.", audioPath);

            await RecorderLog.InfoAsync(nameof(MediaMuxService),
                $"Muxing started. Video='{videoPath}', Audio='{audioPath}', Output='{outputPath}'");

            StorageFile videoFile = await StorageFile.GetFileFromPathAsync(videoPath);
            StorageFile audioFile = await StorageFile.GetFileFromPathAsync(audioPath);

            string outputFolderPath = Path.GetDirectoryName(outputPath)
                ?? throw new InvalidOperationException("Could not determine output folder.");

            string outputFileName = Path.GetFileName(outputPath);
            if (string.IsNullOrWhiteSpace(outputFileName))
                throw new InvalidOperationException("Could not determine output file name.");

            StorageFolder outputFolder = await StorageFolder.GetFolderFromPathAsync(outputFolderPath);
            StorageFile outputFile = await outputFolder.CreateFileAsync(
                outputFileName,
                CreationCollisionOption.ReplaceExisting);

            var composition = new MediaComposition();

            var videoClip = await MediaClip.CreateFromFileAsync(videoFile);
            var audioClip = await BackgroundAudioTrack.CreateFromFileAsync(audioFile);

            composition.Clips.Add(videoClip);
            composition.BackgroundAudioTracks.Add(audioClip);

            var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.HD1080p);

            var videoProps = await videoFile.Properties.GetVideoPropertiesAsync();

            if (videoProps.Width > 0 && videoProps.Height > 0)
            {
                profile.Video.Width = videoProps.Width;
                profile.Video.Height = videoProps.Height;
            }

            profile.Video.PixelAspectRatio.Numerator = 1;
            profile.Video.PixelAspectRatio.Denominator = 1;

            ulong pixelCount = (ulong)profile.Video.Width * (ulong)profile.Video.Height;
            if (pixelCount >= 3840UL * 2160UL)
            {
                profile.Video.Bitrate = 35_000_000;
            }
            else if (pixelCount >= 2560UL * 1440UL)
            {
                profile.Video.Bitrate = 20_000_000;
            }
            else if (pixelCount >= 1920UL * 1080UL)
            {
                profile.Video.Bitrate = 12_000_000;
            }
            else
            {
                profile.Video.Bitrate = 8_000_000;
            }

            await composition.RenderToFileAsync(outputFile, MediaTrimmingPreference.Precise, profile);

            if (!File.Exists(outputPath))
                throw new InvalidOperationException("Mux finished but the output file was not created.");

            var info = new FileInfo(outputPath);
            if (!info.Exists || info.Length == 0)
                throw new InvalidOperationException("Mux finished but the output file is empty.");

            await RecorderLog.InfoAsync(nameof(MediaMuxService),
                $"Mux complete. Output='{outputPath}', Width={profile.Video.Width}, Height={profile.Video.Height}, Bytes={info.Length}");
        }

        public async Task ConcatVideosAsync(IReadOnlyList<string> segmentPaths, string outputPath)
        {
            if (segmentPaths == null || segmentPaths.Count == 0)
                throw new InvalidOperationException("No segment paths were provided for concatenation.");

            if (segmentPaths.Count == 1)
            {
                File.Copy(segmentPaths[0], outputPath, true);
                return;
            }

            string outputFolderPath = Path.GetDirectoryName(outputPath)
                ?? throw new InvalidOperationException("Could not determine output folder.");

            string outputFileName = Path.GetFileName(outputPath);
            if (string.IsNullOrWhiteSpace(outputFileName))
                throw new InvalidOperationException("Could not determine output file name.");

            StorageFolder outputFolder = await StorageFolder.GetFolderFromPathAsync(outputFolderPath);
            StorageFile outputFile = await outputFolder.CreateFileAsync(
                outputFileName,
                CreationCollisionOption.ReplaceExisting);

            var composition = new MediaComposition();

            foreach (string path in segmentPaths)
            {
                if (!File.Exists(path))
                    continue;

                StorageFile file = await StorageFile.GetFileFromPathAsync(path);
                var clip = await MediaClip.CreateFromFileAsync(file);
                composition.Clips.Add(clip);
            }

            if (composition.Clips.Count == 0)
                throw new InvalidOperationException("No valid video segments were available to concatenate.");

            StorageFile firstFile = await StorageFile.GetFileFromPathAsync(segmentPaths[0]);
            var firstProps = await firstFile.Properties.GetVideoPropertiesAsync();

            var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.HD1080p);
            if (firstProps.Width > 0 && firstProps.Height > 0)
            {
                profile.Video.Width = firstProps.Width;
                profile.Video.Height = firstProps.Height;
            }

            profile.Video.PixelAspectRatio.Numerator = 1;
            profile.Video.PixelAspectRatio.Denominator = 1;

            ulong pixelCount = (ulong)profile.Video.Width * (ulong)profile.Video.Height;
            if (pixelCount >= 3840UL * 2160UL)
            {
                profile.Video.Bitrate = 35_000_000;
            }
            else if (pixelCount >= 2560UL * 1440UL)
            {
                profile.Video.Bitrate = 20_000_000;
            }
            else if (pixelCount >= 1920UL * 1080UL)
            {
                profile.Video.Bitrate = 12_000_000;
            }
            else
            {
                profile.Video.Bitrate = 8_000_000;
            }

            await composition.RenderToFileAsync(outputFile, MediaTrimmingPreference.Precise, profile);

            if (!File.Exists(outputPath))
                throw new InvalidOperationException("Concatenation finished but the output file was not created.");

            var info = new FileInfo(outputPath);
            if (!info.Exists || info.Length == 0)
                throw new InvalidOperationException("Concatenation finished but the output file is empty.");
        }
    }
}