using System.Runtime.InteropServices;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Glint.Player;

// The transparent brush also needs DWM alpha composition enabled on the HWND.
sealed partial class TransparentBackdrop : SystemBackdrop
{
    static Windows.UI.Composition.Compositor? _compositor;
    static IntPtr _queueController;
    Windows.UI.Composition.CompositionBrush? _brush;

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop target, XamlRoot xamlRoot)
    {
        base.OnTargetConnected(target, xamlRoot);
        EnsureSystemDispatcherQueue();
        _compositor ??= new Windows.UI.Composition.Compositor();
        target.SystemBackdrop = _brush = _compositor.CreateColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop target)
    {
        target.SystemBackdrop = null;
        _brush?.Dispose();
        _brush = null;
        base.OnTargetDisconnected(target);
    }

    protected override void OnDefaultSystemBackdropConfigurationChanged(ICompositionSupportsSystemBackdrop target, XamlRoot xamlRoot)
    {
        // The transparent brush does not depend on theme or activation changes.
    }

    internal static void ConfigureWindow(IntPtr hwnd)
    {
        var margins = new Margins();
        Marshal.ThrowExceptionForHR(DwmExtendFrameIntoClientArea(hwnd, ref margins));
        var region = CreateRectRgn(-2, -2, -1, -1);
        try
        {
            var blur = new BlurBehind { Flags = 3, Enable = 1, Region = region };
            Marshal.ThrowExceptionForHR(DwmEnableBlurBehindWindow(hwnd, ref blur));
        }
        finally { DeleteObject(region); }
    }

    internal static IntPtr ClearBackground(IntPtr hwnd, IntPtr dc)
    {
        GetClientRect(hwnd, out var rect);
        FillRect(dc, ref rect, GetStockObject(4)); // BLACK_BRUSH has zero alpha
        return new IntPtr(1);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Margins { public int Left, Right, Top, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    struct BlurBehind { public uint Flags; public int Enable; public IntPtr Region; public int Transition; }
    [StructLayout(LayoutKind.Sequential)]
    struct Rect { public int Left, Top, Right, Bottom; }

    [DllImport("dwmapi.dll")] static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);
    [DllImport("dwmapi.dll")] static extern int DwmEnableBlurBehindWindow(IntPtr hwnd, ref BlurBehind blur);
    [DllImport("gdi32.dll")] static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr handle);
    [DllImport("gdi32.dll")] static extern IntPtr GetStockObject(int index);
    [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] static extern int FillRect(IntPtr dc, ref Rect rect, IntPtr brush);

    static void EnsureSystemDispatcherQueue()
    {
        if (Windows.System.DispatcherQueue.GetForCurrentThread() != null || _queueController != IntPtr.Zero) return;
        var options = new DispatcherQueueOptions
        {
            dwSize = Marshal.SizeOf<DispatcherQueueOptions>(),
            threadType = 2,    // DQTYPE_THREAD_CURRENT
            apartmentType = 2, // DQTAT_COM_STA
        };
        CreateDispatcherQueueController(options, out _queueController);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct DispatcherQueueOptions
    {
        public int dwSize;
        public int threadType;
        public int apartmentType;
    }

    [DllImport("CoreMessaging.dll")]
    static extern int CreateDispatcherQueueController(DispatcherQueueOptions options, out IntPtr controller);
}
