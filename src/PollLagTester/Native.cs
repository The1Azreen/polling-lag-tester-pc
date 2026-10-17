using System.Runtime.InteropServices;

namespace PollLagTester;

/// <summary>Win32 / HID interop. Everything here is blittable so no marshalling cost on the hot path.</summary>
internal static unsafe class Native
{
    public const uint WM_INPUT = 0x00FF;
    public const uint WM_INPUT_DEVICE_CHANGE = 0x00FE;
    public const uint WM_HOTKEY = 0x0312;
    public const uint WM_APP = 0x8000;

    public const uint RIDEV_REMOVE = 0x00000001;
    public const uint RIDEV_INPUTSINK = 0x00000100;
    public const uint RIDEV_DEVNOTIFY = 0x00002000;
    public const uint RID_INPUT = 0x10000003;
    public const uint RIM_TYPEMOUSE = 0, RIM_TYPEKEYBOARD = 1, RIM_TYPEHID = 2;
    public const uint RIDI_PREPARSEDDATA = 0x20000005;
    public const uint RIDI_DEVICENAME = 0x20000007;
    public const nint GIDC_ARRIVAL = 1;

    public const uint MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_NOREPEAT = 0x4000;
    public const uint VK_F9 = 0x78;

    public const int HIDP_STATUS_SUCCESS = 0x00110000;
    public const int HidP_Input = 0;

    public static readonly nint HWND_MESSAGE = -3;

    [StructLayout(LayoutKind.Sequential)]
    public struct RAWINPUTDEVICE { public ushort UsagePage; public ushort Usage; public uint Flags; public nint Target; }

    [StructLayout(LayoutKind.Sequential)]
    public struct RAWINPUTHEADER { public uint Type; public uint Size; public nint Device; public nint WParam; }

    [StructLayout(LayoutKind.Sequential)]
    public struct RAWMOUSE
    {
        public ushort Flags; ushort _pad;
        public ushort ButtonFlags; public ushort ButtonData;
        public uint RawButtons; public int LastX; public int LastY; public uint Extra;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RAWKEYBOARD { public ushort MakeCode; public ushort Flags; public ushort Reserved; public ushort VKey; public uint Message; public uint Extra; }

    [StructLayout(LayoutKind.Sequential)]
    public struct RAWHID { public uint SizeHid; public uint Count; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG { public nint hwnd; public uint message; public nint wParam; public nint lParam; public uint time; public int ptX, ptY; public uint lPrivate; }

    [StructLayout(LayoutKind.Sequential)]
    public struct WNDCLASSEXW
    {
        public uint cbSize; public uint style;
        public delegate* unmanaged<nint, uint, nint, nint, nint> lpfnWndProc;
        public int cbClsExtra; public int cbWndExtra;
        public nint hInstance, hIcon, hCursor, hbrBackground;
        public char* lpszMenuName; public char* lpszClassName; public nint hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct HIDP_CAPS
    {
        public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
        public fixed ushort Reserved[17];
        public ushort NumberLinkCollectionNodes, NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices,
            NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices,
            NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
    }

    [StructLayout(LayoutKind.Explicit, Size = 72)]
    public struct HIDP_VALUE_CAPS
    {
        [FieldOffset(0)] public ushort UsagePage;
        [FieldOffset(2)] public byte ReportID;
        [FieldOffset(6)] public ushort LinkCollection;
        [FieldOffset(12)] public byte IsRange;
        [FieldOffset(16)] public byte HasNull;
        [FieldOffset(18)] public ushort BitSize;
        [FieldOffset(20)] public ushort ReportCount;
        [FieldOffset(40)] public int LogicalMin;
        [FieldOffset(44)] public int LogicalMax;
        [FieldOffset(56)] public ushort UsageMin; // NotRange.Usage shares this offset
        [FieldOffset(58)] public ushort UsageMax;
    }

