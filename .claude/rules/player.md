---
paths:
  - "src/livepaper/Helpers/PlayerHelper.cs"
  - "src/livepaper/Helpers/AudioMonitor.cs"
---

## Player

`PlayerHelper` is the single entry point for mpvpaper. Kills all existing mpvpaper processes before starting a new one. stdout/stderr are redirected and drained so mpvpaper output never corrupts the terminal.

**Single video:**
```sh
mpvpaper -o "<mpv-options>" '*' /path/to/wallpaper.mp4
```
`'*'` targets all Wayland outputs.

**Playlist (Play All / Shuffle):**
- Writes all-but-last paths to `~/.cache/livepaper/playlist.txt`; passes last as positional arg (N-1 in file + 1 positional = N total)
- Adds `--playlist=<file> --loop-playlist=inf`; does NOT include `loop` (per-file) in playlist mode

`PlayerHelper.SetMute(bool)` sends `set_property mute` via the mpv IPC socket.

## Auto-Mute (`AudioMonitor`)

`AudioMonitor.Start/Stop` called by ViewModel when `AutoMute` toggled or settings change. Three concurrent tasks:

1. **`WatchStreamsAsync`** — runs `pactl subscribe`; maintains `ConcurrentDictionary<uint, CancellationTokenSource>` per-stream. On `'new'`: starts `parec`, verifies non-mpv after 100ms in background. On `'remove'`: cancels immediately. Initial reconciliation on startup. Filters mpv streams (`application.process.binary = "mpv"` / `application.name = "mpv"`).

2. **`MonitorStreamAsync`** — `parec --monitor-stream=<id> --format=float32le --channels=1 --rate=8000 --raw`; reads 160-sample chunks (20ms), computes peak dBFS. `Interlocked.Increment/Decrement` on `_aboveThresholdCount`. `finally` always decrements if above threshold when cancelled.

3. **`WatchMuteAsync`** — polls `_aboveThresholdCount` every 20ms. Counts consecutive ms above/below threshold; fires `PlayerHelper.SetMute` once delay exceeded.

**Critical invariant**: always `--monitor-stream=<id>`. Never `@DEFAULT_MONITOR@` — captures livepaper's own audio → oscillation (mute → silence → unmute → audio detected → mute...).

**`_aboveThresholdCount` reset**: `Interlocked.Exchange(..., 0)` at top of `WatchStreamsAsync` (not in `Stop()`), so `finally` blocks from previous run can decrement safely without racing against new monitors.

**Daemon persistence**: on close with AutoMute enabled, `SpawnDetachedMonitor()` launches `livepaper --monitor` via `setsid`, writes PID to `~/.config/livepaper/monitor.pid`. On next open, `KillDetachedMonitor()` kills it before app's own `AudioMonitor` starts.
