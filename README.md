# Glint

A native Twitch client for Windows, with 7TV, BTTV and FFZ emotes built in.

It's a WinUI 3 app rather than a web page: video plays through [mpv](https://mpv.io) with hardware
decoding, and chat is drawn with Direct2D (via Win2D).

## Features

- Live streams with a quality picker
- Chat with Twitch, 7TV, BTTV and FFZ emotes, including animated and zero-width emotes. Glint picks up 7TV emote changes while you watch.
- Recent chat history loads when you open a channel
- Past broadcasts, with chat replay that follows the video
- Tabs for several channels at once. Chat keeps running in the background tabs.
- Log in with Twitch to chat and to see your followed channels in the sidebar
- Tab completion for emotes and @names, a chat-only mode, and fullscreen

## Requirements

- Windows 10 (version 2004 or later) or Windows 11, 64-bit
- So far it's only been tested on Windows 11

## Building

You'll need the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```
powershell -ExecutionPolicy Bypass -File tools\fetch-mpv.ps1
dotnet build src\Glint
```

`fetch-mpv.ps1` downloads `libmpv-2.dll` from [shinchiro's mpv builds](https://github.com/shinchiro/mpv-winbuild-cmake).
The file is too big to keep in git.

`tools\install.ps1` builds a release copy into `%LOCALAPPDATA%\Programs\Glint` and adds Start menu and
desktop shortcuts. Run it again to update.

## Keys

| Key | Action |
|---|---|
| F, F11 or double-click the video | Fullscreen |
| Esc | Leave fullscreen |
| M | Mute |
| Space | Pause |
| ← / → | Skip back / forward 10 seconds (VODs only) |
| Ctrl+W | Close tab |
| Ctrl+Tab / Ctrl+Shift+Tab | Next / previous tab |
| Ctrl+T or Ctrl+L | Add a channel |
| F5 | Reload the stream |
| Tab (in the chat box) | Complete an emote or @name |

## How it works

- Channel info, playback and VODs come from the same API the twitch.tv website uses. That API isn't
  official, so Twitch could change it and break things.
- Chat uses Twitch's IRC interface. It's read-only until you log in.
- Login uses Twitch's device-code sign-in. Your token is encrypted with Windows' per-user data
  protection and stored in `%APPDATA%\Glint\settings.json`.
- Emote images are cached in `%LOCALAPPDATA%\Glint`.

## Not done yet

- 7TV name paints and badges
- An emote picker
- Clips
- Subscriber-only VODs

## Thanks

[mpv](https://mpv.io), and [7TV](https://7tv.app), [BTTV](https://betterttv.com) and [FFZ](https://frankerfacez.com)
for their public APIs. [Chatterino](https://chatterino.com) inspired a lot of how chat behaves.

Glint isn't affiliated with Twitch.

## Licence

Glint is MIT licensed. libmpv isn't part of this repository. It's downloaded separately and covered by
mpv's own licence (GPLv2+ or LGPLv2.1+, depending on the build).
