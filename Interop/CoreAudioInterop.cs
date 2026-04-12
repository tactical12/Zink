using System;
using System.Runtime.InteropServices;

namespace Zink.Interop
{
    public enum EDataFlow
    {
        eRender,
        eCapture,
        eAll,
        EDataFlow_enum_count
    }

    internal enum ERole
    {
        eConsole,
        eMultimedia,
        eCommunications,
        ERole_enum_count
    }

    [Flags]
    internal enum CLSCTX : uint
    {
        INPROC_SERVER = 0x1,
        INPROC_HANDLER = 0x2,
        LOCAL_SERVER = 0x4,
        ALL = INPROC_SERVER | INPROC_HANDLER | LOCAL_SERVER
    }

    internal enum AUDCLNT_SHAREMODE
    {
        SHARED,
        EXCLUSIVE
    }

    [Flags]
    internal enum AUDCLNT_STREAMFLAGS : uint
    {
        NONE = 0x0,
        CROSSPROCESS = 0x00010000,
        LOOPBACK = 0x00020000,
        EVENTCALLBACK = 0x00040000,
        NOPERSIST = 0x00080000,
        RATEADJUST = 0x00100000,
        AUTOCONVERTPCM = 0x80000000,
        SRC_DEFAULT_QUALITY = 0x08000000
    }

    [Flags]
    internal enum AUDCLNT_BUFFERFLAGS : uint
    {
        NONE = 0x0,
        DATA_DISCONTINUITY = 0x1,
        SILENT = 0x2,
        TIMESTAMP_ERROR = 0x4
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct WAVEFORMATEX
    {
        public ushort wFormatTag;
        public ushort nChannels;
        public uint nSamplesPerSec;
        public uint nAvgBytesPerSec;
        public ushort nBlockAlign;
        public ushort wBitsPerSample;
        public ushort cbSize;
    }

    [ComImport]
    [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    internal class MMDeviceEnumeratorComObject
    {
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    internal interface IMMDeviceEnumerator
    {
        int EnumAudioEndpoints(EDataFlow dataFlow, uint dwStateMask, out IMMDeviceCollection ppDevices);
        int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice ppEndpoint);
        int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string pwstrId, out IMMDevice ppDevice);
        int RegisterEndpointNotificationCallback(IntPtr pClient);
        int UnregisterEndpointNotificationCallback(IntPtr pClient);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("0BD7A1BE-7A1A-44DB-8397-C0A4A8B13FDB")]
    internal interface IMMDeviceCollection
    {
        int GetCount(out uint pcDevices);
        int Item(uint nDevice, out IMMDevice ppDevice);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    internal interface IMMDevice
    {
        int Activate(ref Guid iid, CLSCTX dwClsCtx, IntPtr pActivationParams, [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);
        int OpenPropertyStore(int stgmAccess, out IntPtr ppProperties);
        int GetId([MarshalAs(UnmanagedType.LPWStr)] out string ppstrId);
        int GetState(out uint pdwState);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2")]
    internal interface IAudioClient
    {
        int Initialize(
            AUDCLNT_SHAREMODE shareMode,
            AUDCLNT_STREAMFLAGS streamFlags,
            long hnsBufferDuration,
            long hnsPeriodicity,
            IntPtr pFormat,
            IntPtr audioSessionGuid);

        int GetBufferSize(out uint bufferSize);
        int GetStreamLatency(out long phnsLatency);
        int GetCurrentPadding(out uint currentPadding);
        int IsFormatSupported(AUDCLNT_SHAREMODE shareMode, IntPtr pFormat, out IntPtr closestMatch);
        int GetMixFormat(out IntPtr deviceFormatPointer);
        int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        int Start();
        int Stop();
        int Reset();
        int SetEventHandle(IntPtr eventHandle);
        int GetService(ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppv);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317")]
    internal interface IAudioCaptureClient
    {
        int GetBuffer(
            out IntPtr data,
            out uint numFramesToRead,
            out AUDCLNT_BUFFERFLAGS flags,
            out long devicePosition,
            out long qpcPosition);

        int ReleaseBuffer(uint numFramesRead);
        int GetNextPacketSize(out uint numFramesInNextPacket);
    }

    internal static class CoreAudioGuids
    {
        public static readonly Guid IID_IAudioClient = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
        public static readonly Guid IID_IAudioCaptureClient = new("C8ADBD64-E71E-48a0-A4DE-185C395CD317");
    }

    internal static class HResult
    {
        public static void Check(int hr, string api)
        {
            if (hr < 0)
                Marshal.ThrowExceptionForHR(hr);
        }
    }
}