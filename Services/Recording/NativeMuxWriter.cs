using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Zink.Services.Recording
{
    internal static class NativeMuxWriter
    {
        private const string DllName = "ZinkRecorderMux.dll";
        private static bool _resolverInstalled;
        static NativeMuxWriter()
        {
            InstallResolver();
        }

        private static void InstallResolver()
        {
            if (_resolverInstalled)
                return;

            NativeLibrary.SetDllImportResolver(typeof(NativeMuxWriter).Assembly, ResolveNativeLibrary);
            _resolverInstalled = true;
        }

        private static IntPtr ResolveNativeLibrary(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
        {
            if (!string.Equals(libraryName, DllName, StringComparison.OrdinalIgnoreCase))
                return IntPtr.Zero;

            string baseDir = AppContext.BaseDirectory;
            string fullPath = Path.Combine(baseDir, DllName);

            if (!File.Exists(fullPath))
            {
                throw new DllNotFoundException(
                    $"Native mux DLL was not found at expected path: {fullPath}");
            }

            try
            {
                return NativeLibrary.Load(fullPath);
            }
            catch (Exception ex)
            {
                throw new DllNotFoundException(
                    $"Failed to load native mux DLL from '{fullPath}'. " +
                    $"This usually means the DLL exists but one of its native dependencies is missing. " +
                    $"Inner error: {ex.Message}", ex);
            }
        }

        [DllImport(DllName, CharSet = CharSet.Unicode, CallingConvention = CallingConvention.StdCall)]
        public static extern int ZrmCreateWriter(
            string outputPath,
            uint width,
            uint height,
            uint fpsNum,
            uint fpsDen,
            uint videoBitrate,
            uint audioSampleRate,
            uint audioChannels,
            uint audioBitsPerSample);

        [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
        public static extern int ZrmWriteVideoFrame(
            long sampleTime100ns,
            long sampleDuration100ns,
            byte[] bgraData,
            uint cbBgraData);

        [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
        public static extern int ZrmWriteAudioPacket(
            long sampleTime100ns,
            long sampleDuration100ns,
            byte[] pcmData,
            uint cbPcmData);

        [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
        public static extern int ZrmFinalizeWriter();

        [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
        public static extern void ZrmShutdownWriter();

        public static void ThrowIfFailed(int hr, string api)
        {
            if (hr < 0)
                Marshal.ThrowExceptionForHR(hr);
        }
    }
}
