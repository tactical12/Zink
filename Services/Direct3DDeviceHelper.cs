using Windows.Graphics.DirectX.Direct3D11;
using Zink.Services.Recording;

namespace Zink.Services
{
    public static class Direct3DDeviceHelper
    {
        public static IDirect3DDevice CreateDevice()
        {
            return Direct3D11Helpers.CreateD3DDevice();
        }
    }
}
