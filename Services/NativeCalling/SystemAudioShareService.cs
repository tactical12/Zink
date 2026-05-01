using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Concentus.Enums;
using ConcentusEncoder = Concentus.Structs.OpusEncoder;
using Zink.Models;
using Zink.Services.Recording;

namespace Zink.Services.NativeCalling
{
    public sealed class SystemAudioShareService
    {
        public static SystemAudioShareService Instance { get; } = new SystemAudioShareService();

        private const int SampleRate = 48000;
        private const int Channels = 2;
        private const int FrameSize = 960;
        private const int OpusBitrate = 128000;
        private static readonly byte[] ScreenShareAudioHeader = { (byte)'Z', (byte)'S', (byte)'A', (byte)'1' };

        private readonly object _gate = new();
        private readonly Queue<short> _sampleQueue = new();
        private readonly SystemLoopbackCaptureService _capture = new();

        private ConcentusEncoder? _encoder;
        private string? _selectedDeviceId;
        private string _selectedDeviceName = "Default";
        private DateTimeOffset _lastAudioLogUtc = DateTimeOffset.MinValue;
        private long _capturedPackets;
        private long _encodedPackets;
        private long _capturedBytes;
        private long _silentPackets;
        private long _encodeFailures;
        private long _fallbackMonoPackets;

        public event Action<byte[]>? AudioCaptured;

        public bool IsRunning => _capture.IsRunning;
        public string? SelectedDeviceId => _selectedDeviceId;
        public string SelectedDeviceName => _selectedDeviceName;

        private SystemAudioShareService()
        {
            _capture.AudioPacketArrived += Capture_AudioPacketArrived;
        }

        public async Task<IReadOnlyList<RecorderDeviceItem>> GetOutputDevicesAsync()
        {
            var devices = await AudioDeviceService.GetRenderDevicesAsync();
            var list = new List<RecorderDeviceItem>
            {
                new RecorderDeviceItem
                {
                    Id = "",
                    Name = "Default"
                }
            };

            list.AddRange(devices);
            return list;
        }

        public async Task SetOutputDeviceAsync(string? deviceId, string deviceName)
        {
            bool wasRunning = IsRunning;

            if (wasRunning)
                await StopAsync();

            lock (_gate)
            {
                _selectedDeviceId = string.IsNullOrWhiteSpace(deviceId) ? null : deviceId;
                _selectedDeviceName = string.IsNullOrWhiteSpace(deviceName) ? "Default" : deviceName;
            }

            if (wasRunning)
                await StartAsync();
        }

        public async Task StartAsync()
        {
            lock (_gate)
            {
                if (IsRunning)
                    return;

                _sampleQueue.Clear();
                _capturedPackets = 0;
                _encodedPackets = 0;
                _capturedBytes = 0;
                _silentPackets = 0;
                _encodeFailures = 0;
                _fallbackMonoPackets = 0;
                _lastAudioLogUtc = DateTimeOffset.MinValue;
                _encoder = new ConcentusEncoder(SampleRate, Channels, OpusApplication.OPUS_APPLICATION_AUDIO)
                {
                    Bitrate = OpusBitrate,
                    Complexity = 10,
                    SignalType = OpusSignal.OPUS_SIGNAL_MUSIC
                };
            }

            Debug.WriteLine($"[ScreenShare:Audio] Starting system audio share. Device='{_selectedDeviceName}', target={SampleRate}Hz/{Channels}ch, opusBitrate={OpusBitrate}.");
            await _capture.StartAsync(_selectedDeviceId);
        }

        public async Task StopAsync()
        {
            await _capture.StopAsync();

            lock (_gate)
            {
                _sampleQueue.Clear();
                _encoder = null;
            }

            Debug.WriteLine($"[ScreenShare:Audio] Stopped system audio share. capturedPackets={_capturedPackets}; encodedPackets={_encodedPackets}; capturedBytes={_capturedBytes}; silentPackets={_silentPackets}.");
        }

