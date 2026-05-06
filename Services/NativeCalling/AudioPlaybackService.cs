using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using ConcentusDecoder = Concentus.Structs.OpusDecoder;
using NAudio.Wave;

namespace Zink.Services.NativeCalling
{
    public sealed class AudioPlaybackService
    {
        public sealed class OutputDeviceInfo
        {
            public int DeviceNumber { get; init; }
            public string Name { get; init; } = string.Empty;

            public override string ToString() => Name;
        }

        public static AudioPlaybackService Instance { get; } = new AudioPlaybackService();

        private readonly object _gate = new();
        private readonly Queue<byte[]> _packetQueue = new();
        private readonly Queue<byte[]> _mediaPacketQueue = new();

        private BufferedWaveProvider? _buffer;
        private BufferedWaveProvider? _mediaBuffer;
        private WaveOutEvent? _output;
        private WaveOutEvent? _mediaOutput;
        private readonly ConcentusDecoder _decoder;
        private readonly ConcentusDecoder _mediaDecoder;

        private const int SampleRate = 16000;
        private const int Channels = 1;
        private const int FrameSize = 320;
        private const int MediaSampleRate = 48000;
        private const int MediaChannels = 2;
        private const int MediaFrameSize = 960;
        private const int MaxBufferedPackets = 12;
        private const int MaxBufferedMediaPackets = 42;
        private const int StartPlaybackPacketThreshold = 2;
        private const int MediaStartPlaybackPacketThreshold = 8;
        private const int MediaDesiredLatencyMs = 120;
        private const int MediaTargetBufferedMs = 240;
        private const int MediaHardTrimBufferedMs = 640;
        private const float VoicePlaybackGain = 1.8f;
        private const float MediaPlaybackGain = 1.6f;
        private static readonly byte[] ScreenShareAudioHeader = { (byte)'Z', (byte)'S', (byte)'A', (byte)'1' };

        private int _selectedOutputDeviceNumber = -1;
        private bool _isPrimed;
        private bool _isMediaPrimed;
        private long _mediaPacketsReceived;
        private long _mediaPacketsDropped;
        private long _mediaPacketsDecoded;
        private DateTimeOffset _lastMediaPlaybackLogUtc = DateTimeOffset.MinValue;

        public int SelectedOutputDeviceNumber
        {
            get
            {
                lock (_gate)
                {
                    return _selectedOutputDeviceNumber;
                }
            }
        }

        private AudioPlaybackService()
        {
            _decoder = new ConcentusDecoder(SampleRate, Channels);
            _mediaDecoder = new ConcentusDecoder(MediaSampleRate, MediaChannels);
        }

        public IReadOnlyList<OutputDeviceInfo> GetOutputDevices()
        {
            var devices = new List<OutputDeviceInfo>
            {
                new OutputDeviceInfo
                {
                    DeviceNumber = -1,
                    Name = "Default"
                }
            };

            for (int i = 0; i < WaveOut.DeviceCount; i++)
            {
                var capabilities = WaveOut.GetCapabilities(i);

                devices.Add(new OutputDeviceInfo
                {
                    DeviceNumber = i,
                    Name = capabilities.ProductName
                });
            }

            return devices;
        }

        public bool SetOutputDevice(int deviceNumber)
        {
            lock (_gate)
            {
                if (deviceNumber < -1 || deviceNumber >= WaveOut.DeviceCount)
                    return false;

                bool wasRunning = _output != null || _mediaOutput != null;

                if (wasRunning)
                    StopPlaybackLocked();

                _selectedOutputDeviceNumber = deviceNumber;

                if (wasRunning)
                    StartPlaybackLocked();

                return true;
            }
        }

        public string GetCurrentOutputDisplayName()
        {
            var devices = GetOutputDevices();
            var current = devices.FirstOrDefault(x => x.DeviceNumber == SelectedOutputDeviceNumber);
            return current?.Name ?? "Default";
        }

        public void Start()
        {
            lock (_gate)
            {
                if (_output != null)
                    return;

                StartPlaybackLocked();
            }
        }

        public void Stop()
        {
            lock (_gate)
            {
                StopPlaybackLocked();
            }
        }

