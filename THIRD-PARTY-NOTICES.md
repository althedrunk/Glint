# Third-party components

Glint's own source code is MIT licensed. Other components retain their own licences. The downloadable Windows build includes licence files, notices and package metadata in its `licenses` folder.

- .NET: https://github.com/dotnet/runtime
- Windows App SDK and WinUI: https://github.com/microsoft/WindowsAppSDK
- Win2D: https://github.com/microsoft/Win2D
- WebView2 SDK: https://learn.microsoft.com/microsoft-edge/webview2/
- SkiaSharp and Skia: https://github.com/mono/SkiaSharp

The Windows download does not contain libmpv. When video playback is first requested, Glint offers to download the Windows x64 LGPL build directly from mpv's official development release:

https://github.com/mpv-player/mpv/releases/tag/git-release

Glint checks the archive against the SHA-256 digest supplied by GitHub before installing the library. The library, its supplied LGPL licence and a record of the download are saved in `%LOCALAPPDATA%\Glint\player`. Glint does not update or replace that library automatically on subsequent launches.

The downloaded library is LGPLv3 according to its publisher. Its licence is separate from Glint's MIT licence. The publisher's source code and build instructions are available at https://github.com/mpv-player/mpv. The downloaded `download.json` records the exact archive name, URL and checksums. You may replace `libmpv-2.dll` with a compatible version, including your own modified build. Glint imposes no restriction on reverse engineering to debug modifications to that library.

Source builds may use a separately obtained libmpv beside `Glint.exe`. That library's licence depends on its build and must be checked before redistribution.
