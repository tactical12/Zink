using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using System;
using System.Runtime.InteropServices;
using Windows.Graphics;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace Zink.Services.Recording
{
    internal static class Direct3D11Helpers
    {
        [ComImport]
        [Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDirect3DDxgiInterfaceAccess
        {
            IntPtr GetInterface(in Guid iid);
        }

        [DllImport("d3d11.dll")]
        private static extern int D3D11CreateDevice(
            IntPtr pAdapter,
            DriverType driverType,
            IntPtr software,
            DeviceCreationFlags flags,
            IntPtr pFeatureLevels,
            int featureLevels,
            int sdkVersion,
            out IntPtr ppDevice,
            out int pFeatureLevel,
            out IntPtr ppImmediateContext);

        [DllImport("d3d11.dll")]
        private static extern int CreateDirect3D11DeviceFromDXGIDevice(
            IntPtr dxgiDevice,
            out IntPtr graphicsDevice);

        [DllImport("d3d11.dll")]
        private static extern int CreateDirect3D11SurfaceFromDXGISurface(
            IntPtr dxgiSurface,
            out IntPtr graphicsSurface);

        public static IDirect3DDevice CreateD3DDevice()
        {
            const int D3D11_SDK_VERSION = 7;

            IntPtr d3dDevicePtr;
            IntPtr immediateContextPtr;
            int featureLevel;

            int hr = D3D11CreateDevice(
                IntPtr.Zero,
                DriverType.Hardware,
                IntPtr.Zero,
                DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport,
                IntPtr.Zero,
                0,
                D3D11_SDK_VERSION,
                out d3dDevicePtr,
                out featureLevel,
                out immediateContextPtr);

            if (hr < 0)
                Marshal.ThrowExceptionForHR(hr);

            try
            {
                using var d3dDevice = new SharpDX.Direct3D11.Device(d3dDevicePtr);
                using var dxgiDevice = d3dDevice.QueryInterface<SharpDX.DXGI.Device>();

                IntPtr inspectablePtr;
                hr = CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out inspectablePtr);
                if (hr < 0)
                    Marshal.ThrowExceptionForHR(hr);

                try
                {
                    return MarshalInterface<IDirect3DDevice>.FromAbi(inspectablePtr);
                }
                finally
                {
                    if (inspectablePtr != IntPtr.Zero)
                        Marshal.Release(inspectablePtr);
                }
            }
            finally
            {
                if (immediateContextPtr != IntPtr.Zero)
                    Marshal.Release(immediateContextPtr);
            }
        }

        public static SharpDX.Direct3D11.Device CreateSharpDXDevice(IDirect3DDevice device)
        {
            var access = device.As<IDirect3DDxgiInterfaceAccess>();

            IntPtr dxgiDevicePtr = access.GetInterface(typeof(SharpDX.DXGI.Device).GUID);
            if (dxgiDevicePtr == IntPtr.Zero)
                throw new InvalidOperationException("Failed to get DXGI device pointer from IDirect3DDevice.");

            using var dxgiDevice = new SharpDX.DXGI.Device(dxgiDevicePtr);
            return dxgiDevice.QueryInterface<SharpDX.Direct3D11.Device>();
        }

        public static Texture2D InitializeComposeTexture(SharpDX.Direct3D11.Device device, SizeInt32 size)
        {
            var desc = new Texture2DDescription
            {
                Width = Math.Max(1, size.Width),
                Height = Math.Max(1, size.Height),
                ArraySize = 1,
                MipLevels = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
                CpuAccessFlags = CpuAccessFlags.None,
                OptionFlags = ResourceOptionFlags.None
            };

            return new Texture2D(device, desc);
        }

        public static Texture2D CreateSharpDXTexture2D(IDirect3DSurface surface)
        {
            var access = surface.As<IDirect3DDxgiInterfaceAccess>();

            IntPtr texturePtr = access.GetInterface(typeof(Texture2D).GUID);
            if (texturePtr == IntPtr.Zero)
                throw new InvalidOperationException("Failed to get Texture2D pointer from IDirect3DSurface.");

            return new Texture2D(texturePtr);
        }

        public static IDirect3DSurface CreateDirect3DSurface(Texture2D texture)
        {
            using var dxgiSurface = texture.QueryInterface<SharpDX.DXGI.Surface>();

            IntPtr inspectablePtr;
            int hr = CreateDirect3D11SurfaceFromDXGISurface(dxgiSurface.NativePointer, out inspectablePtr);
            if (hr < 0)
                Marshal.ThrowExceptionForHR(hr);

            try
            {
                return MarshalInterface<IDirect3DSurface>.FromAbi(inspectablePtr);
            }
            finally
            {
                if (inspectablePtr != IntPtr.Zero)
                    Marshal.Release(inspectablePtr);
            }
        }
    }
}