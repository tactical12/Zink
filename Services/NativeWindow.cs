using System;
using System.Runtime.InteropServices;

namespace Zink.Services
{
    internal delegate IntPtr WindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    internal sealed class NativeWindow : IDisposable
    {
        private const string WindowClassName = "ZinkTrayWindowClass";

        private readonly WindowProc _wndProcDelegate;
        private IntPtr _hwnd;
        private ushort _classAtom;
        private bool _disposed;

        public IntPtr Handle => _hwnd;

        public event Func<IntPtr, uint, IntPtr, IntPtr, IntPtr>? MessageReceived;

        public NativeWindow()
        {
            _wndProcDelegate = WndProc;

            var hInstance = GetModuleHandle(null);

            WNDCLASSEX wc = new WNDCLASSEX();
            wc.cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>();
            wc.lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate);
            wc.hInstance = hInstance;
            wc.lpszClassName = WindowClassName;

            _classAtom = RegisterClassEx(ref wc);
            if (_classAtom == 0)
            {
                int error = Marshal.GetLastWin32Error();
                const int ERROR_CLASS_ALREADY_EXISTS = 1410;
                if (error != ERROR_CLASS_ALREADY_EXISTS)
                    throw new InvalidOperationException($"RegisterClassEx failed: {error}");
            }

            _hwnd = CreateWindowEx(
                0,
                WindowClassName,
                "",
                0,
                0, 0, 0, 0,
                HWND_MESSAGE,
                IntPtr.Zero,
                hInstance,
                IntPtr.Zero);

            if (_hwnd == IntPtr.Zero)
                throw new InvalidOperationException($"CreateWindowEx failed: {Marshal.GetLastWin32Error()}");
        }

        private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            if (MessageReceived != null)
            {
                foreach (Func<IntPtr, uint, IntPtr, IntPtr, IntPtr> handler in MessageReceived.GetInvocationList())
                {
                    IntPtr result = handler(hWnd, msg, wParam, lParam);
                    if (result != IntPtr.Zero)
                        return result;
                }
            }

            return DefWindowProc(hWnd, msg, wParam, lParam);
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;

            if (_hwnd != IntPtr.Zero)
            {
                DestroyWindow(_hwnd);
                _hwnd = IntPtr.Zero;
            }
        }

        private static readonly IntPtr HWND_MESSAGE = new IntPtr(-3);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WNDCLASSEX
        {
            public uint cbSize;
            public uint style;
            public IntPtr lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;
            public string? lpszMenuName;
            public string lpszClassName;
            public IntPtr hIconSm;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateWindowEx(
            int dwExStyle,
            string lpClassName,
            string lpWindowName,
            int dwStyle,
            int x,
            int y,
            int nWidth,
            int nHeight,
            IntPtr hWndParent,
            IntPtr hMenu,
            IntPtr hInstance,
            IntPtr lpParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string? lpModuleName);
    }
}