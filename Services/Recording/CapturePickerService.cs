using System;
using System.Threading.Tasks;
using Windows.Graphics.Capture;
using WinRT.Interop;

namespace Zink.Services.Recording
{
    public static class CapturePickerService
    {
        public static async Task<GraphicsCaptureItem?> PickCaptureTargetAsync(IntPtr hwnd)
        {
            if (!GraphicsCaptureSession.IsSupported())
                return null;

            var picker = new GraphicsCapturePicker();
            InitializeWithWindow.Initialize(picker, hwnd);

            return await picker.PickSingleItemAsync();
        }
    }
}