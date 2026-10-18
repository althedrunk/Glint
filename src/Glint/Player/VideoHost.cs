using System.Runtime.InteropServices;

namespace Glint.Player;

// A plain Win32 child window that mpv renders into. It's positioned over a XAML placeholder element;
// native rendering avoids any browser/compositor copy of the video frames.
public sealed partial class VideoHost : IDisposable
{
    const string ClassName = "GlintVideoHost";
    const uint WS_CHILD = 0x40000000, WS_CLIPCHILDREN = 0x02000000, WS_CLIPSIBLINGS = 0x04000000;
    const uint SWP_NOACTIVATE = 0x0010, SWP_SHOWWINDOW = 0x0040, SWP_HIDEWINDOW = 0x0080, SWP_NOZORDER = 0x0004;
    static readonly IntPtr HWND_TOP = IntPtr.Zero;

    delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    static WndProcDelegate? _wndProc;
    static bool _registered;

    public IntPtr Hwnd { get; }
    bool _visible;
    (int X, int Y, int W, int H) _bounds;

    public VideoHost(IntPtr parent)
    {
        var hInstance = GetModuleHandleW(IntPtr.Zero);
        if (!_registered)
        {
            _wndProc = WndProc;
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
                style = 0x0008, // CS_DBLCLKS
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
                hInstance = hInstance,
                hCursor = LoadCursorW(IntPtr.Zero, 32512), // IDC_ARROW
                hbrBackground = GetStockObject(4), // BLACK_BRUSH
                lpszClassName = ClassName,
            };
            if (RegisterClassExW(ref wc) == 0) throw new InvalidOperationException("RegisterClassEx failed");
            _registered = true;
        }
        Hwnd = CreateWindowExW(0, ClassName, "", WS_CHILD | WS_CLIPCHILDREN | WS_CLIPSIBLINGS,
            0, 0, 1, 1, parent, IntPtr.Zero, hInstance, IntPtr.Zero);
        if (Hwnd == IntPtr.Zero) throw new InvalidOperationException("CreateWindowEx failed");
    }

    public void SetBounds(int x, int y, int w, int h, bool visible)
    {
        visible &= w > 0 && h > 0;
        if (_bounds == (x, y, w, h) && _visible == visible) return;
        _bounds = (x, y, w, h);
        _visible = visible;
        SetWindowPos(Hwnd, HWND_TOP, x, y, Math.Max(1, w), Math.Max(1, h),
            SWP_NOACTIVATE | (visible ? SWP_SHOWWINDOW : SWP_HIDEWINDOW));
    }

    // Re-asserts z-order above the XAML island without moving.
    public void BringToTop()
    {
        if (_visible) SetWindowPos(Hwnd, HWND_TOP, _bounds.X, _bounds.Y, _bounds.W, _bounds.H, SWP_NOACTIVATE);
    }

    static IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam) => DefWindowProcW(hwnd, msg, wParam, lParam);

    public void Dispose() => DestroyWindow(Hwnd);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WNDCLASSEXW
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
    static extern ushort RegisterClassExW(ref WNDCLASSEXW wc);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial IntPtr CreateWindowExW(uint exStyle, string className, string windowName, uint style,
        int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int w, int h, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyWindow(IntPtr hwnd);

    [LibraryImport("user32.dll")]
    private static partial IntPtr DefWindowProcW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [LibraryImport("user32.dll")]
    private static partial IntPtr LoadCursorW(IntPtr instance, int name);

    [LibraryImport("gdi32.dll")]
    private static partial IntPtr GetStockObject(int obj);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr GetModuleHandleW(IntPtr name);
}
