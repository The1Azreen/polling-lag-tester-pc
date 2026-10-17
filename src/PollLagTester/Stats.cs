using System.Text.Json;
using System.Text.Json.Serialization;

namespace PollLagTester;

/// <summary>Fixed-bin histogram: O(1) inserts, constant memory no matter how long a session runs.</summary>
internal sealed class Histogram(double binWidth, double max)
{
    public readonly double BinWidth = binWidth;
    public readonly int[] Counts = new int[(int)Math.Ceiling(max / binWidth)];
    public long N;
    public double Sum, SumSq, Min = double.PositiveInfinity, Max = double.NegativeInfinity;

    public double Range => Counts.Length * BinWidth;
    public double Mean => N > 0 ? Sum / N : double.NaN;

    public void Add(double v)
    {
        int i = (int)(v / BinWidth);
        if ((uint)i >= (uint)Counts.Length) return;
        Counts[i]++;
        N++;
        Sum += v;
        SumSq += v * v;
        if (v < Min) Min = v;
        if (v > Max) Max = v;
    }

    public void Clear()
    {
        Array.Clear(Counts);
        N = 0;
        Sum = SumSq = 0;
        Min = double.PositiveInfinity;
        Max = double.NegativeInfinity;
    }

    /// <param name="p">0..1</param>
    public double Percentile(double p)
    {
        if (N == 0) return double.NaN;
        double target = p * N;
        long cum = 0;
        for (int i = 0; i < Counts.Length; i++)
        {
            int c = Counts[i];
            if (c == 0) continue;
            if (cum + c >= target)
                return Math.Clamp((i + (target - cum) / c) * BinWidth, Min, Max);
            cum += c;
        }
        return Max;
    }

    public long CountRange(double lo, double hi)
    {
        int a = (int)Math.Max(0, lo / BinWidth);
        int b = (int)Math.Min(Counts.Length - 1, hi / BinWidth);
        long s = 0;
        for (int i = a; i <= b; i++) s += Counts[i];
        return s;
    }

    /// <summary>Standard deviation of the samples that fall in [lo, hi] (used for jitter around the nominal interval).</summary>
    public double StdDevRange(double lo, double hi)
    {
        int a = (int)Math.Max(0, lo / BinWidth);
        int b = (int)Math.Min(Counts.Length - 1, hi / BinWidth);
        double n = 0, s = 0, ss = 0;
        for (int i = a; i <= b; i++)
        {
            int c = Counts[i];
            if (c == 0) continue;
            double v = (i + 0.5) * BinWidth;
            n += c; s += c * v; ss += c * v * v;
        }
        return n > 1 ? Math.Sqrt(Math.Max(0, (ss - s * s / n) / (n - 1))) : double.NaN;
    }

    public int[] Rebin(double lo, double hi, int bins)
    {
        var r = new int[bins];
        double span = hi - lo;
        if (span <= 0) return r;
        for (int i = 0; i < Counts.Length; i++)
        {
            int c = Counts[i];
            if (c == 0) continue;
            double v = (i + 0.5) * BinWidth;
            if (v < lo || v >= hi) continue;
            r[Math.Min(bins - 1, (int)((v - lo) / span * bins))] += c;
        }
        return r;
    }
}

/// <summary>Last N values (live charts).</summary>
internal sealed class RingSeries(int capacity)
{
    readonly double[] _v = new double[capacity];
    int _head, _count;

    public void Push(double x)
    {
        _v[_head] = x;
        _head = (_head + 1) % _v.Length;
        if (_count < _v.Length) _count++;
    }

    public void Clear() => _head = _count = 0;

    public double[] ToArray()
    {
        var r = new double[_count];
        int start = (_head - _count + _v.Length) % _v.Length;
        for (int i = 0; i < _count; i++) r[i] = _v[(start + i) % _v.Length];
        return r;
    }
}

/// <summary>Whole-session series that halves its resolution when full, so memory stays bounded.</summary>
internal sealed class Downsampled(double baseStep, int cap)
{
    readonly List<double> _v = new();
    readonly double _baseStep = baseStep;
    int _factor = 1, _accTotal, _accN;
    double _acc;

    public double Step => _baseStep * _factor;

    public void Add(double v)
    {
        _accTotal++;
        if (double.IsFinite(v)) { _acc += v; _accN++; }
        if (_accTotal < _factor) return;
        _v.Add(_accN > 0 ? _acc / _accN : double.NaN);
        _acc = 0; _accN = 0; _accTotal = 0;
        if (_v.Count < cap) return;
        int half = _v.Count / 2;
        for (int i = 0; i < half; i++) _v[i] = Avg(_v[2 * i], _v[2 * i + 1]);
        _v.RemoveRange(half, _v.Count - half);
        _factor *= 2;
    }

    static double Avg(double a, double b) =>
        double.IsFinite(a) && double.IsFinite(b) ? (a + b) / 2 : double.IsFinite(a) ? a : b;

    public void Clear() { _v.Clear(); _factor = 1; _acc = 0; _accN = 0; _accTotal = 0; }
    public double[] ToArray() => _v.ToArray();
}

internal sealed class RunningStat
{
    public long N;
    public double Sum, Min = double.PositiveInfinity, Max = double.NegativeInfinity;
    public double Mean => N > 0 ? Sum / N : double.NaN;

    public void Add(double v)
    {
        N++; Sum += v;
        if (v < Min) Min = v;
        if (v > Max) Max = v;
    }

