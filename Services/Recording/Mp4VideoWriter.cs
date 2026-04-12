using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas;
using Windows.Graphics.DirectX;
using Windows.Media;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Zink.Services.Recording
{
    public sealed class Mp4VideoWriter
    {
        private static readonly CanvasDevice SharedCanvasDevice = new();

        public async Task WriteAsync(
            IReadOnlyList<VideoFramePacket> frames,
            string outputPath)
        {
            if (frames == null || frames.Count == 0)
                throw new InvalidOperationException("No video frames to write.");

            if (string.IsNullOrWhiteSpace(outputPath))
                throw new ArgumentException("Output path is required.", nameof(outputPath));

            var orderedFrames = frames
                .Where(f => f != null &&
                            f.Width > 0 &&
                            f.Height > 0 &&
                            f.Bgra32Bytes != null &&
                            f.Bgra32Bytes.Length == f.Width * f.Height * 4)
                .OrderBy(f => f.Timestamp)
                .ToList();

            if (orderedFrames.Count == 0)
                throw new InvalidOperationException("No valid video frames are available to write.");

            // Use fixed output cadence to avoid timing wobble between captured frames.
            const uint outputFps = 8;
            TimeSpan fixedFrameDuration = TimeSpan.FromMilliseconds(1000.0 / outputFps);

            uint width = (uint)orderedFrames[0].Width;
            uint height = (uint)orderedFrames[0].Height;

            await RecorderLog.InfoAsync(nameof(Mp4VideoWriter),
                $"Starting video-only write. Output='{outputPath}', Frames={orderedFrames.Count}, Size={width}x{height}, Fps={outputFps}");

            string folderPath = Path.GetDirectoryName(outputPath)
                ?? throw new InvalidOperationException("Output folder path could not be determined.");

            string fileName = Path.GetFileName(outputPath);
            if (string.IsNullOrWhiteSpace(fileName))
                throw new InvalidOperationException("Output file name could not be determined.");

            StorageFolder storageFolder = await StorageFolder.GetFolderFromPathAsync(folderPath);
            StorageFile outputFile = await storageFolder.CreateFileAsync(
                fileName,
                CreationCollisionOption.ReplaceExisting);

            using IRandomAccessStream outputStream = await outputFile.OpenAsync(FileAccessMode.ReadWrite);

            outputStream.Size = 0;
            outputStream.Seek(0);

            var inputVideoProps = VideoEncodingProperties.CreateUncompressed(
                MediaEncodingSubtypes.Bgra8,
                width,
                height);

            inputVideoProps.FrameRate.Numerator = outputFps;
            inputVideoProps.FrameRate.Denominator = 1;
            inputVideoProps.PixelAspectRatio.Numerator = 1;
            inputVideoProps.PixelAspectRatio.Denominator = 1;

            var descriptor = new VideoStreamDescriptor(inputVideoProps);

            var mss = new MediaStreamSource(descriptor)
            {
                BufferTime = TimeSpan.Zero
            };

            int index = 0;

            mss.Starting += (s, e) =>
            {
                e.Request.SetActualStartPosition(TimeSpan.Zero);
            };

            mss.SampleRequested += (s, e) =>
            {
                if (index >= orderedFrames.Count)
                {
                    e.Request.Sample = null;
                    return;
                }

                var frame = orderedFrames[index];
                TimeSpan normalizedTimestamp = TimeSpan.FromTicks(fixedFrameDuration.Ticks * index);

                using var bitmap = CanvasBitmap.CreateFromBytes(
                    SharedCanvasDevice,
                    frame.Bgra32Bytes!,
                    frame.Width,
                    frame.Height,
                    DirectXPixelFormat.B8G8R8A8UIntNormalized);

                using var renderTarget = new CanvasRenderTarget(SharedCanvasDevice, frame.Width, frame.Height, 96);
                using (var ds = renderTarget.CreateDrawingSession())
                {
                    ds.DrawImage(bitmap);
                }

                var sample = MediaStreamSample.CreateFromDirect3D11Surface(
                    renderTarget,
                    normalizedTimestamp);

                sample.Duration = fixedFrameDuration;
                e.Request.Sample = sample;
                index++;
            };

            var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.HD1080p);
            profile.Video.Width = width;
            profile.Video.Height = height;
            profile.Video.Bitrate = 12_000_000;
            profile.Video.FrameRate.Numerator = outputFps;
            profile.Video.FrameRate.Denominator = 1;
            profile.Video.PixelAspectRatio.Numerator = 1;
            profile.Video.PixelAspectRatio.Denominator = 1;

            var transcoder = new MediaTranscoder();
            var prepared = await transcoder.PrepareMediaStreamSourceTranscodeAsync(
                mss,
                outputStream,
                profile);

            if (!prepared.CanTranscode)
                throw new InvalidOperationException($"Cannot transcode video. Failure={prepared.FailureReason}");

            await prepared.TranscodeAsync();

            await RecorderLog.InfoAsync(nameof(Mp4VideoWriter),
                $"Video-only write completed. Output='{outputPath}'");
        }
    }
}