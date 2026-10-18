# Glint

A Twitch client for Windows that doesn't feel like a browser tab.

twitch.tv chugs in the browser, and none of the desktop apps out there had proper 7TV support, so here's
one that does. It's a native WinUI 3 app. Video goes through mpv and chat is drawn on the GPU, so it stays smooth
even in channels with 50k people spamming emotes.

## What it does

- Plays live streams through mpv with hardware decoding. Switching channels takes about a second.
- Chat with 7TV, BTTV and FFZ emotes: animated ones, zero-width ones, and updates on the spot when a channel adds or removes an emote
- Past broadcasts, with chat replay synced to the video
- Optional ad skipping (off by default, see below)
- Tabs, a sidebar of the channels you follow, Tab-completion for emotes and names, a chat-only mode

## Ads

Glint plays ads like the website does unless you turn on **Settings → Video → Skip ads**. With that on,
when ad segments show up in the stream it switches to another copy of the same stream that usually doesn't
have them, and switches back once the break is over. That goes against Twitch's terms of service, which is
why it's off unless you choose it.

## Building

You'll need the .NET 10 SDK.

```
powershell -ExecutionPolicy Bypass -File tools\fetch-mpv.ps1
dotnet build src\Glint
```

`tools\install.ps1` builds a release copy into `%LOCALAPPDATA%\Programs\Glint` and adds Start menu and
desktop shortcuts. Run it again to update.

## Keys

| | |
|---|---|
| F, F11, double-click | fullscreen |
| M | mute |
| Space | pause |
| ← / → | skip 10s (VODs) |
| Ctrl+W | close tab |
| Ctrl+Tab | next tab |
| Ctrl+T | add a channel |
| F5 | reload / jump back to live |
| Tab (in the chat box) | complete an emote or @name |

## Not done yet

- 7TV name paints and badges
- An emote picker
- Clips
- Only tested on Windows 11

## Thanks

[mpv](https://mpv.io) for the playback, [7TV](https://7tv.app), [BTTV](https://betterttv.com) and [FFZ](https://frankerfacez.com)
for their open APIs, [Chatterino](https://chatterino.com) for setting the bar for how Twitch chat should feel,
and [streamlink](https://streamlink.github.io) for the ad-filtering idea.

Glint isn't affiliated with Twitch.

## Licence

MIT. mpv (libmpv) is LGPL/GPL and is downloaded separately by `tools\fetch-mpv.ps1`.
