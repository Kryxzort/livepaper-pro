using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using livepaper.Models;

namespace livepaper.Helpers;

public static class PlayerHelper
{
    private static Process? _current;
    private static Timer? _playlistTimer;
    private static List<string>? _timedPaths;
    private static int _timedIndex;
    private static string _timedOptions = "";
    private static bool _timedShuffle;
    private static TimeSpan _timedInterval;
    private static List<string>? _history;
    private static int _historyIndex = -1;
    private static bool _timedTimerPaused;
    private static bool _timedTimerStopped;
    private static long _timedRemainingMs;
    private static DateTime _lastTickTime;
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(100);
    private static readonly object _lock = new();
    private static CancellationTokenSource? _daemonCts;
    private static bool _waitForVideoEnd;
    private static bool _waitingForVideoEnd;
    private static CancellationTokenSource? _waitCts;
    private static CancellationTokenSource? _prelaunchCts;
    public static CancellationToken DaemonToken => _daemonCts?.Token ?? CancellationToken.None;

    public static bool IsPlaying =>
        (File.Exists(IpcSocket) && Process.GetProcessesByName("mpvpaper").Length > 0) ||
        IsLweRunning;

    private static bool IsLweRunning
    {
        get
        {
            try
            {
                if (!File.Exists(LwePidPath)) return false;
                return File.ReadAllLines(LwePidPath).Any(line =>
                {
                    if (!int.TryParse(line.Trim(), out int pid)) return false;
                    try { using var p = Process.GetProcessById(pid); return !p.HasExited; }
                    catch { return false; }
                });
            }
            catch { return false; }
        }
    }

    // Stale-tolerant check that survives the brief gap during a timed-playlist
    // switch where mpvpaper has been killed but the next instance hasn't launched.
    public static bool IsTimedPlaylistActive()
    {
        try
        {
            if (!File.Exists(TimedStatePath)) return false;
            var state = JsonSerializer.Deserialize<TimedState>(File.ReadAllText(TimedStatePath));
            return state != null && state.Paths.Count > 0 && !state.TimerStopped;
        }
        catch { return false; }
    }

    private static string IpcSocket => Path.Combine(
        Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") ?? Path.GetTempPath(),
        "livepaper", "mpv.sock");

