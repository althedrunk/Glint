using System.Reflection;
using System.Text.Json;

namespace Glint;

public sealed class PreferenceProfile
{
    public string Name { get; set; } = "";
    public Dictionary<string, JsonElement> Values { get; set; } = new();
    public override string ToString() => Name;
}

public static class PreferenceCatalog
{
    public static readonly IReadOnlyDictionary<string, string[]> Sections = new Dictionary<string, string[]>
    {
        ["Appearance"] = ["AccentColor", "BackgroundStyle", "InterfaceDensity", "SidebarWidth", "SidebarAvatarSize"],
        ["Chat"] = ["ShowTimestamps", "TimestampFormat", "ChatFontSize", "ChatFontFamily", "PauseChatOnHover", "ShowBadges",
            "ShowSevenTvPaints", "ShowSevenTvBadges", "ChatMessageSpacing", "ChatLineSpacing", "EmoteScale", "EmoteAnimation", "AlternateChatRows"],
        ["Filtering"] = ["IgnoredWords", "IgnoredUsers", "ShowAnnouncements", "ShowSubscriptionNotices"],
        ["Player"] = ["PreferredQuality", "LowLatency", "BlockAds", "NewTabVolume", "UseLastVolumeForNewTabs", "SeekSeconds", "LongSeekSeconds", "ControlsHideSeconds"],
        ["Multiview"] = ["MultiviewCount", "MultiviewLayout", "MultiviewChats", "MultiviewDock", "MultiviewChatWidth", "MultiviewChatHeight"],
        ["Notifications"] = ["HighlightWords", "HighlightUsers", "FlashOnMention", "SoundOnMention", "SoundOnHighlight", "NotifyWhenFocused", "MentionTone", "HighlightTone", "NotificationVolume"],
        ["Shortcuts"] = ["ShortcutOverrides"],
    };

    static readonly HashSet<string> Allowed = Sections.Values.SelectMany(x => x).ToHashSet();

    public static PreferenceProfile Capture(string name, AppSettings settings, string? section = null) => new()
    {
        Name = name.Trim(),
        Values = Keys(section).ToDictionary(key => key, key => JsonSerializer.SerializeToElement(Property(key).GetValue(settings))),
    };

    public static void Apply(PreferenceProfile profile, AppSettings settings, string? section = null)
    {
        var keys = Keys(section).ToHashSet();
        foreach (var (key, value) in profile.Values)
        {
            if (!keys.Contains(key) || !Allowed.Contains(key)) continue;
            var property = Property(key);
            try
            {
                var parsed = value.Deserialize(property.PropertyType);
                if (parsed != null) property.SetValue(settings, parsed);
            }
            catch (JsonException) { }
        }
    }

    public static void Reset(string section, AppSettings settings) => Apply(Capture("Defaults", new(), section), settings, section);
    public static void ResetAll(AppSettings settings) => Apply(Capture("Defaults", new()), settings);
    static IEnumerable<string> Keys(string? section) => section == null ? Allowed : Sections[section];
    static PropertyInfo Property(string key) => typeof(AppSettings).GetProperty(key)!;
}
