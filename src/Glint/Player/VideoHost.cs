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
    static readonly Dictionary<IntPtr, VideoHost> Hosts = new();

    public IntPtr Hwnd { get; }
    public event Action<string>? Input;
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
        Hosts[Hwnd] = this;
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

    public void Hide() => SetBounds(_bounds.X, _bounds.Y, _bounds.W, _bounds.H, false);

    string _cutoutKey = "";

    // XAML popups (menus, flyouts, tooltips) draw underneath this native window, so punch holes in it where
    // they are rather than hiding the whole video. Rects are in the parent's client pixels.
    public void SetCutouts(IReadOnlyList<(int X, int Y, int W, int H)> popups)
    {
        var local = new List<(int X, int Y, int W, int H)>();
        if (_visible)
            foreach (var p in popups)
            {
                int x1 = Math.Max(p.X, _bounds.X), y1 = Math.Max(p.Y, _bounds.Y);
                int x2 = Math.Min(p.X + p.W, _bounds.X + _bounds.W), y2 = Math.Min(p.Y + p.H, _bounds.Y + _bounds.H);
                if (x2 > x1 && y2 > y1) local.Add((x1 - _bounds.X, y1 - _bounds.Y, x2 - x1, y2 - y1));
            }
        var key = local.Count == 0 ? "" : $"{_bounds.W}x{_bounds.H}:" + string.Join(";", local);
        if (key == _cutoutKey) return;
        _cutoutKey = key;
        if (local.Count == 0)
        {
            SetWindowRgn(Hwnd, IntPtr.Zero, true);
            return;
        }
        var region = CreateRectRgn(0, 0, _bounds.W, _bounds.H);
        foreach (var r in local)
        {
            var hole = CreateRectRgn(r.X, r.Y, r.X + r.W, r.Y + r.H);
            CombineRgn(region, region, hole, 4); // RGN_DIFF
            DeleteObject(hole);
        }
        SetWindowRgn(Hwnd, region, true); // the window owns the region from here
    }

    // Re-asserts z-order above the XAML island without moving.
    public void BringToTop()
    {
        if (_visible) SetWindowPos(Hwnd, HWND_TOP, _bounds.X, _bounds.Y, _bounds.W, _bounds.H, SWP_NOACTIVATE);
    }

    static IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        // With wid, mpv doesn't always receive input sent to the host HWND.
        if (Hosts.TryGetValue(hwnd, out var host))
        {
            switch (msg)
            {
                case 0x0200: host.Input?.Invoke("move"); break;
                case 0x0201: host.Input?.Invoke("click"); break;
                case 0x0203: host.Input?.Invoke("dblclick"); return IntPtr.Zero;
                case 0x020A:
                    return IntPtr.Zero;
            }
        }
        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        Hosts.Remove(Hwnd);
        DestroyWindow(Hwnd);
    }

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

    [DllImport("gdi32.dll")] static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] static extern int CombineRgn(IntPtr dest, IntPtr a, IntPtr b, int mode);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr handle);
    [DllImport("user32.dll")] static extern int SetWindowRgn(IntPtr hwnd, IntPtr region, bool redraw);

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
