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
        private static IntPtr _legacyWriter;

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

        [DllImport(DllName, CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr znk_writer_create(
            string outputPath,
            int width,
            int height,
            int fps,
            int sampleRate,
            int channels,
            int bitsPerSample);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int znk_writer_write_video_frame(
            IntPtr writerHandle,
            byte[] bgraData,
            int bgraByteCount,
            int width,
            int height,
            long timestamp100ns);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int znk_writer_finalize(IntPtr writerHandle);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void znk_writer_destroy(IntPtr writerHandle);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int znk_writer_write_audio_pcm(
            IntPtr writerHandle,
            byte[] pcmData,
            int pcmByteCount,
            int sampleRate,
            int channels,
            int bitsPerSample,
            long timestamp100ns);

        [DllImport(DllName, CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)]
        public static extern int znk_concat_mp4_segments(
            string outputPath,
            string inputPathsDoubleNull,
            int targetFps);

        public static void ThrowIfFailed(int hr, string api)
        {
            if (hr < 0)
                Marshal.ThrowExceptionForHR(hr);
        }

        public static int ZrmCreateWriter(
            string outputPath,
            uint width,
            uint height,
            uint fpsNum,
            uint fpsDen,
            uint videoBitrate,
            uint audioSampleRate,
            uint audioChannels,
            uint audioBitsPerSample)
        {
            ZrmShutdownWriter();

            uint fps = fpsDen == 0 ? fpsNum : Math.Max(1, fpsNum / fpsDen);
            _legacyWriter = znk_writer_create(
                outputPath,
                (int)width,
                (int)height,
                (int)fps,
                (int)audioSampleRate,
                (int)audioChannels,
                (int)audioBitsPerSample);

            return _legacyWriter == IntPtr.Zero ? unchecked((int)0x80004005) : 0;
        }

        public static int ZrmWriteVideoFrame(
            long sampleTime100ns,
            long sampleDuration100ns,
            byte[] bgraData,
            uint cbBgraData)
        {
            if (_legacyWriter == IntPtr.Zero)
                return unchecked((int)0x80070057);

            return znk_writer_write_video_frame(
                _legacyWriter,
                bgraData,
                (int)cbBgraData,
                0,
                0,
                sampleTime100ns);
        }

        public static int ZrmWriteAudioPacket(
            long sampleTime100ns,
            long sampleDuration100ns,
            byte[] pcmData,
            uint cbPcmData)
        {
            if (_legacyWriter == IntPtr.Zero)
                return unchecked((int)0x80070057);

            return znk_writer_write_audio_pcm(
                _legacyWriter,
                pcmData,
                (int)cbPcmData,
                48000,
                2,
                16,
                sampleTime100ns);
        }

        public static int ZrmFinalizeWriter()
        {
            if (_legacyWriter == IntPtr.Zero)
                return unchecked((int)0x80070057);

            int hr = znk_writer_finalize(_legacyWriter);
            znk_writer_destroy(_legacyWriter);
            _legacyWriter = IntPtr.Zero;
            return hr;
        }

        public static void ZrmShutdownWriter()
        {
            if (_legacyWriter == IntPtr.Zero)
                return;

            znk_writer_destroy(_legacyWriter);
            _legacyWriter = IntPtr.Zero;
        }
    }
}
