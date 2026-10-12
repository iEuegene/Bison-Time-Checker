using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using BisonTime.Core;
using static BisonTime.App.Ui;

namespace BisonTime.App;

/// <summary>
/// One day: total for the report, pause / finish, projects with their rows and comment, "Інше".
/// Used twice: in the tray flyout and as the "Сьогодні" page of the main window. Rebuilt from data on every change.
/// </summary>
internal sealed class DayView : Grid
{
    readonly bool _flyout;
    string _day = TimeUtil.DayKey(Now);
    readonly HashSet<string> _open = new();
    bool _otherOpen, _showShort;
    readonly HashSet<string> _openApps = new(StringComparer.OrdinalIgnoreCase);
    string? _openVisit;
    readonly StackPanel _head = new();
    readonly StackPanel _body = new() { Margin = new Thickness(12, 12, 12, 16) };
    readonly ScrollViewer _scroll;
    readonly DispatcherTimer _refresh;
    DispatcherTimer? _commentSave;

    Settings _settings = new();
    DayFile _file = new();
    DayReport _report = new();
    string? _current;
    Dictionary<string, long> _off = new();

    /// <summary>The flyout hides itself when the user goes to another window from here.</summary>
    public event Action? CloseRequested;

    static long Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    static string Today => TimeUtil.DayKey(Now);

    public DayView(bool flyout)
    {
        _flyout = flyout;
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var headBox = new Border { Background = B("HeadBrush"), Padding = new Thickness(flyout ? 18 : 28, 14, flyout ? 14 : 28, 16), Child = _head };
        if (flyout) headBox.CornerRadius = new CornerRadius(19, 19, 0, 0);   // inside the flyout's rounded sheet
        _scroll = new ScrollViewer { Content = _body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        if (!flyout) _body.Margin = new Thickness(24, 16, 24, 24);
        SetRow(_scroll, 1);
        Children.Add(headBox);
        Children.Add(_scroll);

        _refresh = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _refresh.Tick += (_, _) => { if (IsVisible && !EditingText) Refresh(); };
        _refresh.Start();
        App.Tracker.Changed += () => Dispatcher.BeginInvoke(() => { if (IsVisible && !EditingText) Refresh(); });
        App.Instance.UpdateChanged += () => Dispatcher.BeginInvoke(() => { if (IsVisible) Refresh(); });
        IsVisibleChanged += (_, _) => { if (IsVisible) Refresh(); };
    }

    bool EditingText => System.Windows.Input.Keyboard.FocusedElement is TextBox;

    /// <summary>Show a day (today by default) with the current project expanded.</summary>
    public void Open(string? day = null)
    {
        _day = day != null && string.CompareOrdinal(day, Today) <= 0 ? day : Today;
        _open.Clear();
        _openApps.Clear();
        _openVisit = null;
        Load();
        if (_current != null) _open.Add(_current);
        Render();
    }

    void Go(int delta)
    {
        var d = TimeUtil.ShiftDay(_day, delta);
        if (string.CompareOrdinal(d, Today) > 0) return;
        if (string.CompareOrdinal(d, TimeUtil.ShiftDay(Today, -(Tracker.KeepSegmentsDays - 1))) < 0) return;
        _day = d;
        _open.Clear();
        _openApps.Clear();
        _openVisit = null;
        Refresh();
    }

    // ---------------- data ----------------

    void Load()
    {
        if (_day != Today && string.CompareOrdinal(_day, TimeUtil.ShiftDay(Today, -(Tracker.KeepSegmentsDays - 1))) < 0) _day = Today;
        _settings = App.Store.LoadSettings();
        _file = App.Store.LoadDay(_day);
        _report = Report.Build(App.Tracker.DaySegments(_day), _file.State, _settings);
        _current = null;
        if (_day == Today && App.Tracker.Open is { } o)
            _current = Classifier.Classify(new WindowInfo(o.Proc, o.Title), _settings).Project;
        _off = App.Tracker.DayOff(_day);
        // Keep the dashboard in step with every edit made here.
        if (_file.Segments.Count > 0 || _day == Today)
            try { Stats.Save(App.Store, _day, _report); } catch { /* stats are a convenience */ }
    }

    public void Refresh()
    {
        Load();
        Render();
    }

    void UpdateDay(Action<DayState> change)
    {
        var f = App.Store.LoadDay(_day);
        change(f.State);
        App.Store.SaveDay(_day, f);
        Refresh();
    }

    void UpdateSettings(Action<Settings> change)
    {
        var s = App.Store.LoadSettings();
        change(s);
        App.Store.SaveSettings(s);
        Refresh();
    }

    IEnumerable<string> ProjectNames() =>
        _settings.Projects.Where(p => !p.Value.Archived).Select(p => p.Key)
            .OrderBy(n => n == "Inweb").ThenBy(n => n, StringComparer.CurrentCultureIgnoreCase);

    void GoTo(AppPage page)
    {
        CloseRequested?.Invoke();
        App.Instance.ShowApp(page);
    }

    // ---------------- header ----------------

    string DayLabel()
    {
        if (_day == Today) return "Сьогодні";
        if (_day == TimeUtil.ShiftDay(Today, -1)) return "Вчора";
        var d = DateTime.ParseExact(_day, "yyyy-MM-dd", null);
        return d.ToString("ddd, d MMMM", new System.Globalization.CultureInfo("uk-UA"));
    }

    void RenderHead()
    {
        _head.Children.Clear();
        var prev = IconBtn(G.Left, () => Go(-1), "Попередній день", 13);
        prev.IsEnabled = string.CompareOrdinal(_day, TimeUtil.ShiftDay(Today, -(Tracker.KeepSegmentsDays - 1))) > 0;
        var next = IconBtn(G.Right, () => Go(1), "Наступний день", 13);
        next.IsEnabled = string.CompareOrdinal(_day, Today) < 0;
        var label = T(DayLabel(), 14, "MutedBrush", FontWeights.Medium);
        label.MinWidth = 70;
        label.TextAlignment = TextAlignment.Center;
        var tools = _flyout
            ? HRow(IconBtn(G.Dashboard, () => GoTo(AppPage.Dashboard), "Дашборд"), IconBtn(G.Settings, () => GoTo(AppPage.Projects), "Налаштування"))
            : null;
        var row1 = Cols("Auto,Auto,Auto,*,Auto", prev, label, next, null, tools);
        if (_flyout)
            row1.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 1 && Window.GetWindow(this) is { } w) try { w.DragMove(); } catch { /* not pressed */ } };
        _head.Children.Add(row1);

