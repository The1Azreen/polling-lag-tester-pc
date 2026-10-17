using System.Diagnostics;
using System.Runtime.InteropServices;
using static PollLagTester.Native;

namespace PollLagTester;

/// <summary>
/// Decodes HID gamepad reports with the OS's own HID parser (HidP_*) using the device's report descriptor,
/// so it works for any HID controller: DualSense/DS4, Xbox (HID-compliant game controller), Switch Pro, 8BitDo, ...
/// Only runs while the Controller Lab is open or a stick test is active.
/// </summary>
internal sealed unsafe class Gamepad : IDisposable
{
    const int AX = 0, AY = 1, AZ = 2, ARX = 3, ARY = 4, ARZ = 5, AAccel = 9, ABrake = 10, AxisSlots = 11;
    static readonly string[] AxisNames = ["X", "Y", "Z", "Rx", "Ry", "Rz", "Slider", "Dial", "Wheel", "Accel", "Brake"];

    struct AxisCap { public ushort Page, Usage, Link; public long Min, Max; public int Bits; public bool Signed; public int Slot; }

    byte* _pp;
    readonly List<AxisCap> _caps = new();
    AxisCap _hat;
    bool _hasHat;
    readonly uint _maxButtons;

    public readonly int InputLen;
    public readonly string? Error;
    public readonly double[] Axis = new double[AxisSlots];
    public uint Buttons;
    public int Hat = -1;
    public long Parsed, ParseErrors;

    readonly int _rxSlot = -1, _rySlot = -1, _ltSlot = -1, _rtSlot = -1;
    readonly bool _combinedTriggers;
    public readonly string Mapping;

    public readonly Stick L = new(), R = new();
    public double LT = double.NaN, RT = double.NaN;

    bool _driftActive;
    long _driftStartQpc, _driftEndQpc;
    double _driftSeconds;
    readonly RunningStat _ltRest = new(), _rtRest = new();
    public object? DriftResult;

    static readonly long TrailTicks = Stopwatch.Frequency / 250; // ~4 ms between trail points

    public Gamepad(nint device)
    {
        Array.Fill(Axis, double.NaN);
        Mapping = "unknown";
        uint size = 0;
        GetRawInputDeviceInfoW(device, RIDI_PREPARSEDDATA, null, &size);
        if (size == 0) { Error = "No HID descriptor available for this device."; return; }
        _pp = (byte*)NativeMemory.Alloc(size);
        if (GetRawInputDeviceInfoW(device, RIDI_PREPARSEDDATA, _pp, &size) == uint.MaxValue) { Error = "Could not read HID descriptor."; return; }

        HIDP_CAPS caps;
        if (HidP_GetCaps((nint)_pp, &caps) != HIDP_STATUS_SUCCESS) { Error = "HidP_GetCaps failed."; return; }
        InputLen = caps.InputReportByteLength;

        ushort n = caps.NumberInputValueCaps;
        var vc = new HIDP_VALUE_CAPS[Math.Max(1, (int)n)];
        fixed (HIDP_VALUE_CAPS* p = vc)
            if (n > 0 && HidP_GetValueCaps(HidP_Input, p, &n, (nint)_pp) != HIDP_STATUS_SUCCESS) n = 0;

        for (int i = 0; i < n; i++)
        {
            var c = vc[i];
            if (c.ReportCount > 1) continue;
            ushort lo = c.UsageMin, hi = c.IsRange != 0 ? c.UsageMax : c.UsageMin;
            for (int u = lo; u <= hi && u <= 0xFFFF; u++)
            {
                int slot = SlotFor(c.UsagePage, (ushort)u);
                bool isHat = c.UsagePage == 0x01 && u == 0x39;
                if (slot < 0 && !isHat) continue;
                long min = c.LogicalMin, max = c.LogicalMax;
                bool signed = min < 0;
                int bits = Math.Clamp((int)c.BitSize, 1, 32);
                if (max <= min) { min = 0; max = (1L << bits) - 1; signed = false; } // descriptor declared an unsigned max that overflowed
                var cap = new AxisCap { Page = c.UsagePage, Usage = (ushort)u, Link = c.LinkCollection, Min = min, Max = max, Bits = bits, Signed = signed, Slot = slot };
                if (isHat) { _hat = cap; _hasHat = true; }
                else if (!_caps.Exists(x => x.Slot == slot)) _caps.Add(cap);
            }
        }
        _maxButtons = HidP_MaxUsageListLength(HidP_Input, 0x09, (nint)_pp);

        bool Has(int s) => _caps.Exists(x => x.Slot == s);
        if (Has(AZ) && Has(ARZ))
        {
            _rxSlot = AZ; _rySlot = ARZ;
            if (Has(ARX)) _ltSlot = ARX;
            if (Has(ARY)) _rtSlot = ARY;
            Mapping = "Right stick = Z/Rz, triggers = Rx/Ry (PlayStation / DirectInput layout)";
        }
        else if (Has(ARX) && Has(ARY))
        {
            _rxSlot = ARX; _rySlot = ARY;
            if (Has(AZ)) { _combinedTriggers = true; _ltSlot = AZ; }
            Mapping = "Right stick = Rx/Ry, triggers = Z (Xbox HID layout)";
        }
        if (Has(ABrake)) _ltSlot = ABrake;
        if (Has(AAccel)) _rtSlot = AAccel;
        if (!Has(AX) || !Has(AY)) Error = "This device doesn't expose X/Y stick axes over HID.";
    }