    [DllImport("user32.dll", SetLastError = true)] public static extern ushort RegisterClassExW(WNDCLASSEXW* wc);
    [DllImport("user32.dll", SetLastError = true)] public static extern nint CreateWindowExW(uint exStyle, char* cls, char* name, uint style, int x, int y, int w, int h, nint parent, nint menu, nint inst, nint param);
    [DllImport("user32.dll")] public static extern nint DefWindowProcW(nint h, uint m, nint w, nint l);
    [DllImport("user32.dll")] public static extern int GetMessageW(MSG* msg, nint h, uint min, uint max);
    [DllImport("user32.dll")] public static extern nint DispatchMessageW(MSG* msg);
    [DllImport("user32.dll")] public static extern int PostMessageW(nint h, uint m, nint w, nint l);
    [DllImport("user32.dll")] public static extern int DestroyWindow(nint h);
    [DllImport("user32.dll", SetLastError = true)] public static extern int RegisterRawInputDevices(RAWINPUTDEVICE* devs, uint n, uint size);
    [DllImport("user32.dll")] public static extern uint GetRawInputData(nint hRawInput, uint cmd, void* data, uint* size, uint headerSize);
    [DllImport("user32.dll")] public static extern uint GetRawInputDeviceInfoW(nint dev, uint cmd, void* data, uint* size);
    [DllImport("user32.dll")] public static extern int RegisterHotKey(nint h, int id, uint mods, uint vk);
    [DllImport("user32.dll")] public static extern int UnregisterHotKey(nint h, int id);
    [DllImport("user32.dll")] public static extern int MessageBeep(uint type);

    [DllImport("kernel32.dll")] public static extern nint GetModuleHandleW(char* name);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern nint CreateFileW(char* name, uint access, uint share, nint sa, uint disp, uint flags, nint template);
    [DllImport("kernel32.dll")] public static extern int CloseHandle(nint h);
    [DllImport("kernel32.dll")] public static extern nint GetCurrentProcess();
    [DllImport("kernel32.dll")] public static extern int SetProcessInformation(nint proc, int infoClass, void* info, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern nint CreateJobObjectW(nint sa, char* name);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern int SetInformationJobObject(nint job, int cls, void* info, uint len);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern int AssignProcessToJobObject(nint job, nint proc);

    [DllImport("hid.dll")] public static extern byte HidD_GetProductString(nint h, void* buf, uint len);
    [DllImport("hid.dll")] public static extern byte HidD_GetManufacturerString(nint h, void* buf, uint len);
    [DllImport("hid.dll")] public static extern int HidP_GetCaps(nint preparsed, HIDP_CAPS* caps);
    [DllImport("hid.dll")] public static extern int HidP_GetValueCaps(int reportType, HIDP_VALUE_CAPS* caps, ushort* len, nint preparsed);
    [DllImport("hid.dll")] public static extern int HidP_GetUsageValue(int reportType, ushort page, ushort link, ushort usage, uint* value, nint preparsed, byte* report, uint reportLen);
    [DllImport("hid.dll")] public static extern int HidP_GetUsages(int reportType, ushort page, ushort link, ushort* list, uint* len, nint preparsed, byte* report, uint reportLen);
    [DllImport("hid.dll")] public static extern uint HidP_MaxUsageListLength(int reportType, ushort page, nint preparsed);

    [StructLayout(LayoutKind.Sequential)]
    struct PROCESS_POWER_THROTTLING_STATE { public uint Version, ControlMask, StateMask; }

    /// <summary>
    /// Windows 11 puts background processes on EcoQoS, which delays wake-ups and would skew timestamps
    /// while a game has focus. Opt this process out.
    /// </summary>
    public static void OptOutOfPowerThrottling()
    {
        var s = new PROCESS_POWER_THROTTLING_STATE { Version = 1, ControlMask = 0x1 | 0x4, StateMask = 0 };
        SetProcessInformation(GetCurrentProcess(), 4 /* ProcessPowerThrottling */, &s, (uint)sizeof(PROCESS_POWER_THROTTLING_STATE));
    }

    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass, SchedulingClass;
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
        public nuint ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    /// <summary>Job object that kills child processes (PresentMon) if this process dies.</summary>
    public static nint CreateKillOnCloseJob()
    {
        nint job = CreateJobObjectW(0, null);
        if (job == 0) return 0;
        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION { LimitFlags = 0x2000 /* KILL_ON_JOB_CLOSE */ };
        SetInformationJobObject(job, 9 /* ExtendedLimitInformation */, &info, (uint)sizeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION));
        return job;
    }
}
