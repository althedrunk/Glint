Glint for Windows x64

INSTALLER
1. Run the Setup.exe download and follow the setup screens.
2. Open Glint from the Start menu. A desktop shortcut is optional.
3. To update, close Glint and run the newer installer.

PORTABLE
1. Extract the Portable ZIP into a writable folder, such as Documents\Glint.
2. Open Glint.exe. Keep Data and Docs beside it.
3. To update, close Glint and replace Glint.exe and Docs. Keep your existing Data
   folder, including portable.flag. Do not replace Data with an empty copy.

VIDEO SETUP
Add a Twitch channel. The first time you play video, choose Download in the
video setup dialog. Glint downloads the video component directly from mpv's
official GitHub release and checks its checksum. This is a one-time setup.

No .NET SDK, separate .NET runtime installation or administrator access is needed.
Runtime files are bundled inside Glint.exe. Windows may keep temporary runtime
files in its normal per-user temp folder.

REQUIREMENTS
64-bit Windows 10 version 2004 or later, or Windows 11.
This beta has been tested on Windows 11. An internet connection is required.
Twitch sign-in needs the Microsoft Edge WebView2 Runtime. If it is missing,
install the Evergreen Runtime from Microsoft's official download page:
https://developer.microsoft.com/microsoft-edge/webview2/

ACCOUNT AND SETTINGS
Watching and reading chat do not require signing in.
The installed edition keeps settings and protected login details under %APPDATA%\Glint.
The downloaded video component is stored under %LOCALAPPDATA%\Glint\player.
The portable edition keeps settings, player downloads, cache and logs in Data
beside Glint.exe. Move the whole folder to keep your data. Sign in again on a new
PC: login details are protected for your Windows account. No account details,
channel list or personal settings are supplied in either download.

UPDATES AND REMOVAL
Uninstall the installed edition through Windows Settings > Apps. Its settings
and downloaded player remain in the locations above.
For portable, close Glint and delete its extracted folder. Back up Data first if
you want to keep its settings.

SOURCE AND LICENCES
https://github.com/althedrunk/Glint
See Docs\LICENSE, Docs\THIRD-PARTY-NOTICES.md and Docs\licenses.

This is an unsigned public beta. Windows may show an unknown-publisher warning.
Report bugs at https://github.com/althedrunk/Glint/issues.
