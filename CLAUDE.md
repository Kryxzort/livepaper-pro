# CLAUDE.md

**livepaper** — Linux desktop app (C# + Avalonia UI) that fetches live wallpapers and applies them via [mpvpaper](https://github.com/GhostNaN/mpvpaper) on Wayland.

## System Dependencies

- `mpvpaper`, `mpv` — wallpaper player
- `.NET SDK` — build
- Wayland compositor (Hyprland, Sway, GNOME on Wayland)
- `pactl` / `parec` (libpulse) — Auto-Mute stream detection
- `ffmpeg` — thumbnail extraction (Import flow + WE GIF static frames cached to `~/.cache/livepaper/we_thumbs/`)
- `playerctl` *(optional)* — `AutoMuteOnlyIfMprisActive`; absent → always returns false
- `wl-clipboard` — `wl-copy` for keybind Copy buttons; falls back to Avalonia clipboard (releases on close)
- `dbus-send` — `org.freedesktop.FileManager1.ShowItems` for file highlighting; falls back to `xdg-open <dir>`
- `linux-wallpaperengine` *(optional)* — Wallpaper Engine scene support; spawned per-monitor via `setsid`; no IPC socket

## Common Commands

```bash
dotnet run --project src/livepaper                            # run
dotnet run --project src/livepaper -- --restore               # restore last session
dotnet run --project src/livepaper -- --random                # random library wallpaper
dotnet build src/livepaper                                    # build (no solution file at root)
dotnet publish src/livepaper -r linux-x64 --self-contained    # release binary
bash scripts/install.sh                                       # build + install to ~/.local/bin
```

## CLI Flags

- `--restore` — re-applies last session without opening UI. For timed playlists: `setsid`-spawns `--timer-daemon` and returns.
- `--random` — picks random library video, applies, exits. Saves pick so `--restore` replays it.
- `--kill` — stops playback, exits.
- `--monitor` *(internal)* — starts `AudioMonitor`, blocks. Spawned detached when app closes with AutoMute on.
- `--timer-daemon` *(internal)* — owns timed-playlist tick loop, blocks. Spawned by `--restore` (timed case) and GUI on close.
- `--action=<action>` — sends command to running session, exits. Actions:
  - `toggle-mute`, `toggle-pause`, `stop`, `play`, `toggle-play`
  - `next-wallpaper`, `previous-wallpaper`, `random`
  - `volume-up` / `volume-down` — ±5, clamped 0–100, persisted to settings.json

## Architecture

```text
src/livepaper/
├── Models/         # WallpaperResult, WallpaperDetail, LibraryItem, AppSettings, LastSession, AppTheme, LweMonitorSettings
├── Scrapers/       # MotionBgsScraper, MoewallsScraper, WallpaperEngineScraper
├── Services/       # IBgsProvider interface + one service per source
├── Helpers/        # DownloadHelper, PlayerHelper, LibraryService, SettingsService, AudioMonitor, ThemeService, MonitorDetector
├── ViewModels/     # MVVM (CommunityToolkit.Mvvm); includes LweMonitorViewModel
└── Views/          # Avalonia XAML views
```

Each scraper is a static class (HTTP + HTML parsing). Each service wraps a scraper and implements `IBgsProvider`.

## Commit Style

Short, title-case, no period. e.g. `Fix App Name`, `Add Shuffle Toggle`.
