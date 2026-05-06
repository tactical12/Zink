using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas;
using Windows.Foundation;
using Windows.Graphics.DirectX;
using Windows.Media;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;
using Windows.Storage.Streams;
using Zink.Models;

namespace Zink.Services.Recording
{
    public sealed class Mp4VideoWriter
    {
        private static readonly CanvasDevice SharedCanvasDevice = new();

        public async Task WriteAsync(
            IReadOnlyList<VideoFramePacket> frames,
            string outputPath,
            RecordingOptions? options = null)
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
            uint outputFps = Math.Clamp(options?.FrameRate ?? 60, 1u, 240u);
            TimeSpan fixedFrameDuration = TimeSpan.FromMilliseconds(1000.0 / outputFps);

            uint sourceWidth = (uint)orderedFrames[0].Width;
            uint sourceHeight = (uint)orderedFrames[0].Height;
            uint width = options?.OutputWidth > 0 ? (uint)options.OutputWidth : sourceWidth;
            uint height = options?.OutputHeight > 0 ? (uint)options.OutputHeight : sourceHeight;
            uint bitrate = options?.VideoBitrate > 0
                ? options.VideoBitrate
                : CalculateNativeBitrate(width, height, outputFps);
            TimeSpan outputDuration = orderedFrames.Count > 1
                ? orderedFrames[^1].Timestamp - orderedFrames[0].Timestamp + fixedFrameDuration
                : fixedFrameDuration;
            int outputFrameCount = Math.Max(1, (int)Math.Ceiling(outputDuration.TotalSeconds * outputFps));

            await RecorderLog.InfoAsync(nameof(Mp4VideoWriter),
                $"Starting video-only write. Output='{outputPath}', SourceFrames={orderedFrames.Count}, SourceSize={sourceWidth}x{sourceHeight}, OutputSize={width}x{height}, Fps={outputFps}, Bitrate={bitrate}");

            if (width == sourceWidth && height == sourceHeight)
            {
                try
                {
                    await WriteNativeAsync(orderedFrames, outputPath, (int)width, (int)height, (int)outputFps);
                    return;
                }
                catch (Exception ex)
                {
                    await RecorderLog.ErrorAsync(nameof(Mp4VideoWriter), ex, "Native video writer failed; falling back to MediaTranscoder");
                }
            }

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
            int sourceIndex = 0;
            TimeSpan sourceStart = orderedFrames[0].Timestamp;

            mss.Starting += (s, e) =>
            {
                e.Request.SetActualStartPosition(TimeSpan.Zero);
            };

            mss.SampleRequested += (s, e) =>
            {
                if (index >= outputFrameCount)
                {
                    e.Request.Sample = null;
                    return;
                }

                TimeSpan normalizedTimestamp = TimeSpan.FromTicks(fixedFrameDuration.Ticks * index);
                TimeSpan sourceTimestamp = sourceStart + normalizedTimestamp;

                while (sourceIndex + 1 < orderedFrames.Count &&
                       orderedFrames[sourceIndex + 1].Timestamp <= sourceTimestamp)
                {
                    sourceIndex++;
                }

                var frame = orderedFrames[sourceIndex];

                using var bitmap = CanvasBitmap.CreateFromBytes(
                    SharedCanvasDevice,
                    frame.Bgra32Bytes!,
                    frame.Width,
                    frame.Height,
                    DirectXPixelFormat.B8G8R8A8UIntNormalized);

                using var renderTarget = new CanvasRenderTarget(SharedCanvasDevice, (float)width, (float)height, 96);
                using (var ds = renderTarget.CreateDrawingSession())
                {
                    Rect sourceRect = CreateCoverSourceRect(frame.Width, frame.Height, (double)width, (double)height);
                    ds.DrawImage(bitmap, new Rect(0, 0, width, height), sourceRect);
                }

                var sample = MediaStreamSample.CreateFromDirect3D11Surface(
                    renderTarget,
                    normalizedTimestamp);

                sample.Duration = fixedFrameDuration;
                e.Request.Sample = sample;
                index++;
            };

            var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.Uhd2160p);
            profile.Video.Width = width;
            profile.Video.Height = height;
            profile.Video.Bitrate = bitrate;
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

        private static Rect CreateCoverSourceRect(int sourceWidth, int sourceHeight, double outputWidth, double outputHeight)
        {
            double sourceAspect = sourceWidth / (double)sourceHeight;
            double outputAspect = outputWidth / outputHeight;

            if (sourceAspect > outputAspect)
            {
                double croppedWidth = sourceHeight * outputAspect;
                return new Rect((sourceWidth - croppedWidth) / 2.0, 0, croppedWidth, sourceHeight);
            }

            double croppedHeight = sourceWidth / outputAspect;
            return new Rect(0, (sourceHeight - croppedHeight) / 2.0, sourceWidth, croppedHeight);
        }

        private static uint CalculateNativeBitrate(uint width, uint height, uint fps)
        {
            double bitsPerSecond = width * (double)height * fps * 0.16;
            return (uint)Math.Clamp(bitsPerSecond, 12_000_000, 100_000_000);
        }

        private static async Task WriteNativeAsync(
            IReadOnlyList<VideoFramePacket> frames,
            string outputPath,
            int width,
            int height,
            int fps)
        {
            IntPtr writer = NativeMuxWriter.znk_writer_create(
                outputPath,
                width,
                height,
                fps,
                48000,
                2,
                16);

            if (writer == IntPtr.Zero)
                throw new InvalidOperationException("Native writer could not be created.");

            try
            {
                TimeSpan start = frames[0].Timestamp;

                foreach (var frame in frames)
                {
                    if (frame.Bgra32Bytes is null)
                        continue;

                    long timestamp100ns = (frame.Timestamp - start).Ticks;

                    int hr = NativeMuxWriter.znk_writer_write_video_frame(
                        writer,
                        frame.Bgra32Bytes,
                        frame.Bgra32Bytes.Length,
                        frame.Width,
                        frame.Height,
                        timestamp100ns);

                    NativeMuxWriter.ThrowIfFailed(hr, nameof(NativeMuxWriter.znk_writer_write_video_frame));
                }

                NativeMuxWriter.ThrowIfFailed(
                    NativeMuxWriter.znk_writer_finalize(writer),
                    nameof(NativeMuxWriter.znk_writer_finalize));

                await RecorderLog.InfoAsync(nameof(Mp4VideoWriter),
                    $"Native video-only write completed. Output='{outputPath}', Frames={frames.Count}, Size={width}x{height}, Fps={fps}");
            }
            finally
            {
                NativeMuxWriter.znk_writer_destroy(writer);
            }
        }
    }
}
