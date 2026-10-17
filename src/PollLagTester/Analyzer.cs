using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using static PollLagTester.Native;

namespace PollLagTester;

/// <summary>
/// Low-priority thread that drains the collector's ring, computes statistics 4x a second and publishes a
/// pre-serialised JSON snapshot. All state lives on this thread; the web layer only posts commands.
/// </summary>
internal sealed unsafe class Analyzer
{
    public const double TickSeconds = 0.25;
    const double SameReportUs = 50;   // keyboard: several keys changing in one HID report arrive back-to-back
    const double ChatterMs = 10;      // re-press faster than this after a release = switch bounce / chatter

    public static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        IncludeFields = true,
        Converters = { new FiniteDoubleConverter(), new FiniteFloatConverter() },
    };

    readonly RawInputCollector _collector;
    readonly LatencyTracker _latency;
    readonly SessionStore _store;
    readonly ConcurrentQueue<Action> _commands = new();
    readonly Dictionary<nint, DeviceStats> _devices = new();
    readonly double _usPerTick = 1e6 / Stopwatch.Frequency;
    readonly double _msPerTick = 1e3 / Stopwatch.Frequency;
    readonly long _startQpc = Stopwatch.GetTimestamp();
    readonly Process _self = Process.GetCurrentProcess();
    readonly byte[] _report = GC.AllocateArray<byte>(HidReportRing.SlotSize, pinned: true);
    Thread? _thread;
    volatile bool _stop;

    InputMode _mode;
    nint _selected;  // 0 = auto
    nint _autoPick;
    bool _recording;
    DateTime _recStartUtc;
    long _recStartQpc;
    string? _lastSavedId;

    TimeSpan _lastCpu;
    long _lastCpuQpc;
    double _cpuPct, _memMb, _eventsPerSec;
    long _lastReceived;

    byte[] _json = "{}"u8.ToArray();
    byte[] _padJson = "{}"u8.ToArray();
    long _padViewerQpc;
    long _lastPadJsonQpc;

    public Analyzer(RawInputCollector collector, LatencyTracker latency, SessionStore store, InputMode mode)
    {
        _collector = collector;
        _latency = latency;
        _store = store;
        _mode = mode;
    }

    public byte[] Json => Volatile.Read(ref _json);

    /// <summary>Controller Lab stream. Calling it marks a viewer as present, which enables HID decoding.</summary>
    public byte[] PadJson
    {
        get
        {
            Volatile.Write(ref _padViewerQpc, Stopwatch.GetTimestamp());
            return Volatile.Read(ref _padJson);
        }
    }

    public void Start()
    {
        _lastCpu = _self.TotalProcessorTime;
        _lastCpuQpc = Stopwatch.GetTimestamp();
        _thread = new Thread(Loop) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "Analyzer" };
        _thread.Start();
    }

    public void Stop()
    {
        _stop = true;
        _thread?.Join(2000);
    }

    public Task<T> Invoke<T>(Func<T> f)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _commands.Enqueue(() =>
        {
            try { tcs.SetResult(f()); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        return tcs.Task;
    }

    // ---- commands (run on the analyzer thread) ----

    public Task<bool> SetModeAsync(InputMode mode) => Invoke(() =>
    {
        if (mode == _mode) return true;
        if (_recording) StopSessionCore();
        foreach (var d in _devices.Values) d.Dispose();
        _devices.Clear();
        _selected = _autoPick = 0;
        _mode = mode;
        _collector.SetMode(mode);
        return true;
    });

    public Task<bool> SelectAsync(string id) => Invoke(() =>
    {
        _selected = id == "auto" ? 0 : (nint)Convert.ToInt64(id, 16);
        return true;
    });

    public Task<bool> ResetLiveAsync() => Invoke(() =>
    {
        foreach (var d in _devices.Values) d.ResetLive();
        return true;
    });

    public Task<bool> StartSessionAsync() => Invoke(() => { StartSessionCore(); return true; });

    public Task<string?> StopSessionAsync() => Invoke(StopSessionCore);

    public Task<bool> StartDriftAsync(double seconds) => Invoke(() =>
    {
        var pad = SelectedDevice()?.Pad;
        if (pad == null) return false;
        pad.StartDrift(Stopwatch.GetTimestamp(), Math.Clamp(seconds, 2, 30));
        return true;
    });

    public Task<bool> ResetCircularityAsync() => Invoke(() =>
    {
        SelectedDevice()?.Pad?.ResetCircularity();
        return true;
    });

    void StartSessionCore()
    {
        foreach (var d in _devices.Values) d.ResetSession();
        _latency.ResetSession();
        _recording = true;
        _latency.Recording = true;
        _recStartUtc = DateTime.UtcNow;
        _recStartQpc = Stopwatch.GetTimestamp();
    }

    string? StopSessionCore()
    {
        if (!_recording) return null;
        long now = Stopwatch.GetTimestamp();
        foreach (var d in _devices.Values) d.Session = IntervalSummary.From(d.SessionHist, _mode);
        _recording = false;
        _latency.Recording = false;
        var primary = SelectedDevice();
        var lat = _latency.BuildSessionJson();
        string id = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var devices = _devices.Values
            .Where(x => x.SessionHist.N > 0 || x.Presses > 0)
            .OrderByDescending(x => x == primary).ThenByDescending(x => x.SessionHist.N)
            .Select(x => new
            {
                id = Hex(x.Handle),
                name = x.Info.Name,
                manufacturer = x.Info.Manufacturer,
                vidPid = x.Info.VidPid,
                summary = x.Session,
                series = new { a = x.SessA.ToArray(), b = x.SessB.ToArray(), step = x.SessA.Step },
                hist = HistJson(x.SessionHist, x.Session),
                keys = KeysJson(x),
            })
            .ToArray();
        var head = devices.FirstOrDefault();
        var doc = new
        {
            id,
            version = 1,
            startedAt = _recStartUtc,
            endedAt = DateTime.UtcNow,
            durationSec = (now - _recStartQpc) / (double)Stopwatch.Frequency,
            mode = ModeName(_mode),
            headline = new
            {
                device = head?.name,
                avgHz = head?.summary?.AvgHz,
                nominalHz = head?.summary?.NominalHz,
                jitterUs = head?.summary?.JitterUs,
                lagMs = lat.InputToPhotonAvg,
                fps = lat.Fps,
                target = lat.Target,
            },
            devices,
            latency = lat.Doc,
        };
        try
        {
            _store.Save(id, doc);
            _lastSavedId = id;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Could not save session: {ex.Message}");
            return null;
        }
        return id;
    }

    // ---- main loop ----

    void Loop()
    {
        var batch = new InputEvent[8192];
        long tickTicks = (long)(TickSeconds * Stopwatch.Frequency);
        long nextTick = Stopwatch.GetTimestamp() + tickTicks;
        while (!_stop)
        {
            while (_commands.TryDequeue(out var cmd))
            {
                try { cmd(); }
                catch (Exception ex) { Console.Error.WriteLine(ex); }
            }

            long now = Stopwatch.GetTimestamp();
            bool parsePads = _mode == InputMode.Controller &&
                (now - Volatile.Read(ref _padViewerQpc) < 3 * Stopwatch.Frequency || AnyDriftActive());
            int n;
            while ((n = _collector.Ring.Drain(batch)) > 0)
                for (int i = 0; i < n; i++) Handle(in batch[i], parsePads);

            now = Stopwatch.GetTimestamp();
            if (now >= nextTick)
            {
                try { Tick(now); }
                catch (Exception ex) { Console.Error.WriteLine(ex); }
                nextTick += tickTicks;
                if (nextTick < now) nextTick = now + tickTicks;
            }
            if (parsePads && now - _lastPadJsonQpc > Stopwatch.Frequency / 40)
            {
                _lastPadJsonQpc = now;
                BuildPadJson(now);
            }
            Thread.Sleep(parsePads ? 10 : 20);
        }
        if (_recording) StopSessionCore();
    }

    bool AnyDriftActive() => SelectedDevice()?.Pad?.DriftActive == true;

    void Handle(in InputEvent e, bool parsePads)
    {
        switch (e.Kind)
        {
            case EventKind.Hotkey:
                if (_recording) StopSessionCore(); else StartSessionCore();
                MessageBeep(_recording ? 0x40u : 0x30u);
                return;
            case EventKind.DeviceAdded:
            case EventKind.DeviceRemoved:
                if (_devices.TryGetValue(e.Device, out var dev)) dev.Connected = e.Kind == EventKind.DeviceAdded;
                return;
        }

        bool matches = _mode switch
        {
            InputMode.Mouse => e.Kind == EventKind.Mouse,
            InputMode.Keyboard => e.Kind == EventKind.Key,
            _ => e.Kind == EventKind.Hid,
        };
        if (!matches) return;

        if (!_devices.TryGetValue(e.Device, out var d))
        {
            d = new DeviceStats(e.Device, _mode);
            _devices[e.Device] = d;
        }

        switch (e.Kind)
        {
            case EventKind.Mouse:
                if ((e.Code & 0x3FF) != 0) MouseButtons(d, e.Code, e.Qpc);
                break;
            case EventKind.Key:
                if (!Key(d, in e)) return; // auto-repeat or a fake key: not a new report
                break;
            case EventKind.Hid:
                if (parsePads) DecodePad(d, in e);
                break;
        }
        Report(d, e.Qpc);
    }

    void Report(DeviceStats d, long qpc)
    {
        d.Reports++;
        d.LastSeenQpc = qpc;
        d.CountReport();
        if (d.LastQpc != 0)
        {
            double us = (qpc - d.LastQpc) * _usPerTick;
            if (_mode == InputMode.Keyboard && us < SameReportUs) return; // same USB report, keep the first timestamp
            if (us < d.MaxIntervalUs) d.AddInterval(qpc, (float)us, _recording);
            else d.Gaps++;
        }
        d.LastQpc = qpc;
    }

    bool Key(DeviceStats d, in InputEvent e)
    {
        int vk = e.Code;
        if (vk == 0xFF) return false;
        bool e0 = (e.Flags & 2) != 0;
        if (e0 && (e.A == 0x2A || e.A == 0x36)) return false; // fake shift generated around E0 keys
        int idx = (vk & 0xFF) | (e0 ? 0x100 : 0);
        if ((e.Flags & 1) != 0) { Release(d, idx, e.Qpc); return true; }
        return Press(d, idx, e.Qpc);
    }

    void MouseButtons(DeviceStats d, ushort flags, long qpc)
    {
        for (int b = 0; b < 5; b++)
        {
            if ((flags & (1 << (2 * b))) != 0) Press(d, 512 + b, qpc);
            if ((flags & (1 << (2 * b + 1))) != 0) Release(d, 512 + b, qpc);
        }
    }

    bool Press(DeviceStats d, int idx, long qpc)
    {
        if (d.Down[idx] != 0) return false; // typematic repeat
        if (d.Up[idx] != 0)
        {
            double gap = (qpc - d.Up[idx]) * _msPerTick;
            if (gap < ChatterMs) d.AddChatter(DeviceInfo.KeyName(idx), gap, Seconds(qpc));
        }
        d.Down[idx] = qpc;
        d.Presses++;
        return true;
    }

    void Release(DeviceStats d, int idx, long qpc)
    {
        if (d.Down[idx] != 0) d.Taps.Add((qpc - d.Down[idx]) * _msPerTick);
        d.Down[idx] = 0;
        d.Up[idx] = qpc;
    }

    void DecodePad(DeviceStats d, in InputEvent e)
    {
        d.Pad ??= new Gamepad(d.Handle);
        fixed (byte* buf = _report)
        {
            if (!_collector.Reports.TryRead(e.Seq, buf, out int len)) return;
            d.Pad.Process(buf, len, e.Qpc);
        }
    }

    // ---- periodic work ----

    void Tick(long now)
    {
        double window = _mode == InputMode.Keyboard ? 15 : 2;
        long cutoff = now - (long)(window * Stopwatch.Frequency);
        DeviceStats? best = null;
        foreach (var d in _devices.Values)
        {
            d.UpdateLive(cutoff);
            d.PushTick(_recording, TickSeconds);
            if (_recording) d.Session = IntervalSummary.From(d.SessionHist, _mode);
            d.Pad?.CheckDrift(now);
            if (best == null || d.LiveCount > best.LiveCount) best = d;
        }
        if (best != null && best.LiveCount > 0) _autoPick = best.Handle;

        if (now - _lastCpuQpc >= Stopwatch.Frequency)
        {
            _self.Refresh();
            var cpu = _self.TotalProcessorTime;
            double wall = (now - _lastCpuQpc) / (double)Stopwatch.Frequency;
            _cpuPct = (cpu - _lastCpu).TotalSeconds / wall / Environment.ProcessorCount * 100;
            _memMb = _self.WorkingSet64 / 1048576.0;
            long rec = _collector.Received;
            _eventsPerSec = (rec - _lastReceived) / wall;
            _lastReceived = rec;
            _lastCpu = cpu;
            _lastCpuQpc = now;
        }

        _latency.Tick(now);
        Volatile.Write(ref _json, JsonSerializer.SerializeToUtf8Bytes(BuildSnapshot(now), JsonOpts));
    }

    DeviceStats? SelectedDevice()
    {
        if (_selected != 0 && _devices.TryGetValue(_selected, out var s)) return s;
        return _autoPick != 0 && _devices.TryGetValue(_autoPick, out var a) ? a : null;
    }

    void BuildPadJson(long now)
    {
        var d = SelectedDevice();
        object payload = d?.Pad == null
            ? new { name = d?.Info.Name, ok = false, waiting = true }
            : d.Pad.ToJson(d.Info.Name, now);
        Volatile.Write(ref _padJson, JsonSerializer.SerializeToUtf8Bytes(payload, JsonOpts));
    }

    object BuildSnapshot(long now)
    {
        var d = SelectedDevice();
        double freq = Stopwatch.Frequency;
        var summary = _recording ? d?.Session ?? d?.Live : d?.Live;
        return new
        {
            t = (now - _startQpc) / freq,
            mode = ModeName(_mode),
            admin = Elevation.IsAdmin,
            hotkey = _collector.HotkeyRegistered ? "Ctrl+Shift+F9" : null,
            registerError = _collector.LastError,
            recording = new
            {
                active = _recording,
                startedAt = _recording ? _recStartUtc : (DateTime?)null,
                elapsed = _recording ? (now - _recStartQpc) / freq : 0,
            },
            lastSavedId = _lastSavedId,
            selected = _selected == 0 ? "auto" : Hex(_selected),
            devices = _devices.Values.OrderByDescending(x => x.Reports).Select(x => new
            {
                id = Hex(x.Handle),
                name = x.Info.Name,
                vidPid = x.Info.VidPid,
                connected = x.Connected,
                liveHz = _mode == InputMode.Keyboard ? x.Live?.NominalHz : x.Live?.AvgHz,
                nominalHz = x.Live?.NominalHz,
                reports = x.Reports,
                active = now - x.LastSeenQpc < freq,
            }).ToArray(),
            device = d == null ? null : new
            {
                id = Hex(d.Handle),
                name = d.Info.Name,
                manufacturer = d.Info.Manufacturer,
                vidPid = d.Info.VidPid,
                path = d.Info.Path,
                connected = d.Connected,
                reports = d.Reports,
                gaps = d.Gaps,
                live = d.Live,
                session = _recording ? d.Session : null,
                liveSeries = new { a = d.LiveA.ToArray(), b = d.LiveB.ToArray(), step = TickSeconds },
                sessionSeries = _recording ? new { a = d.SessA.ToArray(), b = d.SessB.ToArray(), step = d.SessA.Step } : null,
                liveHist = HistJson(d.LiveHist, d.Live),
                sessionHist = _recording ? HistJson(d.SessionHist, d.Session) : null,
                keys = KeysJson(d),
                hasPad = d.Pad != null,
            },
            latency = _latency.BuildJson(_mode, summary?.PollWaitMs ?? double.NaN),
            overhead = new
            {
                cpuPct = _cpuPct,
                cpuCorePct = _cpuPct * Environment.ProcessorCount,
                memMb = _memMb,
                eventsPerSec = _eventsPerSec,
                dropped = _collector.Ring.Dropped,
                ringFill = _collector.Ring.Count,
                presentMonCpuPct = _latency.CpuPct,
                cores = Environment.ProcessorCount,
            },
        };
    }

    object HistJson(Histogram h, IntervalSummary? s)
    {
        double hi;
        int bins;
        double nominalUs = s != null && double.IsFinite(s.NominalHz) ? 1e6 / s.NominalHz : double.NaN;
        if (_mode == InputMode.Keyboard)
        {
            hi = double.IsFinite(nominalUs) ? Math.Min(nominalUs * 12, h.Range) : 10_000;
            bins = 120;
        }
        else
        {
            hi = double.IsFinite(nominalUs) ? Math.Min(nominalUs * 2.5, h.Range) : 5000;
            bins = 60;
        }
        return new { lo = 0.0, hi, bins = h.Rebin(0, hi, bins), nominalUs, total = h.N };
    }

    static object KeysJson(DeviceStats d) => new
    {
        presses = d.Presses,
        tapAvgMs = d.Taps.Mean,
        tapMinMs = d.Taps.N > 0 ? d.Taps.Min : double.NaN,
        tapMaxMs = d.Taps.N > 0 ? d.Taps.Max : double.NaN,
        chatter = d.Chatter,
        chatterLog = d.ChatterLog.TakeLast(12).Reverse().ToArray(),
    };

    double Seconds(long qpc) => (qpc - _startQpc) / (double)Stopwatch.Frequency;

    static string Hex(nint h) => ((long)h).ToString("X");

    public static string ModeName(InputMode m) => m switch
    {
        InputMode.Mouse => "mouse",
        InputMode.Keyboard => "keyboard",
        _ => "controller",
    };
}
