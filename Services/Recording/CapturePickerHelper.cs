using System;
using System.Threading.Tasks;
using Windows.Graphics.Capture;

namespace Zink.Services.Recording
{
    public static class CapturePickerHelper
    {
        public static async Task<GraphicsCaptureItem?> PickCaptureItemAsync(IntPtr hwnd)
        {
            return await CaptureSourceHelper.GetOrCreateAsync(hwnd);
        }
    }
}