    public void Reset() { N = 0; Sum = 0; Min = double.PositiveInfinity; Max = double.NegativeInfinity; }
}

/// <summary>Polling interval statistics for one device over one window.</summary>
internal sealed class IntervalSummary
{
    static readonly double[] StandardRates = [125, 250, 500, 1000, 2000, 4000, 8000];

    public long N { get; set; }
    public double AvgHz { get; set; }
    public double MedianHz { get; set; }
    public double NominalHz { get; set; }
    public bool Snapped { get; set; }
    public bool Estimated { get; set; }
    public double Confidence { get; set; } = double.NaN;
    public double MeanUs { get; set; }
    public double MedianUs { get; set; }
    public double MinUs { get; set; }
    public double MaxUs { get; set; }
    public double P1Us { get; set; }
    public double P99Us { get; set; }
    public double JitterUs { get; set; } = double.NaN;
    public double WithinPct { get; set; } = double.NaN;
    public double OffCadencePct { get; set; } = double.NaN;
    public double PollWaitMs { get; set; }
    public string? Hint { get; set; }

    public static IntervalSummary? From(Histogram h, InputMode mode)
    {
        if (h.N < 3) return null;
        var s = new IntervalSummary
        {
            N = h.N,
            MeanUs = h.Mean,
            MedianUs = h.Percentile(0.5),
            MinUs = h.Min,
            MaxUs = h.Max,
            P1Us = h.Percentile(0.01),
            P99Us = h.Percentile(0.99),
        };
        s.AvgHz = 1e6 / s.MeanUs;
        s.MedianHz = 1e6 / s.MedianUs;

        if (mode == InputMode.Keyboard)
        {
            // Keyboards only send a report when a key changes, so the gaps are "human time".
            // But each event still lands on a USB poll, so gaps are multiples of the poll period.
            var (rate, conf, grid) = EstimateQuantized(h);
            s.Estimated = true;
            s.AvgHz = double.NaN;
            s.MedianHz = double.NaN;
            s.NominalHz = rate;
            s.Confidence = conf;
            s.WithinPct = grid * 100;
            s.Snapped = double.IsFinite(rate);
            if (!s.Snapped)
                s.Hint = h.N < 12 ? "need more key presses" : s.P1Us < 700 ? "faster than 1 kHz (jitter too high to resolve)" : "no clear timing grid yet";
            s.PollWaitMs = double.IsFinite(rate) ? 1e3 / rate / 2 : double.NaN;
            return s;
        }

        (s.NominalHz, s.Snapped) = Snap(s.MedianHz);
        double nom = 1e6 / s.NominalHz;
        s.WithinPct = 100.0 * h.CountRange(nom * 0.85, nom * 1.15) / h.N;
        s.OffCadencePct = 100.0 * h.CountRange(nom * 1.5, double.MaxValue) / h.N;
        s.JitterUs = h.StdDevRange(nom * 0.5, nom * 1.5);
        // On average an event happens half a poll interval before the device is next polled.
        s.PollWaitMs = nom / 2000;
        return s;
    }

    static (double, bool) Snap(double hz)
    {
        foreach (var r in StandardRates)
            if (Math.Abs(hz - r) / r <= 0.10) return (r, true);
        return (Math.Round(hz), false);
    }

    /// <summary>
    /// Finds the slowest standard poll rate whose period the observed gaps line up with.
    /// A gap "hits" if it is within tolerance of an integer multiple of the period; we require the
    /// hit fraction to be well above what random gaps would give by chance.
    /// </summary>
    static (double rate, double conf, double grid) EstimateQuantized(Histogram h)
    {
        if (h.N < 12) return (double.NaN, double.NaN, double.NaN);
        foreach (double rate in StandardRates)
        {
            double p = 1e6 / rate;
            double tol = Math.Max(0.15 * p, 40); // OS delivery jitter is tens of microseconds
            if (tol >= 0.45 * p) continue;
            double hit = 0;
            for (int i = 0; i < h.Counts.Length; i++)
            {
                int c = h.Counts[i];
                if (c == 0) continue;
                double v = (i + 0.5) * h.BinWidth;
                double k = Math.Round(v / p);
                if (k >= 1 && Math.Abs(v - k * p) <= tol) hit += c;
            }
            double score = hit / h.N;
            double chance = 2 * tol / p;
            if (score >= chance + (1 - chance) * 0.6)
                return (rate, (score - chance) / (1 - chance), score);
        }
        return (double.NaN, double.NaN, double.NaN);
    }
}

/// <summary>Writes NaN/Infinity as null and rounds to 3 decimals to keep payloads small.</summary>
internal sealed class FiniteDoubleConverter : JsonConverter<double>
{
    public override double Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.Null ? double.NaN : reader.GetDouble();

    public override void Write(Utf8JsonWriter writer, double value, JsonSerializerOptions options)
    {
        if (double.IsFinite(value)) writer.WriteNumberValue(Math.Round(value, 3));
        else writer.WriteNullValue();
    }
}

internal sealed class FiniteFloatConverter : JsonConverter<float>
{
    public override float Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.Null ? float.NaN : reader.GetSingle();

    public override void Write(Utf8JsonWriter writer, float value, JsonSerializerOptions options)
    {
        if (float.IsFinite(value)) writer.WriteNumberValue(Math.Round(value, 4));
        else writer.WriteNullValue();
    }
}
