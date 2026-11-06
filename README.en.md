# 12 island — a Dynamic Island for Windows

12 island lives at the top of the screen: a notch or a capsule at rest, a panel when you point at it, holding notifications,
music control, system readings, clipboard history and file conversion in one place.

## Features

- **Notifications**: watches a fixed whitelist of chat and mail programs — WeChat, QQ, TIM, WeCom, DingTalk, Feishu, Teams,
  Slack, Discord, Telegram, WhatsApp, LINE, Skype, Zoom, Tencent Meeting, Notion, Outlook, Foxmail, Thunderbird, NetEase Mail
  Master and QQ Mail. When a message waits, its taskbar (or dock) icon flashes and the collapsed island becomes a capsule that
  carries the waiting programs' icons and counts, drawing attention with a looping bounce or sonic burst until clicked. Click
  the capsule to bring that program's window up and mark all of its notices read. Nothing outside the whitelist is shown.
- **Music**: cover, equalizer, lyrics and playback control through Windows' media sessions, with optional full control (NetEase
  Cloud Music / KuGou / Apple Music). On the collapsed island the volume's rhythm strip follows the sound itself.
- **System monitor**: CPU, memory, drives, network, temperatures, power, frame rate and processes; readings can also show on the
  collapsed island.
- **Clipboard**: text, pictures and files, with search, stars, notes and one-click copy back; clearing keeps the starred ones.
- **Conversion**: pictures, documents, spreadsheets, text, sound, video and PDF, all converted on this machine (sound and video
  use the bundled FFmpeg); pick exactly which files convert with per-row checkboxes, and results land next to the source files.
- **Look**: notch, capsule or indicator at rest; dark / light themes (system-following), adjustable opacity; page order, size and
  place are all adjustable; one island per display. An expanded panel stays five seconds and collapses on its own, with
  「悬停」(hold) and 「收起」(collapse) buttons at its bottom-right to keep it or close it at once.

## Running

- **Windows**: Windows 10 (build 19041 or newer) and Windows 11, x64.
- **No installation and no dependencies**: the released `12Island.exe` is a single self-contained file — the .NET runtime, the
  WPF native libraries and FFmpeg are all inside it. Put it anywhere and run it.
- Data lives in `%LOCALAPPDATA%\12Island`; copy that folder to keep your settings and history on another machine.

## Notes

- **Notifications**: what is watched is the flashing of a taskbar or dock icon — the shell announces every flash to programs that
  register a shell hook — so the taskbar, Winstep Nexus, RocketDock and the like are all covered. 12 island reads no chat
  history and uploads nothing.
- **Mail**: fill in the address and the app password (generated in the mailbox's settings, not the login password) under
  "Settings › Notifications"; standard IMAP servers such as QQ Mail, 163, Gmail and Outlook work.
- **Temperatures and power** need an outside sensor source (HWiNFO's shared memory or LibreHardwareMonitor's web service);
  without one the matching readings hide themselves.
- **Conversion**: drop files onto the convert page, or right-click them in Explorer and choose "Convert with 12 island".

## Building

Requires the .NET 10 SDK (x64):

```
dotnet publish src/12Island/12Island.csproj -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true
```

- The version is the name of the newest file in `releases/`; `scripts/Version-Bump.ps1` sets the next version from how much of
  the source changed (major / minor / patch), and each version's changes are written in its release notes file.
- The installer and the portable package are produced by `scripts/Publish-Release.ps1`.

## Releasing

1. Create a GitHub release with the tag `vX.Y.Z` and the notes from `releases/X.Y.Z.md`;
2. upload `12Island-X.Y.Z-windows-x64.exe` — the in-app updater recognises a new version by that asset name.

## License

Released under the [MIT License](LICENSE).
