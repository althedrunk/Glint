<p align="center">
  <img src="docs/branding/glint-logo.png" alt="Glint" width="280">
</p>

A native Twitch client for Windows, with hardware-accelerated video and chat with Twitch, 7TV, BTTV and FFZ emotes.

## Features

- Live streams with quality selection, including higher resolutions when the channel provides them.
- Channel tabs that keep playing in the background. Each tab has its own mute and volume controls; switching tabs leaves audio unchanged.
- Multiview for two to four streams, with a grid or one main video. Enlarge a tile without reloading the stream.
- Chats that follow the selected video, stay pinned, or show each stream. Put them beside or below the videos and adjust the space they use.
- A separate message recipient in multiview, with a draft for each channel.
- Past broadcasts with seeking, playback speed controls and chat replay.
- Animated and zero-width emotes, recent chat history, and completion for emotes and @names.
- Theatre, fullscreen and chat-only modes.
- Optional Twitch login for sending messages and viewing followed channels. Watching and reading chat work without signing in.

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

## Keyboard shortcuts

| Key | Action |
|---|---|
| F or F11 | Toggle fullscreen |
| Double-click a video | Toggle fullscreen in single view; enlarge or restore a multiview tile |
| T | Toggle theatre mode; leave fullscreen for theatre |
| C | Show or hide chat |
| Esc | Restore an enlarged tile, close the broadcasts panel, or leave fullscreen or theatre |
| M | Mute or unmute the selected tab |
| ↑ / ↓ or mouse wheel over a video | Adjust its volume |
| Space | Pause or resume the selected video |
| ← / → | Seek back or forward 10 seconds in a past broadcast |
| Ctrl+Shift+M | Enter or leave multiview |
| Ctrl+T or Ctrl+L | Add a channel |
| Ctrl+W | Close the selected tab |
| Ctrl+Tab / Ctrl+Shift+Tab | Next or previous tab |
| F5 | Reload the selected stream |
| Tab in the message box | Complete an emote or @name |

Single-letter shortcuts are inactive while typing or using an input control.

## Notes

Glint uses Twitch's unofficial web API for channel information and playback. Changes to that API can affect compatibility. Subscriber-only broadcasts and clips are not currently supported.

Settings are stored in `%APPDATA%\Glint\settings.json`. Login tokens are encrypted for the current Windows user.

Glint is built with WinUI 3, [mpv](https://mpv.io) and Win2D. Thanks to [7TV](https://7tv.app), [BTTV](https://betterttv.com) and [FFZ](https://frankerfacez.com) for their emote services, and [Chatterino](https://chatterino.com) for inspiration.

Glint is independent of Twitch and is not affiliated with or endorsed by it.

## Licence

Glint is [MIT licensed](LICENSE). libmpv is downloaded separately and covered by [mpv's licence](https://github.com/mpv-player/mpv/blob/master/Copyright).