    static int SlotFor(ushort page, ushort usage) => page switch
    {
        0x01 when usage >= 0x30 && usage <= 0x38 => usage - 0x30,
        0x02 when usage == 0xC4 => AAccel,
        0x02 when usage == 0xC5 => ABrake,
        _ => -1,
    };

    public bool Ok => _pp != null && Error == null;
    public bool DriftActive => _driftActive;
    public int ButtonCount => (int)Math.Min(_maxButtons, 32);

    public void Process(byte* report, int len, long qpc)
    {
        if (_pp == null) return;
        bool any = false;
        foreach (var a in _caps)
        {
            uint raw;
            if (HidP_GetUsageValue(HidP_Input, a.Page, a.Link, a.Usage, &raw, (nint)_pp, report, (uint)len) != HIDP_STATUS_SUCCESS) continue;
            Axis[a.Slot] = Normalize(a, raw);
            any = true;
        }
        if (_hasHat)
        {
            uint raw;
            if (HidP_GetUsageValue(HidP_Input, _hat.Page, _hat.Link, _hat.Usage, &raw, (nint)_pp, report, (uint)len) == HIDP_STATUS_SUCCESS)
            {
                long v = Signed(_hat, raw);
                Hat = v < _hat.Min || v > _hat.Max ? -1 : (int)(v - _hat.Min);
            }
        }
        if (_maxButtons > 0)
        {
            uint cnt = _maxButtons;
            ushort* list = stackalloc ushort[(int)cnt];
            if (HidP_GetUsages(HidP_Input, 0x09, 0, list, &cnt, (nint)_pp, report, (uint)len) == HIDP_STATUS_SUCCESS)
            {
                uint mask = 0;
                for (int i = 0; i < cnt; i++) if (list[i] >= 1 && list[i] <= 32) mask |= 1u << (list[i] - 1);
                Buttons = mask;
            }
        }
        if (!any) { ParseErrors++; return; }
        Parsed++;

        if (_combinedTriggers)
        {
            double z = Axis[_ltSlot];
            LT = Math.Max(0, (z - 0.5) * 2);
            RT = Math.Max(0, (0.5 - z) * 2);
        }
        else
        {
            if (_ltSlot >= 0) LT = Axis[_ltSlot];
            if (_rtSlot >= 0) RT = Axis[_rtSlot];
        }
        bool drift = _driftActive && qpc <= _driftEndQpc;
        L.Add(Axis[AX] * 2 - 1, Axis[AY] * 2 - 1, qpc, TrailTicks, drift);
        if (_rxSlot >= 0) R.Add(Axis[_rxSlot] * 2 - 1, Axis[_rySlot] * 2 - 1, qpc, TrailTicks, drift);
        if (drift)
        {
            if (double.IsFinite(LT)) _ltRest.Add(LT);
            if (double.IsFinite(RT)) _rtRest.Add(RT);
        }
    }

    static long Signed(in AxisCap a, uint raw)
    {
        long v = raw;
        if (a.Signed && a.Bits < 32 && (v & (1L << (a.Bits - 1))) != 0) v -= 1L << a.Bits;
        return v;
    }

    static double Normalize(in AxisCap a, uint raw) =>
        Math.Clamp((Signed(a, raw) - a.Min) / (double)(a.Max - a.Min), 0, 1);

    public void StartDrift(long now, double seconds)
    {
        _driftSeconds = seconds;
        _driftStartQpc = now;
        _driftEndQpc = now + (long)(seconds * Stopwatch.Frequency);
        _driftActive = true;
        DriftResult = null;
        L.ResetDrift(); R.ResetDrift(); _ltRest.Reset(); _rtRest.Reset();
    }

    /// <summary>Called every analyzer tick so the test also finishes when the pad sends nothing.</summary>
    public void CheckDrift(long now)
    {
        if (!_driftActive || now < _driftEndQpc) return;
        _driftActive = false;
        // Xbox pads only report on change: an untouched pad sends nothing, so fall back to the last resting value.
        bool idleOnly = L.DriftSamples == 0;
        DriftResult = new
        {
            seconds = _driftSeconds,
            finishedAt = DateTime.UtcNow,
            idleOnly,
            left = L.DriftReport(),
            right = _rxSlot >= 0 ? R.DriftReport() : null,
            ltRestMax = _ltRest.N > 0 ? _ltRest.Max : LT,
            rtRestMax = _rtRest.N > 0 ? _rtRest.Max : RT,
        };
    }

