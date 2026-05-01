using SharpDX;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using SharpDX.MediaFoundation;
using SharpDX.Mathematics.Interop;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Zink.Services.NativeCalling
{
    public sealed class MediaFoundationH264Encoder : IDisposable
    {
        private static readonly Guid CmsH264EncoderMft = new("6CA50344-051A-4DED-9779-A43305165E35");
        private static readonly Guid CodecApiAvLowLatencyMode = new("9C27891A-ED7A-40E1-88E8-B22727A024EE");
        private static readonly Guid CodecApiAvEncCommonLowLatency = new("9D3ECD55-89E8-490A-970A-0C9548D5A56E");
        private static readonly Guid CodecApiAvEncCommonRealTime = new("143A0FF6-A131-43DA-B81E-98FBB8EC378E");
        private static readonly Guid CodecApiAvEncCommonQualityVsSpeed = new("98332DF8-03CD-476B-89FA-3F9E442DEC9F");
        private static readonly Guid CodecApiAvEncVideoMaxKeyframeDistance = new("2987123A-BA93-4704-B489-EC1E5F25292C");
        private static readonly Guid CodecApiAvEncVideoForceKeyFrame = new("398C1B98-8353-475A-9EF2-8F265D260345");
        private static readonly Guid CodecApiAvEncVideoNumGopsPerIdr = new("83BC5BDB-5B89-4521-8F66-33151C373176");
        private const int RecoveryKeyFrameIntervalFrames = NativeScreenShareStreamingService.TargetFps;
        private const int MfENeedMoreInput = unchecked((int)0xC00D6D72);
        private const int MfEStreamChange = unchecked((int)0xC00D6D61);
        private const int MfEUnsupportedD3DType = unchecked((int)0xC00D6D76);
        private const int MfENoEventsAvailable = unchecked((int)0xC00D3E80);
        private const int MftOutputStreamProvidesSamples = 0x00000100;
        private const int MftOutputStreamCanProvideSamples = 0x00000200;
        private const int DxgiInputTexturePoolSize = 16;
        private const int ImfTransformProcessOutputVtableSlot = 25;
        private const long FrameDuration100Ns = 10_000_000L / NativeScreenShareStreamingService.TargetFps;
        private const bool UseDxgiSurfaceInputForHardwareEncoder = false;
        private static readonly int[] YFromR = BuildContributionTable(47, 16 << 8);
        private static readonly int[] YFromG = BuildContributionTable(157, 0);
        private static readonly int[] YFromB = BuildContributionTable(16, 0);
        private static readonly int[] UFromR = BuildContributionTable(-26, 128 << 8);
        private static readonly int[] UFromG = BuildContributionTable(-87, 0);
        private static readonly int[] UFromB = BuildContributionTable(112, 0);
        private static readonly int[] VFromR = BuildContributionTable(112, 128 << 8);
        private static readonly int[] VFromG = BuildContributionTable(-102, 0);
        private static readonly int[] VFromB = BuildContributionTable(-10, 0);

        private Transform _encoder = null!;
        private readonly int _width;
        private readonly int _height;
        private int _bitrate;
        private bool _useRgb32Input;
        private bool _useDxgiSurfaceInput;
        private byte[]? _nv12Buffer;
        private int _inputBufferLength;
        private string _encoderMode = "Not started";
        private bool _isHardwareAccelerated;
        private bool _dxgiDeviceManagerAttached;
        private long _sampleTime;
        private int _frameIndex;
        private int _forceNextKeyFrame;
        private bool _loggedFirstOutputFrame;
        private bool _loggedOutputStreamMode;
        private bool _loggedForceKeyFrameUnavailable;
        private byte[]? _cachedParameterSetsAnnexB;
        private DirectX12VideoDeviceManager? _directX12VideoDeviceManager;
        private SharpDX.Direct3D11.Device? _nativeMediaFoundationDevice;
        private readonly SharpDX.Direct3D11.Device? _preferredMediaFoundationDevice;
        private SharpDX.Direct3D11.Device? _encoderD3D11Device;
        private Texture2D[]? _dxgiInputTextures;
        private int _dxgiInputTextureIndex;
        private DXGIDeviceManager? _dxgiDeviceManager;
        private MediaEventGenerator? _hardwareEventGenerator;
        private readonly Queue<PendingHardwareInputSample> _pendingHardwareInputs = new();
        private readonly object _hardwareEventSync = new();
        private Thread? _hardwareEventThread;
        private ProcessOutputNativeDelegate? _processOutputNative;
        private bool _stopHardwareEventThread;
        private int _hardwareInputRequests;
        private int _hardwareOutputRequests;
        private int _loggedHardwareEvents;
        private bool _loggedWaitingForHardwareInput;
        private DateTimeOffset _lastHardwareInputWaitLogUtc = DateTimeOffset.MinValue;
        private string _gpuDeviceManagerMode = "Not attached";
        private bool _loggedHardwareInputBackPressure;
        private bool _loggedUnreadableHardwareOutput;
        private bool _gpuTextureInputDisabled;
        private DateTimeOffset _lastOutputStreamChangeLogUtc = DateTimeOffset.MinValue;

        static MediaFoundationH264Encoder()
        {
            try
            {
                MediaManager.Startup();
            }
            catch
            {
            }
        }

        public MediaFoundationH264Encoder(
            int width,
            int height,
            int bitrate,
            bool preferHardware = true,
            bool requireHardware = false,
            SharpDX.Direct3D11.Device? preferredMediaFoundationDevice = null)
        {
            _width = width;
            _height = height;
            _bitrate = bitrate;
            _preferredMediaFoundationDevice = preferredMediaFoundationDevice;
            Debug.WriteLine($"[ScreenShare:H264] Creating encoder {width}x{height} @ {bitrate}bps.");
            InitializeEncoder(bitrate, preferHardware, requireHardware);
        }

        private void InitializeEncoder(int bitrate, bool preferHardware, bool requireHardware)
        {
            var allowHardware = preferHardware || requireHardware;
            var forceNativeDxgiDeviceManager = false;

            while (true)
            {
                var selection = CreateEncoderTransform(allowHardware, requireHardware);
                _encoder = selection.Encoder;
                _encoderMode = selection.Mode;
                _isHardwareAccelerated = selection.IsHardwareAccelerated;
                _dxgiDeviceManagerAttached = false;

                try
                {
                    if (selection.IsHardwareAccelerated)
                    {
                        TryUnlockAsyncHardwareTransform(_encoder);

                        if (UseDxgiSurfaceInputForHardwareEncoder)
                        {
                            TryAttachDxgiDeviceManager(forceNativeDxgiDeviceManager);
                        }
                        else
                        {
                            _gpuDeviceManagerMode = "System-memory NV12 hardware path; DXGI manager intentionally not attached";
                            Debug.WriteLine("[ScreenShare:H264] Using system-memory NV12 hardware path with async-unlocked NVENC and no DXGI manager.");
                        }
                    }

                    try
                    {
                        ConfigureEncoder(
                            bitrate,
                            enableHardwareAsyncMode: selection.IsHardwareAccelerated);
                    }
                    catch (SharpDXException ex) when (
                        selection.IsHardwareAccelerated &&
                        _dxgiDeviceManagerAttached &&
                        ex.ResultCode.Code == MfEUnsupportedD3DType &&
                        !forceNativeDxgiDeviceManager)
                    {
                        Debug.WriteLine("[ScreenShare:H264] NVENC rejected the D3D11On12 media input type; recreating NVENC with a native D3D11 DXGI manager.");

                        try
                        {
                            _encoder.Dispose();
                        }
                        catch
                        {
                        }

                        DisposeGpuEncodingResources();
                        forceNativeDxgiDeviceManager = true;
                        continue;
                    }

                    return;
                }
                catch (Exception ex) when (selection.IsHardwareAccelerated && requireHardware)
                {
                    Debug.WriteLine($"[ScreenShare:H264] Forced GPU encoder rejected configuration: {ex.Message}");

                    try
                    {
                        _encoder.Dispose();
                    }
                    catch
                    {
                    }

                    DisposeGpuEncodingResources();
                    throw new InvalidOperationException(
                        "GPU hardware H.264 encoding is required, but the hardware encoder rejected the current configuration.",
                        ex);
                }
                catch (Exception ex) when (selection.IsHardwareAccelerated)
                {
                    Debug.WriteLine($"[ScreenShare:H264] Hardware encoder rejected configuration, falling back to software MFT: {ex.Message}");

                    try
                    {
                        _encoder.Dispose();
                    }
                    catch
                    {
                    }

                    DisposeGpuEncodingResources();
                    allowHardware = false;
                }
            }
        }

        private void ConfigureEncoder(int bitrate, bool enableHardwareAsyncMode)
        {
            _bitrate = bitrate;
            RealtimeModeEnabled = false;
            LowLatencyOutputEnabled = false;
            _useRgb32Input = false;
            _useDxgiSurfaceInput = false;
            _nv12Buffer = null;
            _inputBufferLength = 0;

            RealtimeModeEnabled = TryEnableRealtimeEncoderMode(_encoder, enableHardwareAsyncMode);

            using var outputType = CreateH264OutputType(bitrate);
            LowLatencyOutputEnabled = TrySetLowLatencyOutputTypeAttributes(outputType);
            _encoder.SetOutputType(0, outputType, 0);

            if (_isHardwareAccelerated && UseDxgiSurfaceInputForHardwareEncoder)
            {
                if (!_dxgiDeviceManagerAttached || _encoderD3D11Device == null)
                    throw new InvalidOperationException("The GPU encoder requires a DXGI device manager and D3D11 input device.");

                _nv12Buffer = new byte[_width * _height * 3 / 2];
                _inputBufferLength = _nv12Buffer.Length;
                SetInputType(VideoFormatGuids.NV12, "D3D11 NV12 DXGI surface");
                EnsureDxgiInputTextures();
                _useDxgiSurfaceInput = true;
                InitializeHardwareEventPump();
                Debug.WriteLine("[ScreenShare:H264] Using D3D11 NV12 DXGI surface input for the GPU hardware encoder.");
            }
            else
            {
                ConfigureSystemMemoryInput();
            }

            _encoder.ProcessMessage(TMessageType.NotifyBeginStreaming, IntPtr.Zero);
            _encoder.ProcessMessage(TMessageType.NotifyStartOfStream, IntPtr.Zero);
            if (_isHardwareAccelerated && !UseDxgiSurfaceInputForHardwareEncoder)
                InitializeHardwareEventPump();
            Debug.WriteLine("[ScreenShare:H264] Encoder started.");
        }

        private void ConfigureSystemMemoryInput()
        {
            _useRgb32Input = !_isHardwareAccelerated && TrySetInputType(VideoFormatGuids.Rgb32, "RGB32");
            if (_useRgb32Input)
            {
                _inputBufferLength = _width * _height * 4;
                Debug.WriteLine("[ScreenShare:H264] Using RGB32 input to avoid managed NV12 conversion.");
            }
            else
            {
                if (_isHardwareAccelerated)
                    Debug.WriteLine("[ScreenShare:H264] Using NV12 input for the GPU hardware encoder.");

                _nv12Buffer = new byte[_width * _height * 3 / 2];
                _inputBufferLength = _nv12Buffer.Length;
                if (!TrySetInputType(VideoFormatGuids.NV12, "NV12"))
                    throw new InvalidOperationException("The H.264 encoder did not accept RGB32 or NV12 input.");
            }
        }

        public string EncoderMode => _encoderMode;
        public string InputFormat
        {
            get
            {
                if (_useDxgiSurfaceInput)
                    return "D3D11 NV12 DXGI surface";
                return _useRgb32Input
                    ? "RGB32 direct"
                    : (_isHardwareAccelerated ? "NV12 hardware encoder input" : "NV12 managed fallback");
            }
        }
        public bool IsHardwareAccelerated => _isHardwareAccelerated;
        public bool DxgiDeviceManagerAttached => _dxgiDeviceManagerAttached;
        public string GpuDeviceManagerMode => _gpuDeviceManagerMode;
        public int RecoveryKeyFrameInterval => RecoveryKeyFrameIntervalFrames;
        public bool RealtimeModeEnabled { get; private set; }
        public bool LowLatencyOutputEnabled { get; private set; }
        public bool CanEncodeGpuTexture => !_gpuTextureInputDisabled && _useDxgiSurfaceInput && _encoderD3D11Device != null;

        public void ForceNextKeyFrame()
        {
            Interlocked.Exchange(ref _forceNextKeyFrame, 1);
        }

        public IReadOnlyList<H264EncodedFrame> Encode(Bitmap bitmap)
        {
            if (_useDxgiSurfaceInput)
                return EncodeDxgiSurface(bitmap);

            var frames = new List<H264EncodedFrame>();
            if (_hardwareEventGenerator != null)
            {
                frames.AddRange(DrainOutput());
                if (!TryConsumeHardwareInputRequest())
                {
                    var now = DateTimeOffset.UtcNow;
                    if (now - _lastHardwareInputWaitLogUtc >= TimeSpan.FromSeconds(2))
                    {
                        _loggedWaitingForHardwareInput = true;
                        _lastHardwareInputWaitLogUtc = now;
                        Debug.WriteLine("[ScreenShare:H264] Waiting for NVENC METransformNeedInput before submitting system-memory frames.");
                    }

                    return frames;
                }
            }

            using var inputBuffer = MediaFactory.CreateMemoryBuffer(_inputBufferLength);
            int maxLength;
            int currentLength;
            var inputPtr = inputBuffer.Lock(out maxLength, out currentLength);
            int bytesWritten;
            try
            {
                bytesWritten = FillInputBuffer(bitmap, inputPtr);
            }
            finally
            {
                inputBuffer.Unlock();
            }

            inputBuffer.CurrentLength = bytesWritten;

            using var sample = MediaFactory.CreateSample();
            sample.AddBuffer(inputBuffer);
            sample.SampleTime = _sampleTime;
            sample.SampleDuration = FrameDuration100Ns;

            RequestRecoveryKeyFrameIfNeeded();
            try
            {
                _encoder.ProcessInput(0, sample, 0);
            }
            catch
            {
                ReturnHardwareInputRequest();
                throw;
            }

            _sampleTime += FrameDuration100Ns;

            frames.AddRange(DrainOutput());
            _frameIndex++;
            return frames;
        }

        private unsafe IReadOnlyList<H264EncodedFrame> EncodeDxgiSurface(Bitmap bitmap)
        {
            if (_nv12Buffer == null)
                throw new InvalidOperationException("NV12 input buffer was not initialized.");
            if (_encoderD3D11Device == null)
                throw new InvalidOperationException("D3D11 input device was not initialized.");

            var frames = new List<H264EncodedFrame>();
            frames.AddRange(DrainOutput());

            ConvertBitmapToNv12(bitmap, _width, _height, _nv12Buffer);

            if (_hardwareEventGenerator != null && !TryConsumeHardwareInputRequest())
            {
                var now = DateTimeOffset.UtcNow;
                if (now - _lastHardwareInputWaitLogUtc >= TimeSpan.FromSeconds(2))
                {
                    _loggedWaitingForHardwareInput = true;
                    _lastHardwareInputWaitLogUtc = now;
                    Debug.WriteLine("[ScreenShare:H264] Waiting for NVENC METransformNeedInput before submitting DXGI frames.");
                }

                return frames;
            }

            var texture = TryGetNextAvailableDxgiInputTexture();
            if (texture == null)
            {
                ReturnHardwareInputRequest();

                if (!_loggedHardwareInputBackPressure)
                {
                    _loggedHardwareInputBackPressure = true;
                    Debug.WriteLine("[ScreenShare:H264] GPU input texture pool is full; waiting for NVENC output before submitting more frames.");
                }

                return frames;
            }

            fixed (byte* nv12 = _nv12Buffer)
            {
                var box = new DataBox((IntPtr)nv12, _width, _nv12Buffer.Length);
                _encoderD3D11Device.ImmediateContext.UpdateSubresource(box, texture, 0);
                _encoderD3D11Device.ImmediateContext.Flush();
            }

            SubmitDxgiTexture(texture, frames);
            return frames;
        }

        public IReadOnlyList<H264EncodedFrame> EncodeGpuBgraTexture(Texture2D sourceTexture, int sourceWidth, int sourceHeight)
        {
            if (_gpuTextureInputDisabled)
                throw new InvalidOperationException("GPU texture input has been disabled after a D3D video processor failure.");
            if (_encoderD3D11Device == null)
                throw new InvalidOperationException("D3D11 input device was not initialized.");
            if (!_useDxgiSurfaceInput)
                throw new InvalidOperationException("The encoder is not using D3D11 DXGI input.");

            var frames = new List<H264EncodedFrame>();
            frames.AddRange(DrainOutput());

            if (_hardwareEventGenerator != null && !TryConsumeHardwareInputRequest())
            {
                var now = DateTimeOffset.UtcNow;
                if (now - _lastHardwareInputWaitLogUtc >= TimeSpan.FromSeconds(2))
                {
                    _loggedWaitingForHardwareInput = true;
                    _lastHardwareInputWaitLogUtc = now;
                    Debug.WriteLine("[ScreenShare:H264] Waiting for NVENC METransformNeedInput before submitting GPU texture frames.");
                }

                return frames;
            }

            var texture = TryGetNextAvailableDxgiInputTexture();
            if (texture == null)
            {
                ReturnHardwareInputRequest();

                if (!_loggedHardwareInputBackPressure)
                {
                    _loggedHardwareInputBackPressure = true;
                    Debug.WriteLine("[ScreenShare:H264] GPU input texture pool is full; waiting for NVENC output before submitting more frames.");
                }

                return frames;
            }

            try
            {
                ConvertBgraTextureToNv12Texture(sourceTexture, sourceWidth, sourceHeight, texture);
            }
            catch (SharpDXException ex) when (ex.ResultCode.Code == unchecked((int)0x80070057))
            {
                ReturnHardwareInputRequest();
                _gpuTextureInputDisabled = true;
                Debug.WriteLine($"[ScreenShare:H264] GPU texture video processor input rejected ({ex.ResultCode}); disabling GPU texture path for this encoder and falling back to bitmap NVENC input.");
                return frames;
            }
            catch
            {
                ReturnHardwareInputRequest();
                throw;
            }

            SubmitDxgiTexture(texture, frames);
            return frames;
        }

        private void SubmitDxgiTexture(Texture2D texture, List<H264EncodedFrame> frames)
        {
            MediaBuffer? inputBuffer = null;
            Sample? sample = null;

            MediaFactory.CreateDXGISurfaceBuffer(
                typeof(Texture2D).GUID,
                texture,
                0,
                new RawBool(false),
                out inputBuffer);

            try
            {
                sample = MediaFactory.CreateSample();
                inputBuffer.CurrentLength = _inputBufferLength;
                sample.AddBuffer(inputBuffer);
                sample.SampleTime = _sampleTime;
                sample.SampleDuration = FrameDuration100Ns;

                RequestRecoveryKeyFrameIfNeeded();
                try
                {
                    _encoder.ProcessInput(0, sample, 0);
                }
                catch
                {
                    ReturnHardwareInputRequest();
                    throw;
                }

                _pendingHardwareInputs.Enqueue(new PendingHardwareInputSample(sample, inputBuffer, texture));
                sample = null;
                inputBuffer = null;
                _loggedWaitingForHardwareInput = false;
            }
            finally
            {
                sample?.Dispose();
                inputBuffer?.Dispose();
            }

            _sampleTime += FrameDuration100Ns;
            frames.AddRange(DrainOutput());
            _frameIndex++;
        }

        private int FillInputBuffer(Bitmap bitmap, IntPtr inputPtr)
        {
            if (_useRgb32Input)
            {
                CopyBitmapToRgb32(bitmap, _width, _height, inputPtr);
                return _inputBufferLength;
            }

            if (_nv12Buffer == null)
                throw new InvalidOperationException("NV12 input buffer was not initialized.");

            ConvertBitmapToNv12(bitmap, _width, _height, _nv12Buffer);
            Marshal.Copy(_nv12Buffer, 0, inputPtr, _nv12Buffer.Length);
            return _nv12Buffer.Length;
        }

        private IReadOnlyList<H264EncodedFrame> DrainOutput()
        {
            var frames = new List<H264EncodedFrame>();
            _encoder.GetOutputStreamInfo(0, out var info);
            int outputBufferSize = Math.Max(info.CbSize, _width * _height);
            var useEncoderAllocatedOutput =
                (info.DwFlags & MftOutputStreamProvidesSamples) != 0 ||
                (info.DwFlags & MftOutputStreamCanProvideSamples) != 0;

            if (!_loggedOutputStreamMode)
            {
                _loggedOutputStreamMode = true;
                Debug.WriteLine($"[ScreenShare:H264] Output stream flags=0x{info.DwFlags:X}; encoder-allocated output={useEncoderAllocatedOutput}.");
            }

            if (_isHardwareAccelerated && _hardwareEventGenerator != null)
                return DrainHardwareAsyncOutput(outputBufferSize, useEncoderAllocatedOutput);

            while (true)
            {
                Sample? callerOutputSample = null;
                MediaBuffer? callerOutputBuffer = null;
                TOutputDataBuffer[]? output = null;

                try
                {
                    if (!useEncoderAllocatedOutput)
                    {
                        callerOutputSample = MediaFactory.CreateSample();
                        callerOutputBuffer = MediaFactory.CreateMemoryBuffer(outputBufferSize);
                        callerOutputSample.AddBuffer(callerOutputBuffer);
                    }

                    output = new[]
                    {
                        new TOutputDataBuffer
                        {
                            DwStreamID = 0,
                            PSample = callerOutputSample
                        }
                    };

                    try
                    {
                        _encoder.ProcessOutput(
                            TransformProcessOutputFlags.None,
                            output,
                            out _);
                    }
                    catch (SharpDXException ex) when (ex.ResultCode.Code == MfENeedMoreInput)
                    {
                        break;
                    }
                    catch (SharpDXException ex) when (ex.ResultCode.Code == MfEStreamChange)
                    {
                        TryHandleOutputStreamChange("synchronous ProcessOutput");
                        break;
                    }

                    var sampleToRead = output[0].PSample ?? callerOutputSample;
                    if (sampleToRead == null ||
                        !TryReadEncodedSampleBytes(sampleToRead, out var data, out _))
                        break;

                    AddEncodedFrame(frames, data);
                }
                finally
                {
                    if (output != null)
                    {
                        try
                        {
                            output[0].PEvents?.Dispose();
                        }
                        catch
                        {
                        }

                        if (useEncoderAllocatedOutput)
                        {
                            try
                            {
                                output[0].PSample?.Dispose();
                            }
                            catch
                            {
                            }
                        }
                    }

                    callerOutputBuffer?.Dispose();
                    callerOutputSample?.Dispose();
                }
            }

            return frames;
        }

        private IReadOnlyList<H264EncodedFrame> DrainHardwareAsyncOutput(
            int outputBufferSize,
            bool useEncoderAllocatedOutput)
        {
            var frames = new List<H264EncodedFrame>();
            if (_hardwareEventGenerator == null)
                return frames;

            while (TryConsumeHardwareOutputRequest())
            {
                frames.AddRange(ProcessSingleOutput(outputBufferSize, useEncoderAllocatedOutput));
                _loggedHardwareInputBackPressure = false;
            }

            return frames;
        }

        private IReadOnlyList<H264EncodedFrame> ProcessSingleOutput(
            int outputBufferSize,
            bool useEncoderAllocatedOutput)
        {
            var frames = new List<H264EncodedFrame>();
            Sample? callerOutputSample = null;
            MediaBuffer? callerOutputBuffer = null;
            TransformProcessOutputStatus processStatus = 0;
            int outputBufferStatus = 0;
            bool processReturnedSample = false;
            NativeProcessOutputResult? nativeOutput = null;

            try
            {
                if (!useEncoderAllocatedOutput)
                {
                    callerOutputSample = MediaFactory.CreateSample();
                    callerOutputBuffer = MediaFactory.CreateMemoryBuffer(outputBufferSize);
                    callerOutputSample.AddBuffer(callerOutputBuffer);
                }

                try
                {
                    nativeOutput = ProcessOutputNative(
                        callerOutputSample,
                        useEncoderAllocatedOutput);
                    processStatus = nativeOutput.ProcessStatus;
                    outputBufferStatus = nativeOutput.OutputBufferStatus;
                    processReturnedSample = nativeOutput.Sample != null || callerOutputSample != null;
                }
                catch (SharpDXException ex) when (ex.ResultCode.Code == MfENeedMoreInput)
                {
                    return frames;
                }
                catch (SharpDXException ex) when (ex.ResultCode.Code == MfEStreamChange)
                {
                    TryHandleOutputStreamChange("hardware async ProcessOutput");
                    return frames;
                }

                var sampleToRead = nativeOutput.Sample ?? callerOutputSample;
                var data = Array.Empty<byte>();
                var diagnostics = "sample missing";
                var hasFrame = sampleToRead != null &&
                    TryReadEncodedSampleBytes(sampleToRead, out data, out diagnostics);
                if (hasFrame)
                {
                    AddEncodedFrame(frames, data);
                    ReleaseCompletedHardwareInput();
                }
                else
                {
                    if (!_loggedUnreadableHardwareOutput)
                    {
                        _loggedUnreadableHardwareOutput = true;
                        var sampleDescription = sampleToRead == null
                            ? "sample=null"
                            : DescribeSample(sampleToRead);
                        Debug.WriteLine($"[ScreenShare:H264] NVENC signaled output but no encoded bytes were readable; returnedSample={processReturnedSample}; processStatus={processStatus}; bufferStatus=0x{outputBufferStatus:X}; {sampleDescription}; {diagnostics}");
                    }

                    ReleaseCompletedHardwareInput();
                }
            }
            finally
            {
                nativeOutput?.Dispose();
                callerOutputBuffer?.Dispose();
                callerOutputSample?.Dispose();
            }

            return frames;
        }

        private NativeProcessOutputResult ProcessOutputNative(
            Sample? callerOutputSample,
            bool useEncoderAllocatedOutput)
        {
            var nativeBuffer = new NativeMftOutputDataBuffer
            {
                DwStreamID = 0,
                PSample = callerOutputSample?.NativePointer ?? IntPtr.Zero,
                DwStatus = 0,
                PEvents = IntPtr.Zero
            };
            var nativeBufferPtr = Marshal.AllocHGlobal(Marshal.SizeOf<NativeMftOutputDataBuffer>());

            try
            {
                Marshal.StructureToPtr(nativeBuffer, nativeBufferPtr, false);
                var hr = GetNativeProcessOutput()(
                    _encoder.NativePointer,
                    (int)TransformProcessOutputFlags.None,
                    1,
                    nativeBufferPtr,
                    out var processStatus);
                new Result(hr).CheckError();

                nativeBuffer = Marshal.PtrToStructure<NativeMftOutputDataBuffer>(nativeBufferPtr);
                Sample? outputSample = null;
                Collection? outputEvents = null;

                if (useEncoderAllocatedOutput && nativeBuffer.PSample != IntPtr.Zero)
                    outputSample = new Sample(nativeBuffer.PSample);
                if (nativeBuffer.PEvents != IntPtr.Zero)
                    outputEvents = new Collection(nativeBuffer.PEvents);

                return new NativeProcessOutputResult(
                    outputSample,
                    outputEvents,
                    nativeBuffer.DwStatus,
                    (TransformProcessOutputStatus)processStatus);
            }
            finally
            {
                Marshal.FreeHGlobal(nativeBufferPtr);
            }
        }

        private ProcessOutputNativeDelegate GetNativeProcessOutput()
        {
            if (_processOutputNative != null)
                return _processOutputNative;

            var vtable = Marshal.ReadIntPtr(_encoder.NativePointer);
            var processOutputPtr = Marshal.ReadIntPtr(
                vtable,
                IntPtr.Size * ImfTransformProcessOutputVtableSlot);
            _processOutputNative = Marshal.GetDelegateForFunctionPointer<ProcessOutputNativeDelegate>(processOutputPtr);
            return _processOutputNative;
        }

        private bool TryHandleOutputStreamChange(string reason)
        {
            var now = DateTimeOffset.UtcNow;
            if (now - _lastOutputStreamChangeLogUtc >= TimeSpan.FromSeconds(1))
            {
                _lastOutputStreamChangeLogUtc = now;
                Debug.WriteLine($"[ScreenShare:H264] Encoder requested output stream renegotiation after {reason}; refreshing H.264 output type.");
            }

            _loggedOutputStreamMode = false;
            _loggedUnreadableHardwareOutput = false;

            if (TrySetExplicitH264OutputType($"{reason} stream change"))
                return true;

            if (TrySelectAvailableH264OutputType($"{reason} stream change"))
                return true;

            Debug.WriteLine($"[ScreenShare:H264] Encoder output stream renegotiation failed after {reason}; no compatible H.264 output type was accepted.");
            return false;
        }

        private bool TrySetExplicitH264OutputType(string reason)
        {
            try
            {
                using var outputType = CreateH264OutputType(_bitrate);
                LowLatencyOutputEnabled = TrySetLowLatencyOutputTypeAttributes(outputType);
                _encoder.SetOutputType(0, outputType, 0);
                Debug.WriteLine($"[ScreenShare:H264] Encoder output type refreshed after {reason}: H.264 {_width}x{_height} @ {_bitrate}bps.");
                return true;
            }
            catch (SharpDXException ex)
            {
                Debug.WriteLine($"[ScreenShare:H264] Encoder rejected explicit output type after {reason}: 0x{ex.ResultCode.Code:X8} {ex.Message}");
                return false;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ScreenShare:H264] Encoder explicit output type refresh failed after {reason}: {ex.Message}");
                return false;
            }
        }

        private bool TrySelectAvailableH264OutputType(string reason)
        {
            for (var index = 0; index < 32; index++)
            {
                MediaType? candidate = null;
                try
                {
                    if (!_encoder.TryGetOutputAvailableType(0, index, out candidate) ||
                        candidate == null)
                        continue;

                    var subtype = candidate.Get(MediaTypeAttributeKeys.Subtype);
                    if (subtype != VideoFormatGuids.H264)
                        continue;

                    candidate.Set(MediaTypeAttributeKeys.AvgBitrate, _bitrate);
                    candidate.Set(MediaTypeAttributeKeys.FrameSize, PackRatio(_width, _height));
                    candidate.Set(MediaTypeAttributeKeys.FrameRate, PackRatio(NativeScreenShareStreamingService.TargetFps, 1));
                    candidate.Set(MediaTypeAttributeKeys.PixelAspectRatio, PackRatio(1, 1));
                    candidate.Set(MediaTypeAttributeKeys.InterlaceMode, (int)VideoInterlaceMode.Progressive);
                    candidate.Set(MediaTypeAttributeKeys.MaxKeyframeSpacing, RecoveryKeyFrameIntervalFrames);
                    LowLatencyOutputEnabled = TrySetLowLatencyOutputTypeAttributes(candidate);
                    _encoder.SetOutputType(0, candidate, 0);
                    Debug.WriteLine($"[ScreenShare:H264] Encoder selected available output type {index} after {reason}: H.264 {_width}x{_height} @ {_bitrate}bps.");
                    return true;
                }
                catch (SharpDXException ex)
                {
                    if (index == 0 || index == 31)
                        Debug.WriteLine($"[ScreenShare:H264] Encoder output candidate {index} rejected after {reason}: 0x{ex.ResultCode.Code:X8} {ex.Message}");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[ScreenShare:H264] Encoder output type candidate refresh failed after {reason}: {ex.Message}");
                    return false;
                }
                finally
                {
                    candidate?.Dispose();
                }
            }

            Debug.WriteLine($"[ScreenShare:H264] Encoder did not expose a usable H.264 output type after {reason}.");
            return false;
        }

        private void AddEncodedFrame(List<H264EncodedFrame> frames, byte[] data)
        {
            var annexBData = NormalizeH264AccessUnitToAnnexB(data, out var convertedToAnnexB);
            var hasIdr = ContainsNalUnitType(annexBData, 5);
            var hasSps = ContainsNalUnitType(annexBData, 7);
            var hasPps = ContainsNalUnitType(annexBData, 8);

            if ((hasSps || hasPps) && TryExtractParameterSets(annexBData, out var parameterSets))
                _cachedParameterSetsAnnexB = parameterSets;

            if (hasIdr &&
                _cachedParameterSetsAnnexB != null &&
                (!hasSps || !hasPps))
            {
                annexBData = CombineParameterSetsWithFrame(_cachedParameterSetsAnnexB, annexBData);
                hasSps = true;
                hasPps = true;
            }

            if (!_loggedFirstOutputFrame)
            {
                _loggedFirstOutputFrame = true;
                Debug.WriteLine(
                    $"[ScreenShare:H264] First encoded frame: raw={data.Length} bytes, send={annexBData.Length} bytes, framing={(convertedToAnnexB ? "length-prefixed->AnnexB" : "AnnexB")}, sps={hasSps}, pps={hasPps}, idr={hasIdr}.");
            }

            if (!hasIdr && _frameIndex > 0 && _frameIndex % RecoveryKeyFrameIntervalFrames == 0)
            {
                Debug.WriteLine(
                    $"[ScreenShare:H264] Recovery keyframe interval reached at encoder frame {_frameIndex}, but output had no IDR; treating it as delta.");
            }

            frames.Add(new H264EncodedFrame(annexBData, hasIdr));
        }

        private static byte[] NormalizeH264AccessUnitToAnnexB(byte[] frame, out bool converted)
        {
            converted = false;

            if (frame.Length < 5 || HasStartCode(frame))
                return frame;

            if (!TryConvertLengthPrefixedNalUnits(frame, out var annexB))
                return frame;

            converted = true;
            return annexB;
        }

        private static bool HasStartCode(byte[] frame)
        {
            return frame.Length >= 4 &&
                frame[0] == 0 &&
                frame[1] == 0 &&
                (frame[2] == 1 || (frame[2] == 0 && frame[3] == 1));
        }

        private static bool TryConvertLengthPrefixedNalUnits(byte[] frame, out byte[] annexB)
        {
            var output = new List<byte>(frame.Length + 16);
            var offset = 0;

            while (offset + 4 <= frame.Length)
            {
                var nalLength =
                    (frame[offset] << 24) |
                    (frame[offset + 1] << 16) |
                    (frame[offset + 2] << 8) |
                    frame[offset + 3];
                offset += 4;

                if (nalLength <= 0 || nalLength > frame.Length - offset)
                {
                    annexB = frame;
                    return false;
                }

                AppendStartCode(output);
                for (var i = 0; i < nalLength; i++)
                    output.Add(frame[offset + i]);

                offset += nalLength;
            }

            if (offset != frame.Length || output.Count == 0)
            {
                annexB = frame;
                return false;
            }

            annexB = output.ToArray();
            return true;
        }

        private static bool TryExtractParameterSets(byte[] annexBFrame, out byte[] parameterSets)
        {
            var output = new List<byte>();
            var found = false;

            foreach (var nal in EnumerateAnnexBNalUnits(annexBFrame))
            {
                if (nal.Type != 7 && nal.Type != 8)
                    continue;

                AppendStartCode(output);
                for (var i = nal.Offset; i < nal.Offset + nal.Length; i++)
                    output.Add(annexBFrame[i]);
                found = true;
            }

            parameterSets = output.ToArray();
            return found;
        }

        private static byte[] CombineParameterSetsWithFrame(byte[] parameterSets, byte[] frame)
        {
            var combined = new byte[parameterSets.Length + frame.Length];
            System.Buffer.BlockCopy(parameterSets, 0, combined, 0, parameterSets.Length);
            System.Buffer.BlockCopy(frame, 0, combined, parameterSets.Length, frame.Length);
            return combined;
        }

        private static void AppendStartCode(List<byte> output)
        {
            output.Add(0);
            output.Add(0);
            output.Add(0);
            output.Add(1);
        }

        private static bool TryReadEncodedSampleBytes(
            Sample sample,
            out byte[] data,
            out string diagnostics)
        {
            data = Array.Empty<byte>();
            diagnostics = string.Empty;

            try
            {
                var totalLength = sample.TotalLength;
                if (totalLength > 0)
                {
                    using var copyBuffer = MediaFactory.CreateMemoryBuffer(totalLength);
                    sample.CopyToBuffer(copyBuffer);
                    data = ReadMediaBufferBytes(copyBuffer, totalLength);
                    if (data.Length > 0)
                        return true;
                }
            }
            catch (Exception ex)
            {
                diagnostics = $"copy failed: {ex.Message}";
            }

            try
            {
                using var contiguousBuffer = sample.ConvertToContiguousBuffer();
                data = ReadMediaBufferBytes(contiguousBuffer, sample.TotalLength);
                if (data.Length > 0)
                    return true;
            }
            catch (Exception ex)
            {
                diagnostics = string.IsNullOrEmpty(diagnostics)
                    ? $"contiguous failed: {ex.Message}"
                    : $"{diagnostics}; contiguous failed: {ex.Message}";
            }

            try
            {
                var totalBytes = 0;
                var chunks = new List<byte[]>();
                for (var index = 0; index < sample.BufferCount; index++)
                {
                    using var buffer = sample.GetBufferByIndex(index);
                    var chunk = ReadMediaBufferBytes(buffer);
                    if (chunk.Length == 0)
                        continue;

                    chunks.Add(chunk);
                    totalBytes += chunk.Length;
                }

                if (totalBytes > 0)
                {
                    data = new byte[totalBytes];
                    var offset = 0;
                    foreach (var chunk in chunks)
                    {
                        System.Buffer.BlockCopy(chunk, 0, data, offset, chunk.Length);
                        offset += chunk.Length;
                    }

                    return true;
                }
            }
            catch (Exception ex)
            {
                diagnostics = string.IsNullOrEmpty(diagnostics)
                    ? $"buffers failed: {ex.Message}"
                    : $"{diagnostics}; buffers failed: {ex.Message}";
            }

            if (string.IsNullOrEmpty(diagnostics))
                diagnostics = "no readable buffers";
            return false;
        }

        private static byte[] ReadMediaBufferBytes(MediaBuffer buffer, int preferredLength = 0)
        {
            int maxLength;
            int currentLength;
            var ptr = buffer.Lock(out maxLength, out currentLength);
            try
            {
                var bytesToCopy = currentLength > 0 ? currentLength : buffer.CurrentLength;
                if (bytesToCopy <= 0 && preferredLength > 0)
                    bytesToCopy = Math.Min(preferredLength, maxLength);
                if (bytesToCopy <= 0)
                    return Array.Empty<byte>();

                var data = new byte[bytesToCopy];
                Marshal.Copy(ptr, data, 0, data.Length);
                return data;
            }
            finally
            {
                buffer.Unlock();
            }
        }

        private static string DescribeSample(Sample sample)
        {
            try
            {
                var parts = new List<string>
                {
                    $"sampleBuffers={sample.BufferCount}",
                    $"sampleTotal={sample.TotalLength}"
                };

                var bufferCount = Math.Min(sample.BufferCount, 4);
                for (var index = 0; index < bufferCount; index++)
                {
                    using var buffer = sample.GetBufferByIndex(index);
                    parts.Add($"b{index}=current:{buffer.CurrentLength}/max:{buffer.MaxLength}");
                }

                return string.Join("; ", parts);
            }
            catch (Exception ex)
            {
                return $"sample diagnostic failed: {ex.Message}";
            }
        }

        private static bool ContainsIdrNalUnit(byte[] frame)
        {
            if (HasStartCode(frame))
                return ContainsNalUnitType(frame, 5);

            for (var i = 0; i + 4 < frame.Length; i++)
            {
                var startCodeLength = 0;
                if (frame[i] == 0 && frame[i + 1] == 0 && frame[i + 2] == 1)
                    startCodeLength = 3;
                else if (i + 5 < frame.Length &&
                         frame[i] == 0 &&
                         frame[i + 1] == 0 &&
                         frame[i + 2] == 0 &&
                         frame[i + 3] == 1)
                    startCodeLength = 4;

                if (startCodeLength == 0)
                    continue;

                var nalIndex = i + startCodeLength;
                if (nalIndex < frame.Length && (frame[nalIndex] & 0x1F) == 5)
                    return true;
            }

            var offset = 0;
            while (offset + 5 <= frame.Length)
            {
                var nalLength =
                    (frame[offset] << 24) |
                    (frame[offset + 1] << 16) |
                    (frame[offset + 2] << 8) |
                    frame[offset + 3];
                offset += 4;

                if (nalLength <= 0 || nalLength > frame.Length - offset)
                    return false;

                if ((frame[offset] & 0x1F) == 5)
                    return true;

                offset += nalLength;
            }

            return false;
        }

        private static bool ContainsNalUnitType(byte[] annexBFrame, byte nalType)
        {
            foreach (var nal in EnumerateAnnexBNalUnits(annexBFrame))
            {
                if (nal.Type == nalType)
                    return true;
            }

            return false;
        }

        private static IEnumerable<AnnexBNalUnit> EnumerateAnnexBNalUnits(byte[] frame)
        {
            var index = 0;
            while (TryFindStartCode(frame, index, out var startCodeIndex, out var startCodeLength))
            {
                var nalOffset = startCodeIndex + startCodeLength;
                var nextStart = frame.Length;
                if (TryFindStartCode(frame, nalOffset, out var nextStartCodeIndex, out _))
                    nextStart = nextStartCodeIndex;

                var nalLength = nextStart - nalOffset;
                if (nalLength > 0 && nalOffset < frame.Length)
                    yield return new AnnexBNalUnit(nalOffset, nalLength, (byte)(frame[nalOffset] & 0x1F));

                index = nextStart;
            }
        }

        private static bool TryFindStartCode(byte[] frame, int startIndex, out int index, out int length)
        {
            for (var i = Math.Max(0, startIndex); i + 3 < frame.Length; i++)
            {
                if (frame[i] == 0 && frame[i + 1] == 0 && frame[i + 2] == 1)
                {
                    index = i;
                    length = 3;
                    return true;
                }

                if (i + 4 < frame.Length &&
                    frame[i] == 0 &&
                    frame[i + 1] == 0 &&
                    frame[i + 2] == 0 &&
                    frame[i + 3] == 1)
                {
                    index = i;
                    length = 4;
                    return true;
                }
            }

            index = -1;
            length = 0;
            return false;
        }

        private static long PackRatio(int high, int low)
        {
            return ((long)high << 32) | (uint)low;
        }

        private static EncoderTransformSelection CreateEncoderTransform(bool preferHardware, bool requireHardware)
        {
            if (preferHardware &&
                TryCreateHardwareEncoderTransform(out var hardwareEncoder, out var hardwareMode))
            {
                return new EncoderTransformSelection(hardwareEncoder, hardwareMode, true);
            }

            if (requireHardware)
                throw new InvalidOperationException("No GPU H.264 hardware encoder MFT was found on this Windows install.");

            Debug.WriteLine("[ScreenShare:H264] Using stable Microsoft H.264 encoder MFT.");
            return new EncoderTransformSelection(
                new Transform(CmsH264EncoderMft),
                "Microsoft H.264 software MFT (safe low-latency)",
                false);
        }

        private static bool TryCreateHardwareEncoderTransform(out Transform encoder, out string mode)
        {
            encoder = null!;
            mode = "";

            var outputInfo = new TRegisterTypeInformation
            {
                GuidMajorType = MediaTypeGuids.Video,
                GuidSubtype = VideoFormatGuids.H264
            };
            var nv12InputInfo = new TRegisterTypeInformation
            {
                GuidMajorType = MediaTypeGuids.Video,
                GuidSubtype = VideoFormatGuids.NV12
            };

            var inputs = new TRegisterTypeInformation?[] { nv12InputInfo, null };
            foreach (var inputInfo in inputs)
            {
                IReadOnlyList<Activate> activations;
                try
                {
                    activations = EnumerateHardwareEncoderActivations(inputInfo, outputInfo);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[ScreenShare:H264] Hardware encoder enumeration failed: {ex.Message}");
                    return false;
                }

                foreach (var activation in activations)
                {
                    try
                    {
                        var friendlyName = GetActivationString(
                            activation,
                            TransformAttributeKeys.MftFriendlyNameAttribute,
                            "hardware H.264 encoder");
                        var vendorId = GetActivationString(
                            activation,
                            TransformAttributeKeys.MftEnumHardwareVendorIdAttribute,
                            "");

                        encoder = activation.ActivateObject<Transform>();
                        mode = $"GPU H.264 hardware MFT ({GetHardwareEncoderFamily(friendlyName, vendorId)}): {friendlyName}";
                        Debug.WriteLine($"[ScreenShare:H264] Using {mode}.");
                        return true;
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[ScreenShare:H264] Hardware encoder activation skipped: {ex.Message}");
                    }
                    finally
                    {
                        activation.Dispose();
                    }
                }
            }

            return false;
        }

        private void TryAttachDxgiDeviceManager(bool forceNativeDeviceManager)
        {
            if (!forceNativeDeviceManager)
            {
                try
                {
                    DisposeGpuEncodingResources();

                    _directX12VideoDeviceManager = DirectX12VideoDeviceManager.Create();
                    AttachDxgiDeviceManager(_directX12VideoDeviceManager.D3D11On12Device);
                    _encoderD3D11Device = _directX12VideoDeviceManager.D3D11On12Device;
                    _gpuDeviceManagerMode = _directX12VideoDeviceManager.Description;
                    Debug.WriteLine("[ScreenShare:H264] DirectX 12 / D3D11On12 video device manager attached to hardware encoder.");
                    return;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[ScreenShare:H264] DirectX 12 / D3D11On12 manager was rejected by NVENC; keeping DX12 capture and retrying with Media Foundation native DXGI manager: {ex.Message}");
                    DisposeGpuEncodingResources();
                }
            }
            else
            {
                Debug.WriteLine("[ScreenShare:H264] Skipping D3D11On12 for NVENC retry; using a native D3D11 DXGI manager.");
            }

            try
            {
                _nativeMediaFoundationDevice = _preferredMediaFoundationDevice ?? CreateNativeMediaFoundationDeviceForEncoder();

                EnableMultithreadProtection(_nativeMediaFoundationDevice);
                AttachDxgiDeviceManager(_nativeMediaFoundationDevice);
                _encoderD3D11Device = _nativeMediaFoundationDevice;
                _gpuDeviceManagerMode = _preferredMediaFoundationDevice != null
                    ? "WGC shared native D3D11 device + NVENC Media Foundation DXGI manager"
                    : "DirectX 12 WGC capture + NVENC native Media Foundation DXGI manager";
                Debug.WriteLine(_preferredMediaFoundationDevice != null
                    ? "[ScreenShare:H264] WGC shared native D3D11 device attached to NVENC for GPU texture input."
                    : "[ScreenShare:H264] Native Media Foundation DXGI manager attached to NVENC after D3D11On12 rejection.");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ScreenShare:H264] NVENC native Media Foundation DXGI manager failed: {ex}");
                DisposeGpuEncodingResources();
                _gpuDeviceManagerMode = "NVENC DXGI manager failed";
                throw new InvalidOperationException(
                    "NVENC rejected both the D3D11On12 media manager and the native Media Foundation DXGI manager.",
                    ex);
            }
        }

        private void AttachDxgiDeviceManager(ComObject device)
        {
            _dxgiDeviceManager = new DXGIDeviceManager();
            _dxgiDeviceManager.ResetDevice(device);
            _encoder.ProcessMessage(TMessageType.SetD3DManager, _dxgiDeviceManager.NativePointer);
            _dxgiDeviceManagerAttached = true;
        }

        private SharpDX.Direct3D11.Device CreateNativeMediaFoundationDeviceForEncoder()
        {
            var requestedVendorId = GetPreferredAdapterVendorId(_encoderMode);
            var flags = DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport;

            using var factory = new Factory1();
            Adapter1? fallbackAdapter = null;
            Adapter1? selectedAdapter = null;

            try
            {
                for (var index = 0; ; index++)
                {
                    Adapter1 adapter;
                    try
                    {
                        adapter = factory.GetAdapter1(index);
                    }
                    catch
                    {
                        break;
                    }

                    var description = adapter.Description1;
                    var isSoftware = (description.Flags & AdapterFlags.Software) == AdapterFlags.Software;
                    if (isSoftware)
                    {
                        adapter.Dispose();
                        continue;
                    }

                    if (fallbackAdapter == null)
                    {
                        fallbackAdapter = adapter;
                    }
                    else if (description.VendorId != requestedVendorId)
                    {
                        adapter.Dispose();
                    }

                    if (requestedVendorId != 0 && description.VendorId == requestedVendorId)
                    {
                        selectedAdapter = adapter;
                        break;
                    }
                }

                selectedAdapter ??= fallbackAdapter;
                if (selectedAdapter == null)
                    throw new InvalidOperationException("No hardware DXGI adapter was found for Media Foundation GPU encoding.");

                var selectedDescription = selectedAdapter.Description1;
                Debug.WriteLine($"[ScreenShare:H264] Creating native D3D11 Media Foundation device on adapter '{selectedDescription.Description}' vendor=0x{selectedDescription.VendorId:X4} for {_encoderMode}.");
                return new SharpDX.Direct3D11.Device(selectedAdapter, flags);
            }
            finally
            {
                if (fallbackAdapter != null && !ReferenceEquals(fallbackAdapter, selectedAdapter))
                    fallbackAdapter.Dispose();

                selectedAdapter?.Dispose();
            }
        }

        private static int GetPreferredAdapterVendorId(string encoderMode)
        {
            if (encoderMode.Contains("NVENC", StringComparison.OrdinalIgnoreCase) ||
                encoderMode.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase))
                return 0x10DE;

            if (encoderMode.Contains("Intel", StringComparison.OrdinalIgnoreCase) ||
                encoderMode.Contains("Quick Sync", StringComparison.OrdinalIgnoreCase))
                return 0x8086;

            if (encoderMode.Contains("AMD", StringComparison.OrdinalIgnoreCase) ||
                encoderMode.Contains("AMF", StringComparison.OrdinalIgnoreCase))
                return 0x1002;

            return 0;
        }

        private void DisposeGpuEncodingResources()
        {
            _dxgiDeviceManagerAttached = false;
            _gpuDeviceManagerMode = "Not attached";
            _encoderD3D11Device = null;
            _useDxgiSurfaceInput = false;
            _gpuTextureInputDisabled = false;
            _loggedHardwareInputBackPressure = false;

            DisposePendingHardwareInputs();
            DisposeHardwareEventGenerator();

            if (_dxgiInputTextures != null)
            {
                foreach (var texture in _dxgiInputTextures)
                {
                    try
                    {
                        texture.Dispose();
                    }
                    catch
                    {
                    }
                }
            }

            try
            {
                _dxgiDeviceManager?.Dispose();
            }
            catch
            {
            }

            try
            {
                _directX12VideoDeviceManager?.Dispose();
            }
            catch
            {
            }

            try
            {
                if (!ReferenceEquals(_nativeMediaFoundationDevice, _preferredMediaFoundationDevice))
                    _nativeMediaFoundationDevice?.Dispose();
            }
            catch
            {
            }

            _dxgiDeviceManager = null;
            _directX12VideoDeviceManager = null;
            _nativeMediaFoundationDevice = null;
            _dxgiInputTextures = null;
            _dxgiInputTextureIndex = 0;
        }

        private static IReadOnlyList<Activate> EnumerateHardwareEncoderActivations(
            TRegisterTypeInformation? inputInfo,
            TRegisterTypeInformation outputInfo)
        {
            var activations = new List<Activate>();
            var category = TransformCategoryGuids.VideoEncoder;
            var activationArrayPtr = IntPtr.Zero;
            var inputInfoPtr = AllocateNativeTypeInfo(inputInfo);
            var outputInfoPtr = AllocateNativeTypeInfo(outputInfo);

            try
            {
                var hr = MFTEnumEx(
                    ref category,
                    (int)(TransformEnumFlag.Hardware | TransformEnumFlag.Asyncmft | TransformEnumFlag.SortAndFilter),
                    inputInfoPtr,
                    outputInfoPtr,
                    out activationArrayPtr,
                    out var activationCount);

                if (hr < 0)
                    Marshal.ThrowExceptionForHR(hr);

                for (var i = 0; i < activationCount; i++)
                {
                    var activationPtr = Marshal.ReadIntPtr(activationArrayPtr, i * IntPtr.Size);
                    if (activationPtr != IntPtr.Zero)
                        activations.Add(new Activate(activationPtr));
                }
            }
            finally
            {
                if (activationArrayPtr != IntPtr.Zero)
                    CoTaskMemFree(activationArrayPtr);
                if (inputInfoPtr != IntPtr.Zero)
                    Marshal.FreeHGlobal(inputInfoPtr);
                if (outputInfoPtr != IntPtr.Zero)
                    Marshal.FreeHGlobal(outputInfoPtr);
            }

            return activations;
        }

        private static IntPtr AllocateNativeTypeInfo(TRegisterTypeInformation? typeInfo)
        {
            if (!typeInfo.HasValue)
                return IntPtr.Zero;

            return AllocateNativeTypeInfo(typeInfo.Value);
        }

        private static IntPtr AllocateNativeTypeInfo(TRegisterTypeInformation typeInfo)
        {
            var native = new NativeMftRegisterTypeInfo
            {
                GuidMajorType = typeInfo.GuidMajorType,
                GuidSubtype = typeInfo.GuidSubtype
            };
            var ptr = Marshal.AllocHGlobal(Marshal.SizeOf<NativeMftRegisterTypeInfo>());
            Marshal.StructureToPtr(native, ptr, false);
            return ptr;
        }

        private static string GetActivationString(Activate activation, MediaAttributeKey<string> key, string fallback)
        {
            try
            {
                var value = activation.Get<string>(key);
                return string.IsNullOrWhiteSpace(value) ? fallback : value;
            }
            catch
            {
                return fallback;
            }
        }

        private static string GetHardwareEncoderFamily(string friendlyName, string vendorId)
        {
            var text = $"{friendlyName} {vendorId}".ToUpperInvariant();
            if (text.Contains("NVIDIA") || text.Contains("NVENC") || text.Contains("10DE"))
                return "NVENC";
            if (text.Contains("AMD") || text.Contains("ADVANCED MICRO DEVICES") || text.Contains("AMF") || text.Contains("1002") || text.Contains("1022"))
                return "AMD AMF";
            if (text.Contains("INTEL") || text.Contains("QUICK SYNC") || text.Contains("8086"))
                return "Intel Quick Sync";

            return "GPU";
        }

        [DllImport("mfplat.dll", ExactSpelling = true)]
        private static extern int MFTEnumEx(
            ref Guid guidCategory,
            int flags,
            IntPtr inputTypeRef,
            IntPtr outputTypeRef,
            out IntPtr activateArrayOut,
            out int activateCountRef);

        [DllImport("mfplat.dll", ExactSpelling = true)]
        private static extern int MFCreateDXGIDeviceManager(
            out int resetToken,
            out IntPtr deviceManager);

        [DllImport("ole32.dll", ExactSpelling = true)]
        private static extern void CoTaskMemFree(IntPtr ptr);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int ProcessOutputNativeDelegate(
            IntPtr transform,
            int flags,
            int outputBufferCount,
            IntPtr outputSamples,
            out int status);

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeMftOutputDataBuffer
        {
            public int DwStreamID;
            public IntPtr PSample;
            public int DwStatus;
            public IntPtr PEvents;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeMftRegisterTypeInfo
        {
            public Guid GuidMajorType;
            public Guid GuidSubtype;
        }

        private bool TrySetInputType(Guid subtype, string name)
        {
            try
            {
                SetInputType(subtype, name);
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ScreenShare:H264] Encoder rejected {name} input: {ex.Message}");
                return false;
            }
        }

        private void SetInputType(Guid subtype, string name)
        {
            using var inputType = CreateVideoInputType(subtype);
            _encoder.SetInputType(0, inputType, 0);
            Debug.WriteLine($"[ScreenShare:H264] Encoder accepted {name} input type.");
        }

        private MediaType CreateH264OutputType(int bitrate)
        {
            var outputType = new MediaType();
            outputType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            outputType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264);
            outputType.Set(MediaTypeAttributeKeys.AvgBitrate, bitrate);
            outputType.Set(MediaTypeAttributeKeys.FrameSize, PackRatio(_width, _height));
            outputType.Set(MediaTypeAttributeKeys.FrameRate, PackRatio(NativeScreenShareStreamingService.TargetFps, 1));
            outputType.Set(MediaTypeAttributeKeys.PixelAspectRatio, PackRatio(1, 1));
            outputType.Set(MediaTypeAttributeKeys.InterlaceMode, (int)VideoInterlaceMode.Progressive);
            outputType.Set(MediaTypeAttributeKeys.MaxKeyframeSpacing, RecoveryKeyFrameIntervalFrames);
            return outputType;
        }

        private MediaType CreateVideoInputType(Guid subtype)
        {
            var inputType = new MediaType();
            var sampleSize = subtype == VideoFormatGuids.Rgb32
                ? _width * _height * 4
                : _width * _height * 3 / 2;

            inputType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            inputType.Set(MediaTypeAttributeKeys.Subtype, subtype);
            inputType.Set(MediaTypeAttributeKeys.FrameSize, PackRatio(_width, _height));
            inputType.Set(MediaTypeAttributeKeys.FrameRate, PackRatio(NativeScreenShareStreamingService.TargetFps, 1));
            inputType.Set(MediaTypeAttributeKeys.PixelAspectRatio, PackRatio(1, 1));
            inputType.Set(MediaTypeAttributeKeys.InterlaceMode, (int)VideoInterlaceMode.Progressive);
            inputType.Set(MediaTypeAttributeKeys.FixedSizeSamples, 1);
            inputType.Set(MediaTypeAttributeKeys.AllSamplesIndependent, 1);
            inputType.Set(MediaTypeAttributeKeys.SampleSize, sampleSize);
            inputType.Set(MediaTypeAttributeKeys.DefaultStride, subtype == VideoFormatGuids.Rgb32 ? _width * 4 : _width);
            return inputType;
        }

        private Texture2D? TryGetNextAvailableDxgiInputTexture()
        {
            EnsureDxgiInputTextures();
            var textures = _dxgiInputTextures ?? throw new InvalidOperationException("DXGI input textures were not initialized.");

            for (var offset = 0; offset < textures.Length; offset++)
            {
                var index = (_dxgiInputTextureIndex + offset) % textures.Length;
                var texture = textures[index];
                if (IsDxgiInputTexturePending(texture))
                    continue;

                _dxgiInputTextureIndex = (index + 1) % textures.Length;
                return texture;
            }

            return null;
        }

        private void EnsureDxgiInputTextures()
        {
            if (_dxgiInputTextures != null)
                return;
            if (_encoderD3D11Device == null)
                throw new InvalidOperationException("D3D11 input device was not initialized.");

            var bindFlagCandidates = new[]
            {
                BindFlags.VideoEncoder,
                BindFlags.None,
                BindFlags.ShaderResource
            };

            foreach (var bindFlags in bindFlagCandidates)
            {
                var desc = CreateDxgiInputTextureDescription(bindFlags);

                try
                {
                    var textures = new List<Texture2D>(DxgiInputTexturePoolSize);
                    try
                    {
                        for (var i = 0; i < DxgiInputTexturePoolSize; i++)
                            textures.Add(new Texture2D(_encoderD3D11Device, desc));

                        _dxgiInputTextures = textures.ToArray();
                    }
                    catch
                    {
                        foreach (var texture in textures)
                        {
                            try
                            {
                                texture.Dispose();
                            }
                            catch
                            {
                            }
                        }

                        throw;
                    }

                    Debug.WriteLine($"[ScreenShare:H264] Created D3D11 NV12 DXGI input texture pool with bind flags: {bindFlags}.");
                    return;
                }
                catch (SharpDXException ex)
                {
                    Debug.WriteLine($"[ScreenShare:H264] D3D11 NV12 texture creation failed with bind flags {bindFlags}: {ex.Message}");
                    DisposeDxgiInputTextures();
                }
            }

            throw new InvalidOperationException("The GPU encoder D3D11 device did not accept any NV12 DXGI input texture descriptor.");
        }

        private Texture2DDescription CreateDxgiInputTextureDescription(BindFlags bindFlags)
        {
            return new Texture2DDescription
            {
                Width = _width,
                Height = _height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.NV12,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = bindFlags,
                CpuAccessFlags = CpuAccessFlags.None,
                OptionFlags = ResourceOptionFlags.None
            };
        }

        private void ConvertBgraTextureToNv12Texture(
            Texture2D sourceTexture,
            int sourceWidth,
            int sourceHeight,
            Texture2D targetTexture)
        {
            if (_encoderD3D11Device == null)
                throw new InvalidOperationException("D3D11 input device was not initialized.");

            using var videoDevice = _encoderD3D11Device.QueryInterface<VideoDevice>();
            using var videoContext = _encoderD3D11Device.ImmediateContext.QueryInterface<VideoContext>();

            var description = new VideoProcessorContentDescription
            {
                InputFrameFormat = VideoFrameFormat.Progressive,
                InputFrameRate = new Rational(NativeScreenShareStreamingService.TargetFps, 1),
                InputWidth = sourceWidth,
                InputHeight = sourceHeight,
                OutputFrameRate = new Rational(NativeScreenShareStreamingService.TargetFps, 1),
                OutputWidth = _width,
                OutputHeight = _height,
                Usage = VideoUsage.PlaybackNormal
            };

            videoDevice.CreateVideoProcessorEnumerator(ref description, out var enumerator);
            using var enumeratorScope = enumerator;
            videoDevice.CreateVideoProcessor(enumerator, 0, out var processor);
            using var processorScope = processor;
            var inputViewDescription = new VideoProcessorInputViewDescription
            {
                Dimension = VpivDimension.Texture2D,
                Texture2D = new Texture2DVpiv
                {
                    MipSlice = 0,
                    ArraySlice = 0
                }
            };
            videoDevice.CreateVideoProcessorInputView(
                sourceTexture,
                enumerator,
                inputViewDescription,
                out var inputView);
            using var inputViewScope = inputView;
            var outputViewDescription = new VideoProcessorOutputViewDescription
            {
                Dimension = VpovDimension.Texture2D,
                Texture2D = new Texture2DVpov
                {
                    MipSlice = 0
                }
            };
            videoDevice.CreateVideoProcessorOutputView(
                targetTexture,
                enumerator,
                outputViewDescription,
                out var outputView);
            using var outputViewScope = outputView;

            var destination = GetAspectFitRectangle(sourceWidth, sourceHeight, _width, _height);
            videoContext.VideoProcessorSetStreamFrameFormat(processor, 0, VideoFrameFormat.Progressive);
            videoContext.VideoProcessorSetStreamSourceRect(
                processor,
                0,
                new RawBool(true),
                new RawRectangle(0, 0, sourceWidth, sourceHeight));
            videoContext.VideoProcessorSetStreamDestRect(
                processor,
                0,
                new RawBool(true),
                destination);
            videoContext.VideoProcessorSetStreamAutoProcessingMode(processor, 0, new RawBool(false));

            var background = new VideoColor
            {
                Rgba = new VideoColorRgba
                {
                    R = 0,
                    G = 0,
                    B = 0,
                    A = 1
                }
            };
            videoContext.VideoProcessorSetOutputBackgroundColor(processor, new RawBool(false), background);

            var streams = new[]
            {
                new VideoProcessorStream
                {
                    Enable = new RawBool(true),
                    OutputIndex = 0,
                    InputFrameOrField = 0,
                    PastFrames = 0,
                    FutureFrames = 0,
                    PInputSurface = inputView
                }
            };

            videoContext.VideoProcessorBlt(processor, outputView, _frameIndex, streams.Length, streams);
            _encoderD3D11Device.ImmediateContext.Flush();
        }

        private RawRectangle GetAspectFitRectangle(
            int sourceWidth,
            int sourceHeight,
            int targetWidth,
            int targetHeight)
        {
            var scale = Math.Min((double)targetWidth / sourceWidth, (double)targetHeight / sourceHeight);
            var width = Math.Max(1, (int)Math.Round(sourceWidth * scale));
            var height = Math.Max(1, (int)Math.Round(sourceHeight * scale));
            var left = (targetWidth - width) / 2;
            var top = (targetHeight - height) / 2;
            return new RawRectangle(left, top, left + width, top + height);
        }

        private bool IsDxgiInputTexturePending(Texture2D texture)
        {
            foreach (var pendingInput in _pendingHardwareInputs)
            {
                if (ReferenceEquals(pendingInput.Texture, texture))
                    return true;
            }

            return false;
        }

        private void DisposeDxgiInputTextures()
        {
            if (_dxgiInputTextures == null)
                return;

            foreach (var texture in _dxgiInputTextures)
            {
                try
                {
                    texture.Dispose();
                }
                catch
                {
                }
            }

            _dxgiInputTextures = null;
            _dxgiInputTextureIndex = 0;
        }

        private void InitializeHardwareEventPump()
        {
            DisposeHardwareEventGenerator();

            try
            {
                _hardwareEventGenerator = _encoder.QueryInterfaceOrNull<MediaEventGenerator>();
                if (_hardwareEventGenerator != null)
                {
                    lock (_hardwareEventSync)
                    {
                        _hardwareInputRequests = 0;
                        _hardwareOutputRequests = 0;
                        _loggedHardwareEvents = 0;
                        _stopHardwareEventThread = false;
                    }

                    _hardwareEventThread = new Thread(HardwareEventLoop)
                    {
                        IsBackground = true,
                        Name = "Zink NVENC MFT Event Pump"
                    };
                    _hardwareEventThread.Start();
                    Debug.WriteLine("[ScreenShare:H264] Hardware encoder async event pump enabled.");
                }
                else
                {
                    Debug.WriteLine("[ScreenShare:H264] Hardware encoder did not expose an async event generator; using synchronous output drain.");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ScreenShare:H264] Hardware encoder async event pump unavailable: {ex.Message}");
                _hardwareEventGenerator = null;
            }
        }

        private void HardwareEventLoop()
        {
            while (true)
            {
                MediaEventGenerator? eventGenerator;
                lock (_hardwareEventSync)
                {
                    if (_stopHardwareEventThread)
                        return;

                    eventGenerator = _hardwareEventGenerator;
                }

                if (eventGenerator == null)
                    return;

                try
                {
                    using var mediaEvent = eventGenerator.GetEvent(isBlocking: true);
                    mediaEvent.Status.CheckError();
                    HandleHardwareEvent(mediaEvent.TypeInfo);
                }
                catch (SharpDXException ex)
                {
                    lock (_hardwareEventSync)
                    {
                        if (_stopHardwareEventThread)
                            return;
                    }

                    Debug.WriteLine($"[ScreenShare:H264] Hardware encoder event pump error: 0x{ex.ResultCode.Code:X8} {ex.Message}");
                    Thread.Sleep(5);
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    lock (_hardwareEventSync)
                    {
                        if (_stopHardwareEventThread)
                            return;
                    }

                    Debug.WriteLine($"[ScreenShare:H264] Hardware encoder event pump failed: {ex.Message}");
                    Thread.Sleep(5);
                }
            }
        }

        private void HandleHardwareEvent(MediaEventTypes eventType)
        {
            lock (_hardwareEventSync)
            {
                switch (eventType)
                {
                    case MediaEventTypes.TransformNeedInput:
                        _hardwareInputRequests++;
                        break;

                    case MediaEventTypes.TransformHaveOutput:
                        _hardwareOutputRequests++;
                        break;

                    case MediaEventTypes.Error:
                        Debug.WriteLine("[ScreenShare:H264] GPU hardware encoder reported a Media Foundation error event.");
                        break;
                }

                if (_loggedHardwareEvents < 24)
                {
                    _loggedHardwareEvents++;
                    Debug.WriteLine($"[ScreenShare:H264] Hardware encoder event: {eventType}; needInput={_hardwareInputRequests}; haveOutput={_hardwareOutputRequests}.");
                }
            }
        }

        private bool TryConsumeHardwareInputRequest()
        {
            lock (_hardwareEventSync)
            {
                if (_hardwareInputRequests <= 0)
                    return false;

                _hardwareInputRequests--;
                return true;
            }
        }

        private void ReturnHardwareInputRequest()
        {
            if (_hardwareEventGenerator == null)
                return;

            lock (_hardwareEventSync)
            {
                _hardwareInputRequests++;
            }
        }

        private bool TryConsumeHardwareOutputRequest()
        {
            lock (_hardwareEventSync)
            {
                if (_hardwareOutputRequests <= 0)
                    return false;

                _hardwareOutputRequests--;
                return true;
            }
        }

        private void ReleaseCompletedHardwareInput()
        {
            if (_pendingHardwareInputs.Count == 0)
                return;

            _pendingHardwareInputs.Dequeue().Dispose();
        }

        private void DisposePendingHardwareInputs()
        {
            while (_pendingHardwareInputs.Count > 0)
                _pendingHardwareInputs.Dequeue().Dispose();
        }

        private void DisposeHardwareEventGenerator()
        {
            lock (_hardwareEventSync)
            {
                _stopHardwareEventThread = true;
                _hardwareInputRequests = 0;
                _hardwareOutputRequests = 0;
                _loggedWaitingForHardwareInput = false;
            }

            try
            {
                _hardwareEventGenerator?.Dispose();
            }
            catch
            {
            }

            try
            {
                if (_hardwareEventThread != null &&
                    _hardwareEventThread != Thread.CurrentThread &&
                    !_hardwareEventThread.Join(200))
                {
                    Debug.WriteLine("[ScreenShare:H264] Hardware encoder event pump did not stop within 200ms.");
                }
            }
            catch
            {
            }

            _hardwareEventGenerator = null;
            _hardwareEventThread = null;
        }

        private static void EnableMultithreadProtection(SharpDX.Direct3D11.Device device)
        {
            try
            {
                using var multithread = device.QueryInterface<Multithread>();
                var wasProtected = multithread.SetMultithreadProtected(true);
                Debug.WriteLine($"[ScreenShare:H264] Native D3D11 multithread protection enabled; previously protected={wasProtected}.");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ScreenShare:H264] Native D3D11 multithread protection skipped: {ex.Message}");
            }
        }

        private static bool TryEnableRealtimeEncoderMode(Transform encoder, bool enableHardwareAsyncMode)
        {
            try
            {
                var attributes = encoder.Attributes;
                using (attributes)
                {
                    if (enableHardwareAsyncMode)
                    {
                        attributes.Set(TransformAttributeKeys.TransformAsyncUnlock, 1);
                        attributes.Set(TransformAttributeKeys.MftHwTimestampWithQpcAttribute, 1);
                    }

                    attributes.Set(SinkWriterAttributeKeys.LowLatency.Guid, 1);
                    attributes.Set(CodecApiAvLowLatencyMode, 1);
                    attributes.Set(CodecApiAvEncCommonLowLatency, 1);
                    attributes.Set(CodecApiAvEncCommonRealTime, 1);
                    attributes.Set(CodecApiAvEncCommonQualityVsSpeed, 100);
                    attributes.Set(CodecApiAvEncVideoMaxKeyframeDistance, RecoveryKeyFrameIntervalFrames);
                    attributes.Set(CodecApiAvEncVideoNumGopsPerIdr, 1);
                }

                Debug.WriteLine(enableHardwareAsyncMode
                    ? "[ScreenShare:H264] Realtime/low-latency encoder attributes enabled with hardware async unlock."
                    : "[ScreenShare:H264] Realtime/low-latency encoder attributes enabled in synchronous mode.");
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ScreenShare:H264] Realtime encoder attributes skipped: {ex.Message}");
                return false;
            }
        }

        private static void TryUnlockAsyncHardwareTransform(Transform encoder)
        {
            try
            {
                using var attributes = encoder.Attributes;
                attributes.Set(TransformAttributeKeys.TransformAsyncUnlock, 1);
                attributes.Set(TransformAttributeKeys.MftHwTimestampWithQpcAttribute, 1);
                Debug.WriteLine("[ScreenShare:H264] Hardware MFT async unlock enabled before GPU manager attachment.");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ScreenShare:H264] Hardware MFT async unlock skipped before GPU manager attachment: {ex.Message}");
            }
        }

        private void RequestRecoveryKeyFrameIfNeeded()
        {
            var forced = Interlocked.Exchange(ref _forceNextKeyFrame, 0) == 1;
            if (!forced && (_frameIndex == 0 || _frameIndex % RecoveryKeyFrameIntervalFrames != 0))
                return;

            try
            {
                using var attributes = _encoder.Attributes;
                attributes.Set(CodecApiAvEncVideoForceKeyFrame, 1);
                if (forced)
                    Debug.WriteLine("[ScreenShare:H264] Forced recovery keyframe requested.");
            }
            catch (Exception ex)
            {
                if (!_loggedForceKeyFrameUnavailable)
                {
                    _loggedForceKeyFrameUnavailable = true;
                    Debug.WriteLine($"[ScreenShare:H264] Force keyframe request skipped: {ex.Message}");
                }
            }
        }

        private static bool TrySetLowLatencyOutputTypeAttributes(MediaType outputType)
        {
            try
            {
                outputType.Set(MediaTypeAttributeKeys.H264MaxCodecConfigDelay, 0);
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ScreenShare:H264] Low-latency output type attributes skipped: {ex.Message}");
                return false;
            }
        }

        private static unsafe void CopyBitmapToRgb32(Bitmap bitmap, int width, int height, IntPtr destination)
        {
            var disposeSource = bitmap.Width != width ||
                bitmap.Height != height ||
                bitmap.PixelFormat != PixelFormat.Format32bppArgb;
            Bitmap? converted = null;
            var source = bitmap;

            if (disposeSource)
            {
                converted = new Bitmap(width, height, PixelFormat.Format32bppArgb);
                using var graphics = Graphics.FromImage(converted);
                graphics.DrawImage(bitmap, 0, 0, width, height);
                source = converted;
            }

            var rect = new Rectangle(0, 0, width, height);
            var data = source.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                byte* src = (byte*)data.Scan0;
                byte* dst = (byte*)destination;
                var rowBytes = width * 4;

                for (var y = 0; y < height; y++)
                {
                    System.Buffer.MemoryCopy(
                        src + y * data.Stride,
                        dst + y * rowBytes,
                        rowBytes,
                        rowBytes);
                }
            }
            finally
            {
                source.UnlockBits(data);
                converted?.Dispose();
            }
        }

        private static unsafe void ConvertBitmapToNv12(Bitmap bitmap, int width, int height, byte[] nv12)
        {
            var disposeSource = bitmap.Width != width || bitmap.Height != height;
            var source = disposeSource ? new Bitmap(bitmap, width, height) : bitmap;

            var rect = new Rectangle(0, 0, width, height);
            var data = source.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                int uvStart = width * height;
                fixed (byte* nv12Base = nv12)
                {
                    ConvertBitmapToNv12Rows(
                        data.Scan0,
                        data.Stride,
                        width,
                        height,
                        (IntPtr)nv12Base,
                        uvStart);
                }
            }
            finally
            {
                source.UnlockBits(data);
                if (disposeSource)
                    source.Dispose();
            }
        }

        private static unsafe void ConvertBitmapToNv12Rows(
            IntPtr sourceBasePtr,
            int sourceStride,
            int width,
            int height,
            IntPtr nv12BasePtr,
            int uvStart)
        {
            var rowPairs = (height + 1) / 2;
            var sourceBase = (byte*)sourceBasePtr;
            var nv12Base = (byte*)nv12BasePtr;

            Parallel.For(0, rowPairs, rowPair =>
            {
                var y = rowPair * 2;
                var nextY = Math.Min(y + 1, height - 1);
                var row0 = sourceBase + y * sourceStride;
                var row1 = sourceBase + nextY * sourceStride;
                var yPlane0 = nv12Base + y * width;
                var yPlane1 = nv12Base + nextY * width;
                var uvPlane = nv12Base + uvStart + rowPair * width;

                for (var x = 0; x < width; x += 2)
                {
                    var nextX = Math.Min(x + 1, width - 1);
                    var p00 = row0 + x * 4;
                    var p01 = row0 + nextX * 4;
                    var p10 = row1 + x * 4;
                    var p11 = row1 + nextX * 4;

                    yPlane0[x] = GetY(p00);
                    yPlane0[nextX] = GetY(p01);
                    yPlane1[x] = GetY(p10);
                    yPlane1[nextX] = GetY(p11);

                    var u = (GetU(p00) + GetU(p01) + GetU(p10) + GetU(p11)) >> 2;
                    var v = (GetV(p00) + GetV(p01) + GetV(p10) + GetV(p11)) >> 2;
                    uvPlane[x] = (byte)u;
                    uvPlane[x + 1] = (byte)v;
                }
            });
        }

        private static unsafe byte GetY(byte* src)
        {
            return ClampToByte((YFromR[src[2]] + YFromG[src[1]] + YFromB[src[0]]) >> 8);
        }

        private static unsafe byte GetU(byte* src)
        {
            return ClampToByte((UFromR[src[2]] + UFromG[src[1]] + UFromB[src[0]]) >> 8);
        }

        private static unsafe byte GetV(byte* src)
        {
            return ClampToByte((VFromR[src[2]] + VFromG[src[1]] + VFromB[src[0]]) >> 8);
        }

        private static int[] BuildContributionTable(int coefficient, int offset)
        {
            var table = new int[256];
            for (var i = 0; i < table.Length; i++)
                table[i] = coefficient * i + offset;
            return table;
        }

        private static byte ClampToByte(int value)
        {
            if (value < 0) return 0;
            if (value > 255) return 255;
            return (byte)value;
        }

        public void Dispose()
        {
            try
            {
                _encoder.ProcessMessage(TMessageType.NotifyEndOfStream, IntPtr.Zero);
                _encoder.ProcessMessage(TMessageType.NotifyEndStreaming, IntPtr.Zero);
            }
            catch
            {
            }

            _encoder.Dispose();
            DisposeGpuEncodingResources();
        }

        private sealed class PendingHardwareInputSample : IDisposable
        {
            public PendingHardwareInputSample(Sample sample, MediaBuffer buffer, Texture2D texture)
            {
                Sample = sample;
                Buffer = buffer;
                Texture = texture;
            }

            public Sample Sample { get; }
            public MediaBuffer Buffer { get; }
            public Texture2D Texture { get; }

            public void Dispose()
            {
                try
                {
                    Sample.Dispose();
                }
                catch
                {
                }

                try
                {
                    Buffer.Dispose();
                }
                catch
                {
                }
            }
        }

        private sealed class EncoderTransformSelection
        {
            public EncoderTransformSelection(Transform encoder, string mode, bool isHardwareAccelerated)
            {
                Encoder = encoder;
                Mode = mode;
                IsHardwareAccelerated = isHardwareAccelerated;
            }

            public Transform Encoder { get; }
            public string Mode { get; }
            public bool IsHardwareAccelerated { get; }
        }

        private sealed class NativeProcessOutputResult : IDisposable
        {
            public NativeProcessOutputResult(
                Sample? sample,
                Collection? events,
                int outputBufferStatus,
                TransformProcessOutputStatus processStatus)
            {
                Sample = sample;
                Events = events;
                OutputBufferStatus = outputBufferStatus;
                ProcessStatus = processStatus;
            }

            public Sample? Sample { get; }
            public Collection? Events { get; }
            public int OutputBufferStatus { get; }
            public TransformProcessOutputStatus ProcessStatus { get; }

            public void Dispose()
            {
                try
                {
                    Sample?.Dispose();
                }
                catch
                {
                }

                try
                {
                    Events?.Dispose();
                }
                catch
                {
                }
            }
        }

        private readonly record struct AnnexBNalUnit(int Offset, int Length, byte Type);
    }

    public sealed class H264EncodedFrame
    {
        public H264EncodedFrame(byte[] data, bool isKeyFrame)
        {
            Data = data;
            IsKeyFrame = isKeyFrame;
        }

        public byte[] Data { get; }
        public bool IsKeyFrame { get; }
    }
}