        _head.Children.Add(T(TimeUtil.FormatReport(_report.DayRounded), _flyout ? 30 : 34, weight: FontWeights.Bold, display: true, ellipsis: false).M(4, 8, 0, 0));
        var other = _report.OtherMs >= TimeUtil.Min ? $" · ще {TimeUtil.FormatExact(_report.OtherMs)} в «Іншому»" : "";
        _head.Children.Add(T("до звіту" + other, 12.5, "MutedBrush").M(4, 2, 0, 0));

        if (_day != Today) return;
        UIElement buttons, status;
        if (App.Tracker.DoneUntil is { } until)
        {
            buttons = HRow(IconText(G.Play, "Продовжити облік", "Primary", () => App.Instance.Resume(), "Повернутися до обліку зараз"));
            status = Status("FaintBrush", $"Робочий день завершено · облік знову з {TimeUtil.Clock(until)}{(TimeUtil.DayKey(until) != Today ? " завтра" : "")}");
        }
        else if (App.Tracker.PausedSince is { } since)
        {
            var min = (Now - since) / TimeUtil.Min;
            buttons = HRow(
                IconText(G.Play, "Продовжити", "Primary", () => App.Instance.TogglePause(), "Alt+Shift+P"),
                IconText(G.Stop, "Завершити роботу", "Ghost", () => App.Instance.FinishDay(), FinishTip).M(8, 0, 0, 0));
            status = Status("OrangeBrush", $"На паузі з {TimeUtil.Clock(since)} · {TimeUtil.FormatExact(min * TimeUtil.Min)} · час не рахується");
        }
        else
        {
            buttons = HRow(
                IconText(G.Pause, "Пауза", "Tonal", () => App.Instance.TogglePause(), "Пауза (Alt+Shift+P)"),
                IconText(G.Stop, "Завершити роботу", "Ghost", () => App.Instance.FinishDay(), FinishTip).M(8, 0, 0, 0));
            status = Status("LimeBrush", _current != null ? $"Облік іде · {_current}" : "Облік іде");
        }
        _head.Children.Add(((FrameworkElement)buttons).M(0, 14, 0, 0));
        _head.Children.Add(((FrameworkElement)status).M(4, 12, 0, 0));
    }

    const string FinishTip = "Зупинити облік до ранку (05:00): не треба пам'ятати зняти паузу";

    static UIElement Status(string dotBrush, string text)
    {
        var dot = new Ellipse { Width = 8, Height = 8, Fill = B(dotBrush), VerticalAlignment = VerticalAlignment.Center };
        return Cols("Auto,*", dot, T(text, 12.5, "MutedBrush").M(8, 0, 0, 0));
    }

    // ---------------- body ----------------

    void Render()
    {
        RenderHead();
        var offset = _scroll.VerticalOffset;
        _body.Children.Clear();
        void Add(UIElement? e, double gap = 6) { if (e is FrameworkElement fe) { fe.Margin = new Thickness(0, 0, 0, gap); _body.Children.Add(fe); } }

        Add(UpdateBar(), 10);
        foreach (var it in _file.State.Idle.ToList()) Add(IdleBanner(it), 10);
        foreach (var p in _report.Projects) Add(ProjectItem(p), 2);
        if (_report.Projects.Count == 0)
            Add(Card(Col(6,
                T("Поки порожньо", 14, display: true, weight: FontWeights.Bold),
                T("Відкрий документ «домен / документ / задача» або вкладку з доменом клієнта в заголовку — BT підхопить проєкт сам.", 12.5, "MutedBrush", wrap: true)), pad: 16));
        Add(OffLine(), 8);
        Add(Other(), 12);
        if (_report.Projects.Any(p => p.Rounded > 0))
            Add(HRow(CopyReport()));
        _scroll.ScrollToVerticalOffset(offset);
    }

    UIElement? UpdateBar()
    {
        if (_day != Today || App.Instance.Update is not { } u) return null;
        var text = Col(2,
            T($"Нова версія {u.Manifest.Version}", 13.5, weight: FontWeights.SemiBold),
            T($"{u.Changed.Count} файлів · {Updater.Size(u.Bytes)} · дані не зачіпаються", 12, "MutedBrush"));
        return Card(Cols("*,Auto", text, Btn("Оновити", "Primary", () => GoTo(AppPage.About)).M(10, 0, 0, 0)), "TonalBrush", null, 14);
    }

    UIElement IdleBanner(IdleItem it)
    {
        var min = (int)Math.Round((it.E - it.S) / (double)TimeUtil.Min);
        var msg = new TextBlock { FontFamily = TextFont, FontSize = 13.5, Foreground = B("TextBrush"), TextWrapping = TextWrapping.Wrap };
        msg.Inlines.Add(new System.Windows.Documents.Run($"{min} хв простою") { FontWeight = FontWeights.SemiBold });
        msg.Inlines.Add(new System.Windows.Documents.Run($" о {TimeUtil.Clock(it.S)}. Додати до проєкту?"));
        void AddTo(string project) => UpdateDay(st =>
        {
            st.Idle.RemoveAll(x => x.Id == it.Id);
            st.Manual.Add(new ManualItem { Id = "m" + it.Id, Project = project, Min = min, Label = $"Простій {TimeUtil.Clock(it.S)}–{TimeUtil.Clock(it.E)}" });
        });
        UIElement add = it.Project != null && _settings.Projects.ContainsKey(it.Project)
            ? Btn($"До {it.Project}", "Primary", () => AddTo(it.Project!))
            : Picker("Додати до…", "Primary", ProjectNames(), AddTo);
        var other = it.Project != null ? Picker("Інший…", "Ghost", ProjectNames(), AddTo).M(6, 0, 0, 0) : null;
        var drop = Btn("Викинути", "Quiet", () => UpdateDay(st => st.Idle.RemoveAll(x => x.Id == it.Id))).M(6, 0, 0, 0);
        return Card(Col(10, msg, HRow(add, other, drop)), "CardBrush", "OrangeBrush", 14);
    }

    static string Plural(int n, string one, string few, string many)
    {
        var d = n % 10;
        var dd = n % 100;
        return d == 1 && dd != 11 ? one : d is >= 2 and <= 4 && dd is < 12 or > 14 ? few : many;
    }

    UIElement ProjectItem(ProjectReport p)
    {
        var open = _open.Contains(p.Name);
        var rows = p.Groups.SelectMany(g => g.Rows).ToList();
        var top = rows.Where(r => !r.Disabled).OrderByDescending(r => r.Ms).FirstOrDefault();
        var sub = $"{rows.Count} {Plural(rows.Count, "рядок", "рядки", "рядків")}" + (top != null ? $" · {top.Label}" : "");
        if (p.Done.Stale) sub = $"змінилось на {(p.Done.Diff > 0 ? "+" : "")}{p.Done.Diff} хв після «Внесено»";
        var subText = T(sub, 12, p.Done.Stale ? "OrangeBrush" : "FaintBrush");

        UIElement time = p.Done.Marked
            ? HRow(Glyph(G.Check, 12, "LimeBrush"), Chip(TimeUtil.FormatReport(p.Rounded), lime: false).M(6, 0, 0, 0))
            : Chip(TimeUtil.FormatReport(p.Rounded), lime: p.Rounded > 0);
        var head = Cols("Auto,*,Auto", Avatar(p.Name, p.Name == _current),
            Col(1, T(p.Name, 14, weight: FontWeights.SemiBold), subText).M(12, 0, 8, 0), time);
        var headBox = new Border { Background = Brushes.Transparent, Padding = new Thickness(10, 10, 10, 10), Child = head };
        Clickable(headBox, () => { if (!_open.Add(p.Name)) _open.Remove(p.Name); Render(); });

        var box = new StackPanel();
        box.Children.Add(headBox);
        if (open)
        {
            var detail = new StackPanel { Margin = new Thickness(58, 0, 12, 12) };
            foreach (var g in p.Groups)
            {
                detail.Children.Add(T(g.Title, 11.5, "FaintBrush", FontWeights.SemiBold).M(0, 6, 0, 2));
                foreach (var r in g.Rows) detail.Children.Add(RowEl(r));
            }
            detail.Children.Add(Cols("*,Auto",
                T($"До округлення {TimeUtil.FormatExact(p.ExactMs)}", 12, "FaintBrush"),
                HRow(Btn("+5 хв", "Tonal", () => AddManual(p.Name, 5)), Btn("+15 хв", "Tonal", () => AddManual(p.Name, 15)).M(6, 0, 0, 0))).M(0, 10, 0, 0));
            detail.Children.Add(CommentBox(p).M(0, 12, 0, 0));
            box.Children.Add(detail);
        }
        var card = new Border { CornerRadius = new CornerRadius(16), Background = open ? B("CardBrush") : Brushes.Transparent, Child = box };
        if (!open) card.MouseEnter += (_, _) => card.Background = B("CardHiBrush");
        if (!open) card.MouseLeave += (_, _) => card.Background = Brushes.Transparent;
        if ((p.Rounded == 0 || p.Done.Marked) && !open) card.Opacity = 0.7;
        return card;
    }

    void AddManual(string project, int min) => UpdateDay(st =>
        st.Manual.Add(new ManualItem { Id = $"m{Now}", Project = project, Min = min, Label = $"Вручну +{min} хв" }));

    UIElement RowEl(Row r)
    {
        var label = T(r.Label, 13, r.Disabled ? "FaintBrush" : "TextBrush");
        if (r.Disabled) label.TextDecorations = TextDecorations.Strikethrough;
        var sub = r.Task != "" ? r.Task : (r.Group is "apps" or "linked" or "ctx" ? r.App : "");
        var what = sub == "" ? (UIElement)label : Col(1, label, T(sub, 11.5, "FaintBrush"));
        var dur = T(TimeUtil.FormatExact(r.Ms), 12.5, r.Disabled ? "FaintBrush" : "MutedBrush");
        var key = r.Key;
        var sw = Toggle(!r.Disabled, on => UpdateDay(st => { if (on) st.Disabled.Remove(key); else st.Disabled[key] = true; }),
            r.Disabled ? "Не враховується" : "Враховується");
        return Cols("*,Auto,Auto", what, dur.M(8, 0, 12, 0), sw).M(0, 5, 0, 5);
    }

    FrameworkElement CommentBox(ProjectReport p)
    {
        var c = p.Comment;
        var box = Input(c.Text, multiline: true);
        box.MinHeight = 60;
        box.MaxHeight = 150;
        box.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        var name = p.Name;
        var auto = c.Auto;
        box.TextChanged += (_, _) =>
        {
            _commentSave?.Stop();
            _commentSave = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _commentSave.Tick += (_, _) =>
            {
                _commentSave!.Stop();
                var f = App.Store.LoadDay(_day);
                f.State.Comments[name] = box.Text;
                f.State.CommentBase[name] = auto;
                App.Store.SaveDay(_day, f);
            };
            _commentSave.Start();
        };
        UIElement hint = c.Stale
            ? HRow(T("є нове · ", 12, "OrangeBrush"), Btn("оновити", "Quiet", () => UpdateDay(st => { st.Comments.Remove(name); st.CommentBase.Remove(name); })))
            : T(c.Edited ? "відредаговано" : "чернетка коментаря", 12, "FaintBrush");
        var copy = IconBtn(G.Copy, () => { try { Clipboard.SetText(box.Text ?? ""); } catch { /* clipboard busy */ } }, "Копіювати коментар", 14);
        var done = p.Done.Marked;
        var mark = Btn(done ? "Внесено ✓" : "Внесено", done ? "Tonal" : "Primary", () => UpdateDay(st =>
        {
            if (done) st.Done.Remove(name); else st.Done[name] = p.Rounded;
        }), "Позначити, що час уже перенесено у звіт");
        return Col(8, WithHint(box, "Що зроблено — допиши, що BT не бачить"), Cols("*,Auto,Auto", hint, copy, mark.M(6, 0, 0, 0)));
    }

    /// <summary>All projects of the day as text for the report: time and comment per line.</summary>
    Button CopyReport()
    {
        Button? b = null;
        b = IconText(G.Copy, "Копіювати звіт", "Primary", () =>
        {
            var lines = _report.Projects.Where(p => p.Rounded > 0).Select(p =>
                $"{p.Name} — {TimeUtil.FormatReport(p.Rounded)}" + (string.IsNullOrWhiteSpace(p.Comment.Text) ? "" : $" — {p.Comment.Text.Trim()}"));
            try { Clipboard.SetText(string.Join(Environment.NewLine, lines)); } catch { return; }
            b!.Content = HRow(Glyph(G.Check, 14, "GreenBrush"), new TextBlock { Text = "Скопійовано", FontFamily = TextFont, Margin = new Thickness(8, 0, 0, 0) });
        }, "Проєкти, час і коментарі — одним текстом");
        return b;
    }

    UIElement? OffLine()
    {
        var parts = _off.Where(x => x.Value >= TimeUtil.Min && x.Key != "done").OrderByDescending(x => x.Value)
            .Select(x => $"{(OffReasons.Names.TryGetValue(x.Key, out var n) ? n : x.Key)} {TimeUtil.FormatExact(x.Value)}").ToList();
        if (parts.Count == 0) return null;
        var t = T("Поза обліком: " + string.Join(" · ", parts), 12, "FaintBrush", wrap: true);
        t.ToolTip = "Час, коли BT нічого не рахував, і чому";
        return t.M(10, 6, 0, 0);
    }

    // ---------------- Інше ----------------

    UIElement? Other()
    {
        if (_report.OtherApps.Count == 0) return null;
        var names = string.Join(", ", _report.OtherApps.Take(3).Select(a => a.Name));
        var head = Cols("*,Auto,Auto",
            T($"Інше · {names}", 13.5, "MutedBrush"),
            T(TimeUtil.FormatExact(_report.OtherMs), 13, "MutedBrush", FontWeights.Medium).M(8, 0, 6, 0),
            Glyph(_otherOpen ? G.Up : G.Down, 11, "FaintBrush"));
        var headBox = new Border { Background = Brushes.Transparent, Padding = new Thickness(14, 12, 14, 12), Child = head };
        Clickable(headBox, () => { _otherOpen = !_otherOpen; Render(); });
        var panel = new StackPanel();
        panel.Children.Add(headBox);
        if (_otherOpen)
        {
            var list = new StackPanel { Margin = new Thickness(14, 0, 14, 10) };
            list.Children.Add(T("По програмах, найбільше часу зверху. У звіт не йде, поки не перенесеш у проєкт.", 11.5, "FaintBrush", wrap: true).M(0, 0, 0, 6));
            foreach (var a in _report.OtherApps) list.Children.Add(OtherAppEl(a));
            panel.Children.Add(list);
        }
        // Dashed outline: "outside the report".
        var g = new Grid();
        g.Children.Add(new Rectangle { Stroke = B("EdgeBrush"), StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 4, 3 }, RadiusX = 14, RadiusY = 14 });
        g.Children.Add(panel);
        return g;
    }

    UIElement OtherAppEl(OtherApp a)
    {
        var open = _openApps.Contains(a.Name);
        var count = a.Visits.Count;
        var head = Cols("*,Auto,Auto,22",
            T(a.Name, 13, a.Ms > 0 ? "TextBrush" : "MutedBrush", FontWeights.Medium),
            T($"{count} {Plural(count, "запис", "записи", "записів")}", 11.5, "FaintBrush").M(8, 0, 10, 0),
            T(TimeUtil.FormatExact(a.Ms), 12.5, a.Ms > 0 ? "LimeBrush" : "FaintBrush", FontWeights.SemiBold),
            Glyph(open ? G.Up : G.Down, 10, "FaintBrush"));
        var headBox = new Border { Background = Brushes.Transparent, Padding = new Thickness(0, 8, 0, 8), Child = head };
        Clickable(headBox, () => { if (!_openApps.Add(a.Name)) _openApps.Remove(a.Name); Render(); });

        var box = new StackPanel();
        box.Children.Add(headBox);
        if (open)
        {
            var hidden = 0;
            var list = new StackPanel { Margin = new Thickness(8, 0, 0, 6) };
            foreach (var t in a.Visits)
            {
                if (t.Moved == null && t.Ms < TimeUtil.Min && !_showShort) { hidden++; continue; }
                list.Children.Add(VisitRow(t));
            }
            var foot = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
            if (hidden > 0 || _showShort)
                foot.Children.Add(Btn(_showShort ? "сховати короткі" : $"ще {hidden} коротких (<1 хв)", "Quiet", () => { _showShort = !_showShort; Render(); }));
            if (!a.Browser)
            {
                var proc = a.Proc;
                foot.Children.Add(Btn($"Ігнорувати {a.Name}", "Quiet",
                    () => UpdateSettings(s => { if (!s.Ignore.Contains(proc)) s.Ignore.Add(proc); }), $"Ніколи не записувати {a.Name}"));
            }
            if (foot.Children.Count > 0) list.Children.Add(foot);
            box.Children.Add(list);
        }
        return new Border { BorderBrush = B("LineBrush"), BorderThickness = new Thickness(0, 1, 0, 0), Child = box };
    }

    /// <summary>One compact line; a click on it shows "→ Проєкт" and "Ігнорувати".</summary>
    UIElement VisitRow(TimelineItem t)
    {
        var label = T(t.Label, 12.5, t.Moved != null ? "MutedBrush" : "TextBrush");
        label.ToolTip = t.Label;
        var dur = t.Moved != null ? T($"→ {t.Moved}", 12, "LimeBrush") : T(TimeUtil.FormatExact(t.Ms), 12, "MutedBrush");
        var line = Cols("42,*,Auto", T(TimeUtil.Clock(t.S), 12, "FaintBrush"), label, dur.M(8, 0, 0, 0));
        var lineBox = new Border { Background = Brushes.Transparent, Padding = new Thickness(0, 4, 0, 4), Child = line };
        Clickable(lineBox, () => { _openVisit = _openVisit == t.Key ? null : t.Key; Render(); });

        var col = new StackPanel();
        col.Children.Add(lineBox);
        if (_openVisit == t.Key)
        {
            var acts = new WrapPanel { Margin = new Thickness(42, 2, 0, 6) };
            if (t.Moved != null)
                acts.Children.Add(Btn("скасувати перенесення", "Quiet", () => UpdateDay(st => st.Moved.Remove(t.Key))));
            else
            {
                acts.Children.Add(Picker("→ Проєкт", "Tonal", ProjectNames(), v => UpdateDay(st => st.Moved[t.Key] = v)));
                if (t.Browser)
                    acts.Children.Add(Btn("Ігнорувати сторінку", "Quiet",
                        () => UpdateSettings(s => { if (!s.Ignore.Contains(t.Label)) s.Ignore.Add(t.Label); }),
                        $"Ніколи не записувати вкладки з «{t.Label}» у заголовку").M(6, 0, 0, 0));
            }
            col.Children.Add(acts);
        }
        return col;
    }
}
