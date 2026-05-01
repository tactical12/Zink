namespace Zink.Services.NativeCalling
{
    internal static class NativeMethods
    {
        public const int SM_XVIRTUALSCREEN = 76;
        public const int SM_YVIRTUALSCREEN = 77;
        public const int SM_CXVIRTUALSCREEN = 78;
        public const int SM_CYVIRTUALSCREEN = 79;
        public const int COLORONCOLOR = 3;
        public const int SRCCOPY = 0x00CC0020;
        public const int CAPTUREBLT = 0x40000000;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern int GetSystemMetrics(int nIndex);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern System.IntPtr GetDC(System.IntPtr hWnd);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern int ReleaseDC(System.IntPtr hWnd, System.IntPtr hDc);

        [System.Runtime.InteropServices.DllImport("gdi32.dll")]
        public static extern int SetStretchBltMode(System.IntPtr hdc, int mode);

        [System.Runtime.InteropServices.DllImport("gdi32.dll", SetLastError = true)]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        public static extern bool StretchBlt(
            System.IntPtr hdcDest,
            int xDest,
            int yDest,
            int wDest,
            int hDest,
            System.IntPtr hdcSrc,
            int xSrc,
            int ySrc,
            int wSrc,
            int hSrc,
            int rop);

        [System.Runtime.InteropServices.DllImport("winmm.dll")]
        public static extern uint timeBeginPeriod(uint uPeriod);

        [System.Runtime.InteropServices.DllImport("winmm.dll")]
        public static extern uint timeEndPeriod(uint uPeriod);
    }
}
