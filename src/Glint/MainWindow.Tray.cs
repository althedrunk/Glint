using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;

namespace Glint;

// "Keep running in the tray": closing the window hides it to the notification area instead of quitting,
// so go-live alerts keep coming. The tray icon brings it back; its menu is the way to really quit.
public sealed partial class MainWindow
{
    const uint TrayMessage = 0x8000 + 0x47; // WM_APP + 'G'
    const uint TrayId = 1;
    bool _trayIcon, _quitting, _hiddenToTray;
    IntPtr _trayIconHandle;
    TraySubclassProc? _traySubclass;
    uint _taskbarCreated;

    void WireTray()
    {
        _taskbarCreated = RegisterWindowMessageW("TaskbarCreated");
        _traySubclass = TrayWindowProc;
        SetWindowSubclass(_hwnd, _traySubclass, 7, 0);
        UpdateTrayIcon();
    }

    internal void UpdateTrayIcon()
    {
        if (S.CloseToTray && !_trayIcon) AddTrayIcon();
        else if (!S.CloseToTray && _trayIcon && !_hiddenToTray) RemoveTrayIcon();
    }

    // Closing with the option on: stop the video and step out of the way, but keep running.
    internal void HideToTray()
    {
        if (_miniTab != null) BringBackFromMini();
        _hiddenToTray = true;
        StopPlayback();
        if (!_trayIcon) AddTrayIcon();
        AppWindow.Hide();
    }

    void ShowFromTray()
    {
        if (_closing) return;
        bool wasHidden = _hiddenToTray;
        _hiddenToTray = false;
        AppWindow.Show();
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter) presenter.Restore();
        Activate();
        SetForegroundWindow(_hwnd);
        if (!wasHidden) return;
        // Pick the stream(s) back up where they were.
        var tabs = MultiviewActive ? _multiviewSlots.Select(s => s.Tab).OfType<ChannelTab>().Distinct().ToList() : _active != null ? new() { _active } : new();
        foreach (var tab in tabs) ReloadTab(tab);
        if (!S.CloseToTray) RemoveTrayIcon();
    }

    void QuitFromTray()
    {
        _quitting = true;
        Quit();
    }

    void AddTrayIcon()
    {
        if (_trayIconHandle == IntPtr.Zero)
            _trayIconHandle = LoadImageW(IntPtr.Zero, Asset("Glint.ico"), 1, GetSystemMetrics(49), GetSystemMetrics(50), 0x10); // small icon from file
        var data = TrayData();
        data.uFlags = 0x1 | 0x2 | 0x4; // message, icon, tip
        data.uCallbackMessage = TrayMessage;
        data.hIcon = _trayIconHandle;
        data.szTip = "Glint";
        _trayIcon = Shell_NotifyIconW(0, ref data); // NIM_ADD
    }

    void RemoveTrayIcon()
    {
        if (!_trayIcon) return;
        var data = TrayData();
        Shell_NotifyIconW(2, ref data); // NIM_DELETE
        _trayIcon = false;
    }

    NotifyIconData TrayData() => new() { cbSize = Marshal.SizeOf<NotifyIconData>(), hWnd = _hwnd, uID = TrayId, szTip = "", szInfo = "", szInfoTitle = "" };

    IntPtr TrayWindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, nuint id, nuint data)
    {
        if (message == TrayMessage)
        {
            switch ((uint)lParam & 0xFFFF)
            {
                case 0x0202: // WM_LBUTTONUP
                case 0x0203: // WM_LBUTTONDBLCLK
                    _dispatcher.TryEnqueue(ShowFromTray);
                    break;
                case 0x0205: // WM_RBUTTONUP
                    _dispatcher.TryEnqueue(ShowTrayMenu);
                    break;
            }
            return IntPtr.Zero;
        }
        if (message == _taskbarCreated && _trayIcon)
        {
            _trayIcon = false; // Explorer restarted and forgot the icon
            AddTrayIcon();
        }
        return DefSubclassProc(hwnd, message, wParam, lParam);
    }

    void ShowTrayMenu()
    {
        var menu = CreatePopupMenu();
        AppendMenuW(menu, 0, 1, "Open Glint");
        AppendMenuW(menu, 0x800, 0, null); // separator
        AppendMenuW(menu, 0, 2, "Quit Glint");
        GetCursorPos(out var point);
        SetForegroundWindow(_hwnd); // otherwise the menu doesn't close when you click elsewhere
        int command = TrackPopupMenuEx(menu, 0x0100 | 0x0002, point.X, point.Y, _hwnd, IntPtr.Zero); // return the command, right button
        DestroyMenu(menu);
        if (command == 1) ShowFromTray();
        else if (command == 2) QuitFromTray();
    }

    delegate IntPtr TraySubclassProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, nuint id, nuint data);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct NotifyIconData
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)] struct TrayPoint { public int X, Y; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern bool Shell_NotifyIconW(uint message, ref NotifyIconData data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint RegisterWindowMessageW(string name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr LoadImageW(IntPtr instance, string name, uint type, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool AppendMenuW(IntPtr menu, uint flags, nuint id, string? text);
    [DllImport("user32.dll")] static extern int TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr hwnd, IntPtr parameters);
    [DllImport("user32.dll")] static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out TrayPoint point);
    [DllImport("comctl32.dll")] static extern bool SetWindowSubclass(IntPtr hwnd, TraySubclassProc proc, nuint id, nuint data);
    [DllImport("comctl32.dll")] static extern IntPtr DefSubclassProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
}
