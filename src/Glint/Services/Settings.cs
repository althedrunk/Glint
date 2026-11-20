using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Glint;

public sealed class AppSettings
{
    public List<string> Favorites { get; set; } = new();
    public List<string> OpenTabs { get; set; } = new();
    // What to reopen at launch: "Live" (tabs whose channel is live), "All", or "None" (the home page).
    public string StartupTabs { get; set; } = "Live";
    public List<string> RecentChannels { get; set; } = new();
    // Home page: section order (Continue, Live, Recent), the ones switched off, and how many rows of live channels.
    public List<string> HomeSections { get; set; } = new() { "Continue", "Live", "Recent" };
    public List<string> HomeHiddenSections { get; set; } = new();
    public int HomeLiveRows { get; set; } = 2; // 0 shows them all
    // Where you got to in each VOD, most recent first, so reopening one carries on from there.
    public List<VodProgress> VodProgress { get; set; } = new();
    public bool CheckForUpdates { get; set; } = true;
    // Closing the window hides Glint to the notification area instead of quitting.
    public bool CloseToTray { get; set; }
    public bool SyncChatToVideo { get; set; } = true;
    public bool MultiviewSync { get; set; }

    public int[]? MiniPlayerBounds { get; set; }
    // Go-live notifications: off unless a channel is switched on, or this covers everyone you follow.
    public List<string> LiveAlertChannels { get; set; } = new();
    public bool LiveAlertAllFollowed { get; set; }
    public string? ActiveTab { get; set; }

    public double Volume { get; set; } = 70;
    public List<string> MutedTabs { get; set; } = new();
    // Migrated to the last active tab when reading older settings.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Muted { get; set; }
    public string PreferredQuality { get; set; } = "Source";
    public bool LowLatency { get; set; } = true;
    public bool BlockAds { get; set; }
    public double NewTabVolume { get; set; } = 70;
    public bool UseLastVolumeForNewTabs { get; set; } = true;
    public double SeekSeconds { get; set; } = 10;
    public double LongSeekSeconds { get; set; } = 60;
    public bool SkipMutedVod { get; set; }
    // When the channel you're watching raids someone, go with it after a countdown (otherwise just offer).
    public bool FollowRaids { get; set; } = true;
    public double ControlsHideSeconds { get; set; } = 2.5;

    public string AccentColor { get; set; } = "Purple";
    public string BackgroundStyle { get; set; } = "Dark";
    public string InterfaceDensity { get; set; } = "Comfortable";
    public string CustomAccent { get; set; } = "#A970FF";
    public string CardSize { get; set; } = "Medium";
    public bool SidebarShowOffline { get; set; } = true;
    public string SidebarSort { get; set; } = "Viewers";
    public double SidebarWidth { get; set; } = 244;
    public double SidebarAvatarSize { get; set; } = 32;

    public double ChatWidth { get; set; } = 360;
    public bool SidebarCollapsed { get; set; }
    public bool ChatOnly { get; set; }
    public bool ChatHidden { get; set; }
    public bool Theatre { get; set; }
    public bool FullscreenChat { get; set; }
    public bool Multiview { get; set; }
    public int MultiviewCount { get; set; } = 2;
    public string MultiviewLayout { get; set; } = "Grid";
    public string MultiviewChats { get; set; } = "Each stream";
    public string MultiviewDock { get; set; } = "Below";
    public double MultiviewChatWidth { get; set; } = 320;
    public double MultiviewChatHeight { get; set; } = 260;
    // Dragged sizes as relative weights; empty means even.
    public List<double> MultiviewVideoColumns { get; set; } = new();
    public List<double> MultiviewVideoRows { get; set; } = new();
    public List<double> MultiviewMainColumns { get; set; } = new();
    public List<double> MultiviewMainRows { get; set; } = new();
    public List<double> MultiviewChatColumns { get; set; } = new();
    public List<double> MultiviewChatRows { get; set; } = new();
    public List<string> MultiviewChannels { get; set; } = new();
    public List<string> MultiviewPinnedChats { get; set; } = new();
    public bool ShowTimestamps { get; set; }
    public double ChatFontSize { get; set; } = 14;
    public string ChatFontFamily { get; set; } = "Segoe UI Variable Text";
    public bool PauseChatOnHover { get; set; } = true;
    public bool EmoteSuggestions { get; set; } = true;
    public double ChatMessageSpacing { get; set; } = 2;
    public double ChatLineSpacing { get; set; } = 1;
    public double EmoteScale { get; set; } = 1;
    public string EmoteAnimation { get; set; } = "Always";
    public string TimestampFormat { get; set; } = "HH:mm";
    public bool AlternateChatRows { get; set; }
    public bool ShowAnnouncements { get; set; } = true;
    public bool ShowSubscriptionNotices { get; set; } = true;
    public List<string> IgnoredUsers { get; set; } = new();
    public List<string> IgnoredWords { get; set; } = new();
    public bool ShowBadges { get; set; } = true;
    public bool ShowSevenTvPaints { get; set; } = true;
    public bool ShowSevenTvBadges { get; set; } = true;
    public List<string> HighlightWords { get; set; } = new();
    public List<string> HighlightUsers { get; set; } = new();
    public bool FlashOnMention { get; set; } = true;
    public bool SoundOnMention { get; set; }
    public bool SoundOnHighlight { get; set; }
    public bool NotifyWhenFocused { get; set; }
    public string MentionTone { get; set; } = "Bell";
    public string HighlightTone { get; set; } = "Soft";
    public double NotificationVolume { get; set; } = 50;
    public Dictionary<string, string> ShortcutOverrides { get; set; } = new();
    public List<PreferenceProfile> Profiles { get; set; } = new();
    public List<PreferenceProfile> MultiviewPresets { get; set; } = new();
    public List<string> RecentEmotes { get; set; } = new();

