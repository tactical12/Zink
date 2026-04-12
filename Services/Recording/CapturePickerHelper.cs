using System;
using System.Threading.Tasks;
using Windows.Graphics.Capture;
using WinRT.Interop;

namespace Zink.Services.Recording
{
    public static class CapturePickerHelper
    {
        public static async Task<GraphicsCaptureItem?> PickCaptureItemAsync(IntPtr hwnd)
        {
            var picker = new GraphicsCapturePicker();
            InitializeWithWindow.Initialize(picker, hwnd);
            return await picker.PickSingleItemAsync();
        }
    }
}