        private void StartPlaybackLocked()
        {
            var format = new WaveFormat(SampleRate, 16, Channels);

            _buffer = new BufferedWaveProvider(format)
            {
                DiscardOnBufferOverflow = true,
                BufferDuration = TimeSpan.FromMilliseconds(450)
            };

            _output = new WaveOutEvent
            {
                DesiredLatency = 80,
                DeviceNumber = _selectedOutputDeviceNumber
            };

            _output.Init(_buffer);
            _output.Play();

            _mediaBuffer = new BufferedWaveProvider(new WaveFormat(MediaSampleRate, 16, MediaChannels))
            {
                DiscardOnBufferOverflow = true,
                BufferDuration = TimeSpan.FromMilliseconds(420)
            };

            _mediaOutput = new WaveOutEvent
            {
                DesiredLatency = MediaDesiredLatencyMs,
                DeviceNumber = _selectedOutputDeviceNumber
            };

            _mediaOutput.Init(_mediaBuffer);
            _mediaOutput.Play();

            _packetQueue.Clear();
            _mediaPacketQueue.Clear();
            _isPrimed = false;
            _isMediaPrimed = false;
            _mediaPacketsReceived = 0;
            _mediaPacketsDropped = 0;
            _mediaPacketsDecoded = 0;
            _lastMediaPlaybackLogUtc = DateTimeOffset.MinValue;
        }

        private void StopPlaybackLocked()
        {
            try
            {
                _output?.Stop();
                _output?.Dispose();
                _output = null;
                _buffer = null;
                _mediaOutput?.Stop();
                _mediaOutput?.Dispose();
                _mediaOutput = null;
                _mediaBuffer = null;

                _packetQueue.Clear();
                _mediaPacketQueue.Clear();
                _isPrimed = false;
                _isMediaPrimed = false;
                _lastMediaPlaybackLogUtc = DateTimeOffset.MinValue;
            }
            finally
            {
                AudioActivityService.Instance.UpdateRemoteLevel(0);
            }
        }

        public void Play(byte[] data)
        {
            if (data == null || data.Length == 0)
            {
                AudioActivityService.Instance.UpdateRemoteLevel(0);
                return;
            }

            lock (_gate)
            {
                if (TryUnwrapScreenShareAudioPacket(data, out var mediaPacket))
                {
                    PlayMediaLocked(mediaPacket);
                    return;
                }

                if (_buffer == null)
                {
                    AudioActivityService.Instance.UpdateRemoteLevel(0);
                    return;
                }

                _packetQueue.Enqueue(data);

                while (_packetQueue.Count > MaxBufferedPackets)
                    _packetQueue.Dequeue();

                if (!_isPrimed)
                {
                    if (_packetQueue.Count < StartPlaybackPacketThreshold)
                    {
                        AudioActivityService.Instance.UpdateRemoteLevel(0);
                        return;
                    }

                    _isPrimed = true;
                }

                if (_buffer.BufferedDuration.TotalMilliseconds > 900)
                {
                    _buffer.ClearBuffer();

                    while (_packetQueue.Count > StartPlaybackPacketThreshold)
                        _packetQueue.Dequeue();
                }

                while (_packetQueue.Count > 0 && _buffer.BufferedDuration.TotalMilliseconds < 220)
                {
                    var packet = _packetQueue.Dequeue();
                    DecodeAndQueuePacket(packet);
                }
            }
        }

        public static bool IsScreenShareAudioPacket(byte[] data)
        {
            if (data == null || data.Length <= ScreenShareAudioHeader.Length)
                return false;

            for (var i = 0; i < ScreenShareAudioHeader.Length; i++)
            {
                if (data[i] != ScreenShareAudioHeader[i])
                    return false;
            }

            return true;
        }

        private void PlayMediaLocked(byte[] data)
        {
            if (_mediaBuffer == null)
            {
                AudioActivityService.Instance.UpdateRemoteLevel(0);
                return;
            }

            _mediaPacketsReceived++;
            _mediaPacketQueue.Enqueue(data);
            while (_mediaPacketQueue.Count > MaxBufferedMediaPackets)
            {
                _mediaPacketQueue.Dequeue();
                _mediaPacketsDropped++;
            }

            if (!_isMediaPrimed)
            {
                if (_mediaPacketQueue.Count < MediaStartPlaybackPacketThreshold)
                    return;

                _isMediaPrimed = true;
            }

            if (_mediaBuffer.BufferedDuration.TotalMilliseconds > MediaHardTrimBufferedMs)
            {
                _mediaBuffer.ClearBuffer();
                while (_mediaPacketQueue.Count > MediaStartPlaybackPacketThreshold)
                {
                    _mediaPacketQueue.Dequeue();
                    _mediaPacketsDropped++;
                }

                _isMediaPrimed = _mediaPacketQueue.Count >= MediaStartPlaybackPacketThreshold;
            }

            while (_mediaPacketQueue.Count > 0 && _mediaBuffer.BufferedDuration.TotalMilliseconds < MediaTargetBufferedMs)
            {
                DecodeAndQueueMediaPacket(_mediaPacketQueue.Dequeue());
            }

            var now = DateTimeOffset.UtcNow;
            if (now - _lastMediaPlaybackLogUtc >= TimeSpan.FromSeconds(2))
            {
                _lastMediaPlaybackLogUtc = now;
                Debug.WriteLine(
                    $"[ScreenShare:AudioPlayback] media received={_mediaPacketsReceived}; decoded={_mediaPacketsDecoded}; dropped={_mediaPacketsDropped}; packetQueue={_mediaPacketQueue.Count}; bufferedMs={_mediaBuffer.BufferedDuration.TotalMilliseconds:0}; desiredLatencyMs={_mediaOutput?.DesiredLatency ?? 0}.");
            }
        }

