using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using BisonTime.Core;
using static BisonTime.App.Ui;

namespace BisonTime.App;

/// <summary>
/// The top-edge widget. Collapsed it is a thin strip at the top of the screen: its colour is the
/// tracking state, its fill is the day so far out of 8 hours. Under the pointer it slides down into
/// a small card: current project, the day's total, what is still to enter, pause / finish / open.
/// It never takes focus (WS_EX_NOACTIVATE): a click on it must not look like a switch away from work.
/// </summary>
internal sealed class Widget : Window
{
    const double CardWidth = 320;
    const double StripHeight = 8;   // what stays visible collapsed: a 4 px bar with the card's edge around it
    const long DayGoalMin = 8 * 60;

    readonly Border _card;
    readonly StackPanel _body = new() { Margin = new Thickness(16, 14, 16, 12) };
    readonly Border _track, _fill;
    readonly TranslateTransform _slide = new();
    readonly DispatcherTimer _enter, _leave;

    bool _expanded, _dragging;
    double _grabOffset;
    long _lastBuild;
    DayReport? _report;
    string? _current;
    string _state = "";

    public Widget()
    {
        Title = "Bison Time Checker · віджет";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;   // fully transparent pixels let clicks through to the window below
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Width = CardWidth;
        SizeToContent = SizeToContent.Height;
        FontFamily = TextFont;
        UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);

        _fill = new Border { Background = B("MintBrush"), HorizontalAlignment = HorizontalAlignment.Left, CornerRadius = new CornerRadius(2) };
        _track = new Border
        {
            Height = 4, Margin = new Thickness(16, 0, 16, 2), Background = B("TonalBrush"),
            CornerRadius = new CornerRadius(2), Child = _fill, ClipToBounds = true,
        };
        var stack = new DockPanel();
        DockPanel.SetDock(_track, Dock.Bottom);
        stack.Children.Add(_track);
        stack.Children.Add(_body);
        _card = new Border
        {
            Background = B("HeadBrush"),
            BorderBrush = B("TonalBrush"),
            BorderThickness = new Thickness(1, 0, 1, 1),
            CornerRadius = new CornerRadius(0, 0, 14, 14),
            Child = stack,
            ClipToBounds = true,
            RenderTransform = _slide,
        };
        Content = _card;

        _enter = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _enter.Tick += (_, _) => { _enter.Stop(); if (_card.IsMouseOver) Expand(true); };
        _leave = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        _leave.Tick += (_, _) => { _leave.Stop(); if (!_card.IsMouseOver && !_dragging && !_menuOpen) Expand(false); };
        // A short dwell before opening: the pointer on its way to a browser tab must not pull the card down.
        _card.MouseEnter += (_, _) => { _leave.Stop(); if (!_expanded) _enter.Start(); };
        _card.MouseLeave += (_, _) => { _enter.Stop(); if (_expanded) _leave.Start(); };