        private void Capture_AudioPacketArrived(object? sender, AudioPacket packet)
        {
            byte[][] encodedPackets;
            double packetPeak = EstimatePeak(packet);

            lock (_gate)
            {
                if (_encoder == null || packet.PcmData.Length == 0)
                    return;

                _capturedPackets++;
                _capturedBytes += packet.PcmData.Length;
                if (packetPeak <= 0.0001)
                    _silentPackets++;

                foreach (var sample in ConvertPacketToCallSamples(packet))
                {
                    _sampleQueue.Enqueue(sample);
                }

                var packets = new List<byte[]>();
                var samplesPerPacket = FrameSize * Channels;
                while (_sampleQueue.Count >= samplesPerPacket)
                {
                    short[] frame = new short[samplesPerPacket];
                    for (int i = 0; i < frame.Length; i++)
                    {
                        frame[i] = _sampleQueue.Dequeue();
                    }

                    byte[] opusBuffer = new byte[4000];
                    int encodedLength;
                    try
                    {
                        encodedLength = _encoder.Encode(
                            frame,
                            0,
                            FrameSize,
                            opusBuffer,
                            0,
                            opusBuffer.Length);
                    }
                    catch (Exception ex)
                    {
                        _encodeFailures++;
                        if (_encodeFailures == 1 || _encodeFailures % 50 == 0)
                            Debug.WriteLine($"[ScreenShare:Audio] Opus media encode failed #{_encodeFailures}: frameSamples={frame.Length}; frameSize={FrameSize}; channels={Channels}; {ex.Message}");

                        encodedLength = 0;
                    }

                    if (encodedLength > 0)
                    {
                        byte[] packetData = new byte[encodedLength];
                        Array.Copy(opusBuffer, packetData, encodedLength);
                        packets.Add(packetData);
                    }
                }

                encodedPackets = packets.ToArray();
                _encodedPackets += encodedPackets.Length;

                var now = DateTimeOffset.UtcNow;
                if (now - _lastAudioLogUtc >= TimeSpan.FromSeconds(2))
                {
                    _lastAudioLogUtc = now;
                    Debug.WriteLine(
                        $"[ScreenShare:Audio] loopback packet format={packet.SampleRate}Hz/{packet.Channels}ch/{packet.BitsPerSample}bit tag=0x{packet.FormatTag:X4}; float={packet.IsFloatFormat}; bytes={packet.PcmData.Length}; peak={packetPeak:0.0000}; queue={_sampleQueue.Count}; encodedNow={encodedPackets.Length}; captured={_capturedPackets}; encoded={_encodedPackets}; silent={_silentPackets}.");
                }
            }

            foreach (var encoded in encodedPackets)
            {
                AudioCaptured?.Invoke(PrefixScreenShareAudioPacket(encoded));
            }
        }

        private static short[] ConvertPacketToCallSamples(AudioPacket packet)
        {
            short[] samples = ConvertToInt16Samples(packet);
            short[] channels = ToChannelCount(samples, packet.Channels, Channels);

            return packet.SampleRate == SampleRate
                ? channels
                : ResampleLinear(channels, packet.SampleRate, SampleRate, Channels);
        }

        private static short[] ConvertToInt16Samples(AudioPacket packet)
        {
            if (packet.BitsPerSample == 16)
            {
                short[] samples = new short[packet.PcmData.Length / 2];
                Buffer.BlockCopy(packet.PcmData, 0, samples, 0, samples.Length * 2);
                return samples;
            }

            if (packet.BitsPerSample == 32)
            {
                short[] samples = new short[packet.PcmData.Length / 4];
                for (int i = 0; i < samples.Length; i++)
                {
                    float value;
                    if (packet.IsFloatFormat)
                    {
                        value = BitConverter.ToSingle(packet.PcmData, i * 4);
                        if (float.IsNaN(value) || float.IsInfinity(value))
                            value = 0f;
                    }
                    else
                    {
                        value = BitConverter.ToInt32(packet.PcmData, i * 4) / 2147483648f;
                    }

                    if (value > 1f) value = 1f;
                    if (value < -1f) value = -1f;

                    samples[i] = (short)Math.Round(value * short.MaxValue);
                }

                return samples;
            }

            return Array.Empty<short>();
        }

