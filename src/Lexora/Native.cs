using System.Runtime.InteropServices;

namespace Lexora;

/// <summary>Win32 interop used by the overlay, hover tracking, text injection and the engine job object.</summary>
static class Native
{
    public const int WS_EX_TOPMOST = 0x8;
    public const int WS_EX_TRANSPARENT = 0x20;
    public const int WS_EX_TOOLWINDOW = 0x80;
    public const int WS_EX_LAYERED = 0x80000;
    public const int WS_EX_NOACTIVATE = 0x08000000;
    public const int CS_DROPSHADOW = 0x20000;
    public const int WM_MOUSEACTIVATE = 0x21;
    public const int MA_NOACTIVATE = 3;
    public const int WM_HOTKEY = 0x312;

    public const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_NOREPEAT = 0x4000;
    public const ushort VK_CONTROL = 0x11, VK_SHIFT = 0x10, VK_MENU = 0x12, VK_DELETE = 0x2E;

    static readonly IntPtr HWND_TOPMOST = new(-1);
    const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10;

    [DllImport("user32.dll")] public static extern bool GetCursorPos(out Point pt);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int processId);
    [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vKey);
    [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(Point pt, uint flags);
    [DllImport("shcore.dll")] static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public static void BringToTopmost(IntPtr hwnd) =>
        SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

    public static int ForegroundProcessId()
    {
        GetWindowThreadProcessId(GetForegroundWindow(), out int pid);
        return pid;
    }

    public static bool IsKeyDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    /// <summary>Scale factor (1.0 = 96 DPI) of the monitor containing a physical-pixel point.</summary>
    public static float ScaleAt(Point pt)
    {
        var mon = MonitorFromPoint(pt, 2 /* MONITOR_DEFAULTTONEAREST */);
        return GetDpiForMonitor(mon, 0, out uint dpi, out _) == 0 ? dpi / 96f : 1f;
    }

    public static void RoundCorners(IntPtr hwnd)
    {
        int round = 2; // DWMWCP_ROUND (Windows 11; ignored elsewhere)
        DwmSetWindowAttribute(hwnd, 33, ref round, sizeof(int));
    }

    // ---- Keyboard injection ----

    [StructLayout(LayoutKind.Sequential)]
    struct INPUT { public uint type; public InputUnion u; }

    [StructLayout(LayoutKind.Explicit)]
    struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }

    [StructLayout(LayoutKind.Sequential)]
    struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr extra; }

    [StructLayout(LayoutKind.Sequential)]
    struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr extra; }

    const uint INPUT_KEYBOARD = 1, KEYEVENTF_KEYUP = 0x2, KEYEVENTF_UNICODE = 0x4;

    [DllImport("user32.dll", SetLastError = true)]
    static extern uint SendInput(uint count, INPUT[] inputs, int size);

    static INPUT Key(ushort vk, ushort scan, uint flags) =>
        new() { type = INPUT_KEYBOARD, u = new InputUnion { ki = new KEYBDINPUT { wVk = vk, wScan = scan, dwFlags = flags } } };

    /// <summary>Types text into the foreground window as real keystrokes (works in browsers/React inputs).</summary>
    public static void TypeText(string text)
    {
        var inputs = new List<INPUT>();
        foreach (char c in text.Replace("\r", "").Replace("\n", " "))
        {
            inputs.Add(Key(0, c, KEYEVENTF_UNICODE));
            inputs.Add(Key(0, c, KEYEVENTF_UNICODE | KEYEVENTF_KEYUP));
        }
        if (inputs.Count > 0) SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<INPUT>());
    }

    public static void PressKey(ushort vk) =>
        SendInput(2, [Key(vk, 0, 0), Key(vk, 0, KEYEVENTF_KEYUP)], Marshal.SizeOf<INPUT>());

    public static void PressCtrlPlus(char key)
    {
        ushort vk = key;
        SendInput(4, [Key(VK_CONTROL, 0, 0), Key(vk, 0, 0), Key(vk, 0, KEYEVENTF_KEYUP), Key(VK_CONTROL, 0, KEYEVENTF_KEYUP)],
            Marshal.SizeOf<INPUT>());
    }

    // ---- Job object: kills the Java engine automatically if this app exits or crashes ----

    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct IO_COUNTERS { public ulong a, b, c, d, e, f; }

    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION Basic;
        public IO_COUNTERS Io;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll")] static extern IntPtr CreateJobObject(IntPtr attrs, string? name);
    [DllImport("kernel32.dll")] static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, int size);
    [DllImport("kernel32.dll")] static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    static IntPtr _killOnCloseJob;

    public static void TieLifetimeToThisProcess(System.Diagnostics.Process child)
    {
        if (_killOnCloseJob == IntPtr.Zero)
        {
            _killOnCloseJob = CreateJobObject(IntPtr.Zero, null);
            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            info.Basic.LimitFlags = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
            SetInformationJobObject(_killOnCloseJob, 9, ref info, Marshal.SizeOf(info));
        }
        AssignProcessToJobObject(_killOnCloseJob, child.Handle);
    }
}
