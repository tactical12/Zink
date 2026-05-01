using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Vortice.Direct3D12;
using static Vortice.Direct3D12.D3D12;

namespace Zink.Services.NativeCalling
{
    internal sealed class DirectX12VideoDeviceManager : IDisposable
    {
        private ID3D12Device? _d3d12Device;
        private ID3D12CommandQueue? _commandQueue;
        private SharpDX.Direct3D11.Device? _d3d11On12Device;
        private DeviceContext? _immediateContext;

        private DirectX12VideoDeviceManager(
            ID3D12Device d3d12Device,
            ID3D12CommandQueue commandQueue,
            SharpDX.Direct3D11.Device d3d11On12Device,
            DeviceContext immediateContext,
            string featureLevel)
        {
            _d3d12Device = d3d12Device;
            _commandQueue = commandQueue;
            _d3d11On12Device = d3d11On12Device;
            _immediateContext = immediateContext;
            Description = $"DirectX 12 hardware device ({featureLevel}) with D3D11On12 media interop";
        }

        public SharpDX.Direct3D11.Device D3D11On12Device =>
            _d3d11On12Device ?? throw new ObjectDisposedException(nameof(DirectX12VideoDeviceManager));

        public string Description { get; private set; }

        public static DirectX12VideoDeviceManager Create()
        {
            var d3d12Device = CreateD3D12Device(out var featureLevel);
            var commandQueue = CreateDirectCommandQueue(d3d12Device);

            try
            {
                var d3d11On12Device = CreateD3D11On12Device(d3d12Device, commandQueue, out var immediateContext);
                return new DirectX12VideoDeviceManager(
                    d3d12Device,
                    commandQueue,
                    d3d11On12Device,
                    immediateContext,
                    featureLevel);
            }
            catch
            {
                commandQueue.Dispose();
                d3d12Device.Dispose();
                throw;
            }
        }

        private static ID3D12Device CreateD3D12Device(out string featureLevel)
        {
            try
            {
                D3D12CreateDevice(null, Vortice.Direct3D.FeatureLevel.Level_12_0, out ID3D12Device device);
                featureLevel = "feature level 12_0";
                return device;
            }
            catch
            {
                D3D12CreateDevice(null, Vortice.Direct3D.FeatureLevel.Level_11_0, out ID3D12Device device);
                featureLevel = "feature level 11_0 via DirectX 12 API";
                return device;
            }
        }

        private static ID3D12CommandQueue CreateDirectCommandQueue(ID3D12Device device)
        {
            try
            {
                return device.CreateCommandQueue(
                    CommandListType.Direct,
                    (int)CommandQueuePriority.High,
                    CommandQueueFlags.None,
                    0);
            }
            catch
            {
                return device.CreateCommandQueue(
                    CommandListType.Direct,
                    (int)CommandQueuePriority.Normal,
                    CommandQueueFlags.None,
                    0);
            }
        }

        private static SharpDX.Direct3D11.Device CreateD3D11On12Device(
            ID3D12Device d3d12Device,
            ID3D12CommandQueue commandQueue,
            out DeviceContext immediateContext)
        {
            var queues = new[] { commandQueue.NativePointer };
            var featureLevels = new[]
            {
                FeatureLevel.Level_12_1,
                FeatureLevel.Level_12_0,
                FeatureLevel.Level_11_1,
                FeatureLevel.Level_11_0
            };

            var result = D3D11On12CreateDevice(
                d3d12Device.NativePointer,
                (int)(DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport),
                featureLevels,
                featureLevels.Length,
                queues,
                queues.Length,
                0,
                out var d3d11DevicePtr,
                out var immediateContextPtr,
                out _);

            if (result < 0)
                Marshal.ThrowExceptionForHR(result);

            immediateContext = new DeviceContext(immediateContextPtr);
            var d3d11Device = new SharpDX.Direct3D11.Device(d3d11DevicePtr);
            EnableMultithreadProtection(d3d11Device);
            ValidateVideoInterfaces(d3d11Device, immediateContext);
            return d3d11Device;
        }

        private static void EnableMultithreadProtection(SharpDX.Direct3D11.Device device)
        {
            using var multithread = device.QueryInterface<Multithread>();
            var wasProtected = multithread.SetMultithreadProtected(true);
            Debug.WriteLine($"[ScreenShare:DX12] D3D11On12 multithread protection enabled; previously protected={wasProtected}.");
        }

        private static void ValidateVideoInterfaces(
            SharpDX.Direct3D11.Device device,
            DeviceContext immediateContext)
        {
            using var videoDevice = device.QueryInterface<VideoDevice>();
            using var videoContext = immediateContext.QueryInterface<VideoContext>();
            Debug.WriteLine("[ScreenShare:DX12] D3D11On12 video interfaces are available for Media Foundation.");
        }

        public void Dispose()
        {
            try
            {
                _immediateContext?.ClearState();
                _immediateContext?.Flush();
            }
            catch
            {
            }

            _immediateContext?.Dispose();
            _d3d11On12Device?.Dispose();
            _commandQueue?.Dispose();
            _d3d12Device?.Dispose();

            _immediateContext = null;
            _d3d11On12Device = null;
            _commandQueue = null;
            _d3d12Device = null;
        }

        [DllImport("d3d11.dll", ExactSpelling = true)]
        private static extern int D3D11On12CreateDevice(
            IntPtr device,
            int flags,
            [In] FeatureLevel[] featureLevels,
            int featureLevelsCount,
            [In] IntPtr[] commandQueues,
            int commandQueuesCount,
            int nodeMask,
            out IntPtr d3d11Device,
            out IntPtr immediateContext,
            out FeatureLevel chosenFeatureLevel);
    }
}
