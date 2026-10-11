using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using BisonTime.Core;
using Microsoft.Win32;
using WinForms = System.Windows.Forms;

namespace BisonTime.App;

public partial class App : Application
{
    public static FileStore Store { get; private set; } = null!;
    public static Tracker Tracker { get; private set; } = null!;
    public static App Instance => (App)Current;

    WinForms.NotifyIcon _tray = null!;
    WinForms.ToolStripMenuItem _pauseItem = null!;
    System.Drawing.Icon _iconNormal = null!, _iconPaused = null!;
    MainWindow? _main;
    SettingsWindow? _settings;
    readonly WindowWatcher _watcher = new();
    DispatcherTimer _timer = null!;
    HwndSource? _hotkeyWindow;
    bool _locked;
    long _lastCleanup;
    string _trayState = "";
    static Mutex? _single;

    const int HotkeyId = 0xB7;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _single = new Mutex(false, @"Local\BisonTimeChecker.SingleInstance");
        // After an update the old copy is still shutting down: wait for it instead of quitting.
        var wait = e.Args.Contains("--after-update") ? TimeSpan.FromSeconds(15) : TimeSpan.Zero;
        bool owned;
        try { owned = _single.WaitOne(wait); }
        catch (AbandonedMutexException) { owned = true; }
        if (!owned) { _single = null; Shutdown(); return; }
        Updates.Cleanup(Updater.AppDir, Updater.DownloadDir);

        DispatcherUnhandledException += (_, ex) =>
        {
            // One broken render must not stop time tracking.
            Store?.Log(new LogEntry { T = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Ev = "error", Why = ex.Exception.Message });
            ex.Handled = true;
        };

        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BisonTime");
        var firstRun = !File.Exists(Path.Combine(dir, "settings.json"));
        Store = new FileStore(dir);
        if (firstRun) Store.SaveSettings(Store.LoadSettings());
        Tracker = new Tracker(Store);
        Tracker.Cleanup();
        Autostart.Apply(Store.LoadSettings().Autostart);

        SystemEvents.SessionSwitch += (_, ev) =>
        {
            if (ev.Reason is SessionSwitchReason.SessionLock or SessionSwitchReason.ConsoleDisconnect or SessionSwitchReason.RemoteDisconnect) _locked = true;
            if (ev.Reason is SessionSwitchReason.SessionUnlock or SessionSwitchReason.ConsoleConnect or SessionSwitchReason.RemoteConnect) _locked = false;
            Sample();
        };
        SystemEvents.SessionEnding += (_, _) => Tracker.Shutdown();

        SetupTray();
        SetupHotkey();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Sample();
        _timer.Start();
        Sample();