    private static string TimedStatePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".config", "livepaper", "timed_state.json");

    private static string TimerDaemonPidPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".config", "livepaper", "timer.pid");

    private static string LwePidPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".config", "livepaper", "lwe.pid");

    private static string GuiTimerPidPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".config", "livepaper", "gui_timer.pid");

    private static string PendingActionPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".config", "livepaper", "pending_action.txt");

    public static void KillTimerDaemon()
    {
        try
        {
            if (!File.Exists(TimerDaemonPidPath)) return;
            var pidText = File.ReadAllText(TimerDaemonPidPath).Trim();
            if (int.TryParse(pidText, out int pid))
            {
                try { System.Diagnostics.Process.GetProcessById(pid).Kill(); } catch { }
            }
            File.Delete(TimerDaemonPidPath);
        }
        catch { }
    }

    public static void WriteGuiTimerPid()
    {
        try
        {
            var path = GuiTimerPidPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, Environment.ProcessId.ToString());
        }
        catch { }
    }

    public static void ClearGuiTimerPid()
    {
        try { File.Delete(GuiTimerPidPath); } catch { }
    }

    private static bool IsGuiTimerAlive()
    {
        try
        {
            if (!File.Exists(GuiTimerPidPath)) return false;
            var pidText = File.ReadAllText(GuiTimerPidPath).Trim();
            if (!int.TryParse(pidText, out int pid)) return false;
            using var _ = System.Diagnostics.Process.GetProcessById(pid);
            return true;
        }
        catch { return false; }
    }

    public static void SpawnTimerDaemon()
    {
        // Defensive guard: if a GUI is alive it owns the in-process timer.
        // Spawning a daemon would create two competing owners of mpvpaper.
        if (IsGuiTimerAlive()) return;

        FlushTimedState(); // persist current remaining time before handing off
        KillTimerDaemon();
        try
        {
            var selfArgs = GetSelfInvocationArgs();
            if (selfArgs.Count == 0) return;
            var psi = new System.Diagnostics.ProcessStartInfo("setsid")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var a in selfArgs) psi.ArgumentList.Add(a);
            psi.ArgumentList.Add("--timer-daemon");
            var proc = System.Diagnostics.Process.Start(psi);
            if (proc != null)
            {
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();
            }
        }
        catch { }
    }

    // Build the argv prefix needed to re-invoke this same livepaper process
    // (without args).
    //   - Self-contained (AppImage, install.sh): the apphost binary and
    //     entry assembly share a name (livepaper / livepaper.dll). The
    //     apphost runs its bundled dll automatically; we just spawn it.
    //   - Framework-dependent (AUR PKGBUILD): the process executable is the
    //     dotnet host. We need to pass the entry assembly path so the
    //     spawned host knows what to run.
    public static List<string> GetSelfInvocationArgs()
    {
        var args = new List<string>();
        var processPath = Environment.ProcessPath
            ?? System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
        if (string.IsNullOrEmpty(processPath)) return args;
        args.Add(processPath);

        var entryAsm = System.Reflection.Assembly.GetEntryAssembly()?.Location;
        if (string.IsNullOrEmpty(entryAsm) || !entryAsm.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            return args;

        // Apphost vs dotnet host: in a self-contained build the apphost is
        // named after the assembly (livepaper ↔ livepaper.dll), so the names
        // match and we don't need a separate dll arg. Framework-dependent
        // builds run under `dotnet` whose name differs.
        var procStem = Path.GetFileNameWithoutExtension(processPath);
        var asmStem = Path.GetFileNameWithoutExtension(entryAsm);
        if (!string.Equals(procStem, asmStem, StringComparison.OrdinalIgnoreCase))
            args.Add(entryAsm);

        return args;
    }

    public static void WriteTimerDaemonPid()
    {
        try
        {
            var path = TimerDaemonPidPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, Environment.ProcessId.ToString());
        }
        catch { }
    }

    public static void DeleteTimerDaemonPid()
    {
        try { File.Delete(TimerDaemonPidPath); } catch { }
    }

    public static void FlushTimedState()
    {
        lock (_lock) { SaveTimedState(); }
    }

    public static Action? OnTimedPlaylistStopped;
    public static Action<string?>? OnWallpaperChanged;
    public static Action<string>? OnSceneCrashed;

    private record TimedState(
        List<string> Paths, int Index,
        string Options, bool Shuffle, int IntervalSeconds,
        List<string> History, int HistoryIndex,
        bool TimerPaused = false, bool TimerStopped = false, long RemainingMs = 0,
        bool WaitForVideoEnd = false);

    private static void SaveTimedState()
    {
        if (_timedPaths == null || _history == null) return;
        try
        {
            var state = new TimedState(
                _timedPaths, _timedIndex,
                _timedOptions, _timedShuffle, (int)_timedInterval.TotalSeconds,
                _history, _historyIndex,
                _timedTimerPaused, _timedTimerStopped, _timedRemainingMs,
                _waitForVideoEnd);
            var path = TimedStatePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(state));
        }
        catch { }
    }

    // Lighter than LoadTimedState: only syncs flags that external mutators
    // can set (TimerStopped/TimerPaused). The owner's _timedRemainingMs and
    // history stay authoritative in-memory, so we don't need to save state
    // every tick.
    private static void RefreshSignals()
    {
        try
        {
            if (!File.Exists(TimedStatePath)) return;
            var state = JsonSerializer.Deserialize<TimedState>(File.ReadAllText(TimedStatePath));
            if (state == null) return;
            _timedTimerStopped = state.TimerStopped;
            _timedTimerPaused = state.TimerPaused;
        }
        catch { }
    }

    // Atomic write: a separate file means the timer owner's state file is
    // never clobbered by external mutators.
    private static void WritePendingAction(string action)
    {
        try
        {
            var path = PendingActionPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, action);
            File.Move(tmp, path, overwrite: true);
        }
        catch { }
    }

    private static string? ConsumePendingAction()
    {
        try
        {
            if (!File.Exists(PendingActionPath)) return null;
            var action = File.ReadAllText(PendingActionPath).Trim();
            File.Delete(PendingActionPath);
            return string.IsNullOrEmpty(action) ? null : action;
        }
        catch { return null; }
    }

    private static bool LoadTimedState()
    {
        try
        {
            if (!File.Exists(TimedStatePath)) return false;
            var state = JsonSerializer.Deserialize<TimedState>(File.ReadAllText(TimedStatePath));
            if (state == null || state.Paths.Count == 0) return false;
            _timedPaths = state.Paths;
            _timedIndex = state.Index;
            _timedOptions = state.Options;
            _timedShuffle = state.Shuffle;
            _timedInterval = TimeSpan.FromSeconds(state.IntervalSeconds);
            _history = state.History;
            _historyIndex = state.HistoryIndex;
            _timedTimerPaused = state.TimerPaused;
            _timedTimerStopped = state.TimerStopped;
            _timedRemainingMs = state.RemainingMs > 0 ? state.RemainingMs : (long)_timedInterval.TotalMilliseconds;
            _waitForVideoEnd = state.WaitForVideoEnd;
            return true;
        }
        catch { return false; }
    }

    public static void Apply(string videoPath, string mpvOptions)
    {
        lock (_lock)
        {
            if (IsScenePath(videoPath))
            {
                var s = SettingsService.Load();
                if (!s.AllowScenes)
                    throw new InvalidOperationException("Enable \"Allow scene support\" in Settings to play scenes");
                if (!IsLweAvailable())
                    throw new InvalidOperationException("linux-wallpaperengine not found in PATH — install it to use scenes");
            }
            TeardownTimer();
            ClearTimedStateFile();
            SwitchToFile(videoPath, mpvOptions);
        }
    }

    public static void ApplyPlaylist(IReadOnlyList<string> videoPaths, string mpvOptions, bool shuffle = false)
    {
        if (videoPaths.Count == 0) return;
        lock (_lock)
        {
            KillAll();
            ClearTimedStateFile();

            if (videoPaths.Count == 1)
            {
                _current = Launch(mpvOptions, videoPaths[0]);
                return;
            }

            var cacheDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".cache", "livepaper");
            Directory.CreateDirectory(cacheDir);

            var playlistPath = Path.Combine(cacheDir, "playlist.txt");
            File.WriteAllLines(playlistPath, videoPaths.Take(videoPaths.Count - 1));

            var shuffleFlag = shuffle ? " --shuffle" : "";
            var options = $"{mpvOptions} --playlist={playlistPath} --loop-playlist=inf{shuffleFlag}";
            _current = Launch(options, videoPaths[videoPaths.Count - 1]);
        }
    }

    public static void ApplyTimedPlaylist(IReadOnlyList<string> paths, string mpvOptions, bool shuffle, int intervalSeconds, bool waitForVideoEnd = false)
    {
        lock (_lock)
        {
            TeardownTimer();
            if (paths.Count == 0) { KillCurrentProcess(); return; }

            var ordered = new List<string>(paths); // caller is responsible for initial order; shuffle flag only controls cycle-end reshuffle

            _timedPaths = ordered;
            _timedIndex = 0;
            _timedOptions = mpvOptions;
            _timedShuffle = shuffle;
            _timedInterval = TimeSpan.FromSeconds(intervalSeconds);
            _timedTimerPaused = false;
            _timedTimerStopped = false;
            _timedRemainingMs = (long)_timedInterval.TotalMilliseconds;
            _waitForVideoEnd = waitForVideoEnd;
            _history = [ordered[0]];
            _historyIndex = 0;
            SwitchToFile(ordered[0], mpvOptions);
            SaveTimedState();

            if (ordered.Count > 1 && intervalSeconds > 0)
                StartTimedTimer();
        }
    }

    public static bool RestoreTimedPlaylist()
    {
        lock (_lock)
        {
            if (!LoadTimedState()) return false;
            if (_timedPaths == null || _history == null || _timedPaths.Count == 0) return false;

            _timedTimerStopped = false;
            _timedTimerPaused = false;
            _timedRemainingMs = (long)_timedInterval.TotalMilliseconds;

            SwitchToFile(_history[_historyIndex], _timedOptions);
            SaveTimedState();

            if (_timedPaths.Count > 1 && _timedInterval.TotalSeconds > 0)
                StartTimedTimer();

            return true;
        }
    }

    public static bool ResumeTimedTimer()
    {
        lock (_lock)
        {
            if (!LoadTimedState()) return false;
            if (_timedPaths == null || _history == null || _timedPaths.Count == 0) return false;

            _timedTimerStopped = false;
            _timedTimerPaused = false;
            // _timedRemainingMs is restored from state — preserves the countdown

            if (_timedPaths.Count > 1 && _timedInterval.TotalSeconds > 0)
                StartTimedTimer();

            return true;
        }
    }

    private static void StartTimedTimer()
    {
        _daemonCts?.Dispose();
        _daemonCts = new CancellationTokenSource();
        _lastTickTime = DateTime.UtcNow;
        ConsumePendingAction(); // discard any stale pending action from a prior session

        _playlistTimer = new Timer(_ => Tick(), null, TickInterval, Timeout.InfiniteTimeSpan);
    }

    private static void Tick()
    {
        lock (_lock)
        {
            var now = DateTime.UtcNow;
            var elapsedMs = (long)(now - _lastTickTime).TotalMilliseconds;
            _lastTickTime = now;

            RefreshSignals();
            if (_timedPaths == null) return;

            // If the current scene crashed, signal and advance immediately
            if (_history != null && _historyIndex >= 0 && _historyIndex < _history.Count &&
                IsScenePath(_history[_historyIndex]) && !IsSkippedPath(_history[_historyIndex]) && !IsLweRunning)
            {
                OnSceneCrashed?.Invoke(_history[_historyIndex]);
                _timedRemainingMs = 0;
            }

            if (_timedTimerStopped)
            {
                // Cover the race where CLI's Stop ran during our kill→launch
                // gap: the new mpvpaper we launched after CLI's KillAll would
                // otherwise survive forever.
                KillCurrentProcess();
                _timedPaths = null;
                _history = null;
                _historyIndex = -1;
                _playlistTimer?.Dispose();
                _playlistTimer = null;
                _daemonCts?.Cancel();
                OnTimedPlaylistStopped?.Invoke();
                return;
            }

            if (_timedTimerPaused)
            {
                _playlistTimer?.Change(TickInterval, Timeout.InfiniteTimeSpan);
                return;
            }

            var pending = ConsumePendingAction();
            if (pending != null)
            {
                DispatchPendingAction(pending);
                _playlistTimer?.Change(TickInterval, Timeout.InfiniteTimeSpan);
                return;
            }

            if (!_waitingForVideoEnd)
                _timedRemainingMs -= elapsedMs;

            if (_timedRemainingMs <= 0 && !_waitingForVideoEnd)
            {
                if (_waitForVideoEnd)
                {
                    var next = AdvanceToNext();
                    if (next != null)
                    {
                        _waitingForVideoEnd = true;
                        _waitCts = new CancellationTokenSource();
                        var opts = _timedOptions;
                        var intervalMs = (long)_timedInterval.TotalMilliseconds;
                        var cts = _waitCts;
                        Task.Run(() => DoVideoEndWait(next, opts, intervalMs, cts.Token));
                    }
                    else
                    {
                        AdvanceAndLaunch();
                    }
                }
                else
                {
                    AdvanceAndLaunch();
                }
            }

            _playlistTimer?.Change(TickInterval, Timeout.InfiniteTimeSpan);
        }
    }

    private static void LaunchAndReset(string path)
    {
        SwitchToFile(path, _timedOptions);
        _timedRemainingMs = (long)_timedInterval.TotalMilliseconds;
        SaveTimedState();
    }

    // Switch to the next wallpaper.
    //
    // Video→video: IPC loadfile replace (seamless, no flash). Falls back to
    // cold-start (kill old → launch new) if mpvpaper is not alive.
    //
    // Any transition involving a scene: pre-launch — the new process starts
    // first, and the old one is killed only after the new one signals
    // readiness (mpvpaper: AV: line; LWE: SceneTransitionDelayMs).
    private static void SwitchToFile(string path, string mpvOptions)
    {
        // Cancel any in-flight pre-launch transition from a previous switch.
        _prelaunchCts?.Cancel();
        _prelaunchCts = null;

        bool nextIsScene = IsScenePath(path);
        bool prevIsScene = IsLweRunning;

        // ── Transition involving a scene ──────────────────────────────────────
        if (nextIsScene || prevIsScene)
        {
            if (nextIsScene)
            {
                var settings = SettingsService.Load();
                if (!settings.AllowScenes || !IsLweAvailable())
                {
                    KillCurrentProcess();
                    return;
                }
                var workshopId = File.ReadAllText(path).Trim();

                // Capture old processes before launching new ones.
                var oldMpvProcs = Process.GetProcessesByName("mpvpaper");
                var oldLwePids = ReadCurrentLwePids();

                // Launch new LWE without killing old yet.
                var newPids = SpawnLweProcesses(workshopId, settings);
                OnWallpaperChanged?.Invoke(path);

                if (newPids.Length > 0)
                {
                    var volOverride = ReadVolumeOverride(path);
                    var count = newPids.Length;
                    _ = Task.Run(async () =>
                    {
                        for (int i = 0; i < 60; i++)
                        {
                            if (GetLweSinkInputIds().Count >= count) break;
                            await Task.Delay(50);
                        }
                        ApplyLweMute(AudioMonitor.IsMuted);
                        if (volOverride.HasValue) ApplyLweVolume(volOverride.Value);
                    });
                }

                var cts = _prelaunchCts = new CancellationTokenSource();
                var delayMs = settings.SceneTransitionDelayMs;
                var capturedMpv = oldMpvProcs;
                var capturedLwePids = oldLwePids;
                _ = Task.Run(async () =>
                {
                    try { await Task.Delay(delayMs, cts.Token); }
                    catch { return; }
                    foreach (var proc in capturedMpv)
                        using (proc) { try { proc.Kill(entireProcessTree: true); } catch { } }
                    if (capturedMpv.Length > 0)
                    {
                        lock (_lock) { _current = null; }
                        var sock = IpcSocket;
                        try { if (File.Exists(sock)) File.Delete(sock); } catch { }
                    }
                    KillPids(capturedLwePids);
                });
            }
            else // scene→video: launch mpvpaper first, kill LWE after AV: fires
            {
                var oldLwePids = ReadCurrentLwePids();
                KillMpvPaperOnly(); // safety: clear any stray mpvpaper + socket

                var readyTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _current = Launch(BakeSpeedOverride(BakeVolumeOverride(mpvOptions, path), path), path, readyTcs);
                OnWallpaperChanged?.Invoke(path);

                var settings = SettingsService.Load();
                var volOverride = ReadVolumeOverride(path);
                var speedOverride = ReadSpeedOverride(path);
                int vol = volOverride ?? settings.Volume;
                double spd = speedOverride ?? settings.Speed;
                Task.Run(() => { SetVolume(vol); SetSpeed(spd); });

                var cts = _prelaunchCts = new CancellationTokenSource();
                var capturedPids = oldLwePids;
                _ = Task.Run(async () =>
                {
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
                    linked.CancelAfter(3000); // fallback: kill LWE after 3s even without AV:
                    try { await readyTcs.Task.WaitAsync(linked.Token); }
                    catch (OperationCanceledException) { }
                    if (!cts.Token.IsCancellationRequested)
                    {
                        KillPids(capturedPids);
                        try { if (File.Exists(LwePidPath)) File.Delete(LwePidPath); } catch { }
                    }
                });
            }
            return;
        }

        // ── Video→video: existing seamless approach ───────────────────────────
        bool mpvAlive = File.Exists(IpcSocket) && Process.GetProcessesByName("mpvpaper").Length > 0;
        if (mpvAlive && TryIpcSwitchToFile(path))
        {
            OnWallpaperChanged?.Invoke(path);
            var volOverride = ReadVolumeOverride(path);
            var speedOverride = ReadSpeedOverride(path);
            var settings = SettingsService.Load();
            int vol = volOverride ?? settings.Volume;
            double spd = speedOverride ?? settings.Speed;
            Task.Run(() => { SetVolume(vol); SetSpeed(spd); });
        }
        else
        {
            KillCurrentProcess();
            _current = Launch(BakeSpeedOverride(BakeVolumeOverride(mpvOptions, path), path), path);
            OnWallpaperChanged?.Invoke(path);
        }
    }

    private static bool TryIpcSwitchToFile(string path)
    {
        // Read AppSettings.Loop directly so the loop state is explicit rather
        // than parsed out of the kill+launch options string. Other launch-only
        // options (hwdec, cache, demuxer) can't be toggled mid-session and only
        // take effect on next cold start.
        bool loopFile = SettingsService.Load().Loop;
        return TrySendCommand("set", "loop-file", loopFile ? "inf" : "no")
            && TrySendCommand("set", "loop-playlist", "no")
            && TrySendCommand("playlist-clear")
            && TrySendCommand("loadfile", path, "replace");
    }

    // Bool-returning variant of SendCommand for callers that need to know
    // whether the IPC succeeded so they can fall back to a fresh launch.
    private static bool TrySendCommand(params object[] args)
    {
        var socketPath = IpcSocket;
        if (!File.Exists(socketPath)) return false;
        try
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            socket.SendTimeout = 500;
            socket.ReceiveTimeout = 500;
            socket.Connect(new UnixDomainSocketEndPoint(socketPath));
            var cmd = JsonSerializer.Serialize(new { command = args });
            socket.Send(Encoding.UTF8.GetBytes(cmd + "\n"));
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static double? TryQueryTimeRemaining()
    {
        var socketPath = IpcSocket;
        if (!File.Exists(socketPath)) return null;
        try
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            socket.SendTimeout = 500;
            socket.ReceiveTimeout = 500;
            socket.Connect(new UnixDomainSocketEndPoint(socketPath));
            var cmd = JsonSerializer.Serialize(new { command = new object[] { "get_property", "time-remaining" } });
            socket.Send(Encoding.UTF8.GetBytes(cmd + "\n"));
            var buf = new byte[4096];
            int n = socket.Receive(buf);
            using var doc = JsonDocument.Parse(buf.AsMemory(0, n));
            if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Number)
                return data.GetDouble();
            return null;
        }
        catch { return null; }
    }

    private static async Task DoVideoEndWait(string next, string opts, long intervalMs, CancellationToken ct)
    {
        // For video→video: disable loop on current, append next to mpv's playlist so
        // the transition happens inside mpv (no blank gap), then restore loop after.
        // Falls back to an immediate SwitchToFile for scene transitions or when IPC is unavailable.
        bool nextIsScene = IsScenePath(next);
        bool prevIsScene = IsLweRunning;

        if (!nextIsScene && !prevIsScene)
        {
            var remaining = TryQueryTimeRemaining();
            if (remaining != null)
            {
                TrySendCommand("set", "loop-file", "no");
                TrySendCommand("loadfile", next, "append");

                var sleepMs = Math.Max(0, (int)(remaining.Value * 1000)) + 2000;
                try { await Task.Delay(sleepMs, ct); }
                catch (OperationCanceledException) { return; }

                lock (_lock)
                {
                    if (!_waitingForVideoEnd) return;
                    _waitingForVideoEnd = false;
                    _waitCts = null;

                    var settings = SettingsService.Load();
                    TrySendCommand("set", "loop-file", settings.Loop ? "inf" : "no");
                    TrySendCommand("playlist-clear");

                    OnWallpaperChanged?.Invoke(next);
                    var volOverride = ReadVolumeOverride(next);
                    var speedOverride = ReadSpeedOverride(next);
                    Task.Run(() => { SetVolume(volOverride ?? settings.Volume); SetSpeed(speedOverride ?? settings.Speed); });

                    _timedRemainingMs = intervalMs;
                    SaveTimedState();
                }
                return;
            }
        }

        // Video→scene: launch LWE SceneTransitionDelayMs before the video ends so the
        // scene is already rendering when mpvpaper is killed (matches normal transition behaviour).
        if (nextIsScene && !prevIsScene)
        {
            var remaining = TryQueryTimeRemaining();
            if (remaining != null)
            {
                var delayMs = SettingsService.Load().SceneTransitionDelayMs;
                var sleepMs = Math.Max(0, (int)(remaining.Value * 1000) - delayMs);
                if (sleepMs > 0)
                {
                    try { await Task.Delay(sleepMs, ct); }
                    catch (OperationCanceledException) { return; }
                }
            }
        }

        // Scene→video and scene→scene: remaining is null (LWE has no mpv socket) — switch immediately.
        lock (_lock)
        {
            if (!_waitingForVideoEnd) return;
            _waitingForVideoEnd = false;
            _waitCts = null;
            SwitchToFile(next, opts);
            _timedRemainingMs = intervalMs;
            SaveTimedState();
        }
    }

    private static void CancelVideoEndWait()
    {
        _waitCts?.Cancel();
        _waitCts = null;
        _waitingForVideoEnd = false;
        // Restore loop so the current video doesn't advance to the queued file
        // in the gap between cancel and the following SwitchToFile call.
        // TryIpcSwitchToFile will set the correct value immediately after.
        TrySendCommand("set", "loop-file", "inf");
    }

    private static void AdvanceAndLaunch()
    {
        CancelVideoEndWait();
        var next = AdvanceToNext();
        if (next != null) LaunchAndReset(next);
    }

    private static void StepBackAndLaunch()
    {
        CancelVideoEndWait();
        if (_history == null || _historyIndex <= 0) return;
        _historyIndex--;
        LaunchAndReset(_history[_historyIndex]);
    }

    private static void RandomAndLaunch()
    {
        if (_timedPaths == null || _timedPaths.Count == 0) return;
        var current = _history != null && _historyIndex >= 0 && _historyIndex < _history.Count
            ? _history[_historyIndex]
            : null;
        var pick = PickRandomExcluding(_timedPaths, current);
        if (_history != null)
        {
            _history.Add(pick);
            if (_history.Count > 100) _history.RemoveAt(0);
            _historyIndex = _history.Count - 1;
        }
        LaunchAndReset(pick);
    }

    // Uniform pick from `pool` excluding `exclude`. No retries: shifts the
    // chosen index past the excluded one to keep the distribution flat.
    private static string PickRandomExcluding(IReadOnlyList<string> pool, string? exclude)
    {
        if (pool.Count == 1 || exclude == null) return pool[Random.Shared.Next(pool.Count)];
        int excludeIdx = -1;
        for (int i = 0; i < pool.Count; i++)
            if (pool[i] == exclude) { excludeIdx = i; break; }
        if (excludeIdx < 0) return pool[Random.Shared.Next(pool.Count)];
        int pick = Random.Shared.Next(pool.Count - 1);
        if (pick >= excludeIdx) pick++;
        return pool[pick];
    }

    private static void DispatchPendingAction(string action)
    {
        switch (action)
        {
            case "next": AdvanceAndLaunch(); break;
            case "prev": StepBackAndLaunch(); break;
            case "random": RandomAndLaunch(); break;
        }
    }

    public static void NextWallpaper()
    {
        if (IsTimedPlaylistActive())
        {
            WritePendingAction("next");
            return;
        }
        // Single-wallpaper sessions (single, random) — step the library.
        // mpv-native playlist mode falls through to playlist-next.
        if (TryStepLibrary(forward: true)) return;
        SendCommand("playlist-next");
    }

    public static void PreviousWallpaper()
    {
        if (IsTimedPlaylistActive())
        {
            WritePendingAction("prev");
            return;
        }
        if (TryStepLibrary(forward: false)) return;
        SendCommand("playlist-prev");
    }

    // For single-wallpaper sessions (no playlist context), `next`/`prev`
    // steps through the library alphabetically. Wraps at the ends. Returns
    // false for mpv-native playlist sessions so the caller can fall through
    // to mpv's own `playlist-next`/`playlist-prev`.
    private static bool TryStepLibrary(bool forward)
    {
        var settings = SettingsService.Load();
        var session = settings.LastSession;
        if (session == null) return false;
        if (session.IsTimedPlaylist || session.IsPlaylist) return false;
        if (session.Paths.Count == 0) return false;

        // Use LoadAll's native order so stepping mirrors the UI grid order
        // exactly (whatever filesystem order GUI displays).
        var library = LibraryService.LoadAll();
        if (library.Count == 0) return false;

        var current = session.Paths[0];
        int currentIdx = library.FindIndex(i => i.VideoPath == current);
        int newIdx = currentIdx < 0
            ? 0
            : forward
                ? (currentIdx + 1) % library.Count
                : (currentIdx - 1 + library.Count) % library.Count;

        var pickPath = library[newIdx].VideoPath;
        Apply(pickPath, settings.BuildMpvOptions());
        settings.LastSession = new LastSession { Paths = [pickPath] };
        SettingsService.Save(settings);
        return true;
    }

    public static void UpdateTimedSettings(bool shuffle, int intervalSeconds, bool waitForVideoEnd = false)
    {
        lock (_lock)
        {
            if (_timedPaths == null && !LoadTimedState()) return;
            _timedShuffle = shuffle;
            _timedInterval = TimeSpan.FromSeconds(intervalSeconds);
            _timedRemainingMs = (long)_timedInterval.TotalMilliseconds;
            _waitForVideoEnd = waitForVideoEnd;
            SaveTimedState();
        }
    }

    public static void Stop()
    {
        lock (_lock)
        {
            KillAll();
            SignalTimerStop();
        }
    }

    // Re-apply the last saved session: timed playlist, mpv-native playlist,
    // or single video. For timed playlists, delegates to a detached daemon.
    public static void Restore()
    {
        var settings = SettingsService.Load();
        if (settings.AutoMute)
            AudioMonitor.SpawnDetachedMonitor();

        var session = settings.LastSession;
        if (session == null) return;

        if (session.IsTimedPlaylist && session.Paths.Count > 0)
            SpawnTimerDaemon();
        else if (session.IsPlaylist && session.Paths.Count > 0)
            ApplyPlaylist(session.Paths, settings.BuildMpvPlaylistOptions(), session.Shuffle);
        else if (session.Paths.Count > 0)
            Apply(session.Paths[0], settings.BuildMpvOptions());
    }

    // Pick a random video and apply it as a single wallpaper. If a timed
    // playlist is active, hands off to the timer owner via a pending action
    // so the daemon picks from its in-memory paths and resets the countdown.
    // Otherwise picks from the full library as a one-shot.
    public static void ApplyRandom()
    {
        if (IsTimedPlaylistActive())
        {
            WritePendingAction("random");
            return;
        }

        var settings = SettingsService.Load();
        var pool = LibraryService.LoadAll().Select(i => i.VideoPath).ToList();
        if (pool.Count == 0) return;

        var current = settings.LastSession?.Paths.FirstOrDefault();
        var pick = PickRandomExcluding(pool, current);
        Apply(pick, settings.BuildMpvOptions());
        settings.LastSession = new LastSession { IsRandom = true, Paths = [pick] };
        SettingsService.Save(settings);
    }

    // Owns the timed-playlist tick loop in a detached process. Resumes an
    // already-running session if possible; otherwise restarts it. Blocks
    // until the timer is signalled to stop.
    public static void RunTimerDaemon()
    {
        var settings = SettingsService.Load();
        var session = settings.LastSession;
        if (session?.IsTimedPlaylist != true || session.Paths.Count == 0) return;

        bool started = IsPlaying && ResumeTimedTimer();
        if (!started)
        {
            if (settings.ResumeFromLast && RestoreTimedPlaylist()) { }
            else
            {
                var paths = session.Shuffle
                    ? session.Paths.OrderBy(_ => Guid.NewGuid()).ToList()
                    : session.Paths;
                ApplyTimedPlaylist(paths, settings.BuildMpvOptions(), session.Shuffle, session.TimedIntervalSeconds, session.WaitForVideoEnd);
            }
        }
        WriteTimerDaemonPid();
        try { DaemonToken.WaitHandle.WaitOne(); }
        finally { DeleteTimerDaemonPid(); }
    }

    public static void TogglePause()
    {
        lock (_lock)
        {
            SendCommand("cycle", "pause");
            try
            {
                if (!File.Exists(TimedStatePath)) return;
                var state = JsonSerializer.Deserialize<TimedState>(File.ReadAllText(TimedStatePath));
                if (state == null) return;
                var updated = state with { TimerPaused = !state.TimerPaused, TimerStopped = false };
                File.WriteAllText(TimedStatePath, JsonSerializer.Serialize(updated));
            }
            catch { }
        }
    }

    public static void SendCommand(params object[] args)
    {
        var socketPath = IpcSocket;
        if (!File.Exists(socketPath)) return;
        try
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            socket.SendTimeout = 500;
            socket.ReceiveTimeout = 500;
            socket.Connect(new UnixDomainSocketEndPoint(socketPath));
            var cmd = JsonSerializer.Serialize(new { command = args });
            socket.Send(Encoding.UTF8.GetBytes(cmd + "\n"));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[PlayerHelper] SendCommand failed: {ex.Message}");
        }
    }

    public static void SetMute(bool mute)
    {
        SendCommand("set_property", "mute", mute);
        if (IsLweRunning) ApplyLweMute(mute);
    }

    public static void SetVolume(int volume)
    {
        SendCommand("set_property", "volume", (double)volume);
        if (IsLweRunning) ApplyLweVolume(volume);
    }

    public static void SetSpeed(double speed)
    {
        SendCommand("set_property", "speed", speed);
    }

    public static void SetVideoScale(string scale)
    {
        double panscan = scale == "fill" ? 1.0 : 0.0;
        SendCommand("set_property", "panscan", panscan);
    }

    // Adjust volume by `delta` (clamped 0-100). Updates the persisted setting
    // so subsequent launches and the GUI slider reflect the change, and also
    // pushes to the running mpv via IPC for an immediate effect.
    public static void AdjustVolume(int delta)
    {
        var settings = SettingsService.Load();
        int newVolume = Math.Clamp(settings.Volume + delta, 0, 100);
        if (newVolume == settings.Volume) return;
        settings.Volume = newVolume;
        SettingsService.Save(settings);
        SetVolume(newVolume);
    }

    // Returns the next wallpaper path, extending history if needed.
    private static string? AdvanceToNext()
    {
        if (_history != null && _historyIndex < _history.Count - 1)
        {
            _historyIndex++;
            return _history[_historyIndex];
        }

        var p = _timedPaths;
        if (p == null) return null;

        for (int attempt = 0; attempt < p.Count; attempt++)
        {
            _timedIndex++;
            if (_timedIndex >= p.Count)
            {
                if (_timedShuffle)
                {
                    var last = p[p.Count - 1];
                    List<string> reshuffled;
                    do { reshuffled = p.OrderBy(_ => Guid.NewGuid()).ToList(); }
                    while (p.Count > 1 && reshuffled[0] == last);
                    _timedPaths = p = reshuffled;
                }
                _timedIndex = 0;
            }

            var path = p[_timedIndex];
            if (_history != null)
            {
                _history.Add(path);
                if (_history.Count > 100) _history.RemoveAt(0);
                _historyIndex = _history.Count - 1;
            }

            if (!IsSkippedPath(path)) return path;
        }

        return null;
    }

    private static Process? Launch(string mpvOptions, string file, TaskCompletionSource<bool>? readyTcs = null)
    {
        var socketPath = IpcSocket;
        Directory.CreateDirectory(Path.GetDirectoryName(socketPath)!);
        if (File.Exists(socketPath)) File.Delete(socketPath);

        var options = $"{mpvOptions} --input-ipc-server={socketPath}";
        var psi = new ProcessStartInfo("setsid")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("mpvpaper");
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add(options);
        psi.ArgumentList.Add("*");
        psi.ArgumentList.Add(file);
        var process = Process.Start(psi);
        if (process != null)
        {
            process.OutputDataReceived += (_, e) =>
            {
                if (readyTcs != null && e.Data != null && (e.Data.StartsWith("AV:") || e.Data.StartsWith("V:")))
                    readyTcs.TrySetResult(true);
            };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }
        return process;
    }

    private static void KillCurrentProcess()
    {
        foreach (var proc in Process.GetProcessesByName("mpvpaper"))
        {
            using (proc)
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
            }
        }
        KillLweProcess();
        _current = null;
        var socketPath = IpcSocket;
        if (File.Exists(socketPath)) File.Delete(socketPath);
    }

    private static void KillLweProcess()
    {
        try
        {
            if (File.Exists(LwePidPath))
            {
                foreach (var line in File.ReadAllLines(LwePidPath))
                {
                    if (!int.TryParse(line.Trim(), out int pid)) continue;
                    try { using var p = Process.GetProcessById(pid); p.Kill(entireProcessTree: true); }
                    catch { }
                }
                File.Delete(LwePidPath);
            }
        }
        catch { }
        foreach (var proc in Process.GetProcessesByName("linux-wallpaperengine"))
            try { proc.Kill(entireProcessTree: true); } catch { }
    }

    private static void ClearTimedStateFile()
    {
        try { File.Delete(TimedStatePath); } catch { }
    }

    private static void SignalTimerStop()
    {
        try
        {
            if (!File.Exists(TimedStatePath)) return;
            var state = JsonSerializer.Deserialize<TimedState>(File.ReadAllText(TimedStatePath));
            if (state == null) return;
            var updated = state with { TimerStopped = true };
            File.WriteAllText(TimedStatePath, JsonSerializer.Serialize(updated));
        }
        catch { }
    }

    // State-only teardown (timer state, history, pending action). Does NOT
    // touch mpvpaper — callers that want to start a new session can
    // IPC-switch the existing mpvpaper instead of killing it.
    private static void TeardownTimer()
    {
        _waitCts?.Cancel();
        _waitCts = null;
        _waitingForVideoEnd = false;
        _prelaunchCts?.Cancel();
        _prelaunchCts = null;
        _playlistTimer?.Dispose();
        _playlistTimer = null;
        _timedPaths = null;
        _history = null;
        _historyIndex = -1;
        _timedTimerPaused = false;
        _timedTimerStopped = false;
        _timedRemainingMs = 0;
        ConsumePendingAction();
    }

    private static void KillAll()
    {
        TeardownTimer();
        KillCurrentProcess();
    }

    public static bool IsLweAvailable()
    {
        try
        {
            var psi = new ProcessStartInfo("which")
            {
                Arguments = "linux-wallpaperengine",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var proc = Process.Start(psi);
            proc?.WaitForExit();
            return proc?.ExitCode == 0;
        }
        catch { return false; }
    }

    public static bool IsScenePath(string path) =>
        path.EndsWith(".scene", StringComparison.OrdinalIgnoreCase);

    private static bool IsSkippedPath(string path)
    {
        if (!IsScenePath(path)) return false;
        var s = SettingsService.Load();
        return !s.AllowScenes || !IsLweAvailable();
    }

    // Spawns one linux-wallpaperengine process per monitor, writes new PIDs to
    // LwePidPath, and returns the new PID strings. Does NOT kill any existing
    // LWE processes — callers are responsible for killing old ones.
    private static string[] SpawnLweProcesses(string workshopId, AppSettings settings)
    {
        var pids = new List<string>();
        bool anyPrimary = settings.LweMonitors.Any(m => m.IsPrimary);

        foreach (var monitor in settings.LweMonitors)
        {
            if (string.IsNullOrWhiteSpace(monitor.Name)) continue;
            var psi = new ProcessStartInfo("setsid")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("linux-wallpaperengine");
            psi.ArgumentList.Add("--noautomute");
            psi.ArgumentList.Add("--screen-root");
            psi.ArgumentList.Add(monitor.Name);

            bool hasAudio = !anyPrimary || monitor.IsPrimary;
            if (settings.NoAudio || !hasAudio)
                psi.ArgumentList.Add("--silent");
            else
            {
                psi.ArgumentList.Add("--volume");
                psi.ArgumentList.Add("100");
            }

            if (monitor.Fps > 0)
            {
                psi.ArgumentList.Add("--fps");
                psi.ArgumentList.Add(monitor.Fps.ToString());
            }
            psi.ArgumentList.Add("--no-fullscreen-pause");
            psi.ArgumentList.Add(workshopId);

            var proc = Process.Start(psi);
            if (proc != null)
            {
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();
                pids.Add(proc.Id.ToString());
            }
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LwePidPath)!);
            File.WriteAllLines(LwePidPath, pids);
        }
        catch { }
        return [.. pids];
    }

    private static int LaunchScene(string workshopId, AppSettings settings)
    {
        KillLweProcess();
        return SpawnLweProcesses(workshopId, settings).Length;
    }

    private static string[] ReadCurrentLwePids()
    {
        try { return File.Exists(LwePidPath) ? File.ReadAllLines(LwePidPath) : []; }
        catch { return []; }
    }

    private static void KillPids(string[] pids)
    {
        foreach (var pidStr in pids)
        {
            if (!int.TryParse(pidStr.Trim(), out int pid)) continue;
            try { using var p = Process.GetProcessById(pid); p.Kill(entireProcessTree: true); } catch { }
        }
    }

    // Kills only mpvpaper processes without touching LWE — used in pre-launch
    // transitions where LWE is being kept alive until the new process is ready.
    private static void KillMpvPaperOnly()
    {
        foreach (var proc in Process.GetProcessesByName("mpvpaper"))
            using (proc) { try { proc.Kill(entireProcessTree: true); } catch { } }
        _current = null;
        var socketPath = IpcSocket;
        try { if (File.Exists(socketPath)) File.Delete(socketPath); } catch { }
    }

    private static List<int> GetLweSinkInputIds()
    {
        try
        {
            var psi = new ProcessStartInfo("pactl")
            {
                Arguments = "list sink-inputs",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var proc = Process.Start(psi)!;
            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit();

            var ids = new List<int>();
            int currentId = -1;
            foreach (var line in output.Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("Sink Input #"))
                {
                    if (int.TryParse(trimmed.Substring("Sink Input #".Length), out int id))
                        currentId = id;
                }
                else if (trimmed.Contains("application.name = \"SDL Application\"") && currentId >= 0)
                {
                    ids.Add(currentId);
                    currentId = -1;
                }
            }
            return ids;
        }
        catch { return []; }
    }

    private static void ApplyLweVolume(int volume)
    {
        foreach (var id in GetLweSinkInputIds())
            RunPactl($"set-sink-input-volume {id} {volume}%");
    }

    private static void ApplyLweMute(bool mute)
    {
        foreach (var id in GetLweSinkInputIds())
            RunPactl($"set-sink-input-mute {id} {(mute ? 1 : 0)}");
    }

    private static void RunPactl(string args)
    {
        try
        {
            var psi = new ProcessStartInfo("pactl")
            {
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var proc = Process.Start(psi);
            proc?.WaitForExit();
        }
        catch { }
    }

    private static int? ReadVolumeOverride(string path)
    {
        var sidecar = Path.ChangeExtension(path, ".volume");
        if (!File.Exists(sidecar)) return null;
        try { return int.TryParse(File.ReadAllText(sidecar).Trim(), out int v) ? v : null; }
        catch { return null; }
    }

    private static double? ReadSpeedOverride(string path)
    {
        var sidecar = Path.ChangeExtension(path, ".speed");
        if (!File.Exists(sidecar)) return null;
        try { return double.TryParse(File.ReadAllText(sidecar).Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double v) ? v : null; }
        catch { return null; }
    }

    private static string BakeVolumeOverride(string options, string path)
    {
        var vol = ReadVolumeOverride(path);
        if (!vol.HasValue) return options;
        if (options.Contains("--no-audio")) return options;
        const string prefix = "--volume=";
        int idx = options.IndexOf(prefix, StringComparison.Ordinal);
        if (idx >= 0)
        {
            int end = idx + prefix.Length;
            while (end < options.Length && char.IsDigit(options[end])) end++;
            return options[..idx] + prefix + vol.Value + options[end..];
        }
        return options + $" {prefix}{vol.Value}";
    }

    private static string BakeSpeedOverride(string options, string path)
    {
        var speed = ReadSpeedOverride(path);
        if (!speed.HasValue) return options;
        string val = speed.Value.ToString("G", System.Globalization.CultureInfo.InvariantCulture);
        const string prefix = "--speed=";
        int idx = options.IndexOf(prefix, StringComparison.Ordinal);
        if (idx >= 0)
        {
            int end = idx + prefix.Length;
            while (end < options.Length && (char.IsDigit(options[end]) || options[end] == '.')) end++;
            return options[..idx] + prefix + val + options[end..];
        }
        return options + $" {prefix}{val}";
    }
}
