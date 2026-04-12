using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Zink.Services.Recording
{
    public static class WaveFileWriter
    {
        public static void WritePcm16Wave(string path, IReadOnlyList<AudioPacket> packets)
        {
            if (packets is null || packets.Count == 0)
                throw new InvalidOperationException("No audio packets to write.");

            var orderedPackets = packets
                .Where(p => p != null && p.PcmData != null)
                .OrderBy(p => p.Timestamp)
                .ToList();

            if (orderedPackets.Count == 0)
                throw new InvalidOperationException("No valid audio packets to write.");

            int sampleRate = orderedPackets[0].SampleRate;
            short channels = (short)orderedPackets[0].Channels;
            short bitsPerSample = (short)orderedPackets[0].BitsPerSample;

            if (bitsPerSample != 16)
                throw new InvalidOperationException("WaveFileWriter expects 16-bit PCM packets.");

            short blockAlign = (short)(channels * bitsPerSample / 8);
            int byteRate = sampleRate * blockAlign;

            long totalDataBytes = ComputeTotalDataBytes(orderedPackets, byteRate, blockAlign);
            if (totalDataBytes <= 0)
                throw new InvalidOperationException("Computed WAV data size is invalid.");

            if (totalDataBytes > int.MaxValue)
                throw new InvalidOperationException("WAV output is too large.");

            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
            using var bw = new BinaryWriter(fs, Encoding.ASCII);

            int subChunk2Size = checked((int)totalDataBytes);
            int chunkSize = 36 + subChunk2Size;

            bw.Write(Encoding.ASCII.GetBytes("RIFF"));
            bw.Write(chunkSize);
            bw.Write(Encoding.ASCII.GetBytes("WAVE"));

            bw.Write(Encoding.ASCII.GetBytes("fmt "));
            bw.Write(16);
            bw.Write((short)1);
            bw.Write(channels);
            bw.Write(sampleRate);
            bw.Write(byteRate);
            bw.Write(blockAlign);
            bw.Write(bitsPerSample);

            bw.Write(Encoding.ASCII.GetBytes("data"));
            bw.Write(subChunk2Size);

            long writtenDataBytes = 0;

            foreach (var packet in orderedPackets)
            {
                long targetOffset = TimeToAlignedByteOffset(packet.Timestamp, byteRate, blockAlign);

                if (targetOffset > writtenDataBytes)
                {
                    WriteZeros(bw, targetOffset - writtenDataBytes);
                    writtenDataBytes = targetOffset;
                }
                else if (targetOffset < writtenDataBytes)
                {
                    // Slight overlap from capture jitter - continue writing at current position
                    // instead of seeking backwards and corrupting continuity.
                    targetOffset = writtenDataBytes;
                }

                bw.Write(packet.PcmData);
                writtenDataBytes += packet.PcmData.Length;
            }

            if (writtenDataBytes < totalDataBytes)
            {
                WriteZeros(bw, totalDataBytes - writtenDataBytes);
            }
        }

        private static long ComputeTotalDataBytes(IReadOnlyList<AudioPacket> packets, int byteRate, int blockAlign)
        {
            long maxEnd = 0;

            foreach (var packet in packets)
            {
                long start = TimeToAlignedByteOffset(packet.Timestamp, byteRate, blockAlign);
                long end = start + packet.PcmData.Length;

                if (end > maxEnd)
                    maxEnd = end;
            }

            return maxEnd;
        }

        private static long TimeToAlignedByteOffset(TimeSpan timestamp, int byteRate, int blockAlign)
        {
            if (timestamp < TimeSpan.Zero)
                timestamp = TimeSpan.Zero;

            long bytes = (long)Math.Round(timestamp.TotalSeconds * byteRate, MidpointRounding.AwayFromZero);

            long remainder = bytes % blockAlign;
            if (remainder != 0)
                bytes -= remainder;

            return Math.Max(0, bytes);
        }

        private static void WriteZeros(BinaryWriter bw, long count)
        {
            if (count <= 0)
                return;

            byte[] zeroBuffer = new byte[8192];

            while (count > 0)
            {
                int chunk = (int)Math.Min(zeroBuffer.Length, count);
                bw.Write(zeroBuffer, 0, chunk);
                count -= chunk;
            }
        }
    }
}