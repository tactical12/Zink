using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Zink.Interop;

namespace Zink.Services.Recording
{
    public abstract class WasapiCaptureBase : IAudioCaptureService
    {
        private readonly object _gate = new();

        private CancellationTokenSource? _cts;
        private Task? _workerTask;

        private IMMDeviceEnumerator? _enumerator;
        private IMMDevice? _device;
        private IAudioClient? _audioClient;
        private IAudioCaptureClient? _captureClient;

        private WAVEFORMATEX _waveFormat;
        private bool _initialized;
        private DateTime _captureStartUtc;
        private long _packetCount;
        private long _totalBytes;

        // Stable audio-clock tracking.
        private long _capturedFrames;
        private int _bytesPerFrame;

        public event EventHandler<AudioPacket>? AudioPacketArrived;

        public bool IsRunning { get; private set; }
        public string? DeviceId { get; private set; }

        protected abstract bool UseLoopback { get; }
        protected abstract EDataFlow DataFlow { get; }

        public async Task StartAsync(string? deviceId = null)
        {
            lock (_gate)
            {
                if (IsRunning)
                    return;
            }

            try
            {
                await RecorderLog.InfoAsync(GetType().Name,
                    $"Starting audio capture. DeviceId='{deviceId ?? "(default)"}', Loopback={UseLoopback}, DataFlow={DataFlow}");

                InitializeAudioClient(deviceId);

                var cts = new CancellationTokenSource();
                _cts = cts;
                _captureStartUtc = DateTime.UtcNow;
                _packetCount = 0;
                _totalBytes = 0;
                _capturedFrames = 0;
                _bytesPerFrame = _waveFormat.nBlockAlign;

                _workerTask = Task.Run(() => CaptureLoop(cts.Token), cts.Token);

                lock (_gate)
                {
                    IsRunning = true;
                    DeviceId = deviceId;
                }

                await RecorderLog.InfoAsync(GetType().Name,
                    $"Audio capture started. Format: {_waveFormat.nSamplesPerSec} Hz, {_waveFormat.nChannels} ch, {_waveFormat.wBitsPerSample} bit, blockAlign={_waveFormat.nBlockAlign}");
            }
            catch (Exception ex)
            {
                await RecorderLog.ErrorAsync(GetType().Name, ex, "StartAsync failed");
                await CleanupFailedStartAsync();
                throw;
            }
        }

        public async Task StopAsync()
        {
            CancellationTokenSource? cts;
            Task? workerTask;
            IAudioClient? audioClient;

            lock (_gate)
            {
                if (!IsRunning && !_initialized)
                    return;

                IsRunning = false;

                cts = _cts;
                workerTask = _workerTask;
                audioClient = _audioClient;

                _cts = null;
                _workerTask = null;
            }

            try
            {
                cts?.Cancel();
            }
            catch
            {
            }

            if (workerTask is not null)
            {
                try
                {
                    await workerTask;
                }
                catch
                {
                }
            }

            try
            {
                audioClient?.Stop();
            }
            catch
            {
            }

            ReleaseComObjects();

            cts?.Dispose();
            DeviceId = null;

            await RecorderLog.InfoAsync(GetType().Name,
                $"Audio capture stopped. Packets={_packetCount}, Bytes={_totalBytes}, CapturedFrames={_capturedFrames}");
        }

        private void InitializeAudioClient(string? deviceId)
        {
            _enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();

            _device = ResolveDevice(deviceId);

            object audioClientObj;

            Guid audioClientGuid = CoreAudioGuids.IID_IAudioClient;
            HResult.Check(
                _device!.Activate(ref audioClientGuid, CLSCTX.ALL, IntPtr.Zero, out audioClientObj),
                "IMMDevice.Activate(IAudioClient)");

            _audioClient = (IAudioClient)audioClientObj;

            IntPtr mixFormatPtr = IntPtr.Zero;

            try
            {
                HResult.Check(
                    _audioClient.GetMixFormat(out mixFormatPtr),
                    "IAudioClient.GetMixFormat");

                _waveFormat = Marshal.PtrToStructure<WAVEFORMATEX>(mixFormatPtr);

                var flags =
                    AUDCLNT_STREAMFLAGS.AUTOCONVERTPCM |
                    AUDCLNT_STREAMFLAGS.SRC_DEFAULT_QUALITY;

                if (UseLoopback)
                {
                    flags |= AUDCLNT_STREAMFLAGS.LOOPBACK;
                }

                long bufferDuration100ns = 10_000_000;

                HResult.Check(
                    _audioClient.Initialize(
                        AUDCLNT_SHAREMODE.SHARED,
                        flags,
                        bufferDuration100ns,
                        0,
                        mixFormatPtr,
                        IntPtr.Zero),
                    "IAudioClient.Initialize");

                object captureClientObj;
                Guid captureClientGuid = CoreAudioGuids.IID_IAudioCaptureClient;
                HResult.Check(
                    _audioClient.GetService(ref captureClientGuid, out captureClientObj),
                    "IAudioClient.GetService(IAudioCaptureClient)");

                _captureClient = (IAudioCaptureClient)captureClientObj;

                HResult.Check(
                    _audioClient.Start(),
                    "IAudioClient.Start");

                _initialized = true;
            }
            finally
            {
                if (mixFormatPtr != IntPtr.Zero)
                {
                    Marshal.FreeCoTaskMem(mixFormatPtr);
                }
            }
        }

