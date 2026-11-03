using System.Security;
using Microsoft.Win32;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace Glint;

// Windows notifications when a channel you follow goes live. Clicking one opens the stream.
public sealed partial class MainWindow
{
    // An unpackaged app gets notifications by registering a name and icon under this id.
    const string ToastAppId = "althedrunk.Glint";
    ToastNotifier? _toasts;

    void WireLiveAlerts()
    {
        try
        {
            using (var key = Registry.CurrentUser.CreateSubKey($@"Software\Classes\AppUserModelId\{ToastAppId}"))
            {
                key.SetValue("DisplayName", "Glint");
                key.SetValue("IconUri", Asset("Logo512.png"));
            }
            _toasts = ToastNotificationManager.CreateToastNotifier(ToastAppId);
        }
        catch (Exception ex) { Log.Error("Notifications", ex); }
    }

    bool WantsLiveAlert(ChannelEntry entry) => S.LiveAlertAllFollowed || S.LiveAlertChannels.Contains(entry.Login);

    internal bool HasLiveAlert(string login) => S.LiveAlertChannels.Contains(login);

    internal void SetLiveAlert(string login, bool on)
    {
        S.LiveAlertChannels.Remove(login);
        if (on) S.LiveAlertChannels.Add(login);
        Settings.Save();
        GetEntry(login).RefreshAlert();
        if (_vodPanelOpen && _active?.Login == login) ChannelAlertButton.IsChecked = on;
    }

    internal string DisplayNameFor(string login) => GetEntry(login).DisplayName;

    async Task NotifyLiveAsync(ChannelEntry entry)
    {
        // Already open in a tab: it starts playing there anyway.
        if (_toasts == null || !WantsLiveAlert(entry) || _tabs.Any(t => t.Entry == entry)) return;
        var avatar = await ToastAvatarAsync(entry);
        string E(string? s) => SecurityElement.Escape(s ?? "") ?? "";
        var xml = $"""
            <toast launch="open={E(entry.Login)}">
              <visual><binding template="ToastGeneric">
                <text>{E(entry.DisplayName)} is live</text>
                <text>{E(entry.Stream?.Title)}</text>
                {(entry.Stream?.Game is { } game ? $"<text placement=\"attribution\">{E(game)}</text>" : "")}
                {(avatar != null ? $"<image placement=\"appLogoOverride\" hint-crop=\"circle\" src=\"{E(avatar.AbsoluteUri)}\"/>" : "")}
              </binding></visual>
              <actions><action content="Watch" arguments="open={E(entry.Login)}"/></actions>
            </toast>
            """;
        try
        {
            var doc = new XmlDocument();
            doc.LoadXml(xml);
            var toast = new ToastNotification(doc);
            toast.Activated += (_, args) =>
            {
                if ((args as ToastActivatedEventArgs)?.Arguments is not { } a || !a.StartsWith("open=")) return;
                var login = a[5..];
                _dispatcher.TryEnqueue(() =>
                {
                    if (_closing) return;
                    if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter { State: Microsoft.UI.Windowing.OverlappedPresenterState.Minimized } p) p.Restore();
                    Activate();
                    OpenChannel(login, true);
                });
            };
            _toasts.Show(toast);
        }
        catch (Exception ex) { Log.Error("Live alert", ex); }
    }

    // Notifications only show local images, so keep a copy of the avatar on disk.
    static async Task<Uri?> ToastAvatarAsync(ChannelEntry entry)
    {
        if (entry.AvatarUrl is not { } url) return null;
        var file = Path.Combine(Paths.LocalData, "cache", "toast", entry.Login + ".png");
        try
        {
            if (!File.Exists(file) || DateTime.UtcNow - File.GetLastWriteTimeUtc(file) > TimeSpan.FromDays(7))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                await File.WriteAllBytesAsync(file, await Net.Http.GetByteArrayAsync(url));
            }
            return new Uri(file);
        }
        catch (Exception ex) { Log.Error("Toast avatar", ex); return null; }
    }
}
