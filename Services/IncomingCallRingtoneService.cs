using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using NAudio.Wave;
using Zink.Services.NativeCalling;

namespace Zink.Services
{
    public sealed class IncomingCallRingtoneService : IDisposable
    {
        private static readonly object InstanceSync = new();
        private static IncomingCallRingtoneService? _instance;

        public static IncomingCallRingtoneService Instance
        {
            get
            {
                lock (InstanceSync)
                {
                    return _instance ??= new IncomingCallRingtoneService();
                }
            }
        }

        public static void TryStart()
        {
            try
            {
                Instance.Start();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[IncomingCallSound] Start skipped because ringtone service is unavailable: {ex}");
            }
        }

        public static void TryStop()
        {
            try
            {
                Instance.Stop();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[IncomingCallSound] Stop skipped because ringtone service is unavailable: {ex}");
            }
        }

        private readonly object _syncRoot = new();
        private readonly List<WaveOutEvent> _outputs = new();
        private DateTimeOffset _startedAt;
        private int _stopVersion;

        private IncomingCallRingtoneService()
        {
        }

        public void Start()
        {
            lock (_syncRoot)
            {
                try
                {
                    Debug.WriteLine("[IncomingCallSound] Start requested.");
                    StopCore();

                    foreach (var deviceNumber in GetPlaybackDeviceNumbers())
                    {
                        try
                        {
                            var output = new WaveOutEvent
                            {
                                DeviceNumber = deviceNumber,
                                DesiredLatency = 80
                            };

                            output.Init(new RingtoneWaveProvider());
                            output.Play();
                            _outputs.Add(output);

                            Debug.WriteLine($"[IncomingCallSound] Ringtone started on device {deviceNumber} ({GetDeviceName(deviceNumber)}).");
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"[IncomingCallSound] Failed to start device {deviceNumber} ({GetDeviceName(deviceNumber)}): {ex.Message}");
                        }
                    }

                    if (_outputs.Count == 0)
                    {
                        Debug.WriteLine("[IncomingCallSound] No playback devices accepted the ringtone.");
                        return;
                    }

                    _startedAt = DateTimeOffset.UtcNow;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[IncomingCallSound] Failed to play ringtone: {ex}");
                }
            }
        }

        public void Stop()
        {
            int version;
            TimeSpan remaining;

            lock (_syncRoot)
            {
                if (_outputs.Count == 0)
                    return;

                version = ++_stopVersion;
                var elapsed = DateTimeOffset.UtcNow - _startedAt;
                remaining = TimeSpan.FromMilliseconds(1500) - elapsed;
                if (remaining <= TimeSpan.Zero)
                {
                    StopCore();
                    return;
                }
            }

            _ = StopAfterMinimumDurationAsync(version, remaining);
        }

        private async Task StopAfterMinimumDurationAsync(int version, TimeSpan delay)
        {
            try
            {
                await Task.Delay(delay);
            }
            catch
            {
            }

            lock (_syncRoot)
            {
                if (version == _stopVersion)
                    StopCore();
            }
        }

        private void StopCore()
        {
            try
            {
                if (_outputs.Count > 0)
                    Debug.WriteLine("[IncomingCallSound] Stop requested.");

                foreach (var output in _outputs)
                {
                    try
                    {
                        output.Stop();
                        output.Dispose();
                    }
                    catch
                    {
                    }
                }
            }
            catch
            {
            }
            finally
            {
                _outputs.Clear();
            }
        }

        public void Dispose()
        {
            Stop();
        }

        private static IEnumerable<int> GetPlaybackDeviceNumbers()
        {
            var selectedDevice = AudioPlaybackService.Instance.SelectedOutputDeviceNumber;
            var seen = new HashSet<int>();

            if (selectedDevice >= -1 && seen.Add(selectedDevice))
                yield return selectedDevice;

            if (seen.Add(-1))
                yield return -1;

            for (var deviceNumber = 0; deviceNumber < WaveOut.DeviceCount; deviceNumber++)
            {
                if (seen.Add(deviceNumber))
                    yield return deviceNumber;
            }
        }

        private static string GetDeviceName(int deviceNumber)
        {
            if (deviceNumber < 0)
                return "Windows default";

            try
            {
                return WaveOut.GetCapabilities(deviceNumber).ProductName;
            }
            catch
            {
                return "Unknown";
            }
        }

        private sealed class RingtoneWaveProvider : IWaveProvider
        {
            private const int SampleRate = 44100;
            private const int Channels = 2;
            private static readonly RingtoneNote[] Pattern =
            {
                new(659.25, 0.13),
                new(783.99, 0.13),
                new(987.77, 0.18),
                new(0.0, 0.08),
                new(783.99, 0.12),
                new(987.77, 0.12),
                new(1174.66, 0.24),
                new(0.0, 0.78)
            };

            private long _sampleIndex;

            public RingtoneWaveProvider()
            {
            }

            public WaveFormat WaveFormat { get; } = new WaveFormat(SampleRate, 16, Channels);

            public int Read(byte[] buffer, int offset, int count)
            {
                var frameSize = Channels * 2;
                var frames = count / frameSize;

                for (var frame = 0; frame < frames; frame++)
                {
                    var time = _sampleIndex / (double)SampleRate;
                    var note = GetNote(time, out var noteTime);
                    var amplitude = 0.0;

                    if (note.Frequency > 0.0)
                    {
                        var envelope = GetEnvelope(noteTime, note.DurationSeconds);
                        var sparkle = Math.Sin(2 * Math.PI * note.Frequency * time);
                        var chime = Math.Sin(2 * Math.PI * note.Frequency * 2.0 * time) * 0.18;
                        amplitude = (sparkle + chime) * envelope * 0.42;
                    }

                    var sample = (short)(amplitude * short.MaxValue);
                    var writeOffset = offset + frame * frameSize;

                    buffer[writeOffset] = (byte)(sample & 0xff);
                    buffer[writeOffset + 1] = (byte)((sample >> 8) & 0xff);
                    buffer[writeOffset + 2] = buffer[writeOffset];
                    buffer[writeOffset + 3] = buffer[writeOffset + 1];

                    _sampleIndex++;
                }

                return frames * frameSize;
            }

            private static RingtoneNote GetNote(double time, out double noteTime)
            {
                var patternDuration = 0.0;
                foreach (var note in Pattern)
                    patternDuration += note.DurationSeconds;

                var cycleTime = time % patternDuration;
                var elapsed = 0.0;

                foreach (var note in Pattern)
                {
                    if (cycleTime < elapsed + note.DurationSeconds)
                    {
                        noteTime = cycleTime - elapsed;
                        return note;
                    }

                    elapsed += note.DurationSeconds;
                }

                noteTime = 0.0;
                return Pattern[^1];
            }

            private static double GetEnvelope(double noteTime, double duration)
            {
                const double attack = 0.018;
                const double release = 0.055;

                var fadeIn = Math.Min(1.0, noteTime / attack);
                var fadeOut = Math.Min(1.0, Math.Max(0.0, duration - noteTime) / release);
                return Math.Min(fadeIn, fadeOut);
            }

            private readonly record struct RingtoneNote(double Frequency, double DurationSeconds);
        }
    }
}
