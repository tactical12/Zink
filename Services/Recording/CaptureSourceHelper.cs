using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Windows.Graphics.Capture;
using WinRT;
using WinRT.Interop;

namespace Zink.Services.Recording
{
    internal static class CaptureSourceHelper
    {
        private static GraphicsCaptureItem? _cachedItem;

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

        public static async Task<GraphicsCaptureItem?> GetOrCreateAsync(IntPtr hwnd)
        {
            if (_cachedItem != null)
                return _cachedItem;

            // 🔹 Try programmatic capture FIRST (no picker)
            var programmatic = TryCreatePrimaryMonitor();
            if (programmatic != null)
            {
                _cachedItem = programmatic;
                return _cachedItem;
            }

            // 🔹 FALLBACK → Picker (only once)
            var picker = new GraphicsCapturePicker();
            InitializeWithWindow.Initialize(picker, hwnd);

            var picked = await picker.PickSingleItemAsync();

            if (picked != null)
                _cachedItem = picked;

            return _cachedItem;
        }

        private static GraphicsCaptureItem? TryCreatePrimaryMonitor()
        {
            try
            {
                if (!GraphicsCaptureSession.IsSupported())
                    return null;

                IntPtr monitor = MonitorFromPoint(new POINT { X = 0, Y = 0 }, MONITOR_DEFAULTTOPRIMARY);
                if (monitor == IntPtr.Zero)
                    return null;

                object factory = ActivationFactory.Get("Windows.Graphics.Capture.GraphicsCaptureItem");
                var interop = factory.As<IGraphicsCaptureItemInterop>();

                Guid iid = typeof(GraphicsCaptureItem).GUID;
                IntPtr itemPtr = interop.CreateForMonitor(monitor, ref iid);

                if (itemPtr == IntPtr.Zero)
                    return null;

                return MarshalInterface<GraphicsCaptureItem>.FromAbi(itemPtr);
            }
            catch
            {
                return null;
            }
        }

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);
    }
}