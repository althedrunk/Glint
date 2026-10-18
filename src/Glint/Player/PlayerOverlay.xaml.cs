using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using WinRT.Interop;

namespace Glint.Player;

// A separate owned window lets XAML controls draw over mpv's native video window.
public sealed partial class PlayerOverlay : Window
{
    readonly IntPtr _hwnd;
    readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _hideTimer;
    readonly SubclassProc _subclassProc;
    readonly List<IntPtr> _inputWindows = new();
    bool _shown;

    public bool IsPointerInside
    {
        get
        {
            if (!_shown || !GetCursorPos(out var point) || !GetWindowRect(_hwnd, out var rect)) return false;
            return point.X >= rect.Left && point.X < rect.Right && point.Y >= rect.Top && point.Y < rect.Bottom;
        }
    }

    // Mouse moved over the overlay (keeps it visible).
    public event Action? Activity;
    public event Action<int>? Wheel;
    public event Action? DoubleClicked;
    // A press on the overlay that no button or slider took (the gradient / empty space).
    public event Action? BackgroundPressed;

    public PlayerOverlay(IntPtr owner)
    {
        InitializeComponent();
        SystemBackdrop = new TransparentBackdrop();
        _hwnd = WindowNative.GetWindowHandle(this);
        Activated += (_, e) =>
        {
            // WinUI's input island can activate its parent even with WS_EX_NOACTIVATE.
            if (e.WindowActivationState != WindowActivationState.Deactivated) SetForegroundWindow(owner);
        };

        if (AppWindow.Presenter is OverlappedPresenter p)
        {
            p.IsResizable = false;
            p.IsMaximizable = false;
            p.IsMinimizable = false;
            p.SetBorderAndTitleBar(false, false);
        }
        AppWindow.IsShownInSwitchers = false;

        var ex = GetWindowLongPtrW(_hwnd, GWL_EXSTYLE);
        SetWindowLongPtrW(_hwnd, GWL_EXSTYLE, ex | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
        SetWindowLongPtrW(_hwnd, GWLP_HWNDPARENT, owner); // owned: stays above the main window, minimises with it
        _subclassProc = WindowProc;
        SetWindowSubclass(_hwnd, _subclassProc, 1, 0);
        Root.Loaded += (_, _) => EnumChildWindows(_hwnd, (child, _) =>
        {
            if (!_inputWindows.Contains(child))
            {
                SetWindowSubclass(child, _subclassProc, 1, 0);
                _inputWindows.Add(child);
            }
            return true;
        }, IntPtr.Zero);
        Closed += (_, _) =>
        {
            foreach (var child in _inputWindows) RemoveWindowSubclass(child, _subclassProc, 1);
            RemoveWindowSubclass(_hwnd, _subclassProc, 1);
        };
        SetWindowLongPtrW(_hwnd, -16, (GetWindowLongPtrW(_hwnd, -16) & ~0x00CF0000L) | 0x80000000L);
        SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0, 0x0037);
        TransparentBackdrop.ConfigureWindow(_hwnd);
        int noRound = 1, noBorder = unchecked((int)0xFFFFFFFE);
        DwmSetWindowAttribute(_hwnd, 33, ref noRound, 4);  // DWMWA_WINDOW_CORNER_PREFERENCE = DONOTROUND
        DwmSetWindowAttribute(_hwnd, 34, ref noBorder, 4); // DWMWA_BORDER_COLOR = none

        Root.OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(160) };
        Root.PointerEntered += (_, _) => Activity?.Invoke();
        Root.PointerMoved += (_, _) => Activity?.Invoke();
        Root.PointerPressed += (_, _) => BackgroundPressed?.Invoke();
        VolumeSlider.VolumeWheel += delta => Wheel?.Invoke(delta);
        Root.DoubleTapped += (_, e) =>
        {
            // only on empty space, not when double-clicking a button
            if (e.OriginalSource is FrameworkElement fe && FindParent<ButtonBase>(fe) == null && FindParent<Slider>(fe) == null)
                DoubleClicked?.Invoke();
        };

