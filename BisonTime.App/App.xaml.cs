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
    WinForms.ToolStripMenuItem _pauseItem = null!, _finishItem = null!;
    System.Drawing.Icon _iconNormal = null!, _iconPaused = null!, _iconDone = null!;
    MainWindow? _main;
    AppWindow? _app;
    long _lastStats;
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
        SaveStats(all: true);   // before raw segments older than 7 days are dropped
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

        if (firstRun) ShowApp(AppPage.Today);
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
        if (now - _lastStats > 10 * 60_000L)
        {
            _lastStats = now;
            SaveStats(all: false);
        }
        if (now - _lastCleanup > 6 * 3600_000L)
        {
            _lastCleanup = now;
            SaveStats(all: true);
            Tracker.Cleanup();
            _watcher.ResetCache();
        }
        if (now - _lastUpdateCheck > 3600_000L)
        {
            _lastUpdateCheck = now;
            _ = DailyUpdateCheck();
        }
    }

    /// <summary>Dashboard numbers for today (and yesterday, for the day switch) or every day that still has segments.</summary>
    void SaveStats(bool all)
    {
        try
        {
            var settings = Store.LoadSettings();
            var today = TimeUtil.DayKey(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            var days = all
                ? Store.Days().Where(d => Store.LoadDay(d).Segments.Count > 0).Append(today).Distinct()
                : new[] { TimeUtil.ShiftDay(today, -1), today };
            foreach (var d in days)
            {
                var f = Store.LoadDay(d);
                if (f.Segments.Count == 0 && d != today) continue;
                Stats.Save(Store, d, Report.Build(Tracker.DaySegments(d), f.State, settings));
            }
        }
        catch (Exception ex)
        {
            Store.Log(new LogEntry { T = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Ev = "stats", Why = ex.Message });
        }
    }

    // ---- Updates ----

    long _lastUpdateCheck = Environment.TickCount64 - 3600_000L + 60_000L; // first check a minute after start
    bool _balloonShown;

    /// <summary>Newer release found by the last check (daily or manual); null when up to date or unknown.</summary>
    internal Updater.Available? Update { get; private set; }
    internal event Action? UpdateChanged;

    internal void SetUpdate(Updater.Available? a)
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
        _iconDone = LoadIcon("bt-done.ico");
        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("Вікно дня", null, (_, _) => ShowMain());
        menu.Items.Add("Відкрити Bison Time Checker", null, (_, _) => ShowApp(AppPage.Today));
        _pauseItem = new WinForms.ToolStripMenuItem("Пауза", null, (_, _) => TogglePause()) { ShortcutKeyDisplayString = "Alt+Shift+P" };
        menu.Items.Add(_pauseItem);
        _finishItem = new WinForms.ToolStripMenuItem("Завершити роботу", null, (_, _) => { if (Tracker.Done) Resume(); else { FinishDay(); ShowMain(); } });
        menu.Items.Add(_finishItem);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("Дашборд", null, (_, _) => ShowApp(AppPage.Dashboard));
        menu.Items.Add("Налаштування", null, (_, _) => ShowApp(AppPage.Projects));
        menu.Items.Add("Довідка", null, (_, _) => ShowApp(AppPage.Help));
        menu.Items.Add("Про BT і оновлення", null, (_, _) => ShowApp(AppPage.About));
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("Вийти з Bison Time Checker", null, (_, _) => Shutdown());
        _tray = new WinForms.NotifyIcon { Icon = _iconNormal, Visible = true, Text = "Bison Time Checker", ContextMenuStrip = menu };
        _tray.MouseClick += (_, ev) => { if (ev.Button == WinForms.MouseButtons.Left) ToggleMain(); };
        _tray.BalloonTipClicked += (_, _) => ShowApp(AppPage.About);
    }

    void UpdateTray()
    {
        string text;
        if (Tracker.DoneUntil is { } until)
            text = $"BT · день завершено, облік з {TimeUtil.Clock(until)}";
        else if (Tracker.PausedSince is { } p)
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
        var state = (Tracker.Done ? "d" : Tracker.Paused ? "p" : "r") + text;
        if (state == _trayState) return;
        _trayState = state;
        _tray.Icon = Tracker.Done ? _iconDone : Tracker.Paused ? _iconPaused : _iconNormal;
        _tray.Text = text;
        _pauseItem.Text = Tracker.Paused ? "Продовжити облік" : "Пауза";
        _pauseItem.Enabled = !Tracker.Done;
        _finishItem.Text = Tracker.Done ? "Продовжити облік" : "Завершити роботу";
    }

    public void TogglePause()
    {
        if (Tracker.Done) return;  // a finished day is resumed explicitly, not by the pause hotkey
        if (Tracker.Paused) Tracker.Resume(); else Tracker.Pause();
        Sample();
        _main?.Refresh();
    }

    /// <summary>"Завершити роботу": no counting until 05:00, the day's numbers saved for the dashboard.</summary>
    public void FinishDay()
    {
        Tracker.FinishDay();
        SaveStats(all: false);
        Sample();
        _main?.Refresh();
    }

    public void Resume()
    {
        Tracker.Resume();
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

    /// <summary>The main window on a given page.</summary>
    public void ShowApp(AppPage page)
    {
        if (_app == null)
        {
            _app = new AppWindow();
            _app.Closed += (_, _) => _app = null;
        }
        _app.Select(page);
        _app.Show();
        if (_app.WindowState == WindowState.Minimized) _app.WindowState = WindowState.Normal;
        _app.Activate();
    }

    public static void OpenFolder(string dir) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });

    protected override void OnExit(ExitEventArgs e)
    {
        try { Tracker?.Shutdown(); } catch { /* best effort */ }
        if (Tracker != null) SaveStats(all: false);
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
