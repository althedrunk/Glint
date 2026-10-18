using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;

namespace Glint;

// twitch.tv's own login page in a small browser window. When the site sets its auth-token cookie we take
// it, check it with Twitch, then clear the window's cookies so the session only lives in Glint's settings.
public sealed partial class SignInWindow : Window
{
    readonly WebView2 _web = new();
    readonly TextBlock _status = new() { Margin = new Thickness(14, 8, 14, 8), FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
    readonly TaskCompletionSource<string?> _result = new();
    Microsoft.UI.Dispatching.DispatcherQueueTimer? _poll;
    bool _done;

    public SignInWindow()
    {
        Title = "Connect twitch.tv";
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "Glint.ico"));
        AppWindow.Resize(new Windows.Graphics.SizeInt32(540, 780));
        if (DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary) is { } area)
            AppWindow.Move(new Windows.Graphics.PointInt32(area.WorkArea.X + (area.WorkArea.Width - 540) / 2, area.WorkArea.Y + (area.WorkArea.Height - 780) / 2));

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition());
        _status.Text = "Sign in to Twitch below. Glint only keeps the session; it never sees your password.";
        Grid.SetRow(_web, 1);
        grid.Children.Add(_status);
        grid.Children.Add(_web);
        Content = grid;
        WindowChrome.UseDarkTitleBar(this);

        Closed += (_, _) => { _poll?.Stop(); _result.TrySetResult(null); };
        _ = StartAsync();
    }

    // The connected login, or null if the window was closed first.
    public Task<string?> Result => _result.Task;

    async Task StartAsync()
    {
        try
        {
            var folder = Path.Combine(Paths.LocalData, "SignIn");
            var env = await CoreWebView2Environment.CreateWithOptionsAsync(null, folder, new CoreWebView2EnvironmentOptions());
            await _web.EnsureCoreWebView2Async(env);
            _web.CoreWebView2.CookieManager.DeleteAllCookies(); // always start signed out
            _web.CoreWebView2.Navigate("https://www.twitch.tv/login");

            _poll = DispatcherQueue.CreateTimer();
            _poll.Interval = TimeSpan.FromSeconds(1);
            _poll.Tick += async (_, _) => await CheckCookiesAsync();
            _poll.Start();
        }
        catch (Exception ex)
        {
            Log.Error("Sign-in window", ex);
            _status.Text = "Couldn't open the sign-in page. Glint needs the Microsoft Edge WebView2 runtime.";
        }
    }

    async Task CheckCookiesAsync()
    {
        if (_done || _web.CoreWebView2 == null) return;
        var cookies = await _web.CoreWebView2.CookieManager.GetCookiesAsync("https://www.twitch.tv");
        var token = cookies.FirstOrDefault(c => c.Name == "auth-token")?.Value;
        if (string.IsNullOrEmpty(token)) return;

        _done = true;
        _poll?.Stop();
        _status.Text = "Signed in, checking with Twitch…";
        var deviceId = cookies.FirstOrDefault(c => c.Name == "unique_id")?.Value;
        var login = await TwitchSession.ConnectAsync(Uri.UnescapeDataString(token), deviceId);
        _web.CoreWebView2.CookieManager.DeleteAllCookies();
        if (login == null)
        {
            _done = false;
            _status.Text = "Twitch didn't accept that session. Try signing in again.";
            _web.CoreWebView2.Navigate("https://www.twitch.tv/login");
            _poll?.Start();
            return;
        }
        _result.TrySetResult(login);
        Close();
    }
}
