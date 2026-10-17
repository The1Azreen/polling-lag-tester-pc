using System.Runtime.InteropServices;

namespace PollLagTester;

internal enum InputMode { Mouse, Keyboard, Controller }

internal enum EventKind : byte { Mouse = 1, Key = 2, Hid = 3, DeviceAdded = 10, DeviceRemoved = 11, Hotkey = 20 }

/// <summary>One raw input report, timestamped (QPC) the moment the collector thread received it.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct InputEvent
{
    public long Qpc;
    public nint Device;
    public long Seq;      // HID: sequence number in HidReportRing
    public int A;         // mouse dx | key make code | HID report size
    public int B;         // mouse dy
    public ushort Code;   // mouse button flags | key VKey
    public ushort Flags;  // mouse flags | key flags
    public EventKind Kind;
}

/// <summary>
/// Single-producer / single-consumer ring. The collector thread only ever writes, the analyzer
/// thread only ever reads, so no locks are needed on the hot path.
/// </summary>
internal sealed class EventRing(int capacityPow2)
{
    readonly InputEvent[] _buf = new InputEvent[capacityPow2];
    readonly int _mask = capacityPow2 - 1;
    long _head;
    long _tail;
    public long Dropped;

    public void Write(in InputEvent e)
    {
        long h = _head;
        if (h - Volatile.Read(ref _tail) >= _buf.Length) { Dropped++; return; }
        _buf[h & _mask] = e;
        Volatile.Write(ref _head, h + 1);
    }

    public int Drain(Span<InputEvent> dst)
    {
        long t = _tail;
        int n = (int)Math.Min(Volatile.Read(ref _head) - t, dst.Length);
        for (int i = 0; i < n; i++) dst[i] = _buf[(t + i) & _mask];
        Volatile.Write(ref _tail, t + n);
        return n;
    }

    public int Count => (int)(Volatile.Read(ref _head) - Volatile.Read(ref _tail));
}

/// <summary>
/// Overwriting ring of raw HID report bytes (controller mode only), so the analyzer can decode
/// stick/button state without the collector doing any parsing work.
/// </summary>
internal sealed unsafe class HidReportRing
{
    public const int SlotSize = 256;
    const int Capacity = 4096;
    readonly byte[] _data = GC.AllocateArray<byte>(SlotSize * Capacity, pinned: true);
    readonly int[] _len = new int[Capacity];
    readonly byte* _base;
    long _head;

    public HidReportRing() { _base = (byte*)Marshal.UnsafeAddrOfPinnedArrayElement(_data, 0); }

    public long Write(byte* src, int len)
    {
        long seq = _head;
        int slot = (int)(seq & (Capacity - 1));
        len = Math.Min(len, SlotSize);
        Buffer.MemoryCopy(src, _base + slot * SlotSize, SlotSize, len);
        _len[slot] = len;
        Volatile.Write(ref _head, seq + 1);
        return seq;
    }

    /// <summary>Copies a report out; false if it has already been overwritten.</summary>
    public bool TryRead(long seq, byte* dst, out int len)
    {
        len = 0;
        if (Volatile.Read(ref _head) - seq > Capacity - 16) return false;
        int slot = (int)(seq & (Capacity - 1));
        len = _len[slot];
        Buffer.MemoryCopy(_base + slot * SlotSize, dst, SlotSize, len);
        return Volatile.Read(ref _head) - seq <= Capacity - 16;
    }
}