    // Twitch login (optional). Tokens are DPAPI-protected for the current Windows user.
    public string? ClientId { get; set; }
    public string? TokenClientId { get; set; }
    public List<string> TokenScopes { get; set; } = new();
    public string? UserLogin { get; set; }
    public string? UserId { get; set; }
    public string? AccessTokenProtected { get; set; }
    public string? RefreshTokenProtected { get; set; }

    public int? WindowX { get; set; }
    public int? WindowY { get; set; }
    public int? WindowWidth { get; set; }
    public int? WindowHeight { get; set; }
    public bool WindowMaximized { get; set; }

    // Optional twitch.tv website session (see TwitchSession). DPAPI-protected like the tokens below.
    public string? WebSessionTokenProtected { get; set; }
    public string? WebSessionLogin { get; set; }
    public string? WebSessionUserId { get; set; }
    public string? WebDeviceId { get; set; }
    public bool CountWatchTime { get; set; } = true;

    [JsonIgnore]
    public string? WebSessionToken
    {
        get => Unprotect(WebSessionTokenProtected);
        set => WebSessionTokenProtected = Protect(value);
    }

    [JsonIgnore]
    public string? AccessToken
    {
        get => Unprotect(AccessTokenProtected);
        set => AccessTokenProtected = Protect(value);
    }

    [JsonIgnore]
    public string? RefreshToken
    {
        get => Unprotect(RefreshTokenProtected);
        set => RefreshTokenProtected = Protect(value);
    }

    static string? Protect(string? value) => string.IsNullOrEmpty(value)
        ? null
        : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser));

    static string? Unprotect(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(value), null, DataProtectionScope.CurrentUser));
        }
        catch { return null; }
    }
}

public sealed class VodProgress
{
    public string Id { get; set; } = "";
    public double Seconds { get; set; }
    public double Length { get; set; }
    // Enough to show it under "Continue watching" before Twitch has answered.
    public string Login { get; set; } = "";
    public string Channel { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Thumbnail { get; set; }
    // Taken off "Continue watching" by hand. The position is kept, and watching more brings it back.
    public bool Hidden { get; set; }
}

public static class Settings
{
    static readonly string FilePath = Path.Combine(Paths.Roaming, "settings.json");
    static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    static System.Threading.Timer? _saveTimer;

    public static AppSettings Current { get; } = Load();

    static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Options) ?? new();
                if (settings.Muted && settings.ActiveTab is { } login &&
                    !settings.MutedTabs.Contains(login, StringComparer.OrdinalIgnoreCase)) settings.MutedTabs.Add(login);
                settings.Muted = false;
                return settings;
            }
        }
        catch (Exception ex) { Log.Error("Settings load", ex); }
        return new AppSettings();
    }

    // Debounced save, cheap to call on every change.
    public static void Save()
    {
        _saveTimer ??= new System.Threading.Timer(_ => SaveNow());
        _saveTimer.Change(600, Timeout.Infinite);
    }

    public static void SaveNow()
    {
        try
        {
            string json;
            lock (Current) json = JsonSerializer.Serialize(Current, Options);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, FilePath, true);
        }
        catch (Exception ex) { Log.Error("Settings save", ex); }
    }
}