    public object ToJson(string name, long now)
    {
        var pressed = new List<int>();
        for (int i = 0; i < 32; i++) if ((Buttons & (1u << i)) != 0) pressed.Add(i + 1);
        var axes = new List<object>();
        foreach (var a in _caps) axes.Add(new { name = AxisNames[a.Slot], v = Axis[a.Slot] });
        return new
        {
            name,
            ok = Ok,
            error = Error,
            mapping = Mapping,
            parsed = Parsed,
            ls = new { x = L.X, y = L.Y },
            rs = _rxSlot >= 0 ? new { x = R.X, y = R.Y } : null,
            lt = LT,
            rt = RT,
            combinedTriggers = _combinedTriggers,
            hat = _hasHat ? Hat : (int?)null,
            buttons = pressed,
            buttonCount = ButtonCount,
            axes,
            trailL = L.Trail(),
            trailR = _rxSlot >= 0 ? R.Trail() : null,
            circL = L.Circularity(),
            circR = _rxSlot >= 0 ? R.Circularity() : null,
            drift = new
            {
                active = _driftActive,
                remaining = _driftActive ? Math.Max(0, (_driftEndQpc - now) / (double)Stopwatch.Frequency) : 0,
                seconds = _driftSeconds,
                result = DriftResult,
            },
        };
    }

    public void ResetCircularity() { L.ResetCirc(); R.ResetCirc(); }

    public void Dispose()
    {
        if (_pp != null) { NativeMemory.Free(_pp); _pp = null; }
    }
}

internal sealed class Stick
{
    public const int CircBins = 72;
    const int TrailLen = 96;

    public double X = double.NaN, Y = double.NaN;
    readonly float[] _tx = new float[TrailLen], _ty = new float[TrailLen];
    int _th, _tc;
    long _lastTrail;
    readonly double[] _maxR = new double[CircBins];

    public long DriftSamples;
    double _sx, _sy, _sxx, _syy, _maxDev;

    public void Add(double x, double y, long qpc, long trailTicks, bool drift)
    {
        X = x; Y = y;
        if (qpc - _lastTrail >= trailTicks)
        {
            _tx[_th] = (float)x; _ty[_th] = (float)y;
            _th = (_th + 1) % TrailLen;
            if (_tc < TrailLen) _tc++;
            _lastTrail = qpc;
        }
        double r = Math.Sqrt(x * x + y * y);
        if (r > 0.3)
        {
            int bin = (int)((Math.Atan2(y, x) + Math.PI) / (2 * Math.PI) * CircBins) % CircBins;
            if (r > _maxR[bin]) _maxR[bin] = r;
        }
        if (drift)
        {
            DriftSamples++;
            _sx += x; _sy += y; _sxx += x * x; _syy += y * y;
            if (r > _maxDev) _maxDev = r;
        }
    }

    public float[] Trail()
    {
        var r = new float[_tc * 2];
        int start = (_th - _tc + TrailLen) % TrailLen;
        for (int i = 0; i < _tc; i++)
        {
            int k = (start + i) % TrailLen;
            r[2 * i] = _tx[k]; r[2 * i + 1] = _ty[k];
        }
        return r;
    }

    public void ResetCirc() => Array.Clear(_maxR);

    public void ResetDrift() { DriftSamples = 0; _sx = _sy = _sxx = _syy = _maxDev = 0; }

    /// <summary>Outer-edge sweep, like the circularity test on hardwaretester.com: 0% = perfect circle at full reach.</summary>
    public object Circularity()
    {
        int filled = 0;
        double err = 0, reach = 0;
        foreach (var r in _maxR)
        {
            if (r < 0.5) continue;
            filled++;
            err += Math.Abs(r - 1);
            reach += r;
        }
        return new
        {
            bins = _maxR,
            coverage = filled / (double)CircBins,
            errorPct = filled > 0 ? err / filled * 100 : double.NaN,
            avgReach = filled > 0 ? reach / filled : double.NaN,
        };
    }

    public object DriftReport()
    {
        double n = DriftSamples;
        double mx, my, noise, maxDev;
        if (n == 0)
        {
            mx = X; my = Y; noise = 0;
            maxDev = Math.Sqrt(X * X + Y * Y);
        }
        else
        {
            mx = _sx / n; my = _sy / n;
            double vx = Math.Max(0, _sxx / n - mx * mx), vy = Math.Max(0, _syy / n - my * my);
            noise = Math.Sqrt(vx + vy);
            maxDev = _maxDev;
        }
        double offset = Math.Sqrt(mx * mx + my * my);
        string verdict = maxDev < 0.03 ? "Excellent" : maxDev < 0.06 ? "Good" : maxDev < 0.10 ? "Fair (fine with a small deadzone)" : "Drift detected";
        return new
        {
            samples = DriftSamples,
            offsetX = mx,
            offsetY = my,
            offset,
            noise,
            maxDev,
            suggestedDeadzonePct = Math.Ceiling(maxDev * 100 + 1),
            verdict,
        };
    }
}
