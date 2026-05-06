using System;
using System.Threading.Tasks;
using Windows.Graphics.Capture;

namespace Zink.Services.Recording
{
    public static class CapturePickerService
    {
        public static async Task<GraphicsCaptureItem?> PickCaptureTargetAsync(IntPtr hwnd)
        {
            return await CaptureSourceHelper.GetOrCreateAsync(hwnd);
        }
    }
}
