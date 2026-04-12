using System;
using System.Runtime.InteropServices;
using Zink.Interop;

namespace Zink.Services.Recording
{
    public sealed class HotkeyService : IDisposable
    {
        private const int SaveLast45HotkeyId = 5001;
        private const uint VkR = 0x52;

        private readonly IntPtr _hwnd;
        private IntPtr _oldWndProc;
        private WndProcDelegate? _newWndProc;

        public HotkeyService(IntPtr hwnd)
        {
            _hwnd = hwnd;
        }

        public void Initialize()
        {
            _newWndProc = CustomWndProc;
            _oldWndProc = NativeMethods.SetWindowLongPtr(
                _hwnd,
                NativeMethods.GWLP_WNDPROC,
                Marshal.GetFunctionPointerForDelegate(_newWndProc));

            NativeMethods.RegisterHotKey(
                _hwnd,
                SaveLast45HotkeyId,
                NativeMethods.MOD_SHIFT | NativeMethods.MOD_NOREPEAT,
                VkR);
        }

        private IntPtr CustomWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            if (msg == NativeMethods.WM_HOTKEY && wParam.ToInt32() == SaveLast45HotkeyId)
            {
                _ = RecordingManager.Instance.SaveLast45SecondsAsync();
            }

            return NativeMethods.CallWindowProc(_oldWndProc, hWnd, msg, wParam, lParam);
        }

        public void Dispose()
        {
            NativeMethods.UnregisterHotKey(_hwnd, SaveLast45HotkeyId);

            if (_oldWndProc != IntPtr.Zero)
            {
                NativeMethods.SetWindowLongPtr(_hwnd, NativeMethods.GWLP_WNDPROC, _oldWndProc);
                _oldWndProc = IntPtr.Zero;
            }

            _newWndProc = null;
        }

        private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    }
}