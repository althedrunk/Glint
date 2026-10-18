<p align="center">
  <img src="docs/branding/glint-logo.png" alt="Glint" width="280">
</p>

A native Twitch client for Windows, with hardware-accelerated video and chat with Twitch, 7TV, BTTV and FFZ emotes.

## Features

- Live streams with quality selection, including higher resolutions when the channel provides them.
- Channel tabs that keep playing in the background. Each tab has its own mute and volume controls; switching tabs leaves audio unchanged.
- Sidebar stream selections replace the current tab. Use + for an empty tab, or Ctrl+click or middle-click a stream to open it in a new tab. Streams already open are selected in their existing tab.
- Multiview for two to four streams, with a grid or one main video. Enlarge a tile without reloading the stream.
- Chats that follow the selected video, stay pinned, or show each stream. Put them beside or below the videos and adjust the space they use.
- A separate message recipient in multiview, with a draft for each channel.
- Past broadcasts with seeking, configurable skip buttons, playback speed controls and chat replay.
- A Browse page with the top live channels, categories and search, so you can find streams beyond the ones you follow.
- Clips of live streams from the player, with a link to Twitch's clip editor.
- 7TV, BTTV and FFZ emotes, including animated and zero-width ones, plus 7TV personal emotes, name paints and badges.
- An emote picker with search, recently used emotes and, when logged in, your own Twitch emotes. Tab completes emotes and @names.
- Highlights for your name, chosen words and chosen users, collected in a mentions list. The taskbar can flash and a sound can play when you're mentioned.
- Replies and reply threads. First-time chatters, highlighted messages and announcements are marked.
- Recent chat history when you open a channel.
- Chat fonts, line and message spacing, emote sizing and animation, alternating message backgrounds, and optional pause-on-hover scrolling. Changes apply live.
- Filters for words, phrases, usernames, announcements and subscription notices.
- Searchable settings with accent colours, backgrounds, sidebar sizing, player defaults, configurable notification sounds and remappable shortcuts.
- Named preference profiles and saved multiview layouts.
- Confirmed resets for individual settings categories or all customisation settings, keeping your account, open streams and saved setups.
- Clickable usernames with a history of their loaded messages in that channel. User history, mentions and reply-thread windows can be moved, resized and searched.
- Theatre, fullscreen and chat-only modes.
- Optional Twitch login for sending messages and viewing followed channels. Watching and reading chat work without signing in.
- An optional twitch.tv session (Settings), signed in on Twitch's own page, for 1440p/source quality, your subscriber and Turbo benefits, subscriber-only VODs, and watch streaks and channel points for the streams you watch.

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
| Esc in the message box | Cancel a reply |

Single-letter shortcuts are inactive while typing or using an input control.

## Notes

Glint uses Twitch's unofficial web API for channel information and playback. Changes to that API can affect compatibility. Clip creation requires the separate API login in Settings.

Settings are stored in `%APPDATA%\Glint\settings.json`. Login tokens are encrypted for the current Windows user.

Glint is built with WinUI 3, [mpv](https://mpv.io) and Win2D. Thanks to [7TV](https://7tv.app), [BTTV](https://betterttv.com) and [FFZ](https://frankerfacez.com) for their emote services, and [Chatterino](https://chatterino.com) for inspiration.

Glint is independent of Twitch and is not affiliated with or endorsed by it.

## Licence

Glint is [MIT licensed](LICENSE). libmpv is downloaded separately and covered by [mpv's licence](https://github.com/mpv-player/mpv/blob/master/Copyright).
