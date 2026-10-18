using System.Text.Json;
using Glint;
using Windows.System;

void Check(bool condition, string name)
{
    if (!condition) throw new Exception(name);
    Console.WriteLine($"PASS: {name}");
}

var settings = new AppSettings { AccentColor = "Teal", ChatFontSize = 18, SidebarWidth = 300, Volume = 33,
    AccessTokenProtected = "secret", WebSessionTokenProtected = "session", OpenTabs = ["one", "two"], MutedTabs = ["two"],
    IgnoredWords = ["spoiler"], ShortcutOverrides = new() { ["chat"] = "G" }, MultiviewCount = 4 };
var profile = PreferenceCatalog.Capture(" Watching ", settings);
Check(profile.Name == "Watching", "Profile names are trimmed");
Check(!profile.Values.Keys.Any(k => k.Contains("Token") || k.Contains("Session") || k == "OpenTabs" || k == "MutedTabs" || k == "Volume"), "Profiles contain preferences without credentials, streams or current audio");
settings.ChatFontSize = 12; settings.IgnoredWords.Add("extra");
Check(profile.Values["IgnoredWords"].GetArrayLength() == 1, "Saved lists are independent snapshots");
var reloaded = JsonSerializer.Deserialize<PreferenceProfile>(JsonSerializer.Serialize(profile))!;
PreferenceCatalog.Apply(reloaded, settings);
Check(settings.ChatFontSize == 18 && settings.IgnoredWords.SequenceEqual(["spoiler"]) && settings.ShortcutOverrides["chat"] == "G", "A serialized profile restores font, lists and shortcuts");
Check(settings.OpenTabs.SequenceEqual(["one", "two"]) && settings.MutedTabs.SequenceEqual(["two"]) && settings.Volume == 33, "Applying a profile preserves streams and audio");
Check(settings.AccessTokenProtected == "secret" && settings.WebSessionTokenProtected == "session", "Applying a profile preserves account credentials");
PreferenceCatalog.Reset("Chat", settings);
Check(settings.ChatFontSize == 14 && settings.AccentColor == "Teal" && settings.MultiviewCount == 4, "Section reset leaves other categories alone");
var preset = PreferenceCatalog.Capture("Four", settings, "Multiview");
settings.MultiviewCount = 2; settings.ChatFontSize = 19;
PreferenceCatalog.Apply(preset, settings, "Multiview");
Check(settings.MultiviewCount == 4 && settings.ChatFontSize == 19, "Multiview presets only restore layout preferences");
profile.Values["AccessTokenProtected"] = JsonSerializer.SerializeToElement("replacement");
profile.Values["OpenTabs"] = JsonSerializer.SerializeToElement(new[] { "three" });
profile.Values["MadeUpProperty"] = JsonSerializer.SerializeToElement(1);
profile.Values["ChatFontSize"] = JsonSerializer.SerializeToElement("wrong type");
PreferenceCatalog.Apply(profile, settings);
Check(settings.AccessTokenProtected == "secret" && settings.OpenTabs.Count == 2 && settings.ChatFontSize == 19, "Unknown, account and incorrectly typed profile entries are ignored");
Check(ShortcutMap.Validate("chat", "M", new())?.Contains("mute") == true, "Duplicate shortcut conflicts are explained");
Check(ShortcutMap.Validate("chat", "Ctrl+C", new()) != null && ShortcutMap.Validate("chat", "Escape", new()) != null, "Text editing and navigation keys are reserved");
Check(ShortcutMap.Validate("chat", "Ctrl+G", new()) == null, "Unused combinations can be assigned");
Check(ShortcutMap.TryParse("Ctrl+Shift+M", out var key, out var mods) && key == VirtualKey.M &&
    mods == (VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift), "Shortcut modifiers parse correctly");
Check(!ShortcutMap.TryParse("Bogus+M", out _, out _), "Invalid shortcut modifiers are rejected");
settings.ShortcutOverrides["fullscreen"] = "Ctrl+F";
Check(ShortcutMap.Bindings(ShortcutMap.Definitions.First(d => d.Id == "fullscreen"), settings).SequenceEqual(["Ctrl+F"]), "Custom bindings replace all defaults for that action");
settings.Profiles.Add(reloaded); settings.MultiviewPresets.Add(preset);
settings.Favorites = ["favourite"]; settings.ActiveTab = "two";
settings.BlockAds = true; settings.SoundOnHighlight = true; settings.EmoteSuggestions = false;
PreferenceCatalog.ResetAll(settings);
var reset = PreferenceCatalog.Capture("Reset", settings);
Check(settings.EmoteSuggestions && PreferenceCatalog.Capture("Defaults", new()).Values.All(pair => JsonElement.DeepEquals(pair.Value, reset.Values[pair.Key])), "Restore all defaults resets every customisation category, including emote suggestions");
Check(settings.AccessTokenProtected == "secret" && settings.WebSessionTokenProtected == "session" && settings.OpenTabs.SequenceEqual(["one", "two"]) &&
    settings.ActiveTab == "two" && settings.Volume == 33 && settings.MutedTabs.SequenceEqual(["two"]) && settings.Favorites.SequenceEqual(["favourite"]),
    "Restore all defaults keeps account, open streams, favourites and current audio");
Check(settings.Profiles.Single() == reloaded && settings.MultiviewPresets.Single() == preset, "Restore all defaults keeps saved profiles and layouts");
var defaults = new AppSettings();
foreach (var definition in ShortcutMap.Definitions)
    foreach (string binding in definition.Defaults)
        Check(ShortcutMap.Validate(definition.Id, binding, defaults) == null, $"Default shortcut {binding} is valid and unique");

namespace Glint
{
    public static class Paths { public static readonly string Roaming = Path.Combine(Path.GetTempPath(), "glint-settings-test-unused"); }
    public static class Log { public static void Error(string text, Exception error) { } }
}
