using System.Diagnostics;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Glint;

public sealed partial class SettingsDialog : ContentDialog
{
    readonly MainWindow _window;
    readonly StackPanel _account = new() { Spacing = 10 };
    CancellationTokenSource? _loginCts;

    static AppSettings S => Settings.Current;
    static Brush Subtle => (Brush)Application.Current.Resources["GlintSubtleTextBrush"];

    public SettingsDialog(MainWindow window)
    {
        _window = window;
        Title = "Settings";
        CloseButtonText = "Close";
        DefaultButton = ContentDialogButton.Close;
        Closed += (_, _) => _loginCts?.Cancel();

        var root = new StackPanel { Spacing = 22, Width = 440 };
        root.Children.Add(Section("Account", _account));
        BuildAccount();

        var timestamps = Toggle("Timestamps", S.ShowTimestamps, on => { S.ShowTimestamps = on; _window.OnChatStyleChanged(); });
        var badges = Toggle("Badges", S.ShowBadges, on => S.ShowBadges = on);
        var font = new Slider { Header = "Text size", Minimum = 11, Maximum = 20, StepFrequency = 0.5, Value = S.ChatFontSize };
        font.ValueChanged += (_, e) => { S.ChatFontSize = e.NewValue; Settings.Save(); _window.OnChatStyleChanged(); };
        root.Children.Add(Section("Chat", new StackPanel { Spacing = 4, Children = { timestamps, badges, font } }));

        var ads = Toggle("Skip ads", S.BlockAds, on => S.BlockAds = on);
        var adsNote = Note("Off by default. When on and Twitch stitches an ad into the stream, Glint switches to an ad-free copy " +
                           "if it can find one, otherwise the picture holds until the break ends. Takes effect next time a stream loads. " +
                           "This goes against Twitch's terms of service, so it's your call.");
        var lowLatency = Toggle("Low latency", S.LowLatency, on => S.LowLatency = on);
        root.Children.Add(Section("Video", new StackPanel { Spacing = 4, Children = { ads, adsNote, lowLatency } }));

        root.Children.Add(Note($"Glint {typeof(App).Assembly.GetName().Version?.ToString(3)}. Video by mpv, emotes from 7TV, BTTV and FFZ."));
        Content = new ScrollViewer { Content = root, MaxHeight = 620 };
    }

    static StackPanel Section(string title, UIElement body) => new()
    {
        Spacing = 6,
        Children = { new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeights.SemiBold }, body },
    };

    static TextBlock Note(string text) => new() { Text = text, FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = Subtle };

    static ToggleSwitch Toggle(string header, bool value, Action<bool> set)
    {
        var t = new ToggleSwitch { Header = header, IsOn = value };
        t.Toggled += (_, _) => { set(t.IsOn); Settings.Save(); };
        return t;
    }

    void BuildAccount()
    {
        _account.Children.Clear();
        if (S.AccessToken != null && S.UserLogin != null)
        {
            _account.Children.Add(new TextBlock { Text = $"Logged in as {S.UserLogin}", TextWrapping = TextWrapping.Wrap });
            var logout = new Button { Content = "Log out" };
            logout.Click += (_, _) => { _window.OnLoggedOut(); BuildAccount(); };
            _account.Children.Add(logout);
            return;
        }

        _account.Children.Add(Note("You can watch and read chat without an account. Log in to chat and see who you follow."));
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 13 };
        var code = new TextBlock
        {
            FontSize = 26, FontWeight = FontWeights.Bold, CharacterSpacing = 160,
            IsTextSelectionEnabled = true, Visibility = Visibility.Collapsed,
        };
        var login = new Button { Content = "Log in with Twitch", Style = (Style)Application.Current.Resources["AccentButtonStyle"] };

        // Only needed if you want to use your own Twitch app instead of Glint's.
        var customId = new TextBox { Header = "Client ID", Text = S.ClientId ?? "", PlaceholderText = "Leave empty to use Glint's" };
        customId.TextChanged += (_, _) => { S.ClientId = customId.Text.Trim().Length > 0 ? customId.Text.Trim() : null; Settings.Save(); };
        var advanced = new Expander
        {
            Header = "Advanced",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            IsExpanded = TwitchAuth.ClientId == null,
            Content = new StackPanel
            {
                Spacing = 6,
                Children =
                {
                    customId,
                    Note("Make one at dev.twitch.tv/console/apps: client type Public, redirect URL http://localhost."),
                },
            },
        };

        login.Click += async (_, _) =>
        {
            if (TwitchAuth.ClientId is not { } id)
            {
                status.Text = "This build doesn't have a Client ID yet, so add one under Advanced.";
                advanced.IsExpanded = true;
                return;
            }
            login.IsEnabled = false;
            _loginCts?.Cancel();
            _loginCts = new CancellationTokenSource();
            try
            {
                var dc = await TwitchAuth.StartDeviceFlowAsync(id);
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
            finally { login.IsEnabled = true; }
        };

        _account.Children.Add(login);
        _account.Children.Add(code);
        _account.Children.Add(status);
        _account.Children.Add(advanced);
    }
}
