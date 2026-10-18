using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Markup;
using Microsoft.Graphics.Canvas.Text;
using Windows.System;
using WinRT.Interop;

namespace Glint;

public sealed partial class SettingsWindow : Window
{
    readonly MainWindow _window;
    readonly StackPanel _account = new() { Spacing = 6 };
    CancellationTokenSource? _loginCts;
    SignInWindow? _signInWindow;
    bool _closed;
    static readonly Lazy<List<string>> ChatFonts = new(GetChatFonts);

    static AppSettings S => Settings.Current;
    static Brush Subtle => (Brush)Application.Current.Resources["GlintSubtleTextBrush"];

    public SettingsWindow(MainWindow window, IntPtr owner)
    {
        _window = window;
        Title = "Settings - Glint";
        SystemBackdrop = new MicaBackdrop();
        Closed += (_, _) => { _closed = true; _loginCts?.Cancel(); _signInWindow?.Close(); };

        BuildShell();

        var hwnd = WindowNative.GetWindowHandle(this);
        SetWindowLongPtrW(hwnd, -8, owner);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "Glint.ico"));
        WindowChrome.UseDarkTitleBar(this);
        double scale = (window.Content as FrameworkElement)?.XamlRoot?.RasterizationScale ?? 1;
        var work = DisplayArea.GetFromWindowId(window.AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        int width = Math.Min((int)Math.Round(860 * scale), work.Width), height = Math.Min((int)Math.Round(820 * scale), work.Height);
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = Math.Min(width, (int)Math.Round(760 * scale));
            presenter.PreferredMinimumHeight = Math.Min(height, (int)Math.Round(400 * scale));
        }
        int x = Math.Clamp(window.AppWindow.Position.X + (int)Math.Round(24 * scale), work.X, work.X + work.Width - width);
        int y = Math.Clamp(window.AppWindow.Position.Y + (window.AppWindow.Size.Height - height) / 2, work.Y, work.Y + work.Height - height);
        AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(x, y, width, height));
    }

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    static extern IntPtr SetWindowLongPtrW(IntPtr hwnd, int index, IntPtr value);

    static List<string> GetChatFonts()
    {
        var symbols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var set = CanvasFontSet.GetSystemFontSet();
        foreach (var face in set.Fonts)
        {
            using (face)
                if (face.IsSymbolFont)
                    foreach (var name in face.FamilyNames.Values) symbols.Add(name);
        }
        string[] nonText = ["Wingdings", "Webdings", "Dingbat", "Symbol", "Emoji", "Icon", "Math", "Marlett", "FontAwesome", "Font Awesome", "MDL2"];
        return CanvasTextFormat.GetSystemFontFamilies()
            .Where(name => !symbols.Contains(name) && !nonText.Any(part => name.Contains(part, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    static TextBlock Heading(string text, bool first = false) => new()
    {
        Text = text, FontSize = 14, FontWeight = FontWeights.SemiBold, Margin = new Thickness(2, first ? 0 : 18, 0, 2),
    };

    internal static Border IgnoreWordsRow(MainWindow window)
    {
        var box = new TextBox { Text = string.Join(Environment.NewLine, S.IgnoredWords), AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            MinHeight = 80, MaxHeight = 160, PlaceholderText = "One word or phrase per line" };
        AutomationProperties.SetName(box, "Ignored words");
        box.TextChanged += (_, _) =>
        {
            S.IgnoredWords = Chat.ChatFilters.ParseWords(box.Text);
            Settings.Save();
            window.OnChatPreferencesChanged();
        };
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock { Text = "Ignored words", FontSize = 14 });
        panel.Children.Add(Note("Hide matching messages in every chat, thread and mentions window. Matches whole words or phrases, ignoring case. Separate with lines or commas."));
        panel.Children.Add(box);
        return Card(panel);
    }

    static Border Card(UIElement content) => new()
    {
        Child = content,
        Padding = new Thickness(16, 12, 16, 12),
        CornerRadius = new CornerRadius(6),
        BorderThickness = new Thickness(1),
        Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
        BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
    };

    // One settings line: label (and optional description) on the left, the control on the right.
    static Border Row(string title, string? description, FrameworkElement control, bool bold = false)
    {
        var grid = new Grid { ColumnSpacing = 16 };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
        text.Children.Add(new TextBlock { Text = title, FontSize = 14, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal });
        if (description != null) text.Children.Add(Note(description));
        control.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(control, 1);
        grid.Children.Add(text);
        grid.Children.Add(control);
        return Card(grid);
    }

    static Border ToggleRow(string title, string? description, bool value, Action<bool> set)
    {
        var t = new ToggleSwitch { IsOn = value, OnContent = "", OffContent = "", MinWidth = 0 };
        AutomationProperties.SetName(t, title);
        t.Toggled += (_, _) => { set(t.IsOn); Settings.Save(); };
        return Row(title, description, t);
    }

    static Border ListRow(string title, string description, string placeholder, List<string> value, Action<List<string>> set)
    {
        var box = new TextBox { PlaceholderText = placeholder, Text = string.Join(", ", value) };
        AutomationProperties.SetName(box, title);
        box.TextChanged += (_, _) => { set(Chat.Highlights.ParseList(box.Text)); Settings.Save(); };
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new StackPanel { Spacing = 2, Children = { new TextBlock { Text = title, FontSize = 14 }, Note(description) } });
        panel.Children.Add(box);
        return Card(panel);
    }

    static TextBlock Note(string text) => new() { Text = text, FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = Subtle };



    // One account section. Signing in on twitch.tv is the main way in; the API login is only for clips
    // (and for anyone who'd rather not use the website sign-in).
    void BuildAccount()
    {
        if (_closed) return;
        _account.Children.Clear();
        if (TwitchSession.IsConnected)
        {
            var signOut = new Button { Content = "Sign out" };
            signOut.Click += (_, _) =>
            {
                TwitchSession.Disconnect();
                TwitchAuth.Logout();
                _window.OnSessionChanged();
                BuildAccount();
            };
            _account.Children.Add(Row(TwitchSession.Login ?? "Signed in", "Signed in on twitch.tv", signOut, bold: true));
            _account.Children.Add(ToggleRow("Count watch time",
                "Sends twitch.tv's once-a-minute watching signal for live streams on screen, for watch streaks and channel points.",
                S.CountWatchTime, on => S.CountWatchTime = on));
            if (S.AccessToken != null && TwitchAuth.CanClip)
            {
                var off = new Button { Content = "Turn off" };
                off.Click += (_, _) => { TwitchAuth.Logout(); BuildAccount(); };
                _account.Children.Add(Row("Clips", "On. Press Alt+X or the scissors on the player.", off));
            }
            else
                _account.Children.Add(Card(ApiLoginPanel("Clips",
                    "Making clips goes through Twitch's official API, which needs a one-time extra approval.", "Turn on")));
            return;
        }

        if (S.AccessToken != null && S.UserLogin != null)
        {
            // Logged in the older way: everything works except the website-only extras.
            var logout = new Button { Content = "Log out" };
            logout.Click += (_, _) => { _window.OnLoggedOut(); BuildAccount(); };
            _account.Children.Add(Row(S.UserLogin, "Logged in with the API login", logout, bold: true));
            _account.Children.Add(Row("Sign in on twitch.tv",
                "Adds 1440p, your sub and Turbo benefits (no ads), subscriber-only VODs and watch streaks.", SignInButton("Sign in", accent: false)));
            return;
        }

        _account.Children.Add(Row("Sign in with Twitch",
            "Uses Twitch's own sign-in page. Gives you chat, your followed channels, 1440p, your sub and Turbo benefits (no ads), subscriber-only VODs and watch streaks. Glint never sees your password.",
            SignInButton("Sign in", accent: true)));

        var customId = new TextBox { Header = "Client ID", Text = S.ClientId ?? "", PlaceholderText = "Leave empty to use Glint's" };
        customId.TextChanged += (_, _) => { S.ClientId = customId.Text.Trim().Length > 0 ? customId.Text.Trim() : null; Settings.Save(); };
        _account.Children.Add(new Expander
        {
            Header = "Other ways to log in",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Content = new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    ApiLoginPanel("API login", "Twitch's official login for apps: chat, follows and clips, without the website extras.", "Log in"),
                    customId,
                    Note("Only needed for your own Twitch app. Make one at dev.twitch.tv/console/apps: client type Public, redirect URL http://localhost."),
                },
            },
        });
    }

    Button SignInButton(string text, bool accent)
    {
        var button = new Button { Content = text };
        if (accent) button.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        button.Click += async (_, _) =>
        {
            button.IsEnabled = false;
            var window = _signInWindow = new SignInWindow();
            window.Activate();
            var login = await window.Result;
            _signInWindow = null;
            if (_closed) return;
            button.IsEnabled = true;
            if (login == null) return;
            _window.OnSessionChanged();
            BuildAccount();
        };
        return button;
    }

    // Twitch's device-code login for the official API: shows a code, the browser opens twitch.tv/activate.
    StackPanel ApiLoginPanel(string title, string description, string buttonText)
    {
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12.5, Visibility = Visibility.Collapsed };
        status.RegisterPropertyChangedCallback(TextBlock.TextProperty, (_, _) =>
            status.Visibility = status.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed);
        var code = new TextBlock { FontSize = 24, FontWeight = FontWeights.Bold, CharacterSpacing = 160, IsTextSelectionEnabled = true, Visibility = Visibility.Collapsed };
        var button = new Button { Content = buttonText };
        button.Click += async (_, _) =>
        {
            if (TwitchAuth.ClientId is not { } id) { status.Text = "This build doesn't have a Client ID, so add one under Other ways to log in."; return; }
            button.IsEnabled = false;
            _loginCts?.Cancel();
            _loginCts = new CancellationTokenSource();
            try
            {
                var dc = await TwitchAuth.StartDeviceFlowAsync(id);
                if (_closed) return;
                code.Text = dc.UserCode;
                code.Visibility = Visibility.Visible;
                status.Text = "Approve Glint in the browser tab that just opened. The code there should match this one.";
                Process.Start(new ProcessStartInfo(dc.VerificationUri.ToString()) { UseShellExecute = true });
                var tokens = await TwitchAuth.PollDeviceFlowAsync(id, dc, _loginCts.Token);
                if (tokens == null) { status.Text = "That code expired, try again."; return; }
                S.AccessToken = tokens.Value.Access;
                S.RefreshToken = tokens.Value.Refresh;
                S.TokenClientId = id;
                Settings.Save();
                await _window.OnLoggedInAsync();
                BuildAccount();
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Log.Error("Login", ex);
                status.Text = "Login failed: " + ex.Message;
            }
            finally { if (!_closed) button.IsEnabled = true; }
        };

        var header = new Grid { ColumnSpacing = 16 };
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = title, FontSize = 14 });
        text.Children.Add(Note(description));
        button.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(button, 1);
        header.Children.Add(text);
        header.Children.Add(button);
        return new StackPanel { Spacing = 8, Children = { header, code, status } };
    }
}
