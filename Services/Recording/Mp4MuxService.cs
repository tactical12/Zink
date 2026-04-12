using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Zink.Services.Recording
{
    public sealed class Mp4MuxService
    {
        private const uint DefaultVideoBitrate = 12_000_000;
        private const uint DefaultFpsFloor = 10;
        private const uint DefaultFpsCeiling = 60;
        private const uint DefaultFallbackFps = 30;

        public async Task WriteVideoAndAudioAsync(
            IReadOnlyList<VideoFramePacket> videoFrames,
            IReadOnlyList<AudioPacket>? systemAudioPackets,
            IReadOnlyList<AudioPacket>? microphonePackets,
            string outputPath)
        {
            if (videoFrames is null || videoFrames.Count == 0)
                throw new InvalidOperationException("No video frames available.");

            if (string.IsNullOrWhiteSpace(outputPath))
                throw new ArgumentException("Output path is required.", nameof(outputPath));

            await RecorderLog.InfoAsync(nameof(Mp4MuxService),
                $"Starting mux. Output='{outputPath}', VideoFrames={videoFrames.Count}, SystemPackets={systemAudioPackets?.Count ?? 0}, MicPackets={microphonePackets?.Count ?? 0}");

            try
            {
                await Task.Run(() =>
                {
                    TimeSpan origin = AudioMixHelpers.ComputeCommonOrigin(
                        videoFrames,
                        systemAudioPackets,
                        microphonePackets);

                    List<ShiftedVideoFrameRef> shiftedVideo = AudioMixHelpers.ShiftVideo(videoFrames, origin);
                    List<AudioPacket> shiftedSystem = AudioMixHelpers.ShiftAudio(systemAudioPackets, origin);
                    List<AudioPacket> shiftedMic = AudioMixHelpers.ShiftAudio(microphonePackets, origin);
                    List<AudioPacket> mixedAudio = AudioMixHelpers.MixPcm16(shiftedSystem, shiftedMic);

                    if (shiftedVideo.Count == 0)
                        throw new InvalidOperationException("No shifted video frames are available.");

                    ValidateShiftedVideo(shiftedVideo);
                    ValidateMixedAudio(mixedAudio);

                    uint width = (uint)shiftedVideo[0].Source.Width;
                    uint height = (uint)shiftedVideo[0].Source.Height;
                    uint fps = EstimateFrameRate(shiftedVideo);

                    uint audioSampleRate = (uint)AudioMixHelpers.TargetSampleRate;
                    uint audioChannels = (uint)AudioMixHelpers.TargetChannels;
                    uint audioBitsPerSample = (uint)AudioMixHelpers.TargetBitsPerSample;

                    int hr = NativeMuxWriter.ZrmCreateWriter(
                        outputPath,
                        width,
                        height,
                        fps,
                        1,
                        DefaultVideoBitrate,
                        audioSampleRate,
                        audioChannels,
                        audioBitsPerSample);

                    NativeMuxWriter.ThrowIfFailed(hr, nameof(NativeMuxWriter.ZrmCreateWriter));

                    try
                    {
                        WriteVideoSamples(shiftedVideo, fps);
                        WriteAudioSamples(mixedAudio);

                        hr = NativeMuxWriter.ZrmFinalizeWriter();
                        NativeMuxWriter.ThrowIfFailed(hr, nameof(NativeMuxWriter.ZrmFinalizeWriter));
                    }
                    catch
                    {
                        try
                        {
                            NativeMuxWriter.ZrmShutdownWriter();
                        }
                        catch
                        {
                        }

                        throw;
                    }
                });

                await RecorderLog.InfoAsync(nameof(Mp4MuxService), "Mux completed successfully.");
            }
            catch (Exception ex)
            {
                await RecorderLog.ErrorAsync(nameof(Mp4MuxService), ex, "WriteVideoAndAudioAsync failed");
                throw;
            }
        }

        private static void ValidateShiftedVideo(IReadOnlyList<ShiftedVideoFrameRef> shiftedVideo)
        {
            if (shiftedVideo.Count == 0)
                throw new InvalidOperationException("No shifted video frames were provided.");

            int expectedWidth = shiftedVideo[0].Source.Width;
            int expectedHeight = shiftedVideo[0].Source.Height;

            for (int i = 0; i < shiftedVideo.Count; i++)
            {
                var frameRef = shiftedVideo[i];
                var source = frameRef.Source;

                if (source.Width <= 0 || source.Height <= 0)
                    throw new InvalidOperationException($"Video frame {i} has invalid dimensions.");

                if (source.Width != expectedWidth || source.Height != expectedHeight)
                    throw new InvalidOperationException("Video frame size changed during recording. Resize handling for mux input is not implemented yet.");

                if (source.Bgra32Bytes is null || source.Bgra32Bytes.Length == 0)
                    throw new InvalidOperationException($"Video frame {i} has no BGRA payload.");

                int expectedBytes = source.Width * source.Height * 4;
                if (source.Bgra32Bytes.Length != expectedBytes)
                    throw new InvalidOperationException($"Video frame {i} has an unexpected BGRA payload size. Expected {expectedBytes}, got {source.Bgra32Bytes.Length}.");
            }
        }

        private static void ValidateMixedAudio(IReadOnlyList<AudioPacket> mixedAudio)
        {
            for (int i = 0; i < mixedAudio.Count; i++)
            {
                var packet = mixedAudio[i];

                if (packet.SampleRate != AudioMixHelpers.TargetSampleRate)
                    throw new InvalidOperationException($"Audio packet {i} has unexpected sample rate {packet.SampleRate}.");

                if (packet.Channels != AudioMixHelpers.TargetChannels)
                    throw new InvalidOperationException($"Audio packet {i} has unexpected channel count {packet.Channels}.");

                if (packet.BitsPerSample != AudioMixHelpers.TargetBitsPerSample)
                    throw new InvalidOperationException($"Audio packet {i} has unexpected bit depth {packet.BitsPerSample}.");

                if (packet.PcmData is null)
                    throw new InvalidOperationException($"Audio packet {i} has null PCM data.");

                int bytesPerFrame = packet.Channels * (packet.BitsPerSample / 8);
                if (bytesPerFrame <= 0)
                    throw new InvalidOperationException($"Audio packet {i} has invalid block alignment.");

                if (packet.PcmData.Length % bytesPerFrame != 0)
                    throw new InvalidOperationException($"Audio packet {i} length is not aligned to the audio frame size.");
            }
        }

        private static void WriteVideoSamples(IReadOnlyList<ShiftedVideoFrameRef> shiftedVideo, uint fps)
        {
            long fallbackDuration100ns = TimeSpan.FromMilliseconds(1000.0 / fps).Ticks;

            for (int i = 0; i < shiftedVideo.Count; i++)
            {
                ShiftedVideoFrameRef frameRef = shiftedVideo[i];
                VideoFramePacket source = frameRef.Source;

                long sampleTime100ns = frameRef.ShiftedTimestamp.Ticks;
                if (sampleTime100ns < 0)
                    continue;

                long sampleDuration100ns;

                if (i + 1 < shiftedVideo.Count)
                {
                    sampleDuration100ns = (shiftedVideo[i + 1].ShiftedTimestamp - frameRef.ShiftedTimestamp).Ticks;
                    if (sampleDuration100ns <= 0)
                        sampleDuration100ns = fallbackDuration100ns;
                }
                else
                {
                    sampleDuration100ns = fallbackDuration100ns;
                }

                int hr = NativeMuxWriter.ZrmWriteVideoFrame(
                    sampleTime100ns,
                    sampleDuration100ns,
                    source.Bgra32Bytes!,
                    (uint)source.Bgra32Bytes!.Length);

                NativeMuxWriter.ThrowIfFailed(hr, nameof(NativeMuxWriter.ZrmWriteVideoFrame));
            }
        }

        private static void WriteAudioSamples(IReadOnlyList<AudioPacket> mixedAudio)
        {
            foreach (AudioPacket packet in mixedAudio)
            {
                if (packet.PcmData is null || packet.PcmData.Length == 0)
                    continue;

                long sampleTime100ns = packet.Timestamp.Ticks;
                if (sampleTime100ns < 0)
                    continue;

                int bytesPerFrame = packet.Channels * (packet.BitsPerSample / 8);
                if (bytesPerFrame <= 0)
                    throw new InvalidOperationException("Audio packet has an invalid block alignment.");

                int sampleFrames = packet.PcmData.Length / bytesPerFrame;
                if (sampleFrames <= 0)
                    continue;

                long sampleDuration100ns = (long)((sampleFrames / (double)packet.SampleRate) * TimeSpan.TicksPerSecond);
                if (sampleDuration100ns <= 0)
                    continue;

                int hr = NativeMuxWriter.ZrmWriteAudioPacket(
                    sampleTime100ns,
                    sampleDuration100ns,
                    packet.PcmData,
                    (uint)packet.PcmData.Length);

                NativeMuxWriter.ThrowIfFailed(hr, nameof(NativeMuxWriter.ZrmWriteAudioPacket));
            }
        }

        private static uint EstimateFrameRate(IReadOnlyList<ShiftedVideoFrameRef> frames)
        {
            if (frames.Count < 2)
                return DefaultFallbackFps;

            double seconds = (frames[^1].ShiftedTimestamp - frames[0].ShiftedTimestamp).TotalSeconds;
            if (seconds <= 0.0)
                return DefaultFallbackFps;

            int fps = (int)Math.Round((frames.Count - 1) / seconds);
            if (fps < DefaultFpsFloor) fps = (int)DefaultFpsFloor;
            if (fps > DefaultFpsCeiling) fps = (int)DefaultFpsCeiling;

            return (uint)fps;
        }
    }
}