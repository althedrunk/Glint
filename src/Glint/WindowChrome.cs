using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using WinRT.Interop;

namespace Glint;

static class WindowChrome
{
    public static void UseDarkTitleBar(Window window)
    {
        int dark = 1;
        DwmSetWindowAttribute(WindowNative.GetWindowHandle(window), 20, ref dark, sizeof(int)); // DWMWA_USE_IMMERSIVE_DARK_MODE
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