        private void DecodeAndQueuePacket(byte[] packet)
        {
            if (_buffer == null || packet.Length == 0)
                return;

            short[] pcm = new short[FrameSize * 6];
            int decodedSamples;

            try
            {
                decodedSamples = _decoder.Decode(
                    packet,
                    0,
                    packet.Length,
                    pcm,
                    0,
                    pcm.Length,
                    false);
            }
            catch
            {
                AudioActivityService.Instance.UpdateRemoteLevel(0);
                return;
            }

            if (decodedSamples <= 0)
            {
                AudioActivityService.Instance.UpdateRemoteLevel(0);
                return;
            }

            ApplyPcmGain(pcm, decodedSamples, VoicePlaybackGain);

            byte[] pcmBytes = new byte[decodedSamples * sizeof(short)];
            Buffer.BlockCopy(pcm, 0, pcmBytes, 0, pcmBytes.Length);
            _buffer.AddSamples(pcmBytes, 0, pcmBytes.Length);

            double level = 0;
            for (int i = 0; i < decodedSamples; i++)
            {
                level += Math.Abs((int)pcm[i]);
            }

            level /= decodedSamples;
            level /= short.MaxValue;

            AudioActivityService.Instance.UpdateRemoteLevel(level);
        }

        private void DecodeAndQueueMediaPacket(byte[] packet)
        {
            if (_mediaBuffer == null || packet.Length == 0)
                return;

            short[] pcm = new short[MediaFrameSize * MediaChannels * 6];
            int decodedSamples;

            try
            {
                decodedSamples = _mediaDecoder.Decode(
                    packet,
                    0,
                    packet.Length,
                    pcm,
                    0,
                    pcm.Length / MediaChannels,
                    false);
            }
            catch
            {
                AudioActivityService.Instance.UpdateRemoteLevel(0);
                return;
            }

            if (decodedSamples <= 0)
            {
                AudioActivityService.Instance.UpdateRemoteLevel(0);
                return;
            }

            var sampleCount = decodedSamples * MediaChannels;
            ApplyPcmGain(pcm, sampleCount, MediaPlaybackGain);

            byte[] pcmBytes = new byte[sampleCount * sizeof(short)];
            Buffer.BlockCopy(pcm, 0, pcmBytes, 0, pcmBytes.Length);
            _mediaBuffer.AddSamples(pcmBytes, 0, pcmBytes.Length);
            _mediaPacketsDecoded++;

            double level = 0;
            for (int i = 0; i < sampleCount; i++)
                level += Math.Abs((int)pcm[i]);

            level /= sampleCount;
            level /= short.MaxValue;
            AudioActivityService.Instance.UpdateRemoteLevel(level);
        }

        private static void ApplyPcmGain(short[] samples, int sampleCount, float gain)
        {
            if (gain <= 1f)
                return;

            var count = Math.Min(sampleCount, samples.Length);
            for (var i = 0; i < count; i++)
            {
                var amplified = (int)MathF.Round(samples[i] * gain);
                samples[i] = (short)Math.Clamp(amplified, short.MinValue, short.MaxValue);
            }
        }

        private static bool TryUnwrapScreenShareAudioPacket(byte[] data, out byte[] opusPacket)
        {
            opusPacket = Array.Empty<byte>();
            if (!IsScreenShareAudioPacket(data))
                return false;

            opusPacket = new byte[data.Length - ScreenShareAudioHeader.Length];
            Buffer.BlockCopy(data, ScreenShareAudioHeader.Length, opusPacket, 0, opusPacket.Length);
            return true;
        }
    }
}
