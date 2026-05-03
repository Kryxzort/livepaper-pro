# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

**livepaper** is a Linux desktop app (C# + Avalonia UI) that fetches live wallpapers from online sources and applies them using [mpvpaper](https://github.com/GhostNaN/mpvpaper). mpvpaper renders video wallpapers on Wayland by playing them with MPV behind all windows.

## System Dependencies

- `mpvpaper` — must be installed and on `$PATH`
- `mpv` — underlying player used by mpvpaper
- Wayland compositor (e.g. Hyprland, Sway, GNOME on Wayland)
- `.NET SDK` — for building
- `pactl` / `parec` (from `libpulse` on Arch) — PulseAudio/PipeWire CLI tools; required for Auto-Mute stream detection and audio level measurement
- `ffmpeg` — required for thumbnail extraction in the **Import Wallpaper** flow and for extracting static frames from GIF thumbnails in the Wallpaper Engine scraper (cached to `~/.cache/livepaper/we_thumbs/`)
- `playerctl` *(optional)* — used by `AutoMuteOnlyIfMprisActive`; if absent, `IsAnyMprisPlayerActive()` always returns false (feature silently disabled)
- `wl-clipboard` — `wl-copy` is invoked by the Settings-tab keybind Copy buttons so snippets persist after livepaper exits. Falls back to Avalonia's clipboard if missing, but the clipboard releases the selection on app close (snippet only pasteable while livepaper is open).

### Optional

- `linux-wallpaperengine` — required for Wallpaper Engine **scene** support. Enabled via Settings → Sources → "Allow scene support". Spawned per-monitor via `setsid`. Has no IPC socket — volume/mute go through PulseAudio `pactl set-sink-input-volume/mute` targeting SDL sink inputs.

## Common Commands

```bash
dotnet run --project src/livepaper                            # run the app
dotnet run --project src/livepaper -- --restore               # restore last session without opening UI
dotnet run --project src/livepaper -- --random                # apply a random library wallpaper without opening UI
dotnet build src/livepaper                                    # build (no solution file at repo root)
dotnet publish src/livepaper -r linux-x64 --self-contained    # single binary release
```

## CLI Flags

- `--restore` — re-applies the last session (single video, playlist, or random) without opening the UI. Useful for compositor autostart (e.g. `exec-once = livepaper --restore` in Hyprland). Always one-shot: for timed playlists it `setsid`-spawns `--timer-daemon` and returns immediately.
- `--random` — picks a random video from the library and applies it, then exits. Saves the picked video so `--restore` replays the same one.
- `--kill` — stops playback (kills mpvpaper and signals the timed playlist timer to stop), then exits.
- `--monitor` *(internal)* — starts `AudioMonitor` using saved settings and blocks indefinitely. Spawned automatically as a detached process when the app closes with AutoMute enabled; killed when the app reopens.
- `--timer-daemon` *(internal)* — owns the timed-playlist tick loop and blocks indefinitely. Spawned automatically as a detached process by `--restore` (timed-playlist case) and by the GUI on close; killed when the app reopens.
- `--action=<action>` — sends a command to the running session and exits. Intended for compositor keybinds. The Settings tab also exposes these as copy-paste keybind snippets, so the action set is duplicated with `--kill`/`--restore` for parity. Actions:
  - `toggle-mute` — toggle user mute (calls `SetUserMute`, persists to `user_mute.state`; blocks auto-unmute)
  - `toggle-pause` — pause/resume mpv playback AND the timed playlist timer (timer resumes with remaining time preserved)
  - `stop` — stop playback (alias of `--kill`)
  - `play` — relaunch the last session (alias of `--restore`)
  - `toggle-play` — stop if playing, otherwise relaunch the last session
  - `next-wallpaper` — advance to next wallpaper in history/playlist
  - `previous-wallpaper` — go back in wallpaper history
  - `random` — alias of `--random` (picks from active playlist if running, else from library)
  - `volume-up` / `volume-down` — adjust volume by 5 (clamped 0-100); persists to settings.json so the slider and next launch reflect the new value

## UI Structure

The app has three tabs:

### Browse Tab
- Source selector (pill-style) to switch between motionbgs.com, moewalls.com, Desktophut, and Wallpaper Engine local
- Grid of wallpaper cards (`ItemsRepeater + UniformGridLayout`, responsive columns); thumbnail + title + SCENE badge (if scene)
- Clicking a thumbnail opens a fullscreen preview modal; GIF thumbnails animate on hover
- **Auto-search**: search box triggers debounced (200ms) load — no Search button needed
- **Sort button** (WE source only, `SupportsSorting`): 7 options — Default, Name A–Z, Name Z–A, Videos first, Scenes first, Newest added, Oldest added
- Refresh button and loading bar (thin strip below the top bar, no layout shift)
- Per-card "Download & Apply" downloads + applies that card only
- **Selection toolbar** docks at bottom when ≥1 card is selected (Shift-click / Ctrl-click / Ctrl+A): "N selected", `Download` (downloads all selected, no apply), `Cancel`

### Library Tab
- Grid of all downloaded wallpapers (`ItemsRepeater`, responsive columns); cards show SCENE badge, crash warning icon (⚠) if `HasCrashed`, green border if `IsCurrentlyPlaying`, accent border if selected
- Circular badge top-right: `+` to add to playlist, `−` to remove. Always visible.
- Library search box (debounced 200ms) + sort button (6 options: Name A–Z, Z–A, Videos first, Scenes first, Newest, Oldest)
- Per-card: Apply (sets as wallpaper), Delete (soft-delete → `.trash/`; **Delete** key also triggers; **Ctrl+Z** to undo)
- "Import" button: file picker (`.mp4`/`.webm`/`.mov`/`.mkv`/`.avi`/`.gif`); title-input modal copies video to library, runs `ffmpeg` for 320px thumbnail at 1s. `.id` sidecar holds `import:<source-path>` for re-import dedupe.
- "Play All" + "Shuffle" toggle — rotation follows global Settings → PLAYLIST panel
- "Stop" button (danger style)
- **Preview modal** (click thumbnail): animated GIF support; crash warning panel with whitelist toggle; **per-wallpaper volume slider** (0–100, "↺ Global" resets to global); **per-wallpaper speed slider** (0.1–4×, hidden for scenes); title, duration, workshop ID with copy button
- **Selection toolbar** docks above the playlist strip when ≥1 card is selected: "N selected", `Add to Playlist`, `Remove from Playlist`, `Delete`, `Cancel`
- **Playlist strip** (always visible at bottom): horizontal row of small thumbnails; GIF thumbnails animate on hover; green border on currently-playing item; circular `−` badge; hover reveals dim + ▶ play overlay; drag to reorder; click plays that wallpaper
  - ⚙ opens settings popup (Sequential/Shuffle; per-playlist `Override global rotation settings` unlocks Interval, AdvanceOnVideoEnd, WaitForVideoEnd)
  - 📂/💾 load/save named playlists (modals); stored at `~/.local/share/livepaper/playlists/<name>.json`
  - ▶ Play starts the playlist
  - Playlist auto-state saved to `~/.config/livepaper/playlist_state.json` on every change
- **Status bar**: playback info; animated undo button appears when `CanUndo`

### Settings Tab
- **Playback**: Loop, Mute audio, Disable cache, Volume slider (0–100, live via mpv IPC), Speed slider (0.1–4.0×, live via IPC)
- **Playlist (global rotation)**: Switch when video ends, Wait for video to end after interval fires, Hours/Minutes/Seconds interval. Drives Play All and any playlist that doesn't have `Override global rotation settings` enabled.
- **Auto-Mute**: Mute when system audio plays + threshold/delay knobs + "Only mute if MPRIS media player is active" checkbox
- **Memory**: Demuxer max bytes / back bytes (NumericUpDown, integer MiB)
- **Rendering**: Hardware decoding (auto / nvdec / vaapi / no), Video scale (fill / fit)
- **Sources / Wallpaper Engine**: workshop folder picker, Copy files toggle, "Allow scene support (experimental)" checkbox. When enabled: monitor list editor (name, FPS, primary toggle), scene transition delay slider.
- **Appearance**: Theme selector (31 built-in themes), Thumbnail aspect ratio (Default / 16:9 / 1:1), Card size (Small / Medium / Large)
- Live mpv options preview; Reset to Defaults; copy-paste keybind snippets for `--action=…`

## Architecture

```
src/livepaper/
├── Models/         # WallpaperResult, WallpaperDetail, LibraryItem, AppSettings, LastSession, AppTheme, LweMonitorSettings
├── Scrapers/       # MotionBgsScraper, MoewallsScraper, WallpaperEngineScraper
├── Services/       # IBgsProvider interface + one service per source
├── Helpers/        # DownloadHelper, PlayerHelper, LibraryService, SettingsService, AudioMonitor, ThemeService, MonitorDetector
├── ViewModels/     # MVVM view models (CommunityToolkit.Mvvm); includes LweMonitorViewModel
└── Views/          # Avalonia XAML views
```

Each scraper is a static class handling HTTP + HTML parsing. Each service wraps a scraper and implements `IBgsProvider` for use by the UI.

## Wallpaper Sources

All HTTP requests must send a Firefox User-Agent:
```
Mozilla/5.0 (X11; Linux x86_64; rv:130.0) Gecko/20100101 Firefox/130.0
```

Use a single shared `HttpClient` instance (not one per request).

### motionbgs.com (HtmlAgilityPack)

**Listing:** `GET https://www.motionbgs.com/hx2/latest/{page}/`
- Parse `//a` tags: thumbnail from `.//img[src]` (prefer `data-cfsrc` over `src` for Cloudflare lazy-load), title from `.//span[@class='ttl']`, resolution from `.//span[@class='frm']`, page URL from `a[href]`
- Skip links where the path is empty, starts with `tag:`, or starts with `search` (filters out nav/brand links)

**Search:** `GET https://www.motionbgs.com/search?q={query}&page={page}`
- Site may redirect to a tag page (e.g. `/tag:car/`) — detect via final URL
- Tag pages: use `ParseLinks` (same as listing)
- Search results pages: parse `//div[contains(@class,'tmb')]` → `//a` tags
- Thumbnail: try `img[data-cfsrc]` → `img[src]` → `noscript > img[src]` (use explicit `if (string.IsNullOrEmpty)` checks, not `??` chains, because `GetAttributeValue` returns `""` not null)

**Individual page** (fetched before download):
- Preview video: `//source[@type='video/mp4'][src]`
- Download link: `//div[@class='download']//a[href]`

### moewalls.com (HtmlAgilityPack)

Plain HTTP with the Firefox User-Agent works — no browser automation needed.

**Listing:** `GET https://moewalls.com/page/{page}`
**Search:** `GET https://moewalls.com/page/{page}/?s={query}`
- Parse `//li[contains(@class,'g1-collection-item')]`
- Thumbnail: `.//img[src]`
- Title + page URL: `.//a[@class='g1-frame'][title, href]`

**Individual page** (fetched before download):
- Preview video: `//source[@type='video/mp4'][src]` — prefix with base URL if relative
- Download element: `//*[@id='moe-download']` (use `*` not `button` — element type changed)
- Download URL: `https://go.moewalls.com/download.php?video={data-url}`
- **Downloads require a `Referer` header** set to the wallpaper's page URL.

### Wallpaper Engine local

- Workshop path: `~/.local/share/Steam/steamapps/workshop/content/431960/`
- Discovery driven from each `<id>/project.json`:
  - `type == "video"` → video wallpaper (`file` field = video filename, any container mpv supports)
  - `type == "scene"` or `scene.pkg` present → scene wallpaper (included only when `AllowScenes` is true)
  - `type == "web"` / `"application"` → skipped always
  - `title` field becomes the wallpaper title
- Thumbnail: `preview.jpg` preferred; GIF thumbnails extract a static frame via ffmpeg and cache it in `~/.cache/livepaper/we_thumbs/`; fallback to any `.png`/`.jpg`/`.jpeg`

## File Naming & Library

Library storage: `~/.local/share/livepaper/library/`
Each entry stores: `{Title}.mp4` + `{Title}.jpg` (thumbnail) + `{Title}.id` (source URL for dedup).

The `.id` sidecar file contains the source page URL. On download, if a wallpaper with the same source URL already exists in the library, the download is skipped and the existing file is applied directly.

Config: `~/.config/livepaper/settings.json` — Cache: `~/.cache/livepaper/`

### Sidecar files (all share the video/scene base name)

| Extension | Purpose |
|---|---|
| `.id` | Source URL or workshop ID |
| `.volume` | Per-video volume override (int, 0–100) |
| `.speed` | Per-video speed override (double, InvariantCulture) |
| `.crashed` | Empty file — scene crashed; card shows warning |
| `.whitelist` | Empty file — scene stays in playlist even if crashed |
| `.scene` | Library scene entry; content = workshop ID |

Soft delete moves all sidecars to `.trash/<batchId>/`. Trash is purged on window close or cleared on startup. Ctrl+Z restores the last batch.

### File paths beyond the main three

- `~/.config/livepaper/lwe.pid` — PIDs of running LWE processes
- `~/.config/livepaper/user_mute.state` — presence = user muted (guards against auto-unmute)
- `~/.cache/livepaper/playlist_observer_paths.json` — shuffled path order for playlist observer reconnection
- `~/.cache/livepaper/we_thumbs/<workshopId>.jpg` — static frame extracted from GIF thumbnails

## Player

`PlayerHelper` is the single entry point for mpvpaper. It kills all existing mpvpaper processes before starting a new one. **stdout/stderr are NOT redirected** for mpvpaper or LWE — `RedirectStandardOutput/Error = false`. Do not add `BeginOutputReadLine/ErrorReadLine` calls.

**IPC readiness**: after spawning mpvpaper, `Launch` polls `TryQueryTimeRemaining()` up to 40 times × 50 ms (2 s total) to detect when the IPC socket is ready. This replaces the old stdout `AV:`/`V:` line detection. `TryQueryTimeRemaining` splits the raw socket buffer on `\n` and tries to parse each line as JSON — mpv sometimes sends multiple responses in one read.

**Single video:**
```
mpvpaper -o "<mpv-options>" '*' /path/to/wallpaper.mp4
```

**Playlist (Play All / Shuffle):**
- Shuffle is **pre-applied** to the path array in `ApplyPlaylist()` (not via mpv `--shuffle` flag) so the shuffled order is known and can be saved to `playlist_observer_paths.json` for observer reconnection
- Writes all-but-last paths to `~/.cache/livepaper/playlist.txt`
- Passes last path as positional arg so all N videos appear exactly once (playlist.txt has N-1 entries; mpv processes `--playlist` before positional arg, giving indices 0..N-1)
- Adds `--playlist=<file> --loop-playlist=inf` to mpv options
- Does NOT include `loop` (per-file loop) in playlist mode — mpv must advance to the next entry
- If any scene paths are present and `AllowScenes` is true, `ApplyPlaylist` upgrades to `ApplyTimedPlaylist` internally (mixed playlist)

`'*'` targets all Wayland outputs.

`PlayerHelper.SetMute(bool)` / `SetUserMute(bool)` — see **Mute: user vs auto** section below.

## Mute: user vs auto

**`SetMute(bool)`** — auto-mute path. If `!mute && (_userMuted || UserMuteStatePath exists)`, returns without unmuting. Cannot override user mute.

**`SetUserMute(bool)`** — user action path. Writes/deletes `UserMuteStatePath`, updates `_userMuted`. `toggle-mute` CLI action uses this, not `cycle mute` IPC.

**`AudioMonitor.IsMuted`** — exposed so `PlayerHelper` can read mute state on LWE launch.

**`PlayerHelper.IsMuted`** — public `bool IsMuted => _isMuted` property used by `Program.cs` for the toggle-mute 3-way logic.

**Scene→video mute**: when transitioning from a scene to a video, `SwitchToFile` bakes `--mute=yes` directly into `launchOpts` if `_isMuted` is true (rather than sending a post-launch IPC command which races against mpv startup). Always read `_isMuted` fresh inside the task — never capture it into a `bool` local before the `Task.Run`, or the value goes stale.

**LWE orphan prevention**: `_prelaunchPidsToKill` (field `private static string[]? _prelaunchPidsToKill`) holds LWE pids from an in-flight scene launch that was cancelled before the process started. Rapid wallpaper switching can cancel the deferred kill task before it executes; `SwitchToFile` checks and kills these at entry. Also cleared in `TeardownTimer` and the crash-detection block.

## Auto-Mute (`AudioMonitor`)

`AudioMonitor.Start/Stop` is called by the ViewModel when `AutoMute` is toggled or its settings change. Three concurrent tasks:

1. **`WatchStreamsAsync`** — runs `pactl subscribe` and maintains a `ConcurrentDictionary<uint, CancellationTokenSource>` of per-stream monitors. On `'new'`: parses the stream ID from the event line, starts `parec` immediately, then verifies non-mpv after 100ms in a background task (cancels if mpv). On `'remove'`: cancels immediately. Initial reconciliation on startup. Filters out mpv streams (`application.process.binary = "mpv"` / `application.name = "mpv"`) and corked (paused) streams.

2. **`MonitorStreamAsync`** (one per non-mpv stream) — runs `parec --monitor-stream=<id> --format=float32le --channels=1 --rate=8000 --raw`, reads 160-sample chunks (20ms), computes peak dBFS. Calls `Interlocked.Increment/Decrement` on `_aboveThresholdCount` as the stream crosses the threshold. `finally` block always decrements if the stream was above threshold when cancelled.

3. **`WatchMuteAsync`** — polls `_aboveThresholdCount` every 20ms via `Task.Delay`. Counts consecutive ms above/below threshold; fires `PlayerHelper.SetMute` once the configured delay is exceeded.

**Critical invariant**: always use `--monitor-stream=<id>` targeting only specific non-mpv streams. Never use `@DEFAULT_MONITOR@` — it captures livepaper's own audio and causes oscillation (mute → silence → unmute → audio detected → mute...).

**SDL Application filter**: LWE registers its audio as `application.name = "SDL Application"`. `GetNonMpvStreamIdsAsync()` skips SDL Application sink inputs — without this, LWE audio triggers auto-mute of itself.

**`_aboveThresholdCount` reset**: done via `Interlocked.Exchange(..., 0)` at the top of `WatchStreamsAsync` (not in `Stop()`), so that stream monitor `finally` blocks from the previous run can decrement safely without racing against new monitors.

**Daemon persistence**: when the app closes with AutoMute enabled, `SpawnDetachedMonitor()` launches `livepaper --monitor` via `setsid` and writes its PID to `~/.config/livepaper/monitor.pid`. On next app open, `KillDetachedMonitor()` reads the PID file and kills the daemon before the app's own `AudioMonitor` takes over.

## Settings & Session Persistence

`AppSettings` (JSON at `~/.config/livepaper/settings.json`):
- Playback: `Loop`, `NoAudio`, `DisableCache`, `Volume` (0–100), `Speed` (0.1–4.0, default 1.0)
- Memory: `DemuxerMaxBytes`, `DemuxerMaxBackBytes` (int, MiB)
- `HwDec`: `"auto"` | `"nvdec"` | `"vaapi"` | `"no"`
- `VideoScale`: `"fill"` (panscan=1.0) | `"fit"` (panscan=0.0)
- Auto-mute: `AutoMute` (default false), `AutoMuteDelayMs` (200), `AutoUnmuteDelayMs` (2000), `AutoMuteThresholdDb` (-70.0), `AutoMuteOnlyIfMprisActive` (false)
- Global rotation: `GlobalIntervalSeconds` (1800), `GlobalAdvanceOnVideoEnd` (true), `GlobalWaitForVideoEnd` (false)
- Playlist: `PlaylistWaitForVideoEnd` (false)
- Wallpaper Engine: `WallpaperEnginePath`, `WeCopyFiles`, `ResumeFromLast`, `AllowScenes` (false)
- LWE: `LweSilent` (false), `LweVolume` (100), `LweMonitors` (list of `{Name, Fps, IsPrimary}`), `SceneTransitionDelayMs` (1000)
- UI: `ThumbnailAspect` ("Default"), `CardSize` ("Medium"), `LibrarySortIndex` (5 = newest first), `Theme` ("Catppuccin Mocha")
- `LastSession`: tracks the last applied mode for `--restore`

`CustomPlaylist` (JSON at `~/.config/livepaper/playlist_state.json` for in-progress, and per-name files in `~/.local/share/livepaper/playlists/`):
- `VideoPaths`, `Name` (nullable; only set after Save or Load)
- `Settings.Order` (Sequential/Shuffle), `Settings.OverrideGlobalSettings`, `Settings.IntervalSeconds`, `Settings.AdvanceOnVideoEnd`, `Settings.WaitForVideoEnd`
- Per-playlist `IntervalSeconds`/`AdvanceOnVideoEnd` are only used when `OverrideGlobalSettings` is true; otherwise the global values apply.

`LastSession` model:
- `IsPlaylist` — was it a Play All session
- `IsTimedPlaylist` — was it a custom timed playlist session
- `IsRandom` — was it a `--random` session
- `Paths` — video path(s) used
- `Shuffle` — was shuffle enabled
- `TimedIntervalSeconds` — switching interval for timed playlist
- `WaitForVideoEnd` — whether wait-for-video-end mode was active
- `AdvanceOnVideoEnd` — whether advance-on-video-end mode was active
- `OverrideGlobalSettings` — whether per-playlist override was active (so `--restore` uses the correct interval/mode rather than current globals)

`--restore` replays the session exactly: single video, playlist (with original paths + shuffle), timed playlist, or the specific video that `--random` picked.

**`RefreshLastSessionFromSettingsIfIdle()`** (ViewModel): syncs `LastSession` when playlist/rotation settings change while nothing is playing, so `--restore` picks up the new interval and mode. Only updates if the current playlist strip paths match `s.Paths` (or `s.OverrideGlobalSettings` is false). Called from all `OnChanged` handlers that affect rotation: `OnPlaylistWaitForVideoEndChanged`, `OnInterval*Changed`, `OnAdvanceOnVideoEndChanged`, `OnOverrideGlobalSettingsChanged`, `OnGlobal*Changed`.

**Timed playlist** (`PlayerHelper.ApplyTimedPlaylist`): `System.Threading.Timer` ticks every 100ms. Decrement uses real elapsed time (`DateTime.UtcNow` deltas) so jitter and pause windows don't drift the countdown. On shuffle, `AdvanceToNext` re-randomizes `_timedPaths` at the end of each cycle, with a do/while guarantee that the new cycle's first ≠ the old cycle's last. State at `~/.config/livepaper/timed_state.json` is only re-written on significant events (advance, pending action processed, pause/stop signals, settings update, graceful handoff) — not every tick — because the in-memory `_timedRemainingMs` is now authoritative.

**Tick state sync**: each tick calls `RefreshSignals()` (lighter than `LoadTimedState`) which only re-reads `TimerStopped` and `TimerPaused`. The owner's authoritative `_timedRemainingMs` / history are left alone.

**Pending-action IPC** (`~/.config/livepaper/pending_action.txt`): atomic write+rename file used by CLI mutators when a timed playlist is active. Single string content: `next` / `prev` / `random`. The timer owner's tick consumes it (read+delete) and dispatches to `AdvanceAndLaunch` / `StepBackAndLaunch` / `RandomAndLaunch`. All three call `LaunchAndReset`, which kills the current mpvpaper, launches the new one, and resets `_timedRemainingMs` to the full interval. This makes `--random` skip-to-random preserve a full interval before normal progression resumes.

**`TimedState` record** persists to `timed_state.json` and includes: `TimerStopped`, `TimerPaused`, `AdvanceOnVideoEnd`, `WaitingForVideoEnd`. On restore/resume, `LoadTimedState` restores `_advanceOnVideoEnd` and `_waitingForVideoEnd`, then `RearmAdvanceOnVideoEnd()` re-arms the `DoVideoEndWait` task. When `_waitingForVideoEnd=true`, `_historyIndex` is already pre-fetched one step ahead — `RestoreTimedPlaylist` adjusts back by one before calling `SwitchToFile`, then calls `RearmAdvanceOnVideoEnd()`.

**`DoVideoEndWait` retry threshold**: 30 iterations (3 s) before breaking on `null` remaining. The socket can appear ready before the video file has finished loading; a 500 ms threshold caused premature scene switches.

**Stop/pause signals**: `TimedState` record includes `TimerStopped` and `TimerPaused` bool flags persisted to `timed_state.json`:
- `Stop()` → `KillAll()` (kills mpvpaper) + `SignalTimerStop()` (read-modify-write `TimerStopped=true`); the daemon's stopped-branch in `Tick()` also calls `KillCurrentProcess()` to cover the kill→launch race where the daemon launched a new mpvpaper after CLI's `KillAll`
- `TogglePause()` → sends `cycle pause` to mpv IPC + flips `TimerPaused` in state file; tick freezes the decrement while paused, resumes from where it left off on unpause
- `NextWallpaper()` / `PreviousWallpaper()` / `ApplyRandom()` write to `pending_action.txt` if a timed playlist is active; otherwise fall back to mpv's `playlist-next`/`playlist-prev` IPC (single video / Play All) or a one-shot library random pick

**Timer ownership**: at most one process owns the timer at a time.
- Daemon writes `timer.pid` (via `WriteTimerDaemonPid`); GUI writes `gui_timer.pid` on startup, deletes on close.
- `KillTimerDaemon()` kills any orphan daemon by PID file (called on GUI startup and inside `SpawnTimerDaemon`).
- `SpawnTimerDaemon()` bails if `IsGuiTimerAlive()` so `--restore` from a terminal can't spawn a competing daemon while the GUI is open.
- GUI's window-close handler clears `gui_timer.pid` *before* calling `SpawnTimerDaemon`, allowing the handoff.

**`IsTimedPlaylistActive()`**: state-file check (`Paths.Count > 0 && !TimerStopped`) used by `toggle-play` and the CLI mutators to decide whether to delegate via `pending_action.txt` or fall back to local execution. Survives the brief kill→launch gap where mpvpaper is momentarily down.

## Distribution

### Scripts (`scripts/`)
- `install.sh` — builds a self-contained single binary (`PublishSingleFile=true`), installs to `~/.local/bin/`, drops desktop entry in `~/.local/share/applications/`, installs icon to `~/.local/share/icons/hicolor/512x512/apps/`
- `build-appimage.sh` — same build, packages into `livepaper-x86_64.AppImage` using `appimagetool` (downloaded automatically if not in PATH)
- `PKGBUILD` + `.SRCINFO` — AUR package `livepaper-git`, published at `aur.archlinux.org/packages/livepaper-git`

When updating the AUR package, regenerate `.SRCINFO` and push to the separate AUR git repo:
```bash
cd scripts && makepkg --printsrcinfo > .SRCINFO
cp PKGBUILD .SRCINFO /tmp/aur-livepaper/
cd /tmp/aur-livepaper && git add PKGBUILD .SRCINFO && git commit -m "..." && git push
```

### Assets (`src/livepaper/Assets/`)
- `livepaper.svg` — source icon (monitor + play button, transparent background)
- `livepaper.png` — 512×512 PNG exported from SVG via `rsvg-convert`

To regenerate the PNG after editing the SVG:
```bash
rsvg-convert -w 512 -h 512 src/livepaper/Assets/livepaper.svg -o src/livepaper/Assets/livepaper.png
```

### Commit style
Short, title-case, no period. e.g. `Fix App Name`, `Add Shuffle Toggle`.

## Avalonia Gotchas

- **`NumericUpDown.Value` is `decimal?`** — binding to an `int` ViewModel property fails silently (the box appears but you can't type). Always use `decimal` for properties bound to `NumericUpDown`.
- **Drag-and-drop in Avalonia 12** uses a completely different API from older versions and most online examples: `DataFormat.CreateInProcessFormat<T>`, `DataTransferItem.Create`, `DataTransfer.Add`, `DragDrop.DoDragDropAsync`, and `DragEventArgs.DataTransfer` (not `.Data`). For reordering within the same control, manual pointer tracking (`PointerPressed`/`PointerMoved`/`PointerReleased` at window level) is simpler and gives full control over visual feedback.
- **Window-level pointer handler**: use `this.AddHandler(PointerPressedEvent, handler, RoutingStrategies.Bubble, handledEventsToo: true)` to catch all clicks including on buttons. Use `IsWithin(source, scrollViewer)` to scope which area the click belongs to, and `IsWithinButton(source, stopAt)` with a `stopAt` boundary to avoid walking past the target container.
- **`StaticResource` vs `DynamicResource`**: themes require `DynamicResource` — `StaticResource` resolves once at load time and won't update when theme colors are swapped at runtime. All color brush bindings in the app use `DynamicResource`.
- **Library grid binds to `FilteredLibraryWallpapers`**, not `LibraryWallpapers`. During bulk `LoadLibrary()`, `_suppressFilterUpdate = true` prevents per-item recalculation; `UpdateFilteredLibrary()` is called once after.
- **Animated GIF cards**: three-layer `Panel` — static JPG (`StaticThumbnailSource`, visible when non-null) → static image (`ThumbnailSource`) → animated GIF (`ActiveGifSource`, only non-null when `IsGifActive`). Set `IsGifActive = true/false` from `PointerEntered/Exited` handlers in code-behind. GIF `AnimatedImageSource` is lazy-loaded on first hover.

## Key NuGet Packages

- `Avalonia`, `Avalonia.Desktop`, `Avalonia.Themes.Fluent` — UI framework
- `Avalonia.Controls.ItemsRepeater` — `ItemsRepeater` control used in Browse and Library grids (replaces `WrapPanel + ItemsControl`)
- `AsyncImageLoader.Avalonia` — `AdvancedImage` control for HTTP image loading (bind `Source` to a string URL)
- `AnimatedImage.Avalonia` — animated GIF rendering; use `aimg:ImageBehavior.AnimatedSource` (not `Source`)
- `Material.Icons.Avalonia` — `<mi:MaterialIcon Kind="..."/>` icons throughout the UI
- `CommunityToolkit.Mvvm` — `[ObservableProperty]`, `[RelayCommand]`, source generators
- `HtmlAgilityPack` — HTML parsing for scrapers
- `System.Text.Json` — JSON parsing for `project.json` and settings

## Theme System

`ThemeService.All` — 31 built-in themes (`AppTheme` record with 15 color fields). `ThemeService.Apply(theme)` writes all 15 colors into `Application.Current.Resources` as `SolidColorBrush`, triggering all `DynamicResource` bindings. Theme name persisted in `AppSettings.Theme`. Applied at app init in `App.Initialize()` before any window shows.

Color keys: `BgBase`, `BgMantle`, `BgCrust`, `Surface0`–`Surface2`, `TextColor`, `Subtext`, `Muted`, `Accent`, `AccentFg`, `AccentHover`, `Danger`, `DangerBg`, `Success`.

## UI Styling

Colors are runtime-swappable via `ThemeService` (see Theme System above). The 15 color resource keys are listed there. Default theme is Catppuccin Mocha.

Button classes: `.accent`, `.ghost`, `.danger`, `.backdrop` (modal overlay — no hover/press feedback). All buttons have a `scale(0.96)` press animation (65ms transition).
Hover states use `/template/ ContentPresenter#PART_ContentPresenter` selectors.
Tab underline styled via `TabItem:selected /template/ Border#PART_SelectedPipe`.