        _card.MouseLeftButtonDown += StartDrag;
        _card.MouseMove += Drag;
        _card.MouseLeftButtonUp += EndDrag;
        _card.ContextMenu = Menu();
        _card.ContextMenu.Opened += (_, _) => _menuOpen = true;
        _card.ContextMenu.Closed += (_, _) => { _menuOpen = false; if (!_card.IsMouseOver) _leave.Start(); };

        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            SetWindowLong(hwnd, GWL_EXSTYLE, GetWindowLong(hwnd, GWL_EXSTYLE) | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
        };
        _card.SizeChanged += (_, _) => { if (!_expanded) _slide.Y = Collapsed; };
        Loaded += (_, _) => { Place(); _slide.Y = Collapsed; };
    }

    bool _menuOpen;

    double Collapsed => -Math.Max(0, _card.ActualHeight - StripHeight);

    ContextMenu Menu()
    {
        var m = new ContextMenu();
        var open = new MenuItem { Header = "Відкрити Bison Time Checker" };
        open.Click += (_, _) => App.Instance.ShowApp(AppPage.Today);
        var hide = new MenuItem { Header = "Сховати віджет" };
        hide.Click += (_, _) =>
        {
            var s = App.Store.LoadSettings();
            s.Widget = false;
            App.Store.SaveSettings(s);
            App.Instance.UpdateWidget();
        };
        m.Items.Add(open);
        m.Items.Add(hide);
        return m;
    }

    // ---------------- show / hide ----------------

    void Expand(bool on)
    {
        if (_expanded == on) return;
        _expanded = on;
        if (on) Refresh(force: true);
        var to = on ? 0 : Collapsed;
        if (!SystemParameters.ClientAreaAnimation) { _slide.BeginAnimation(TranslateTransform.YProperty, null); _slide.Y = to; return; }
        _slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(on ? 200 : 160))
        {
            EasingFunction = new CubicEase { EasingMode = on ? EasingMode.EaseOut : EasingMode.EaseIn },
        });
    }

    /// <summary>Called every second by the app: cheap strip update, the full numbers every few seconds.</summary>
    public void Tick() => Refresh(force: false);

    public void Refresh(bool force = true)
    {
        var now = Environment.TickCount64;
        if (force || _report == null || now - _lastBuild > (_expanded ? 3_000 : 20_000))
        {
            _lastBuild = now;
            var s = App.Store.LoadSettings();
            var day = TimeUtil.DayKey(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            try
            {
                _report = Report.Build(App.Tracker.DaySegments(day), App.Store.LoadDay(day).State, s);
                _current = App.Tracker.Open is { } o ? Classifier.Classify(new WindowInfo(o.Proc, o.Title, o.Url), s).Project : null;
            }
            catch { /* keep the last numbers */ }
        }
        var state = State();
        // Rebuild the card only when something visible changed; never under the pointer's press.
        var key = $"{state.Brush}|{state.Text}|{_report?.DayRounded}|{Pending()}|{ProjectMs()}";
        if (key == _state || _dragging) { UpdateStrip(state.Brush); return; }
        _state = key;
        UpdateStrip(state.Brush);
        Build(state);
    }

    (string Brush, string Text, string Kind) State()
    {
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (App.Tracker.DoneUntil is { } until)
            return ("EdgeBrush", $"День завершено · облік з {TimeUtil.Clock(until)}", "done");
        if (App.Tracker.PausedSince is { } p)
            return ("OrangeBrush", $"На паузі · {TimeUtil.FormatExact(nowMs - p)}", "pause");
        var idle = Native.IdleMs();
        if (idle >= Math.Max(1, App.Store.LoadSettings().IdleMin) * 60_000L)
            return ("FaintBrush", $"Простій · {TimeUtil.FormatExact(idle)}", "idle");
        return ("MintBrush", _current ?? "Інше", "run");
    }

    int Pending() => _report?.Projects.Sum(p => p.Done.Marked ? 0 : p.Done.Stale ? Math.Max(0, p.Done.Diff) : p.Rounded) ?? 0;

    long ProjectMs() => _current == null ? 0 : (_report?.Projects.FirstOrDefault(p => p.Name == _current)?.ExactMs ?? 0) / TimeUtil.Min;

    void UpdateStrip(string brush)
    {
        _fill.Background = B(brush);
        var share = Math.Clamp((_report?.DayRounded ?? 0) / (double)DayGoalMin, 0, 1);
        var width = _track.ActualWidth > 0 ? _track.ActualWidth : CardWidth - 32;
        _fill.Width = Math.Max(share > 0 ? 4 : 0, width * share);
    }

    void Build((string Brush, string Text, string Kind) state)
    {
        _body.Children.Clear();

        // 1. What is being counted right now, and how much of today it already has.
        var dot = new Ellipse { Width = 8, Height = 8, Fill = B(state.Brush), VerticalAlignment = VerticalAlignment.Center };
        var name = T(state.Text, 13, state.Kind == "run" && _current != null ? "TextBrush" : "MutedBrush", FontWeights.Medium).M(8, 0, 8, 0);
        var mine = state.Kind == "run" && _current != null ? T(TimeUtil.FormatExact(ProjectMs() * TimeUtil.Min), 12.5, "MutedBrush", ellipsis: false) : null;
        _body.Children.Add(Cols("Auto,*,Auto", dot, name, mine));

        // 2. The day's total for the report, and what of it is still to enter.
        var total = T(TimeUtil.FormatReport(_report?.DayRounded ?? 0), 20, weight: FontWeights.Bold, display: true, ellipsis: false);
        var pending = Pending();
        UIElement fact = pending > 0
            ? HRow(new Ellipse { Width = 7, Height = 7, Fill = B("LimeBrush"), VerticalAlignment = VerticalAlignment.Center },
                T($"ще внести {TimeUtil.FormatExact(pending * TimeUtil.Min)}", 12.5, "TextBrush", FontWeights.Medium).M(6, 0, 0, 0))
            : (_report?.DayRounded ?? 0) > 0
                ? HRow(Glyph(G.Check, 12, "MintBrush"), T("усе внесено", 12.5, "MutedBrush").M(6, 0, 0, 0))
                : T("до звіту за день", 12.5, "MutedBrush");
        _body.Children.Add(Cols("Auto,*,Auto", total, null, fact).M(0, 8, 0, 0));

        // 3. Actions. The same ones are in the tray menu and the day window: nothing lives only here.
        UIElement main = state.Kind switch
        {
            "done" => IconText(G.Play, "Продовжити облік", "Primary", () => App.Instance.Resume(), "Повернутися до обліку зараз"),
            "pause" => HRow(
                IconText(G.Play, "Продовжити", "Primary", () => App.Instance.TogglePause(), "Alt+Shift+P"),
                IconText(G.Stop, "Завершити", "Ghost", () => App.Instance.FinishDay(), "Облік зупиниться до 05:00 і ввімкнеться сам").M(8, 0, 0, 0)),
            _ => HRow(
                IconText(G.Pause, "Пауза", "Tonal", () => App.Instance.TogglePause(), "Пауза (Alt+Shift+P)"),
                IconText(G.Stop, "Завершити", "Ghost", () => App.Instance.FinishDay(), "Облік зупиниться до 05:00 і ввімкнеться сам").M(8, 0, 0, 0)),
        };
        var open = IconBtn(G.Today, () => App.Instance.ShowApp(AppPage.Today), "Відкрити Bison Time Checker");
        _body.Children.Add(Cols("*,Auto", main, open).M(0, 12, 0, 0));
    }

    // ---------------- position: along the top edge, any monitor ----------------

    /// <summary>Put the widget at its saved spot (centre x, monitor top), or at 62% of the primary screen.</summary>
    public void Place()
    {
        var s = App.Store.LoadSettings();
        var screen = s.WidgetX is { } x && s.WidgetY is { } y
            ? System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(x, y + 1))
            : System.Windows.Forms.Screen.PrimaryScreen!;
        var area = screen.WorkingArea;
        var cx = s.WidgetX ?? area.Left + (int)(area.Width * 0.62);
        MoveTo(cx, area);
    }

    void MoveTo(int centerX, System.Drawing.Rectangle area)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        var w = (int)Math.Round(ActualWidth * VisualTreeHelper.GetDpi(this).DpiScaleX);
        var left = Math.Clamp(centerX - w / 2, area.Left, Math.Max(area.Left, area.Right - w));
        SetWindowPos(hwnd, HWND_TOPMOST, left, area.Top, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE);
    }

    void StartDrag(object sender, MouseButtonEventArgs e)
    {
        if (!_expanded || e.OriginalSource is DependencyObject d && InsideButton(d)) return;
        GetCursorPos(out var p);
        GetWindowRect(new WindowInteropHelper(this).Handle, out var r);
        _grabOffset = p.X - (r.Left + r.Right) / 2.0;
        _dragging = _card.CaptureMouse();
        e.Handled = _dragging;
    }

    void Drag(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        GetCursorPos(out var p);
        // The widget follows the pointer along the top edge and hops to whichever monitor the pointer is on.
        var area = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(p.X, p.Y)).WorkingArea;
        MoveTo((int)(p.X - _grabOffset), area);
    }

    void EndDrag(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        _card.ReleaseMouseCapture();
        GetWindowRect(new WindowInteropHelper(this).Handle, out var r);
        var s = App.Store.LoadSettings();
        s.WidgetX = (r.Left + r.Right) / 2;
        s.WidgetY = r.Top;
        App.Store.SaveSettings(s);
        if (!_card.IsMouseOver) _leave.Start();
    }

    static bool InsideButton(DependencyObject d)
    {
        for (var x = d; x != null; x = VisualTreeHelper.GetParent(x) ?? LogicalTreeHelper.GetParent(x))
            if (x is System.Windows.Controls.Primitives.ButtonBase) return true;
        return false;
    }

    // ---------------- Win32 ----------------

    const int GWL_EXSTYLE = -20;
    const int WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOOLWINDOW = 0x00000080;
    const uint SWP_NOSIZE = 0x0001, SWP_NOACTIVATE = 0x0010;
    static readonly IntPtr HWND_TOPMOST = new(-1);

    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hWnd, int index);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr hWnd, int index, int value);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
}
