using System.Runtime.InteropServices;

namespace PollLagTester;

internal sealed record ChatterEvent(string Key, double GapMs, double AtSec);

/// <summary>Everything measured for one physical device. Only touched by the analyzer thread.</summary>
internal sealed class DeviceStats : IDisposable
{
    const int RecentCap = 1 << 15;

    public readonly nint Handle;
    public readonly InputMode Kind;
    public readonly DeviceInfo.Info Info;
    public bool Connected = true;

    public long LastQpc, LastSeenQpc, Reports, Gaps;
    public readonly Histogram SessionHist, LiveHist;
    readonly long[] _recentQpc = new long[RecentCap];
    readonly float[] _recentUs = new float[RecentCap];
    int _recentHead, _recentCount;
    public int LiveCount;
    public IntervalSummary? Live, Session;

    readonly List<float> _tickIntervals = new();
    int _tickReports;
    public readonly RingSeries LiveA = new(240), LiveB = new(240);
    public readonly Downsampled SessA = new(0.25, 2400), SessB = new(0.25, 2400);

    // Key / button state for tap duration and chatter (bounce) detection.
    public readonly long[] Down = new long[520], Up = new long[520];
    public long Presses, Chatter;
    public readonly RunningStat Taps = new();
    public readonly List<ChatterEvent> ChatterLog = new();

    public Gamepad? Pad;

    public DeviceStats(nint handle, InputMode kind)
    {
        Handle = handle;
        Kind = kind;
        Info = DeviceInfo.Describe(handle, kind);
        // Keyboards: gaps between key events are human-scale, keep up to 250 ms. Others: 40 ms (anything longer = idle).
        double max = kind == InputMode.Keyboard ? 250_000 : 40_000;
        SessionHist = new Histogram(10, max);
        LiveHist = new Histogram(10, max);
    }

    public double MaxIntervalUs => SessionHist.Range;

    public void AddInterval(long qpc, float us, bool recording)
    {
        int i = _recentHead;
        _recentQpc[i] = qpc;
        _recentUs[i] = us;
        _recentHead = (i + 1) & (RecentCap - 1);
        if (_recentCount < RecentCap) _recentCount++;
        _tickIntervals.Add(us);
        if (recording) SessionHist.Add(us);
    }

    public void CountReport() => _tickReports++;

    public void UpdateLive(long cutoff)
    {
        LiveHist.Clear();
        LiveCount = 0;
        for (int k = 0; k < _recentCount; k++)
        {
            int idx = (_recentHead - 1 - k) & (RecentCap - 1);
            if (_recentQpc[idx] < cutoff) break;
            LiveHist.Add(_recentUs[idx]);
            LiveCount++;
        }
        Live = IntervalSummary.From(LiveHist, Kind);
    }

    public void PushTick(bool recording, double tickSeconds)
    {
        double a, b;
        if (Kind == InputMode.Keyboard)
        {
            a = _tickReports / tickSeconds; // key events per second
            b = double.NaN;
        }
        else if (_tickIntervals.Count >= 3)
        {
            var span = CollectionsMarshal.AsSpan(_tickIntervals);
            span.Sort();
            double sum = 0;
            foreach (var v in span) sum += v;
            a = 1e6 / span[span.Length / 2];
            b = 1e6 / (sum / span.Length);
        }
        else a = b = double.NaN;
        LiveA.Push(a);
        LiveB.Push(b);
        if (recording) { SessA.Add(a); SessB.Add(b); }
        _tickIntervals.Clear();
        _tickReports = 0;
    }

    public void ResetLive()
    {
        _recentCount = 0;
        LiveA.Clear(); LiveB.Clear();
        LiveHist.Clear();
        Live = null;
    }

    public void ResetSession()
    {
        SessionHist.Clear();
        SessA.Clear(); SessB.Clear();
        Session = null;
        Presses = 0; Chatter = 0;
        Taps.Reset();
        ChatterLog.Clear();
    }

    public void AddChatter(string key, double gapMs, double atSec)
    {
        Chatter++;
        ChatterLog.Add(new ChatterEvent(key, gapMs, atSec));
        if (ChatterLog.Count > 50) ChatterLog.RemoveAt(0);
    }

    public void Dispose() => Pad?.Dispose();
}
