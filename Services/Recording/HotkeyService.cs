using System;
using System.Runtime.InteropServices;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using Windows.Storage;
using Windows.System;
using Zink.Interop;

namespace Zink.Services.Recording
{
    public sealed class HotkeyService : IDisposable
    {
        private const int SaveLast45HotkeyId = 5001;
        private const uint VkZ = 0x5A;

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
                NativeMethods.MOD_CONTROL | NativeMethods.MOD_NOREPEAT,
                VkZ);
        }

        private IntPtr CustomWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            if (msg == NativeMethods.WM_HOTKEY && wParam.ToInt32() == SaveLast45HotkeyId)
            {
                _ = SaveLast45SecondsFromHotkeyAsync();
            }

            return NativeMethods.CallWindowProc(_oldWndProc, hWnd, msg, wParam, lParam);
        }

        private static async Task SaveLast45SecondsFromHotkeyAsync()
        {
            try
            {
                ShowPopup("Zink Replay", "Recording the last 45 seconds.");

                string? savedPath = await RecordingManager.Instance.SaveLast45SecondsAsync();
                if (string.IsNullOrWhiteSpace(savedPath))
                    return;

                ShowPopup("Zink Replay", "Recording has finished. Open here.");
                await OpenSavedVideoAsync(savedPath);
            }
            catch (Exception ex)
            {
                ShowPopup("Zink Replay Error", ex.Message);
            }
        }

        private static async Task OpenSavedVideoAsync(string savedPath)
        {
            try
            {
                if (!File.Exists(savedPath))
                    return;

                StorageFile file = await StorageFile.GetFileFromPathAsync(savedPath);
                await Launcher.LaunchFileAsync(file);
            }
            catch
            {
            }
        }

        private static void ShowPopup(string title, string message)
        {
            try
            {
                var notification = new AppNotificationBuilder()
                    .AddText(title)
                    .AddText(message)
                    .BuildNotification();

                AppNotificationManager.Default.Show(notification);
            }
            catch
            {
            }
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
