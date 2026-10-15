using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using BisonTime.Core;

namespace BisonTime.App;

internal static class Native
{
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);
    [DllImport("user32.dll")] static extern int GetWindowTextLength(IntPtr hWnd);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LASTINPUTINFO info);
    [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [StructLayout(LayoutKind.Sequential)]
    struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }

    public const uint MOD_ALT = 0x1, MOD_SHIFT = 0x4, MOD_NOREPEAT = 0x4000;
    public const int WM_HOTKEY = 0x0312;

    /// <summary>Milliseconds since the last keyboard or mouse input anywhere in the session.</summary>
    public static long IdleMs()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref info)) return 0;
        return unchecked((uint)Environment.TickCount - info.dwTime);
    }

    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hWnd, int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hWnd, StringBuilder name, int count);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);
    [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct MONITORINFO { public int cbSize; public RECT Monitor, Work; public uint Flags; }

    /// <summary>
    /// A video, a presentation or a game owns the whole screen: the foreground window covers its monitor
    /// and has no title bar. A merely maximised window keeps its title bar, so it does not count.
    /// </summary>
    public static bool FullscreenForeground()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return false;
        var cls = new StringBuilder(64);
        GetClassName(hwnd, cls, cls.Capacity);
        if (cls.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd") return false;   // the desktop itself
        const int WS_CAPTION = 0x00C00000;
        if ((GetWindowLong(hwnd, -16) & WS_CAPTION) == WS_CAPTION) return false;
        if (!GetWindowRect(hwnd, out var r)) return false;
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(MonitorFromWindow(hwnd, 2 /* nearest */), ref info)) return false;
        var m = info.Monitor;
        return r.Left <= m.Left && r.Top <= m.Top && r.Right >= m.Right && r.Bottom >= m.Bottom;
    }

    public static string WindowTitle(IntPtr hWnd)
    {
        var len = GetWindowTextLength(hWnd);
        if (len <= 0) return "";
        var sb = new StringBuilder(len + 1);
        GetWindowText(hWnd, sb, sb.Capacity);
        return sb.ToString();
    }

    public static uint ProcessId(IntPtr hWnd)
    {
        GetWindowThreadProcessId(hWnd, out var pid);
        return pid;
    }
}

/// <summary>Reads "what is in front of the user": the foreground window's program and title.</summary>
internal sealed class WindowWatcher
{
    // Shell surfaces that are not work in themselves: start menu, task switcher, lock screen.
    static readonly HashSet<string> Shell = new(StringComparer.OrdinalIgnoreCase)
    {
        "lockapp", "searchhost", "searchapp", "startmenuexperiencehost", "shellexperiencehost", "textinputhost",
    };

    readonly Dictionary<uint, string> _names = new();
    readonly uint _self = (uint)Environment.ProcessId;
    readonly UrlReader _urls = new();

    // Chromium browsers expose the address bar; Firefox does it differently and is left to the title.
    static readonly HashSet<string> UrlBrowsers = new(StringComparer.OrdinalIgnoreCase)
        { "chrome", "msedge", "brave", "opera", "vivaldi", "chromium", "arc" };

    public WindowInfo? Sample(bool readUrl = false)
    {
        var hwnd = Native.GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return null;
        var pid = Native.ProcessId(hwnd);
        if (pid == 0 || pid == _self) return null;
        var proc = Name(pid);
        if (Shell.Contains(proc)) return null;
        var title = Native.WindowTitle(hwnd);
        if (proc == "explorer" && (title == "" || title == "Program Manager")) return null; // desktop / taskbar
        var url = readUrl && UrlBrowsers.Contains(proc) ? _urls.Get(hwnd, title) : null;
        return new WindowInfo(proc, title, url);
    }

    public void ResetCache() => _names.Clear();

    string Name(uint pid)
    {
        if (_names.TryGetValue(pid, out var n)) return n;
        try { n = Process.GetProcessById((int)pid).ProcessName.ToLowerInvariant(); }
        catch { n = "app"; }
        if (_names.Count > 500) _names.Clear();
        _names[pid] = n;
        return n;
    }
}
