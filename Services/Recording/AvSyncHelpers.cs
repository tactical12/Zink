using System;
using System.Collections.Generic;
using System.Linq;

namespace Zink.Services.Recording
{
    public static class AvSyncHelpers
    {
        public static List<VideoFramePacket> NormalizeVideoTimestamps(IReadOnlyList<VideoFramePacket> frames)
        {
            if (frames.Count == 0)
                return new List<VideoFramePacket>();

            TimeSpan origin = frames[0].Timestamp;

            return frames
                .Where(f => f.Bgra32Bytes is not null)
                .OrderBy(f => f.Timestamp)
                .Select(f => f.CreateShifted(f.Timestamp - origin))
                .ToList();
        }

        public static List<AudioPacket> NormalizeAudioTimestamps(IReadOnlyList<AudioPacket> packets)
        {
            if (packets.Count == 0)
                return new List<AudioPacket>();

            TimeSpan origin = packets[0].Timestamp;

            return packets
                .OrderBy(p => p.Timestamp)
                .Select(p => new AudioPacket
                {
                    Timestamp = p.Timestamp - origin,
                    PcmData = p.PcmData,
                    SampleRate = p.SampleRate,
                    Channels = p.Channels,
                    BitsPerSample = p.BitsPerSample
                })
                .ToList();
        }

        public static TimeSpan ComputeCommonOrigin(
            IReadOnlyList<VideoFramePacket> videoFrames,
            IReadOnlyList<AudioPacket>? systemPackets,
            IReadOnlyList<AudioPacket>? micPackets)
        {
            var candidates = new List<TimeSpan>();

            if (videoFrames.Count > 0)
                candidates.Add(videoFrames.Min(v => v.Timestamp));

            if (systemPackets is not null && systemPackets.Count > 0)
                candidates.Add(systemPackets.Min(a => a.Timestamp));

            if (micPackets is not null && micPackets.Count > 0)
                candidates.Add(micPackets.Min(a => a.Timestamp));

            return candidates.Count == 0 ? TimeSpan.Zero : candidates.Min();
        }

        public static List<AudioPacket> ShiftAudio(IReadOnlyList<AudioPacket>? packets, TimeSpan origin)
        {
            if (packets is null || packets.Count == 0)
                return new List<AudioPacket>();

            return packets
                .OrderBy(p => p.Timestamp)
                .Select(p => new AudioPacket
                {
                    Timestamp = p.Timestamp - origin,
                    PcmData = p.PcmData,
                    SampleRate = p.SampleRate,
                    Channels = p.Channels,
                    BitsPerSample = p.BitsPerSample
                })
                .Where(p => p.Timestamp >= TimeSpan.Zero)
                .ToList();
        }

        public static List<VideoFramePacket> ShiftVideo(IReadOnlyList<VideoFramePacket> frames, TimeSpan origin)
        {
            if (frames.Count == 0)
                return new List<VideoFramePacket>();

            return frames
                .Where(f => f.Bgra32Bytes is not null)
                .OrderBy(f => f.Timestamp)
                .Select(f => f.CreateShifted(f.Timestamp - origin))
                .Where(f => f.Timestamp >= TimeSpan.Zero)
                .ToList();
        }
    }
}