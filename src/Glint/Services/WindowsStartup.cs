using Microsoft.Win32;

namespace Glint;

// Starting with Windows, via the per-user Run key. Nothing else is stored: the key itself is the setting.
public static class WindowsStartup
{
    public const string BackgroundArgument = "--background";
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "Glint";

    static string Command => $"\"{Environment.ProcessPath}\" {BackgroundArgument}";

    public static bool IsOn
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                return key?.GetValue(ValueName) is string value && value.Equals(Command, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }
    }

    public static void Set(bool on)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (on) key.SetValue(ValueName, Command);
            else key.DeleteValue(ValueName, false);
        }
        catch (Exception ex) { Log.Error("Windows startup", ex); }
    }
}