        if (firstRun) ShowMain();
    }

    void Sample()
    {
        var s = Store.LoadSettings();
        var idle = Native.IdleMs() >= Math.Max(1, s.IdleMin) * 60_000L;
        WindowInfo? w = null;
        try { w = _watcher.Sample(); } catch { /* a window vanished mid-read */ }
        Tracker.Tick(w, idle, _locked);
        UpdateTray();

        var now = Environment.TickCount64;
        if (now - _lastCleanup > 6 * 3600_000L)
        {
            _lastCleanup = now;
            Tracker.Cleanup();
            _watcher.ResetCache();
        }
        if (now - _lastUpdateCheck > 3600_000L)
        {
            _lastUpdateCheck = now;
            _ = DailyUpdateCheck();
        }
    }

    // ---- Updates ----

    long _lastUpdateCheck = Environment.TickCount64 - 3600_000L + 60_000L; // first check a minute after start
    bool _balloonShown;

    /// <summary>Newer release found by the last check (daily or manual); null when up to date or unknown.</summary>
    public Updater.Available? Update { get; private set; }
    public event Action? UpdateChanged;

    public void SetUpdate(Updater.Available? a)
    {
        Update = a;
        UpdateChanged?.Invoke();
    }

    async Task DailyUpdateCheck()
    {
        var s = Store.LoadSettings();
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (!s.AutoUpdateCheck || nowMs - s.LastUpdateCheck < 24 * 3600_000L) return;
        try
        {
            var a = await Updater.CheckAsync();
            s = Store.LoadSettings();
            s.LastUpdateCheck = nowMs;
            Store.SaveSettings(s);
            SetUpdate(a);
            if (a != null && !_balloonShown)
            {
                _balloonShown = true;
                _tray.ShowBalloonTip(8000, "Bison Time Checker", $"Доступна версія {a.Manifest.Version}. Оновити: Налаштування → Довідка.", WinForms.ToolTipIcon.Info);
            }
        }
        catch (Exception ex)
        {
            // Offline or GitHub hiccup: try again next hour, quietly.
            Store.Log(new LogEntry { T = nowMs, Ev = "update-check", Why = ex.Message });
        }
    }

    // ---- Tray ----

    static System.Drawing.Icon LoadIcon(string name)
    {
        using var s = typeof(App).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"missing icon {name}");
        return new System.Drawing.Icon(s, WinForms.SystemInformation.SmallIconSize);
    }

    void SetupTray()
    {
        _iconNormal = LoadIcon("bt.ico");
        _iconPaused = LoadIcon("bt-paused.ico");
        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("Відкрити", null, (_, _) => ShowMain());
        _pauseItem = new WinForms.ToolStripMenuItem("Пауза", null, (_, _) => TogglePause()) { ShortcutKeyDisplayString = "Alt+Shift+P" };
        menu.Items.Add(_pauseItem);
        menu.Items.Add("Налаштування", null, (_, _) => ShowSettings());
        menu.Items.Add("Довідка", null, (_, _) => ShowSettings(5));
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("Вийти з Bison Time Checker", null, (_, _) => Shutdown());
        _tray = new WinForms.NotifyIcon { Icon = _iconNormal, Visible = true, Text = "Bison Time Checker", ContextMenuStrip = menu };
        _tray.MouseClick += (_, ev) => { if (ev.Button == WinForms.MouseButtons.Left) ToggleMain(); };
        _tray.BalloonTipClicked += (_, _) => ShowSettings(5);
    }

    void UpdateTray()
    {
        string text;
        if (Tracker.PausedSince is { } p)
        {
            var min = (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - p) / 60_000;
            text = $"BT на паузі {min} хв · Alt+Shift+P";
        }
        else
        {
            var project = Tracker.Open is { } o ? Classifier.Classify(new WindowInfo(o.Proc, o.Title), Store.LoadSettings()).Project : null;
            text = project != null ? $"BT · {project}" : "BT · Інше";
        }
        if (text.Length > 63) text = text[..63];
        var state = (Tracker.Paused ? "p" : "r") + text;
        if (state == _trayState) return;
        _trayState = state;
        _tray.Icon = Tracker.Paused ? _iconPaused : _iconNormal;
        _tray.Text = text;
        _pauseItem.Text = Tracker.Paused ? "Продовжити облік" : "Пауза";
    }

    public void TogglePause()
    {
        if (Tracker.Paused) Tracker.Resume(); else Tracker.Pause();
        Sample();
        _main?.Refresh();
    }

    // ---- Global hotkey Alt+Shift+P (RegisterHotKey, no keyboard hook) ----

    void SetupHotkey()
    {
        var p = new HwndSourceParameters("BisonTimeHotkey") { ParentWindow = new IntPtr(-3) }; // HWND_MESSAGE
        _hotkeyWindow = new HwndSource(p);
        _hotkeyWindow.AddHook((IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
        {
            if (msg == Native.WM_HOTKEY && wParam.ToInt32() == HotkeyId) { TogglePause(); handled = true; }
            return IntPtr.Zero;
        });
        if (!Native.RegisterHotKey(_hotkeyWindow.Handle, HotkeyId, Native.MOD_ALT | Native.MOD_SHIFT | Native.MOD_NOREPEAT, 0x50 /* P */))
            Store.Log(new LogEntry { T = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Ev = "hotkey-busy", Why = "Alt+Shift+P" });
    }

    // ---- Windows ----

    public void ShowMain()
    {
        _main ??= new MainWindow();
        _main.ShowNearTray();
    }

    void ToggleMain()
    {
        if (_main is { IsVisible: true } && (DateTime.UtcNow - _main.HiddenAt).TotalMilliseconds > 300) { _main.Hide(); return; }
        // A click on the tray icon first deactivates (hides) the flyout: don't reopen it at once.
        if (_main != null && (DateTime.UtcNow - _main.HiddenAt).TotalMilliseconds < 300) return;
        ShowMain();
    }

    public void ShowSettings(int tab = 0)
    {
        if (_settings == null)
        {
            _settings = new SettingsWindow();
            _settings.Closed += (_, _) => _settings = null;
        }
        _settings.Select(tab);
        _settings.Show();
        if (_settings.WindowState == WindowState.Minimized) _settings.WindowState = WindowState.Normal;
        _settings.Activate();
    }

    public static void OpenFolder(string dir) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });

    protected override void OnExit(ExitEventArgs e)
    {
        try { Tracker?.Shutdown(); } catch { /* best effort */ }
        if (_hotkeyWindow != null) { Native.UnregisterHotKey(_hotkeyWindow.Handle, HotkeyId); _hotkeyWindow.Dispose(); }
        if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
        // Release before the process ends so an updated copy waiting on it starts at once.
        try { _single?.ReleaseMutex(); } catch { /* not owned */ }
        base.OnExit(e);
    }
}

/// <summary>Start with Windows: HKCU\...\Run, per user, no admin rights.</summary>
internal static class Autostart
{
    const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string Name = "BisonTimeChecker";

    public static void Apply(bool on)
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(Key, writable: true) ?? Registry.CurrentUser.CreateSubKey(Key);
            var exe = Environment.ProcessPath;
            if (on && exe != null) k.SetValue(Name, $"\"{exe}\"");
            else if (!on) k.DeleteValue(Name, throwOnMissingValue: false);
        }
        catch { /* policy may forbid it: not fatal */ }
    }
}
