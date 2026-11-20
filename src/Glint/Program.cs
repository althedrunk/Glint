using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Windows.AppLifecycle;

namespace Glint;

// One Glint per copy: opening it again (say while it's sitting minimised from Windows startup) brings the
// running window forward instead of starting a second one.
public static class Program
{
    // Launched by Windows at sign-in: start minimised, open no tabs, play nothing.
    public static bool StartedInBackground { get; private set; }

    [STAThread]
    static void Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        StartedInBackground = args.Contains(WindowsStartup.BackgroundArgument);
        // Keyed to this exe's location, so a portable copy and an installed one don't take each other over.
        var path = Environment.ProcessPath ?? "Glint";
        var key = AppInstance.FindOrRegisterForKey("Glint-" + Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(path.ToLowerInvariant())))[..16]);
        if (!key.IsCurrent)
        {
            if (!StartedInBackground)
            {
                AllowSetForegroundWindow(-1); // let the running copy come to the front
                Task.Run(() => key.RedirectActivationToAsync(AppInstance.GetCurrent().GetActivatedEventArgs()).AsTask()).Wait();
            }
            return;
        }
        key.Activated += (_, _) => App.Window?.BringToFront();
        XamlGeneratedProgram.XamlGeneratedMain();
    }

    [DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(int processId);
}
