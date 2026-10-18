<p align="center">
  <img src="docs/branding/glint-logo.png" alt="Glint" width="280">
</p>

Glint is a native Twitch client for Windows, bringing streams and chat into a customisable desktop app.

## Overview

- Watch live streams and past broadcasts with hardware-accelerated playback, theatre and fullscreen modes.
- Keep channels open in tabs or watch up to four streams in multiview, with flexible chat layouts and independent audio controls.
- Browse channels and categories, and create clips from the player.
- Chat with Twitch, 7TV, BTTV and FFZ emotes, replies, mentions and searchable message history.
- Customise the appearance, chat, notifications and shortcuts, with saved profiles and layouts.

Watching and reading chat work without an account. Sign in with Twitch for sending messages, followed channels, your emotes and supported subscriber and Turbo benefits.

## Install from source

Requires 64-bit Windows 10 version 2004 or later, or Windows 11, and the [.NET 10 SDK](https://dotnet.microsoft.com/download). Glint has been tested on Windows 11.

From a checkout of this repository, run:

```powershell
powershell -ExecutionPolicy Bypass -File tools\fetch-mpv.ps1
powershell -ExecutionPolicy Bypass -File tools\install.ps1
```

This installs Glint to `%LOCALAPPDATA%\Programs\Glint` and creates Start menu and desktop shortcuts. Run `tools\install.ps1` again to update it after pulling changes.

For a development build:

```powershell
dotnet build src\Glint -c Debug
```

The video library is downloaded separately by `fetch-mpv.ps1` from [shinchiro's mpv builds](https://github.com/shinchiro/mpv-winbuild-cmake).

<details>
<summary>Keyboard shortcuts</summary>

These are the default bindings. Most can be changed in Settings under Shortcuts. Seek distances can be changed under Player.

| Key | Action |
|---|---|
| F or F11 | Toggle fullscreen |
| Double-click a video | Toggle fullscreen in single view; enlarge or restore a multiview tile |
| T | Toggle theatre mode; leave fullscreen for theatre |
| C | Show or hide chat |
| Esc | Restore an enlarged tile, close the broadcasts panel, or leave fullscreen or theatre |
| M | Mute or unmute the selected tab |
| ↑ / ↓ or mouse wheel over the volume control | Adjust volume |
| Space | Pause or resume the selected video |
| ← / → or J / L | Seek back or forward 10 seconds in a past broadcast |
| Shift+← / Shift+→ | Seek back or forward a minute in a past broadcast |
| 0–9 | Jump to 0–90% of a past broadcast |
| Alt+X | Clip the live stream (needs a login) |
| Ctrl+B | Open or close Browse |
| Ctrl+Shift+M | Enter or leave multiview |
| Ctrl+T | Open an empty tab |
| Ctrl+L | Enter a channel in the current tab |
| Ctrl+W | Close the selected tab |
| Ctrl+Tab / Ctrl+Shift+Tab | Next or previous tab |
| F5 | Reload the selected stream |
| Ctrl+F1 | Open Settings |
| Tab in the message box | Complete an emote or @name |
| : + two or more characters | Find emotes; arrows select, Tab or Enter inserts, Esc closes |
| Esc in the message box | Cancel a reply |

Single-letter shortcuts are inactive while typing or using an input control.

</details>

## Notes

Glint uses Twitch's unofficial web API for channel information and playback. Changes to that API can affect compatibility. Clip creation requires the separate API login in Settings.

Settings are stored in `%APPDATA%\Glint\settings.json`. Login tokens are encrypted for the current Windows user.

Glint is built with WinUI 3, [mpv](https://mpv.io) and Win2D. Thanks to [7TV](https://7tv.app), [BTTV](https://betterttv.com) and [FFZ](https://frankerfacez.com) for their emote services, and [Chatterino](https://chatterino.com) for inspiration.

Glint is independent of Twitch and is not affiliated with or endorsed by it.

## Licence

Glint is [MIT licensed](LICENSE). libmpv is downloaded separately and covered by [mpv's licence](https://github.com/mpv-player/mpv/blob/master/Copyright).