        _hideTimer = DispatcherQueue.CreateTimer();
        _hideTimer.Interval = TimeSpan.FromMilliseconds(180);
        _hideTimer.IsRepeating = false;
        _hideTimer.Tick += (_, _) => { if (!_shown) AppWindow.Hide(); };
        Closed += (_, _) => _hideTimer.Stop();
    }

    public bool IsShown => _shown;
    public bool IsCompact { get; private set; }

    public void ConfigureForWidth(double width)
    {
        bool compact = width < 720;
        IsCompact = compact;
        Grid.SetRow(RightControls, compact ? 1 : 0);
        Grid.SetColumn(RightControls, compact ? 0 : 2);
        Grid.SetColumnSpan(RightControls, compact ? 3 : 1);
        RightControls.HorizontalAlignment = HorizontalAlignment.Right;
        VolumeSlider.Width = compact ? 64 : 96;
        QualityText.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        StatusText.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
    }

    public void Place(int x, int y, int width, int height)
    {
        var rect = new Windows.Graphics.RectInt32(x, y, Math.Max(1, width), Math.Max(1, height));
        if (AppWindow.Position.X != x || AppWindow.Position.Y != y || AppWindow.Size.Width != rect.Width || AppWindow.Size.Height != rect.Height)
            AppWindow.MoveAndResize(rect);
    }

    public void ShowOverlay()
    {
        _hideTimer.Stop();
        if (!_shown)
        {
            _shown = true;
            Root.Opacity = 0;
            AppWindow.Show(false);
        }
        Root.Opacity = 1;
    }

    // Fades out, then hides the window so clicks reach the video again.
    public void HideOverlay(bool immediately = false)
    {
        if (!_shown)
        {
            if (immediately) { _hideTimer.Stop(); AppWindow.Hide(); }
            return;
        }
        _shown = false;
        Root.Opacity = 0;
        if (immediately) AppWindow.Hide();
        else _hideTimer.Start();
    }

    static T? FindParent<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d != null)
        {
            if (d is T t) return t;
            d = VisualTreeHelper.GetParent(d);
        }
        return null;
    }

    IntPtr WindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, nuint id, nuint data)
    {
        if (message is 0x0021 or 0x024B) return new IntPtr(3); // Mouse/pointer activation: no activate
        if (hwnd == _hwnd && message == 0x0014) return TransparentBackdrop.ClearBackground(hwnd, wParam);
        if (hwnd == _hwnd && message == 0x031E) TransparentBackdrop.ConfigureWindow(hwnd);
        return DefSubclassProc(hwnd, message, wParam, lParam);
    }

    delegate IntPtr SubclassProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, nuint id, nuint data);
    delegate bool EnumProc(IntPtr hwnd, IntPtr data);

    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr hwnd, EnumProc proc, IntPtr data);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int w, int h, uint flags);
    [DllImport("comctl32.dll")] static extern bool SetWindowSubclass(IntPtr hwnd, SubclassProc proc, nuint id, nuint data);
    [DllImport("comctl32.dll")] static extern bool RemoveWindowSubclass(IntPtr hwnd, SubclassProc proc, nuint id);
    [DllImport("comctl32.dll")] static extern IntPtr DefSubclassProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    const int GWL_EXSTYLE = -20, GWLP_HWNDPARENT = -8;
    const long WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOOLWINDOW = 0x00000080;

    [StructLayout(LayoutKind.Sequential)] struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

    [DllImport("user32.dll")] static extern long GetWindowLongPtrW(IntPtr hwnd, int index);
    [DllImport("user32.dll")] static extern long SetWindowLongPtrW(IntPtr hwnd, int index, long value);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] static extern IntPtr SetWindowLongPtrW(IntPtr hwnd, int index, IntPtr value);
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}