        private IMMDevice ResolveDevice(string? deviceId)
        {
            if (_enumerator is null)
                throw new InvalidOperationException("IMMDeviceEnumerator was not initialized.");

            if (!string.IsNullOrWhiteSpace(deviceId))
            {
                try
                {
                    int hr = _enumerator.GetDevice(deviceId, out IMMDevice selectedDevice);
                    HResult.Check(hr, "IMMDeviceEnumerator.GetDevice");

                    RecorderLog.InfoAsync(GetType().Name,
                        $"Using requested Core Audio device id: {deviceId}")
                        .ConfigureAwait(false);

                    return selectedDevice;
                }
                catch (Exception ex)
                {
                    RecorderLog.ErrorAsync(GetType().Name, ex,
                        $"GetDevice failed for supplied id '{deviceId}'. Falling back to default endpoint.")
                        .ConfigureAwait(false);
                }
            }

            HResult.Check(
                _enumerator.GetDefaultAudioEndpoint(DataFlow, ERole.eMultimedia, out IMMDevice defaultDevice),
                "IMMDeviceEnumerator.GetDefaultAudioEndpoint");

            RecorderLog.InfoAsync(GetType().Name,
                $"Using default endpoint for {DataFlow}.")
                .ConfigureAwait(false);

            return defaultDevice;
        }

        private async Task CaptureLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                uint nextPacketFrames = 0;

                try
                {
                    HResult.Check(
                        _captureClient!.GetNextPacketSize(out nextPacketFrames),
                        "IAudioCaptureClient.GetNextPacketSize");
                }
                catch (Exception ex)
                {
                    await RecorderLog.ErrorAsync(GetType().Name, ex, "GetNextPacketSize failed");
                    Thread.Sleep(5);
                    continue;
                }

                if (nextPacketFrames == 0)
                {
                    Thread.Sleep(2);
                    continue;
                }

                while (nextPacketFrames > 0 && !token.IsCancellationRequested)
                {
                    IntPtr dataPtr = IntPtr.Zero;
                    uint framesRead = 0;
                    AUDCLNT_BUFFERFLAGS flags = AUDCLNT_BUFFERFLAGS.NONE;
                    long devicePosition = 0;
                    long qpcPosition = 0;

                    HResult.Check(
                        _captureClient!.GetBuffer(
                            out dataPtr,
                            out framesRead,
                            out flags,
                            out devicePosition,
                            out qpcPosition),
                        "IAudioCaptureClient.GetBuffer");

                    try
                    {
                        int bytesPerFrame = _bytesPerFrame;
                        if (bytesPerFrame <= 0)
                            throw new InvalidOperationException("Invalid audio block alignment.");

                        int byteCount = checked((int)framesRead * bytesPerFrame);
                        byte[] pcm = new byte[byteCount];

                        bool isSilent = (flags & AUDCLNT_BUFFERFLAGS.SILENT) == AUDCLNT_BUFFERFLAGS.SILENT;

                        if (!isSilent && byteCount > 0 && dataPtr != IntPtr.Zero)
                        {
                            Marshal.Copy(dataPtr, pcm, 0, byteCount);
                        }

                        // Use the audio sample clock, not wall-clock arrival time.
                        TimeSpan packetTimestamp = TimeSpan.FromSeconds(
                            _capturedFrames / (double)_waveFormat.nSamplesPerSec);

                        _capturedFrames += framesRead;
                        _packetCount++;
                        _totalBytes += byteCount;

                        if (_packetCount % 200 == 0)
                        {
                            await RecorderLog.InfoAsync(GetType().Name,
                                $"Packets={_packetCount}, Bytes={_totalBytes}, FramesRead={framesRead}, Silent={isSilent}, Timestamp={packetTimestamp}, DevicePosition={devicePosition}, Qpc={qpcPosition}");
                        }

                        AudioPacketArrived?.Invoke(this, new AudioPacket
                        {
                            Timestamp = packetTimestamp,
                            PcmData = pcm,
                            SampleRate = (int)_waveFormat.nSamplesPerSec,
                            Channels = _waveFormat.nChannels,
                            BitsPerSample = _waveFormat.wBitsPerSample
                        });
                    }
                    catch (Exception ex)
                    {
                        await RecorderLog.ErrorAsync(GetType().Name, ex, "Audio packet processing failed");
                    }
                    finally
                    {
                        try
                        {
                            _captureClient.ReleaseBuffer(framesRead);
                        }
                        catch (Exception ex)
                        {
                            await RecorderLog.ErrorAsync(GetType().Name, ex, "ReleaseBuffer failed");
                        }
                    }

                    HResult.Check(
                        _captureClient.GetNextPacketSize(out nextPacketFrames),
                        "IAudioCaptureClient.GetNextPacketSize");
                }
            }
        }

        private async Task CleanupFailedStartAsync()
        {
            try
            {
                await StopAsync();
            }
            catch
            {
            }
        }

        private void ReleaseComObjects()
        {
            if (_captureClient is not null)
            {
                try { Marshal.ReleaseComObject(_captureClient); } catch { }
                _captureClient = null;
            }

            if (_audioClient is not null)
            {
                try { Marshal.ReleaseComObject(_audioClient); } catch { }
                _audioClient = null;
            }

            if (_device is not null)
            {
                try { Marshal.ReleaseComObject(_device); } catch { }
                _device = null;
            }

            if (_enumerator is not null)
            {
                try { Marshal.ReleaseComObject(_enumerator); } catch { }
                _enumerator = null;
            }

            _initialized = false;
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync();
        }
    }
}