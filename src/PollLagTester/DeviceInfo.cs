using System.Text.RegularExpressions;
using static PollLagTester.Native;

namespace PollLagTester;

internal static unsafe class DeviceInfo
{
    public sealed record Info(string Name, string Manufacturer, string Path, string VidPid);

    static readonly Regex VidRx = new(@"VID[_&]([0-9A-F]{4,8})", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex PidRx = new(@"PID[_&]([0-9A-F]{4})", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Resolves a friendly product name (called once per device, never on the hot path).</summary>
    public static Info Describe(nint handle, InputMode mode)
    {
        string kind = mode switch { InputMode.Mouse => "Mouse", InputMode.Keyboard => "Keyboard", _ => "Controller" };
        if (handle == 0) return new Info($"Injected / virtual {kind.ToLowerInvariant()} input", "", "", "");

        string path = DevicePath(handle);
        string vidPid = "";
        var v = VidRx.Match(path);
        var p = PidRx.Match(path);
        if (v.Success && p.Success) vidPid = $"{v.Groups[1].Value[^4..].ToUpperInvariant()}:{p.Groups[1].Value.ToUpperInvariant()}";

        string product = "", manufacturer = "";
        if (path.Length > 0)
        {
            fixed (char* pp = path)
            {
                nint h = CreateFileW(pp, 0, 3 /* share read|write */, 0, 3 /* OPEN_EXISTING */, 0, 0);
                if (h != -1)
                {
                    char* s = stackalloc char[256];
                    new Span<char>(s, 256).Clear();
                    if (HidD_GetProductString(h, s, 510) != 0) product = new string(s).Trim();
                    new Span<char>(s, 256).Clear();
                    if (HidD_GetManufacturerString(h, s, 510) != 0) manufacturer = new string(s).Trim();
                    CloseHandle(h);
                }
            }
        }

        string name = product.Length > 0 ? product
            : path.Contains("RDP_", StringComparison.OrdinalIgnoreCase) ? $"Remote desktop {kind.ToLowerInvariant()}"
            : vidPid.Length > 0 ? $"{kind} {vidPid}"
            : kind;
        return new Info(name, manufacturer, path, vidPid);
    }

    public static string DevicePath(nint handle)
    {
        uint size = 0;
        GetRawInputDeviceInfoW(handle, RIDI_DEVICENAME, null, &size);
        if (size == 0 || size > 4096) return "";
        char* buf = stackalloc char[(int)size + 1];
        uint got = GetRawInputDeviceInfoW(handle, RIDI_DEVICENAME, buf, &size);
        if (got == uint.MaxValue || got == 0) return "";
        return new string(buf, 0, (int)Math.Min(got, size)).TrimEnd('\0');
    }

    public static string KeyName(int idx)
    {
        if (idx >= 512) return (idx - 512) switch { 0 => "Mouse L", 1 => "Mouse R", 2 => "Mouse M", 3 => "Mouse 4", _ => "Mouse 5" };
        int vk = idx & 0xFF;
        return vk switch
        {
            >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A => ((char)vk).ToString(),
            >= 0x60 and <= 0x69 => "Num" + (vk - 0x60),
            >= 0x70 and <= 0x87 => "F" + (vk - 0x6F),
            0x08 => "Backspace", 0x09 => "Tab", 0x0D => (idx & 0x100) != 0 ? "NumEnter" : "Enter",
            0x10 => "Shift", 0x11 => (idx & 0x100) != 0 ? "RCtrl" : "Ctrl", 0x12 => (idx & 0x100) != 0 ? "RAlt" : "Alt",
            0x14 => "CapsLock", 0x1B => "Esc", 0x20 => "Space",
            0x21 => "PgUp", 0x22 => "PgDn", 0x23 => "End", 0x24 => "Home",
            0x25 => "Left", 0x26 => "Up", 0x27 => "Right", 0x28 => "Down",
            0x2D => "Insert", 0x2E => "Delete", 0x5B => "Win",
            0xBA => ";", 0xBB => "=", 0xBC => ",", 0xBD => "-", 0xBE => ".", 0xBF => "/", 0xC0 => "`",
            0xDB => "[", 0xDC => "\\", 0xDD => "]", 0xDE => "'",
            _ => $"VK {vk:X2}",
        };
    }
}
