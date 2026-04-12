using System;
using System.Runtime.InteropServices;
using Windows.Graphics.Capture;
using WinRT;

namespace Zink.Services.Recording
{
    public static class AutoCaptureHelper
    {
        [ComImport]
        [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IGraphicsCaptureItemInterop
        {
            IntPtr CreateForWindow(IntPtr window, ref Guid iid);
            IntPtr CreateForMonitor(IntPtr monitor, ref Guid iid);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        private const uint MONITOR_DEFAULTTOPRIMARY = 1;

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

        public static GraphicsCaptureItem? TryCreatePrimaryDisplayItem()
        {
            try
            {
                var monitor = MonitorFromPoint(new POINT { X = 0, Y = 0 }, MONITOR_DEFAULTTOPRIMARY);
                if (monitor == IntPtr.Zero)
                    return null;

                var interop = GraphicsCaptureItem.As<IGraphicsCaptureItemInterop>();
                Guid iid = typeof(GraphicsCaptureItem).GUID;

                IntPtr unknown = interop.CreateForMonitor(monitor, ref iid);
                if (unknown == IntPtr.Zero)
                    return null;

                try
                {
                    return MarshalInterface<GraphicsCaptureItem>.FromAbi(unknown);
                }
                finally
                {
                    Marshal.Release(unknown);
                }
            }
            catch
            {
                return null;
            }
        }
    }
}