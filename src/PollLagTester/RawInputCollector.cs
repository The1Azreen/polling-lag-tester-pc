using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using static PollLagTester.Native;

namespace PollLagTester;

/// <summary>
/// Listens to Raw Input on a dedicated high-priority thread with a message-only window.
/// RIDEV_INPUTSINK means we keep receiving reports while a game has focus. No hooks, no injection,
/// no drivers: the game is not touched at all.
/// The thread only timestamps and copies; all maths happens on the analyzer thread.
/// </summary>
internal sealed unsafe class RawInputCollector
{
    const uint WM_APP_REGISTER = WM_APP + 1;
    const uint WM_APP_QUIT = WM_APP + 2;
    const int HotkeyId = 0x504C;
    const int BufferSize = 64 * 1024;

    static RawInputCollector? s_instance;

    public readonly EventRing Ring = new(1 << 17);
    public readonly HidReportRing Reports = new();
    public long Received;
    public bool HotkeyRegistered { get; private set; }
    public string? LastError { get; private set; }

    volatile int _mode;
    nint _hwnd;
    Thread? _thread;
    readonly ManualResetEventSlim _ready = new();
    RAWINPUTDEVICE[] _registered = [];

    public void Start(InputMode mode)
    {
        _mode = (int)mode;
        s_instance = this;
        _thread = new Thread(Run) { IsBackground = true, Priority = ThreadPriority.Highest, Name = "RawInput" };
        _thread.Start();
        _ready.Wait();
    }

    public void SetMode(InputMode mode)
    {
        _mode = (int)mode;
        PostMessageW(_hwnd, WM_APP_REGISTER, 0, 0);
    }

    public void Stop()
    {
        PostMessageW(_hwnd, WM_APP_QUIT, 0, 0);
        _thread?.Join(1000);
    }

    void Run()
    {
        nint inst = GetModuleHandleW(null);
        fixed (char* cls = "PollLagTesterSink")
        {
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)sizeof(WNDCLASSEXW),
                lpfnWndProc = &WndProc,
                hInstance = inst,
                lpszClassName = cls,
            };
            RegisterClassExW(&wc);
            _hwnd = CreateWindowExW(0, cls, cls, 0, 0, 0, 0, 0, HWND_MESSAGE, 0, inst, 0);
        }
        Register();
        HotkeyRegistered = RegisterHotKey(_hwnd, HotkeyId, MOD_CONTROL | MOD_SHIFT | MOD_NOREPEAT, VK_F9) != 0;
        _ready.Set();

        byte* buf = (byte*)NativeMemory.AlignedAlloc(BufferSize, 16);
        MSG msg;
        while (GetMessageW(&msg, 0, 0, 0) > 0)
        {
            // Timestamp first: everything after this line is analysis cost, not measurement error.
            long ts = Stopwatch.GetTimestamp();
            if (msg.message == WM_INPUT)
            {
                Read(msg.lParam, ts, buf);
            }
            else if (msg.message == WM_APP_REGISTER)
            {
                Register();
                continue;
            }
            else if (msg.message == WM_APP_QUIT)
            {
                Unregister();
                UnregisterHotKey(_hwnd, HotkeyId);
                DestroyWindow(_hwnd);
                break;
            }
            // WM_INPUT still needs DefWindowProc so the system can free the raw input handle.
            DispatchMessageW(&msg);
        }
        NativeMemory.AlignedFree(buf);
    }

    [UnmanagedCallersOnly]
    static nint WndProc(nint h, uint m, nint w, nint l)
    {
        var self = s_instance;
        if (self != null)
        {
            if (m == WM_INPUT_DEVICE_CHANGE)
                self.Ring.Write(new InputEvent { Qpc = Stopwatch.GetTimestamp(), Device = l, Kind = w == GIDC_ARRIVAL ? EventKind.DeviceAdded : EventKind.DeviceRemoved });
            else if (m == WM_HOTKEY && w == HotkeyId)
                self.Ring.Write(new InputEvent { Qpc = Stopwatch.GetTimestamp(), Kind = EventKind.Hotkey });
        }
        return DefWindowProcW(h, m, w, l);
    }

    void Unregister()
    {
        if (_registered.Length == 0) return;
        var rm = new RAWINPUTDEVICE[_registered.Length];
        for (int i = 0; i < rm.Length; i++)
            rm[i] = new RAWINPUTDEVICE { UsagePage = _registered[i].UsagePage, Usage = _registered[i].Usage, Flags = RIDEV_REMOVE, Target = 0 };
        fixed (RAWINPUTDEVICE* p = rm) RegisterRawInputDevices(p, (uint)rm.Length, (uint)sizeof(RAWINPUTDEVICE));
        _registered = [];
    }

    // Only the selected input class is registered, so e.g. an 8 kHz mouse costs nothing in keyboard mode.
    void Register()
    {
        Unregister();
        ushort[] usages = (InputMode)_mode switch
        {
            InputMode.Mouse => [0x02],
            InputMode.Keyboard => [0x06],
            _ => [0x04, 0x05, 0x08], // joystick, gamepad, multi-axis controller
        };
        var devs = new RAWINPUTDEVICE[usages.Length];
        for (int i = 0; i < devs.Length; i++)
            devs[i] = new RAWINPUTDEVICE { UsagePage = 0x01, Usage = usages[i], Flags = RIDEV_INPUTSINK | RIDEV_DEVNOTIFY, Target = _hwnd };
        fixed (RAWINPUTDEVICE* p = devs)
        {
            if (RegisterRawInputDevices(p, (uint)devs.Length, (uint)sizeof(RAWINPUTDEVICE)) == 0)
            {
                LastError = $"RegisterRawInputDevices failed (Win32 error {Marshal.GetLastWin32Error()})";
                return;
            }
        }
        LastError = null;
        _registered = devs;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    void Read(nint hRaw, long ts, byte* buf)
    {
        uint size = BufferSize;
        uint hdrSize = (uint)sizeof(RAWINPUTHEADER);
        uint got = GetRawInputData(hRaw, RID_INPUT, buf, &size, hdrSize);
        if (got == uint.MaxValue || got < hdrSize) return;
        Received++;
        var hdr = (RAWINPUTHEADER*)buf;
        byte* body = buf + hdrSize;
        switch (hdr->Type)
        {
            case RIM_TYPEMOUSE:
            {
                var m = (RAWMOUSE*)body;
                Ring.Write(new InputEvent { Qpc = ts, Device = hdr->Device, Kind = EventKind.Mouse, Code = m->ButtonFlags, Flags = m->Flags, A = m->LastX, B = m->LastY });
                break;
            }
            case RIM_TYPEKEYBOARD:
            {
                var k = (RAWKEYBOARD*)body;
                Ring.Write(new InputEvent { Qpc = ts, Device = hdr->Device, Kind = EventKind.Key, Code = k->VKey, Flags = k->Flags, A = k->MakeCode });
                break;
            }
            case RIM_TYPEHID:
            {
                var h = (RAWHID*)body;
                uint n = Math.Max(1u, h->Count);
                uint sz = h->SizeHid;
                byte* rep = body + sizeof(RAWHID);
                for (uint i = 0; i < n; i++)
                {
                    long seq = Reports.Write(rep + i * sz, (int)sz);
                    Ring.Write(new InputEvent { Qpc = ts, Device = hdr->Device, Kind = EventKind.Hid, A = (int)sz, Seq = seq });
                }
                break;
            }
        }
    }
}
