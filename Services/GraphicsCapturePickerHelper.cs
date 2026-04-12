using System;
using System.Threading.Tasks;
using Windows.Graphics.Capture;

namespace Zink.Services
{
    public static class GraphicsCapturePickerHelper
    {
        public static async Task<GraphicsCaptureItem?> PickAsync()
        {
            var picker = new GraphicsCapturePicker();
            return await picker.PickSingleItemAsync();
        }
    }
}