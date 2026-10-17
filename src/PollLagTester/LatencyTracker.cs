using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PollLagTester;

internal sealed record LatStat(long N, double Avg, double P50, double P95, double P99, double Min, double Max);

internal sealed record FrameStat(long Frames, double Fps, double OnePctLow, double FtAvg, double FtP99, double DisplayedFps);

internal sealed record LatencySessionResult(object Doc, double InputToPhotonAvg, double Fps, string? Target);

/// <summary>
/// Input-to-photon latency via Intel PresentMon (ETW). PresentMon matches the OS's keyboard/mouse input events
/// to the first frame that reaches the screen afterwards, entirely in software and with very low overhead.
/// We run the console build with CSV on stdout and only keep the few columns we need.
/// </summary>
internal sealed class LatencyTracker : IDisposable
{
    static readonly HashSet<string> AutoIgnore = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome.exe", "msedge.exe", "firefox.exe", "brave.exe", "opera.exe", "opera_gx.exe", "vivaldi.exe",
        "PollLagTester.exe", "explorer.exe", "Discord.exe", "steamwebhelper.exe", "obs64.exe", "Code.exe",
        "ApplicationFrameHost.exe", "TextInputHost.exe", "SearchHost.exe", "ShellExperienceHost.exe",
        "StartMenuExperienceHost.exe", "Widgets.exe", "Spotify.exe", "EpicGamesLauncher.exe", "Battle.net.exe",
    };
    static readonly Regex SafeExe = new(@"^[\w .()\-]+\.exe$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    readonly string _baseDir;
    readonly object _lock = new();
    readonly nint _job = Native.CreateKillOnCloseJob();
    Process? _proc;
    readonly Queue<string> _stderr = new();
    string _status = "stopped";
    string? _error;
    string _manualTarget = "";
    string? _exe;
    long _exeCheckedQpc;
    public volatile bool Recording;
    public string? DownloadStatus { get; private set; }

    int _cApp = -1, _cFt = -1, _cDisp = -1, _cIn = -1, _cClick = -1, _cFrameLat = -1;

    struct Frame { public long Qpc; public int App; public float Ft, Disp, In, Click, FrameLat; }
    readonly Frame[] _ring = new Frame[32768];
    int _head, _count;
    readonly List<string> _apps = new();
    int _lastAppIdx = -1;
    int _targetIdx = -1;
    long _lastTickQpc;

    readonly RingSeries _serIn = new(240), _serFrameLat = new(240), _serFps = new(240);
    readonly Downsampled _sesIn = new(0.25, 2400), _sesFrameLat = new(0.25, 2400), _sesFps = new(0.25, 2400);
    readonly Histogram _sFt = new(0.02, 250), _sDisp = new(0.02, 250), _sIn = new(0.05, 1000), _sClick = new(0.05, 1000), _sFrameLat = new(0.05, 1000);

    readonly List<float> _lFt = new(), _lDisp = new(), _lIn = new(), _lClick = new(), _lFrameLat = new();
    object? _liveFrames, _liveIn, _liveClick, _liveFrameLat;
    double _liveFtAvg = double.NaN, _liveInAvg = double.NaN, _liveClickAvg = double.NaN, _liveFrameLatAvg = double.NaN;
    object[] _appList = [];

    TimeSpan _pmCpu;
    long _pmCpuQpc;
    public double CpuPct { get; private set; }

    public LatencyTracker(string baseDir) { _baseDir = baseDir; }

    string ToolsDir => Path.Combine(_baseDir, "tools");

    public string? FindExe(bool force = false)
    {
        long now = Stopwatch.GetTimestamp();
        if (!force && now - _exeCheckedQpc < 5 * Stopwatch.Frequency) return _exe;
        _exeCheckedQpc = now;
        foreach (var dir in new[] { ToolsDir, _baseDir, Path.Combine(Environment.CurrentDirectory, "tools") })
        {
            if (!Directory.Exists(dir)) continue;
            var hit = Directory.GetFiles(dir, "PresentMon*.exe")
                .Where(f => !Path.GetFileName(f).Contains("Service", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => f, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            if (hit != null) return _exe = hit;
        }
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var p = Path.Combine(dir.Trim(), "PresentMon.exe");
                if (File.Exists(p)) return _exe = p;
            }
            catch (ArgumentException) { }
        }
        return _exe = null;
    }

    public bool Start(string? target, out string? error)
    {
        lock (_lock)
        {
            error = null;
            if (_proc != null) return true;
            if (!Elevation.IsAdmin) { error = _error = "PresentMon needs administrator rights (ETW tracing). Use 'Restart as admin'."; return false; }
            var exe = FindExe(force: true);
            if (exe == null) { error = _error = "PresentMon not found. Download it from the panel or put PresentMon-*.exe in the tools folder."; return false; }
            target = (target ?? "").Trim();
            if (target.Length > 0 && !target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) target += ".exe";
            if (target.Length > 0 && !SafeExe.IsMatch(target)) { error = _error = "Target must be a process name like game.exe"; return false; }
            _manualTarget = target;

            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (var a in new[] { "--output_stdout", "--no_console_stats", "--stop_existing_session", "--session_name", "PollLagTester", "--exclude", "dwm.exe" })
                psi.ArgumentList.Add(a);
            if (target.Length > 0) { psi.ArgumentList.Add("--process_name"); psi.ArgumentList.Add(target); }

            try
            {
                var p = Process.Start(psi)!;
                if (_job != 0) Native.AssignProcessToJobObject(_job, p.Handle);
                _stderr.Clear();
                p.ErrorDataReceived += (_, e) =>
                {
                    if (string.IsNullOrWhiteSpace(e.Data)) return;
                    lock (_stderr)
                    {
                        _stderr.Enqueue(e.Data.Trim());
                        while (_stderr.Count > 6) _stderr.Dequeue();
                    }
                };
                p.BeginErrorReadLine();
                _proc = p;
                _pmCpu = TimeSpan.Zero;
                _pmCpuQpc = Stopwatch.GetTimestamp();
                _status = "starting";
                _error = null;
                _cApp = -1;
                new Thread(() => ReadLoop(p)) { IsBackground = true, Name = "PresentMon reader" }.Start();
                return true;
            }
            catch (Exception ex)
            {
                error = _error = "Could not start PresentMon: " + ex.Message;
                return false;
            }
        }
    }

    public void Stop()
    {
        Process? p;
        lock (_lock) { p = _proc; _proc = null; _status = "stopped"; }
        if (p == null) return;
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
        p.Dispose();
    }

    void ReadLoop(Process p)
    {
        try
        {
            var r = p.StandardOutput;
            string? line;
            bool header = false;
            while ((line = r.ReadLine()) != null)
            {
                if (!header)
                {
                    if (line.StartsWith("Application,", StringComparison.OrdinalIgnoreCase)) { MapHeader(line); header = true; lock (_lock) _status = "running"; }
                    continue;
                }
                ParseLine(line.AsSpan());
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }

        lock (_lock)
        {
            if (_proc != p) return;
            _proc = null;
            _status = "stopped";
            try
            {
                p.WaitForExit(2000);
                if (p.ExitCode != 0)
                {
                    string err;
                    lock (_stderr) err = string.Join(" | ", _stderr);
                    _error = $"PresentMon exited with code {p.ExitCode}. {err}".Trim();
                }
            }
            catch { }
        }
    }

    void MapHeader(string header)
    {
        var cols = header.Split(',');
        int Find(params string[] names)
        {
            foreach (var n in names)
            {
                int i = Array.FindIndex(cols, c => c.Trim().Equals(n, StringComparison.OrdinalIgnoreCase));
                if (i >= 0) return i;
            }
            return -1;
        }
        lock (_lock)
        {
            _cApp = Find("Application", "ProcessName");
            _cFt = Find("MsBetweenPresents", "FrameTime");
            _cDisp = Find("MsBetweenDisplayChange", "DisplayedTime");
            _cIn = Find("MsAllInputToPhotonLatency", "AllInputToPhotonLatency");
            _cClick = Find("MsClickToPhotonLatency", "ClickToPhotonLatency");
            _cFrameLat = Find("DisplayLatency", "MsDisplayLatency", "MsUntilDisplayed");
        }
    }

    static float P(ReadOnlySpan<char> s) =>
        float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : float.NaN;

    void ParseLine(ReadOnlySpan<char> line)
    {
        float ft = float.NaN, disp = float.NaN, inp = float.NaN, click = float.NaN, fl = float.NaN;
        ReadOnlySpan<char> app = default;
        int col = 0, start = 0;
        for (int i = 0; i <= line.Length; i++)
        {
            if (i < line.Length && line[i] != ',') continue;
            var f = line[start..i];
            if (col == _cApp) app = f;
            else if (col == _cFt) ft = P(f);
            else if (col == _cDisp) disp = P(f);
            else if (col == _cIn) inp = P(f);
            else if (col == _cClick) click = P(f);
            else if (col == _cFrameLat) fl = P(f);
            col++;
            start = i + 1;
        }
        if (app.IsEmpty) return;
        long now = Stopwatch.GetTimestamp();
        lock (_lock)
        {
            int idx = AppIndex(app);
            _ring[_head] = new Frame { Qpc = now, App = idx, Ft = ft, Disp = disp, In = inp, Click = click, FrameLat = fl };
            _head = (_head + 1) & (_ring.Length - 1);
            if (_count < _ring.Length) _count++;
            if (Recording && idx == _targetIdx)
            {
                if (float.IsFinite(ft)) _sFt.Add(ft);
                if (float.IsFinite(disp) && disp > 0) _sDisp.Add(disp);
                if (float.IsFinite(inp)) _sIn.Add(inp);
                if (float.IsFinite(click)) _sClick.Add(click);
                if (float.IsFinite(fl)) _sFrameLat.Add(fl);
            }
        }
    }

    int AppIndex(ReadOnlySpan<char> app)
    {
        if (_lastAppIdx >= 0 && app.SequenceEqual(_apps[_lastAppIdx])) return _lastAppIdx;
        for (int i = 0; i < _apps.Count; i++)
            if (app.SequenceEqual(_apps[i])) return _lastAppIdx = i;
        _apps.Add(app.ToString());
        return _lastAppIdx = _apps.Count - 1;
    }

    public void ResetSession()
    {
        lock (_lock)
        {
            _sFt.Clear(); _sDisp.Clear(); _sIn.Clear(); _sClick.Clear(); _sFrameLat.Clear();
            _sesIn.Clear(); _sesFrameLat.Clear(); _sesFps.Clear();
        }
    }

    public void Tick(long now)
    {
        lock (_lock)
        {
            UpdateCpu(now);
            long freq = Stopwatch.Frequency;
            long cut3 = now - 3 * freq, cut5 = now - 5 * freq;
            var counts = new int[_apps.Count];
            for (int k = 0; k < _count; k++)
            {
                ref var f = ref _ring[(_head - 1 - k) & (_ring.Length - 1)];
                if (f.Qpc < cut3) break;
                counts[f.App]++;
            }
            PickTarget(counts);

            _lFt.Clear(); _lDisp.Clear(); _lIn.Clear(); _lClick.Clear(); _lFrameLat.Clear();
            double tIn = 0, tFl = 0, tFt = 0; int nIn = 0, nFl = 0, nFt = 0;
            for (int k = 0; k < _count && _targetIdx >= 0; k++)
            {
                ref var f = ref _ring[(_head - 1 - k) & (_ring.Length - 1)];
                if (f.Qpc < cut5) break;
                if (f.App != _targetIdx) continue;
                bool fresh = f.Qpc > _lastTickQpc;
                if (float.IsFinite(f.Ft)) { _lFt.Add(f.Ft); if (fresh) { tFt += f.Ft; nFt++; } }
                if (float.IsFinite(f.Disp) && f.Disp > 0) _lDisp.Add(f.Disp);
                if (float.IsFinite(f.In)) { _lIn.Add(f.In); if (fresh) { tIn += f.In; nIn++; } }
                if (float.IsFinite(f.Click)) _lClick.Add(f.Click);
                if (float.IsFinite(f.FrameLat)) { _lFrameLat.Add(f.FrameLat); if (fresh) { tFl += f.FrameLat; nFl++; } }
            }
            _lastTickQpc = now;

            var ft = Lat(_lFt);
            var disp = Lat(_lDisp);
            _liveFtAvg = ft?.Avg ?? double.NaN;
            _liveFrames = ft == null ? null : new FrameStat(ft.N, 1000 / ft.Avg, 1000 / ft.P99, ft.Avg, ft.P99, disp == null ? double.NaN : 1000 / disp.Avg);
            var inL = Lat(_lIn); _liveIn = inL; _liveInAvg = inL?.Avg ?? double.NaN;
            var cl = Lat(_lClick); _liveClick = cl; _liveClickAvg = cl?.Avg ?? double.NaN;
            var fl = Lat(_lFrameLat); _liveFrameLat = fl; _liveFrameLatAvg = fl?.Avg ?? double.NaN;

            double sIn = nIn > 0 ? tIn / nIn : double.NaN;
            double sFl = nFl > 0 ? tFl / nFl : double.NaN;
            double sFps = nFt > 0 ? 1000 / (tFt / nFt) : double.NaN;
            _serIn.Push(sIn); _serFrameLat.Push(sFl); _serFps.Push(sFps);
            if (Recording) { _sesIn.Add(sIn); _sesFrameLat.Add(sFl); _sesFps.Add(sFps); }

            _appList = Enumerable.Range(0, counts.Length)
                .Where(i => counts[i] > 0)
                .OrderByDescending(i => counts[i])
                .Take(6)
                .Select(i => (object)new { name = _apps[i], fps = counts[i] / 3.0, target = i == _targetIdx })
                .ToArray();
        }
    }

    void PickTarget(int[] counts)
    {
        if (_manualTarget.Length > 0)
        {
            _targetIdx = _apps.FindIndex(a => a.Equals(_manualTarget, StringComparison.OrdinalIgnoreCase));
            return;
        }
        int best = -1, bestIgnored = -1;
        for (int i = 0; i < counts.Length; i++)
        {
            if (counts[i] == 0) continue;
            if (AutoIgnore.Contains(_apps[i])) { if (bestIgnored < 0 || counts[i] > counts[bestIgnored]) bestIgnored = i; }
            else if (best < 0 || counts[i] > counts[best]) best = i;
        }
        if (best < 0) best = bestIgnored;
        if (best < 0) return; // nothing presenting: keep the previous target
        // Hysteresis so a short burst from another app doesn't steal the target.
        if (_targetIdx >= 0 && _targetIdx < counts.Length && counts[_targetIdx] >= 0.7 * counts[best] &&
            (!AutoIgnore.Contains(_apps[_targetIdx]) || AutoIgnore.Contains(_apps[best])))
            return;
        _targetIdx = best;
    }

    static LatStat? Lat(List<float> v)
    {
        if (v.Count == 0) return null;
        v.Sort();
        double sum = 0;
        foreach (var x in v) sum += x;
        float Q(double p) => v[Math.Min(v.Count - 1, (int)(p * (v.Count - 1) + 0.5))];
        return new LatStat(v.Count, sum / v.Count, Q(0.5), Q(0.95), Q(0.99), v[0], v[^1]);
    }

    static LatStat? Lat(Histogram h) =>
        h.N == 0 ? null : new LatStat(h.N, h.Mean, h.Percentile(0.5), h.Percentile(0.95), h.Percentile(0.99), h.Min, h.Max);

    void UpdateCpu(long now)
    {
        var p = _proc;
        if (p == null) { CpuPct = 0; return; }
        if (now - _pmCpuQpc < Stopwatch.Frequency) return;
        try
        {
            var cpu = p.TotalProcessorTime;
            double wall = (now - _pmCpuQpc) / (double)Stopwatch.Frequency;
            if (_pmCpu != TimeSpan.Zero) CpuPct = (cpu - _pmCpu).TotalSeconds / wall / Environment.ProcessorCount * 100;
            _pmCpu = cpu;
            _pmCpuQpc = now;
        }
        catch { }
    }

    object Chain(InputMode mode, double pollWaitMs)
    {
        if (mode == InputMode.Controller)
        {
            // Controller input is not visible to Windows' input stack, so estimate: poll wait + the game picks the
            // input up on average half a frame later + frame start -> display.
            double half = _liveFtAvg / 2;
            double total = pollWaitMs + half + _liveFrameLatAvg;
            return new
            {
                measured = false,
                totalMs = total,
                segments = new object[]
                {
                    new { label = "Device poll wait", ms = pollWaitMs, src = "estimated from polling rate" },
                    new { label = "Game samples input", ms = half, src = "½ frame time (PresentMon)" },
                    new { label = "Frame → photon", ms = _liveFrameLatAvg, src = "PresentMon DisplayLatency" },
                },
            };
        }
        double os = double.IsFinite(_liveInAvg) ? _liveInAvg : _liveClickAvg;
        return new
        {
            measured = true,
            totalMs = pollWaitMs + os,
            segments = new object[]
            {
                new { label = "Device poll wait", ms = pollWaitMs, src = "estimated from polling rate" },
                new { label = "OS input → photon", ms = os, src = "measured by PresentMon" },
            },
        };
    }

    public object BuildJson(InputMode mode, double pollWaitMs)
    {
        lock (_lock)
        {
            var exe = FindExe();
            return new
            {
                available = exe != null,
                exe,
                admin = Elevation.IsAdmin,
                running = _proc != null,
                status = _status,
                error = _error,
                download = DownloadStatus,
                target = _targetIdx >= 0 ? _apps[_targetIdx] : _manualTarget.Length > 0 ? _manualTarget : null,
                targetMode = _manualTarget.Length > 0 ? "manual" : "auto",
                apps = _appList,
                live = new { frames = _liveFrames, inputToPhoton = _liveIn, clickToPhoton = _liveClick, frameLatency = _liveFrameLat },
                session = Recording ? SessionStats() : null,
                series = new { input = _serIn.ToArray(), frameLatency = _serFrameLat.ToArray(), fps = _serFps.ToArray(), step = Analyzer.TickSeconds },
                sessionSeries = Recording ? new { input = _sesIn.ToArray(), frameLatency = _sesFrameLat.ToArray(), fps = _sesFps.ToArray(), step = _sesIn.Step } : null,
                chain = Chain(mode, pollWaitMs),
                cpuPct = CpuPct,
            };
        }
    }

    object SessionStats()
    {
        var ft = Lat(_sFt);
        var disp = Lat(_sDisp);
        return new
        {
            frames = ft == null ? null : new FrameStat(ft.N, 1000 / ft.Avg, 1000 / ft.P99, ft.Avg, ft.P99, disp == null ? double.NaN : 1000 / disp.Avg),
            inputToPhoton = Lat(_sIn),
            clickToPhoton = Lat(_sClick),
            frameLatency = Lat(_sFrameLat),
        };
    }

    public LatencySessionResult BuildSessionJson()
    {
        lock (_lock)
        {
            var ft = Lat(_sFt);
            var inp = Lat(_sIn);
            string? target = _targetIdx >= 0 ? _apps[_targetIdx] : null;
            var doc = new
            {
                target,
                stats = SessionStats(),
                series = new { input = _sesIn.ToArray(), frameLatency = _sesFrameLat.ToArray(), fps = _sesFps.ToArray(), step = _sesIn.Step },
            };
            return new LatencySessionResult(doc, inp?.Avg ?? double.NaN, ft == null ? double.NaN : 1000 / ft.Avg, target);
        }
    }

    /// <summary>Fetches the PresentMon console build from the official GameTechDev GitHub releases.</summary>
    public async Task<string> DownloadAsync()
    {
        DownloadStatus = "Looking up the latest PresentMon release…";
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("PollLagTester/1.0");
            var json = await http.GetStringAsync("https://api.github.com/repos/GameTechDev/PresentMon/releases?per_page=20");
            using var doc = JsonDocument.Parse(json);
            string? url = null, name = null;
            var rx = new Regex(@"^PresentMon-[\d.]+-x64\.exe$", RegexOptions.IgnoreCase);
            foreach (var rel in doc.RootElement.EnumerateArray())
            {
                if (rel.TryGetProperty("prerelease", out var pre) && pre.GetBoolean()) continue;
                foreach (var asset in rel.GetProperty("assets").EnumerateArray())
                {
                    var n = asset.GetProperty("name").GetString() ?? "";
                    if (!rx.IsMatch(n)) continue;
                    url = asset.GetProperty("browser_download_url").GetString();
                    name = n;
                    break;
                }
                if (url != null) break;
            }
            if (url == null || name == null) throw new InvalidOperationException("No PresentMon console build (PresentMon-x.y.z-x64.exe) found in recent releases.");
            DownloadStatus = $"Downloading {name}…";
            Directory.CreateDirectory(ToolsDir);
            var dest = Path.Combine(ToolsDir, name);
            var bytes = await http.GetByteArrayAsync(url);
            await File.WriteAllBytesAsync(dest + ".part", bytes);
            File.Move(dest + ".part", dest, overwrite: true);
            FindExe(force: true);
            DownloadStatus = $"Downloaded {name}";
            return dest;
        }
        catch (Exception ex)
        {
            DownloadStatus = "Download failed: " + ex.Message;
            throw;
        }
    }

    public void Dispose() => Stop();
}
