using System.Runtime.InteropServices;
using Glint.Player;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Glint;

// Small always-on-top player. The tab's own video window moves in here, so nothing is streamed twice.
// Double-click the video (or close the window) to send it back to Glint.
sealed class MiniPlayerWindow : Window
{
    readonly VideoHost _host;
    readonly IntPtr _hwnd;
    static AppSettings S => Settings.Current;

    public MiniPlayerWindow(string channel, VideoHost host, string icon)
    {
        _host = host;
        Title = $"{channel} · Glint";
        Content = new Grid { Background = new SolidColorBrush(Microsoft.UI.Colors.Black), RequestedTheme = ElementTheme.Dark };
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        AppWindow.SetIcon(icon);
        AppWindow.TitleBar.PreferredTheme = TitleBarTheme.Dark;
        if (AppWindow.Presenter is OverlappedPresenter p)
        {
            p.IsAlwaysOnTop = true;
            p.IsMinimizable = false;
        }
        Place();
        AppWindow.Changed += (_, e) =>
        {
            if (!e.DidSizeChange && !e.DidPositionChange) return;
            Fit();
            S.MiniPlayerBounds = [AppWindow.Position.X, AppWindow.Position.Y, AppWindow.Size.Width, AppWindow.Size.Height];
            Settings.Save();
        };
        host.Detach(_hwnd);
        Fit();
    }

    // Where it was last time, otherwise the bottom right of the screen Glint is on.
    void Place()
    {
        if (S.MiniPlayerBounds is [var x, var y, var w, var h] && w >= 240 && h >= 160)
        {
            var area = DisplayArea.GetFromRect(new Windows.Graphics.RectInt32(x, y, w, h), DisplayAreaFallback.Nearest).WorkArea;
            AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(
                Math.Clamp(x, area.X, Math.Max(area.X, area.X + area.Width - w)), Math.Clamp(y, area.Y, Math.Max(area.Y, area.Y + area.Height - h)), w, h));
            return;
        }
        var work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        int width = 520, height = 330;
        AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(work.X + work.Width - width - 24, work.Y + work.Height - height - 24, width, height));
    }

    void Fit()
    {
        if (GetClientRect(_hwnd, out var r)) _host.Fill(r.Right - r.Left, r.Bottom - r.Top);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr hwnd, out RECT rect);
}