        private static double EstimatePeak(AudioPacket packet)
        {
            if (packet.PcmData.Length == 0)
                return 0;

            if (packet.BitsPerSample == 16)
            {
                var peak = 0;
                for (int i = 0; i + 1 < packet.PcmData.Length; i += 2)
                {
                    peak = Math.Max(peak, Math.Abs(BitConverter.ToInt16(packet.PcmData, i)));
                }

                return peak / (double)short.MaxValue;
            }

            if (packet.BitsPerSample == 32)
            {
                double peak = 0;
                for (int i = 0; i + 3 < packet.PcmData.Length; i += 4)
                {
                    var sample = packet.IsFloatFormat
                        ? BitConverter.ToSingle(packet.PcmData, i)
                        : BitConverter.ToInt32(packet.PcmData, i) / 2147483648d;

                    if (double.IsNaN(sample) || double.IsInfinity(sample))
                        sample = 0;

                    peak = Math.Max(peak, Math.Abs(sample));
                }

                return Math.Min(1, peak);
            }

            return 0;
        }

        private static short[] ToChannelCount(short[] samples, int sourceChannels, int targetChannels)
        {
            if (samples.Length == 0)
                return Array.Empty<short>();

            if (sourceChannels <= 0 || targetChannels <= 0)
                return Array.Empty<short>();

            if (sourceChannels == targetChannels)
                return samples;

            int frameCount = samples.Length / sourceChannels;
            short[] converted = new short[frameCount * targetChannels];

            for (int frame = 0; frame < frameCount; frame++)
            {
                int sourceOffset = frame * sourceChannels;
                int targetOffset = frame * targetChannels;

                if (sourceChannels == 1)
                {
                    for (var channel = 0; channel < targetChannels; channel++)
                        converted[targetOffset + channel] = samples[sourceOffset];
                }
                else if (targetChannels == 1)
                {
                    int sum = 0;
                    for (var channel = 0; channel < sourceChannels; channel++)
                        sum += samples[sourceOffset + channel];

                    converted[targetOffset] = (short)(sum / sourceChannels);
                }
                else
                {
                    for (var channel = 0; channel < targetChannels; channel++)
                        converted[targetOffset + channel] = samples[sourceOffset + Math.Min(channel, sourceChannels - 1)];
                }
            }

            return converted;
        }

        private static short[] ResampleLinear(short[] input, int sourceRate, int targetRate, int channels)
        {
            if (input.Length == 0 || sourceRate <= 0 || targetRate <= 0 || channels <= 0)
                return Array.Empty<short>();

            int inputFrames = input.Length / channels;
            int outputFrames = (int)Math.Round(inputFrames * (targetRate / (double)sourceRate));
            if (outputFrames <= 0)
                return Array.Empty<short>();

            short[] output = new short[outputFrames * channels];

            for (int frame = 0; frame < outputFrames; frame++)
            {
                double sourcePosition = frame * (sourceRate / (double)targetRate);
                int index0 = (int)Math.Floor(sourcePosition);
                int index1 = Math.Min(index0 + 1, inputFrames - 1);
                double amount = sourcePosition - index0;

                for (var channel = 0; channel < channels; channel++)
                {
                    var sample0 = input[(index0 * channels) + channel];
                    var sample1 = input[(index1 * channels) + channel];
                    double sample = sample0 + ((sample1 - sample0) * amount);
                    if (sample > short.MaxValue) sample = short.MaxValue;
                    if (sample < short.MinValue) sample = short.MinValue;

                    output[(frame * channels) + channel] = (short)Math.Round(sample);
                }
            }

            return output;
        }

        private static byte[] PrefixScreenShareAudioPacket(byte[] opusPacket)
        {
            byte[] packet = new byte[ScreenShareAudioHeader.Length + opusPacket.Length];
            Buffer.BlockCopy(ScreenShareAudioHeader, 0, packet, 0, ScreenShareAudioHeader.Length);
            Buffer.BlockCopy(opusPacket, 0, packet, ScreenShareAudioHeader.Length, opusPacket.Length);
            return packet;
        }
    }
}
