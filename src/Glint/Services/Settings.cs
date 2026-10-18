using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Glint;

public sealed class AppSettings
{
    public List<string> Favorites { get; set; } = new();
    public List<string> OpenTabs { get; set; } = new();
    public string? ActiveTab { get; set; }

    public double Volume { get; set; } = 70;
    public bool Muted { get; set; }
    public string PreferredQuality { get; set; } = "Source";
    public bool LowLatency { get; set; } = true;
    public bool BlockAds { get; set; }

    public double ChatWidth { get; set; } = 360;
    public bool SidebarCollapsed { get; set; }
    public bool ChatOnly { get; set; }
    public bool ShowTimestamps { get; set; }
    public double ChatFontSize { get; set; } = 14;
    public bool ShowBadges { get; set; } = true;

    // Twitch login (optional). Tokens are DPAPI-protected for the current Windows user.
    public string? ClientId { get; set; }
    public string? TokenClientId { get; set; }
    public string? UserLogin { get; set; }
    public string? UserId { get; set; }
    public string? AccessTokenProtected { get; set; }
    public string? RefreshTokenProtected { get; set; }

    public int? WindowX { get; set; }
    public int? WindowY { get; set; }
    public int? WindowWidth { get; set; }
    public int? WindowHeight { get; set; }
    public bool WindowMaximized { get; set; }

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
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Options) ?? new();
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
