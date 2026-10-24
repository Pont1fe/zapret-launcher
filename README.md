# Zapret Launcher

[Русский](README.ru.md) · **English**

A friendly Windows launcher for [zapret-discord-youtube](https://github.com/Flowseal/zapret-discord-youtube) by Flowseal — bypass DPI blocking of Discord, YouTube and Telegram with one click.

## Install

1. Download **ZapretLauncherSetup.exe** from [Releases](https://github.com/Pont1fe/zapret-launcher/releases/latest).
2. Run it and click “Install” (default folder: `C:\ZapretLauncher`). On first launch it downloads the latest zapret by itself.
3. Click the big on/off card or “Find best strategy”.

> Windows SmartScreen may warn that the app is unsigned: “More info” → “Run anyway”. Antivirus software may flag the WinDivert driver that zapret needs — add the launcher folder to exclusions.

## Features

- **One-click bypass**, strategy switching, tray mode; winws runs without a console window.
- **Automatic strategy search**: tests every strategy against Discord, YouTube, YouTube video and Telegram and picks the best one.
- **Site cards** on the main screen: HTTPS time through bypass and TCP ping to each server.
- **Watchdog**: restarts a crashed winws and switches strategy when sites stop opening while the internet works.
- **VPN pause**: bypass pauses while a VPN is on and comes back when it is off.
- **Per-network strategies**: home, mobile hotspot and other Wi-Fi each get the strategy that worked there.
- **Telegram**: Desktop through the local [tg-ws-proxy](https://github.com/Flowseal/tg-ws-proxy), Web through hosts entries — one switch.
- **Auto-update** of zapret, tg-ws-proxy and the launcher itself; your lists and filters are kept, the previous zapret version can be restored with one click.
- **Domains, Game Filter and IPSet** right in the window, no `service.bat` needed.
- **Windows notifications** with action buttons, **start with Windows** via Task Scheduler (no UAC prompt).
- Russian and English interface; uninstall from Windows “Installed apps”.

## Build from source

Only Windows 10/11 is required — the C# compiler of .NET Framework 4.8 that ships with Windows is used:

```
powershell -ExecutionPolicy Bypass -File build.ps1
```

`ZapretLauncher.exe` and `ZapretLauncherSetup.exe` appear in `dist`. Releases are built by GitHub Actions: bump `AppVersion` in `app/App.cs`, update `RELEASE_NOTES.md` and push a tag `vX.Y.Z`.

- `app/` — the launcher (WPF): `Launcher.cs` window and logic, `Zapret.cs` zapret handling, `Telegram.cs`, `Network.cs` site checks, VPN and networks, `Toast.cs` notifications, `I18n.cs` translation, `Shell.cs` icons, shortcuts, self-update, `ui.xaml` layout.
- `installer/` — the installer (WinForms).

## Credits

- [Flowseal/zapret-discord-youtube](https://github.com/Flowseal/zapret-discord-youtube) and [bol-van/zapret](https://github.com/bol-van/zapret) — the bypass itself.
- [Flowseal/tg-ws-proxy](https://github.com/Flowseal/tg-ws-proxy) — the Telegram Desktop proxy.

This is an unofficial wrapper; zapret and tg-ws-proxy are downloaded directly from their authors' releases.
