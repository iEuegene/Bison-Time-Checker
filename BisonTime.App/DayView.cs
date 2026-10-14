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
    bool _otherOpen, _showShort, _chrono;
    string? _openRow;
    readonly HashSet<string> _sel = new();                 // visits of Інше picked for a bulk move
    (string Match, string Text, string Project)? _ruleHint;  // offered after a move: a title fragment or an address id
    readonly HashSet<string> _openApps = new(StringComparer.OrdinalIgnoreCase);
    string? _openVisit;
    readonly StackPanel _head = new();
    readonly StackPanel _sticky = new();
    readonly Border _snack = new()
    {
        Visibility = Visibility.Collapsed, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(12, 0, 12, 12),
        CornerRadius = new CornerRadius(12), Padding = new Thickness(16, 8, 8, 8),
        Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 4, Opacity = 0.45, Color = Colors.Black },
    };
    DispatcherTimer? _snackTimer;
    string? _anchor;                                         // last ticked visit: Shift+click selects up to here
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
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // sticky: bulk-move bar and rule hint
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var headBox = new Border { Background = B("HeadBrush"), Padding = new Thickness(flyout ? 18 : 28, 14, flyout ? 14 : 28, 16), Child = _head };
        if (flyout) headBox.CornerRadius = new CornerRadius(19, 19, 0, 0);   // inside the flyout's rounded sheet
        _scroll = new ScrollViewer { Content = _body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        if (!flyout) _body.Margin = new Thickness(24, 16, 24, 24);
        _sticky.Margin = flyout ? new Thickness(12, 10, 12, 0) : new Thickness(24, 12, 24, 0);
        SetRow(_sticky, 1);
        SetRow(_scroll, 2);
        Children.Add(headBox);
        Children.Add(_sticky);
        Children.Add(_scroll);
        SetRow(_snack, 2);
        Children.Add(_snack);

        _refresh = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _refresh.Tick += (_, _) => { if (IsVisible && !EditingText) Refresh(background: true); };
        _refresh.Start();
        App.Tracker.Changed += () => Dispatcher.BeginInvoke(() => { if (IsVisible && !EditingText) Refresh(background: true); });
        // A refresh held back while the user was busy in the list runs once the pointer leaves it.
        _scroll.MouseLeave += (_, _) => { if (_pending && !Busy) { _pending = false; Render(); } };
        Loaded += (_, _) =>
        {
            if (Window.GetWindow(this) is { } w) w.PreviewKeyDown += OnKey;
        };
        App.Instance.UpdateChanged += () => Dispatcher.BeginInvoke(() => { if (IsVisible) Refresh(); });
        IsVisibleChanged += (_, _) => { if (IsVisible) Refresh(); };
    }

    bool EditingText => System.Windows.Input.Keyboard.FocusedElement is TextBox;

    /// <summary>Show a day (today by default) with the current project expanded.</summary>
    public void Open(string? day = null)
    {
        _day = day != null && string.CompareOrdinal(day, Today) <= 0 ? day : Today;
        _followToday = _day == Today;
        _open.Clear();
        _openApps.Clear();
        _openVisit = null;
        _sel.Clear();
        _ruleHint = null;
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
        _followToday = d == Today;
        _open.Clear();
        _openApps.Clear();
        _openVisit = null;
        _sel.Clear();
        _ruleHint = null;
        Refresh();
    }

    // ---------------- data ----------------

    bool _followToday = true;   // showing "today": move on to the new day after midnight or a long sleep

    void Load()
    {
        if (_followToday && _day != Today)
        {
            _day = Today;
            _open.Clear();
            _openApps.Clear();
            _openVisit = null;
            _sel.Clear();
            _ruleHint = null;
        }
        if (_day != Today && string.CompareOrdinal(_day, TimeUtil.ShiftDay(Today, -(Tracker.KeepSegmentsDays - 1))) < 0) _day = Today;
        _settings = App.Store.LoadSettings();
        _file = App.Store.LoadDay(_day);
        _report = Report.Build(App.Tracker.DaySegments(_day), _file.State, _settings);
        _current = null;
        if (_day == Today && App.Tracker.Open is { } o)
            _current = Classifier.Classify(new WindowInfo(o.Proc, o.Title, o.Url), _settings).Project;
        _off = App.Tracker.DayOff(_day);
        // Keep the dashboard in step with every edit made here.
        if (_file.Segments.Count > 0 || _day == Today)
            try { Stats.Save(App.Store, _day, _report); } catch { /* stats are a convenience */ }
    }

    bool _pending;
    int _limit = 150;   // visits drawn per list before "Показати ще": keeps a long day responsive

    /// <summary>The user is in the middle of something in the list: a menu, ticks, or the pointer over it.</summary>
    bool Busy => Ui.MenuOpen || _sel.Count > 0 || _scroll.IsMouseOver;

    /// <summary>Background refresh (timer, window switch): data and header always, the list only when not busy.</summary>
    void Refresh(bool background)
    {
        if (!background) { Refresh(); return; }
        Load();
        if (Busy) { RenderHead(); _pending = true; return; }
        Render();
    }

    void OnKey(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (!IsVisible || System.Windows.Input.Keyboard.FocusedElement is TextBox) return;
        var mods = System.Windows.Input.Keyboard.Modifiers;
        if (e.Key == System.Windows.Input.Key.C && mods == (System.Windows.Input.ModifierKeys.Control | System.Windows.Input.ModifierKeys.Shift))
        {
            CopyReportText();
            e.Handled = true;
        }
        else if (mods == System.Windows.Input.ModifierKeys.Alt && e.SystemKey is System.Windows.Input.Key.Left or System.Windows.Input.Key.Right)
        {
            Go(e.SystemKey == System.Windows.Input.Key.Left ? -1 : 1);
            e.Handled = true;
        }
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
        var prev = IconBtn(G.Left, () => Go(-1), "Попередній день (Alt+←)", 13);
        prev.IsEnabled = string.CompareOrdinal(_day, TimeUtil.ShiftDay(Today, -(Tracker.KeepSegmentsDays - 1))) > 0;
        var next = IconBtn(G.Right, () => Go(1), "Наступний день (Alt+→)", 13);
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

        // The big number is what is still to be entered; what is already in the report is said under it.
        // A project entered and then grown counts its entered part as "in the report" and only the growth as pending.
        var entered = _report.Projects.Where(p => p.Done.Marked || p.Done.Stale).Sum(p => p.Rounded - p.Done.Diff);
        var pending = _report.Projects.Sum(p => p.Done.Marked ? 0 : p.Done.Stale ? Math.Max(0, p.Done.Diff) : p.Rounded);
        var other = _report.OtherMs >= TimeUtil.Min ? $" · {TimeUtil.FormatExact(_report.OtherMs)} в «Іншому»" : "";
        var size = _flyout ? 30 : 34;
        if (entered > 0 && pending == 0)
        {
            _head.Children.Add(HRow(Glyph(G.Check, size * 0.7, "MintBrush"),
                T(TimeUtil.FormatReport(entered), size, "MintBrush", FontWeights.Bold, display: true, ellipsis: false).M(10, 0, 0, 0)).M(4, 8, 0, 0));
            _head.Children.Add(T("усе внесено у звіт" + other, 12.5, "MutedBrush").M(4, 2, 0, 0));
        }
        else
        {
            _head.Children.Add(T(TimeUtil.FormatReport(pending), size, weight: FontWeights.Bold, display: true, ellipsis: false).M(4, 8, 0, 0));
            _head.Children.Add(T((entered > 0 ? "ще внести" : "до звіту") + other, 12.5, "MutedBrush").M(4, 2, 0, 0));
        }

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
            status = Status("MintBrush", _current != null ? $"Облік іде · {_current}" : "Облік іде");
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
        _pending = false;
        var focused = FocusedId();
        RenderHead();
        // Under the header, outside the scroll: stays in view however far down the ticks are.
        _sticky.Children.Clear();
        if (RuleHint() is { } hint) _sticky.Children.Add(hint);
        if (SelectionBar() is { } bar) _sticky.Children.Add(bar);
        var offset = _scroll.VerticalOffset;
        _body.Children.Clear();
        void Add(UIElement? e, double gap = 6) { if (e is FrameworkElement fe) { fe.Margin = new Thickness(0, 0, 0, gap); _body.Children.Add(fe); } }

        Add(UpdateBar(), 10);
        foreach (var it in _file.State.Idle.ToList()) Add(IdleBanner(it), 10);
        var toEnter = _report.Projects.Where(p => !p.Done.Marked).ToList();
        var done = _report.Projects.Where(p => p.Done.Marked).ToList();
        foreach (var p in toEnter) Add(ProjectItem(p), 2);
        if (done.Count > 0)
        {
            Add(Cols("Auto,*,Auto", Glyph(G.Check, 12, "MintBrush"), T("Внесено у звіт", 12.5, "MutedBrush", FontWeights.SemiBold).M(8, 0, 0, 0),
                T(TimeUtil.FormatReport(done.Sum(p => p.Rounded)), 12.5, "MutedBrush")).M(10, toEnter.Count > 0 ? 14 : 4, 10, 0), 2);
            foreach (var p in done) Add(ProjectItem(p), 2);
        }
        if (_report.Projects.Count == 0)
            Add(Card(Col(6,
                T("Поки порожньо", 14, display: true, weight: FontWeights.Bold),
                T("Відкрий документ «домен / документ / задача» або вкладку з доменом клієнта в заголовку — BT підхопить проєкт сам.", 12.5, "MutedBrush", wrap: true)), pad: 16));
        Add(OffLine(), 8);
        Add(Other(), 12);
        if (_report.Projects.Any(p => p.Rounded > 0))
            Add(HRow(CopyReport()));
        _scroll.ScrollToVerticalOffset(offset);
        if (focused != null)
            Dispatcher.BeginInvoke(() => RestoreFocus(this, focused), System.Windows.Threading.DispatcherPriority.Loaded);
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
        // The chips say it all; the explanation lives in their tooltip, not in another line of text.
        string? tip = p.Done.Stale
            ? (p.Done.Diff > 0
                ? $"У звіті вже {TimeUtil.FormatReport(p.Rounded - p.Done.Diff)}. Додай {p.Done.Diff} хв і натисни «Внесено ще раз»."
                : $"У звіті на {-p.Done.Diff} хв більше, ніж тепер.")
            : null;
        UIElement time = p.Done.Stale
            ? HRow(T(TimeUtil.FormatReport(p.Rounded), 12.5, "FaintBrush").M(0, 0, 8, 0),
                Chip(p.Done.Diff > 0 ? $"+{p.Done.Diff} хв" : $"−{-p.Done.Diff} хв", lime: p.Done.Diff > 0))
            : p.Done.Marked
            ? HRow(Glyph(G.Check, 13, "MintBrush"), T(TimeUtil.FormatReport(p.Rounded), 13, "MutedBrush", FontWeights.Medium).M(6, 0, 0, 0))
            : Chip(TimeUtil.FormatReport(p.Rounded), lime: p.Rounded > 0);
        if (p.Done.Marked) sub = "внесено у звіт";
        // A few minutes round down to nothing: show the real minutes, not a puzzling "0 год 0 хв".
        if (p.Rounded == 0 && p.ExactMs > 0 && !p.Done.Marked)
        {
            time = Chip(TimeUtil.FormatExact(p.ExactMs), lime: false);
            tip = $"Менше половини кроку округлення: у звіт поки йде 0. Лаймовою плашка стане від {(_settings.RoundStep + 1) / 2} хв.";
        }
        var subText = T(sub, 12, "FaintBrush");
        if (tip != null && time is FrameworkElement te) te.ToolTip = tip;
        var head = Cols("Auto,*,Auto", Avatar(p.Name, p.Name == _current),
            Col(1, T(p.Name, 14, weight: FontWeights.SemiBold), subText).M(12, 0, 8, 0), time);
        var headBox = new Border { Background = Brushes.Transparent, Padding = new Thickness(10, 10, 10, 10), Child = head };
        Clickable(headBox, () => { if (!_open.Add(p.Name)) _open.Remove(p.Name); Render(); }, "proj:" + p.Name, $"{p.Name}, {TimeUtil.FormatReport(p.Rounded)}");

        var box = new StackPanel();
        box.Children.Add(headBox);
        if (open)
        {
            var detail = new StackPanel { Margin = new Thickness(58, 0, 12, 12) };
            var all = p.Groups.SelectMany(g => g.Rows).ToList();
            if (all.Any(r => r.HasSnapshot))
            {
                var fresh = all.Where(r => r.IsNew).OrderByDescending(r => r.NewMs).ToList();
                if (fresh.Count > 0)
                {
                    detail.Children.Add(Cols("*,Auto", T("Додалося після «Внесено»", 12, "TextBrush", FontWeights.SemiBold),
                        T("+" + TimeUtil.FormatExact(fresh.Sum(r => r.NewMs)), 12, "TextBrush", FontWeights.SemiBold)).M(0, 6, 0, 2));
                    foreach (var r in fresh) detail.Children.Add(RowEl(r, badge: r.EnteredMs is > 0 ? "+" + TimeUtil.FormatExact(r.NewMs) : "нове"));
                }
                var old = all.Where(r => !r.IsNew).ToList();
                if (old.Count > 0)
                {
                    detail.Children.Add(HRow(Glyph(G.Check, 11, "MintBrush"), T("Уже у звіті", 12, "FaintBrush", FontWeights.SemiBold).M(6, 0, 0, 0)).M(0, 12, 0, 2));
                    foreach (var r in old) detail.Children.Add(RowEl(r, dim: true));
                }
            }
            else
                foreach (var g in p.Groups)
                {
                    detail.Children.Add(T(g.Title, 12, "FaintBrush", FontWeights.SemiBold).M(0, 6, 0, 2));
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
        if ((p.Rounded == 0 || p.Done.Marked) && !open) card.Opacity = p.Done.Marked ? 0.6 : 0.75;
        return card;
    }

    void AddManual(string project, int min) => UpdateDay(st =>
        st.Manual.Add(new ManualItem { Id = $"m{Now}", Project = project, Min = min, Label = $"Вручну +{min} хв" }));

    /// <summary>A row of a project; a click on it shows when exactly the time was spent.</summary>
    UIElement RowEl(Row r, string? badge = null, bool dim = false)
    {
        var label = T(r.Label, 13, r.Disabled ? "FaintBrush" : "TextBrush");
        if (r.Disabled) label.TextDecorations = TextDecorations.Strikethrough;
        var sub = r.Task != "" ? r.Task : (r.Group is "apps" or "linked" or "ctx" ? r.App : "");
        var what = sub == "" ? (UIElement)label : Col(1, label, T(sub, 12, "FaintBrush"));
        var dur = T(TimeUtil.FormatExact(r.Ms), 12.5, r.Disabled ? "FaintBrush" : "MutedBrush");
        var key = r.Key;
        var sw = Toggle(!r.Disabled, on => UpdateDay(st => { if (on) st.Disabled.Remove(key); else st.Disabled[key] = true; }),
            r.Disabled ? "Не враховується" : "Враховується");
        var whatBox = new Border { Background = Brushes.Transparent, Child = what, ToolTip = r.Pieces.Count > 0 ? "Клік — коли саме" : null };
        if (r.Pieces.Count > 0) Clickable(whatBox, () => { _openRow = _openRow == key ? null : key; Render(); }, "row:" + key, r.Label);
        UIElement durEl = badge != null ? HRow(dur, Chip(badge).M(6, 0, 0, 0)) : dur;
        var line = Cols("*,Auto,Auto", whatBox, ((FrameworkElement)durEl).M(8, 0, 12, 0), sw).M(0, 5, 0, 5);
        if (dim) whatBox.Opacity = 0.55;
        if (_openRow != key || r.Pieces.Count == 0) return line;

        var pieces = new StackPanel { Margin = new Thickness(10, 0, 0, 6) };
        foreach (var x in r.Pieces)
            pieces.Children.Add(Cols("*,Auto",
                T($"{TimeUtil.Clock(x.S)} – {TimeUtil.Clock(x.E)}", 12, "MutedBrush"),
                T(TimeUtil.FormatExact(x.Ms), 12, "FaintBrush")).M(0, 2, 0, 2));
        var box = new Border { BorderBrush = B("EdgeBrush"), BorderThickness = new Thickness(2, 0, 0, 0), Child = pieces, Margin = new Thickness(2, 0, 0, 0) };
        return Col(0, line, box);
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
        var label = done ? "Скасувати «Внесено»" : p.Done.Stale ? "Внесено ще раз" : "Внесено у звіт";
        var mark = Btn(label, done ? "Quiet" : "Primary", () =>
        {
            var was = App.Store.LoadDay(_day).State;
            var before = was.Done.TryGetValue(name, out var b) ? b : (int?)null;
            var beforeRows = was.DoneRows.TryGetValue(name, out var br) ? new Dictionary<string, long>(br) : null;
            // Remember what each row held: later growth shows up row by row as "Додалося після «Внесено»".
            var snapshot = p.Groups.SelectMany(g => g.Rows).ToDictionary(r => r.Key, r => r.Disabled ? 0 : r.Ms);
            UpdateDay(st =>
            {
                if (done) { st.Done.Remove(name); st.DoneRows.Remove(name); }
                else { st.Done[name] = p.Rounded; st.DoneRows[name] = snapshot; }
            });
            if (!done)
            {
                _open.Remove(name);   // it moves down to "Внесено у звіт"
                Snack($"{name}: {TimeUtil.FormatReport(p.Rounded)} внесено у звіт", () => UpdateDay(st =>
                {
                    if (before is { } v) st.Done[name] = v; else st.Done.Remove(name);
                    if (beforeRows != null) st.DoneRows[name] = beforeRows; else st.DoneRows.Remove(name);
                }));
                Render();
            }
        }, "Позначити, що час уже перенесено у звіт: проєкт стане сірим і опуститься вниз");
        return Col(8, WithHint(box, "Що зроблено — допиши, що BT не бачить"), Cols("*,Auto,Auto", hint, copy, mark.M(6, 0, 0, 0)));
    }

    /// <summary>All projects of the day as text for the report: time and comment per line.</summary>
    Button CopyReport()
    {
        Button? b = null;
        b = IconText(G.Copy, "Копіювати звіт", "Primary", () =>
        {
            if (!CopyReportText()) return;
            b!.Content = HRow(Glyph(G.Check, 14, "GreenBrush"), new TextBlock { Text = "Скопійовано", FontFamily = TextFont, Margin = new Thickness(8, 0, 0, 0) });
        }, "Проєкти, час і коментарі — одним текстом (Ctrl+Shift+C)");
        return b;
    }

    bool CopyReportText()
    {
        var lines = _report.Projects.Where(p => p.Rounded > 0).Select(p =>
            $"{p.Name} — {TimeUtil.FormatReport(p.Rounded)}" + (string.IsNullOrWhiteSpace(p.Comment.Text) ? "" : $" — {p.Comment.Text.Trim()}"));
        try { Clipboard.SetText(string.Join(Environment.NewLine, lines)); return true; } catch { return false; }
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
        Clickable(headBox, () => { _otherOpen = !_otherOpen; Render(); }, "other", "Інше");
        var panel = new StackPanel();
        panel.Children.Add(headBox);
        if (_otherOpen)
        {
            var list = new StackPanel { Margin = new Thickness(14, 0, 14, 10) };
            list.Children.Add(ModeSwitch().M(0, 0, 0, 8));
            if (_chrono) Chronology(list);
            else
            {
                list.Children.Add(T("Найбільше часу зверху. У звіт не йде, поки не перенесеш у проєкт.", 12, "FaintBrush", wrap: true).M(0, 0, 0, 6));
                foreach (var a in _report.OtherApps) list.Children.Add(OtherAppEl(a));
            }
            panel.Children.Add(list);
        }
        // Dashed outline: "outside the report".
        var g = new Grid();
        g.Children.Add(new Rectangle { Stroke = B("EdgeBrush"), StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 4, 3 }, RadiusX = 14, RadiusY = 14 });
        g.Children.Add(panel);
        return g;
    }

    /// <summary>"По програмах" / "Хронологічно": a two-segment switch.</summary>
    FrameworkElement ModeSwitch()
    {
        Border Seg(string text, bool on, bool first, Action click)
        {
            // WPF does not clip children to a rounded parent: the segment rounds its own outer corners.
            var b = new Border
            {
                Background = on ? B("TonalBrush") : Brushes.Transparent, Padding = new Thickness(12, 5, 12, 5),
                CornerRadius = first ? new CornerRadius(15, 0, 0, 15) : new CornerRadius(0, 15, 15, 0),
                BorderBrush = B("EdgeBrush"), BorderThickness = new Thickness(first ? 0 : 1, 0, 0, 0),
                Child = T(text, 12.5, on ? "TextBrush" : "MutedBrush", on ? FontWeights.SemiBold : FontWeights.Medium),
            };
            Clickable(b, click, "mode:" + text, text);
            return b;
        }
        var row = HRow(
            Seg("По програмах", !_chrono, true, () => { _chrono = false; Render(); }),
            Seg("Хронологічно", _chrono, false, () => { _chrono = true; Render(); }));
        return new Border { BorderBrush = B("EdgeBrush"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(16), Child = row, HorizontalAlignment = HorizontalAlignment.Left };
    }

    // ---------------- undo ----------------

    /// <summary>"Перенесено 34 записи · Відмінити" for 6 seconds: reversible actions run at once, no "Точно?".</summary>
    void Snack(string text, Action undo)
    {
        _snackTimer?.Stop();
        var undoBtn = Btn("Відмінити", "Quiet", () =>
        {
            _snackTimer?.Stop();
            _snack.Visibility = Visibility.Collapsed;
            undo();
        });
        _snack.Background = B("TextBrush");
        var msg = T(text, 13, "GreenBrush", FontWeights.Medium);
        undoBtn.Foreground = B("GreenBrush");
        _snack.Child = Cols("*,Auto", msg, undoBtn);
        _snack.Visibility = Visibility.Visible;
        _snackTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        _snackTimer.Tick += (_, _) => { _snackTimer.Stop(); _snack.Visibility = Visibility.Collapsed; };
        _snackTimer.Start();
    }

    string Records(int n) => $"{n} {Plural(n, "запис", "записи", "записів")}";

    // ---------------- bulk move ----------------

    List<TimelineItem> Selected() => _report.Timeline.Where(t => !t.IsSession && t.Moved == null && _sel.Contains(t.Key)).ToList();

    /// <summary>"Вибрано 18 · 6 хв  [→ Проєкт]  [Скасувати]" while anything is ticked.</summary>
    UIElement? SelectionBar()
    {
        var sel = Selected();
        if (sel.Count == 0) { _sel.Clear(); return null; }
        var ms = sel.Sum(t => t.Ms);
        var move = Picker("→ Проєкт", "Primary", ProjectNames(), MoveSelected, "Перенести всі вибрані записи в проєкт");
        var text = T($"Вибрано {sel.Count} · {TimeUtil.FormatExact(ms)}", 13, weight: FontWeights.SemiBold);
        var bar = Cols("*,Auto,Auto", Col(1, text, T("Shift+клік — вибрати діапазон", 12, "MutedBrush")), move,
            Btn("Скасувати", "Quiet", () => { _sel.Clear(); _anchor = null; Render(); }).M(4, 0, 0, 0));
        var card = Card(bar, "TonalBrush", null, 10);
        card.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 16, ShadowDepth = 3, Opacity = 0.35, Color = Colors.Black };
        return card.M(0, 0, 0, 2);
    }

    void MoveSelected(string project)
    {
        var sel = Selected();
        UpdateDay(st => { foreach (var t in sel) st.Moved[t.Key] = project; });
        _sel.Clear();
        _anchor = null;
        OfferRule(sel, project);
        Snack($"Перенесено {Records(sel.Count)} в {project}", () => { _ruleHint = null; UpdateDay(st => { foreach (var t in sel) st.Moved.Remove(t.Key); }); });
        Render();
    }

    void MoveOne(TimelineItem t, string project)
    {
        UpdateDay(st => st.Moved[t.Key] = project);
        OfferRule(new List<TimelineItem> { t }, project);
        Snack($"Перенесено в {project}", () => { _ruleHint = null; UpdateDay(st => st.Moved.Remove(t.Key)); });
        Render();
    }

    void Ignore(string what, string shown)
    {
        var added = false;
        UpdateSettings(s => { if (!s.Ignore.Contains(what)) { s.Ignore.Add(what); added = true; } });
        if (added) Snack($"{shown} більше не записується", () => UpdateSettings(s => s.Ignore.Remove(what)));
    }

    /// <summary>
    /// Offer to make a move automatic. An address id shared by all moved visits (a Serpstat project, a GA4
    /// property, a Planfix task) is the most precise; otherwise a site name shared by most titles.
    /// </summary>
    void OfferRule(List<TimelineItem> moved, string project)
    {
        _ruleHint = null;
        var anchors = moved.Select(t => Classifier.UrlAnchor(t.Url)).Distinct().ToList();
        if (anchors.Count == 1 && anchors[0] is { } a)
        {
            var match = Classifier.UrlRulePrefix + a;
            if (!_settings.Rules.Any(r => string.Equals(r.Match, match, StringComparison.OrdinalIgnoreCase)))
                _ruleHint = (match, $"Адреса {Classifier.DescribeAnchor(a)}", project);
            return;
        }
        if (moved.Count < 2) return;
        var fragment = Classifier.CommonFragment(moved.Select(t => t.Label));
        if (fragment != null && !_settings.Rules.Any(r => string.Equals(r.Match, fragment, StringComparison.OrdinalIgnoreCase)))
            _ruleHint = (fragment, $"У заголовках було «{fragment}»", project);
    }

    UIElement? RuleHint()
    {
        if (_ruleHint is not { } h) return null;
        var text = T($"{h.Text}. Завжди відносити такі вікна до {h.Project}?", 12.5, wrap: true);
        var yes = Btn("Створити правило", "Primary", () =>
        {
            _ruleHint = null;
            var rule = new Rule { Match = h.Match, Project = h.Project };
            UpdateSettings(s => s.Rules.Add(rule));
            Snack($"Правило створено → {h.Project}", () => UpdateSettings(s => s.Rules.RemoveAll(r => r.Match == rule.Match && r.Project == rule.Project)));
        });
        var no = Btn("Ні", "Quiet", () => { _ruleHint = null; Render(); }).M(4, 0, 0, 0);
        return Card(Col(8, text, HRow(yes, no)), "CardHiBrush", "EdgeBrush", 12).M(0, 0, 0, 8);
    }

    /// <summary>
    /// Shift+click: tick every visit between the previous tick and this one, by time — short ones folded away
    /// included. "По програмах" keeps the range inside the program, "Хронологічно" spans all of Інше.
    /// </summary>
    void SelectRange(string anchorKey, TimelineItem to)
    {
        var from = _report.Timeline.FirstOrDefault(x => x.Key == anchorKey && !x.IsSession);
        if (from == null) return;
        var (a, b) = from.S <= to.S ? (from.S, to.S) : (to.S, from.S);
        var keys = _report.Timeline
            .Where(x => !x.IsSession && x.Moved == null && x.S >= a && x.S <= b && (_chrono || string.Equals(x.App, to.App, StringComparison.OrdinalIgnoreCase)))
            .Select(x => x.Key);
        _sel.UnionWith(keys);
    }

    /// <summary>Tick box of a visit for the bulk move.</summary>
    UIElement Tick(TimelineItem t)
    {
        if (t.Moved != null) return new Border { Width = 28 };
        var on = _sel.Contains(t.Key);
        var box = new Border
        {
            Width = 16, Height = 16, CornerRadius = new CornerRadius(4), VerticalAlignment = VerticalAlignment.Center,
            BorderBrush = B(on ? "LimeBrush" : "EdgeBrush"), BorderThickness = new Thickness(on ? 0 : 1.5),
            Background = on ? B("LimeBrush") : Brushes.Transparent,
            Child = on ? Glyph(G.Check, 10, "GreenBrush") : null, ToolTip = "Вибрати для перенесення",
        };
        var hit = new Border { Background = Brushes.Transparent, Padding = new Thickness(4, 8, 8, 8), Child = box };   // 28×32 target
        Clickable(hit, () =>
        {
            if ((System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Shift) != 0 && _anchor != null && _anchor != t.Key)
                SelectRange(_anchor, t);
            else if (!_sel.Add(t.Key)) _sel.Remove(t.Key);
            _anchor = t.Key;
            Render();
        }, "tick:" + t.Key, "Вибрати: " + t.Label);
        return hit;
    }

    /// <summary>The day in order: visits to Інше between stretches of project work.</summary>
    void Chronology(StackPanel list)
    {
        // Visits under a minute fold away; project stretches they split are glued back together.
        var visible = new List<TimelineItem>();
        var hidden = 0;
        foreach (var t in _report.Timeline)
        {
            if (!t.IsSession && t.Moved == null && t.Ms < TimeUtil.Min && !_showShort) { hidden++; continue; }
            if (t.IsSession && visible.Count > 0 && visible[^1] is { IsSession: true } last && last.Project == t.Project)
            {
                visible[^1] = new TimelineItem { IsSession = true, Project = last.Project, S = last.S, E = Math.Max(last.E, t.E) };
                continue;
            }
            visible.Add(t);
        }
        var drawn = 0;
        foreach (var t in visible)
        {
            if (drawn++ == _limit) { list.Children.Add(MoreButton()); break; }
            if (t.IsSession)
            {
                var name = HRow(new Ellipse { Width = 6, Height = 6, Fill = B("MintBrush"), VerticalAlignment = VerticalAlignment.Center },
                    T(t.Project ?? "", 12.5, "MintBrush", FontWeights.Medium).M(8, 0, 0, 0), T("  у проєкті", 12, "FaintBrush"));
                list.Children.Add(Cols("28,42,*,Auto", null, T(TimeUtil.Clock(t.S), 12, "FaintBrush"), name, T($"до {TimeUtil.Clock(t.E)}", 12, "FaintBrush")).M(0, 4, 0, 4));
            }
            else list.Children.Add(VisitRow(t, showApp: true));
        }
        if (hidden > 0 || _showShort)
            list.Children.Add(HRow(Btn(_showShort ? "сховати короткі" : $"ще {hidden} коротких (<1 хв)", "Quiet", () => { _showShort = !_showShort; Render(); })).M(0, 4, 0, 0));
    }

    UIElement MoreButton() => HRow(Btn("Показати ще", "Tonal", () => { _limit += 300; Render(); }, "Довгий список малюється частинами, щоб вікно не гальмувало")).M(0, 6, 0, 6);

    UIElement OtherAppEl(OtherApp a)
    {
        var open = _openApps.Contains(a.Name);
        var count = a.Visits.Count;
        var head = Cols("*,Auto,Auto,22",
            T(a.Name, 13, a.Ms > 0 ? "TextBrush" : "MutedBrush", FontWeights.Medium),
            T($"{count} {Plural(count, "запис", "записи", "записів")}", 12, "FaintBrush").M(8, 0, 10, 0),
            T(TimeUtil.FormatExact(a.Ms), 12.5, a.Ms > 0 ? "TextBrush" : "FaintBrush", FontWeights.SemiBold),
            Glyph(open ? G.Up : G.Down, 10, "FaintBrush"));
        var headBox = new Border { Background = Brushes.Transparent, Padding = new Thickness(0, 8, 0, 8), Child = head };
        Clickable(headBox, () => { if (!_openApps.Add(a.Name)) _openApps.Remove(a.Name); Render(); }, "app:" + a.Name, $"{a.Name}, {TimeUtil.FormatExact(a.Ms)}");

        var box = new StackPanel();
        box.Children.Add(headBox);
        if (open)
        {
            var hidden = 0;
            var list = new StackPanel { Margin = new Thickness(8, 0, 0, 6) };
            var drawn = 0;
            foreach (var t in a.Visits)
            {
                if (t.Moved == null && t.Ms < TimeUtil.Min && !_showShort) { hidden++; continue; }
                if (drawn++ == _limit) { list.Children.Add(MoreButton()); break; }
                list.Children.Add(VisitRow(t));
            }
            var foot = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
            var free = a.Visits.Where(t => t.Moved == null).Select(t => t.Key).ToList();
            if (free.Count > 1)
            {
                var all = free.All(_sel.Contains);
                foot.Children.Add(Btn(all ? "Зняти вибір" : $"Вибрати всі {free.Count}", "Tonal", () =>
                {
                    if (all) _sel.ExceptWith(free); else _sel.UnionWith(free);
                    Render();
                }, "Разом з короткими: потім «→ Проєкт» угорі"));
            }
            if (hidden > 0 || _showShort)
                foot.Children.Add(Btn(_showShort ? "сховати короткі" : $"ще {hidden} коротких (<1 хв)", "Quiet", () => { _showShort = !_showShort; Render(); }));
            if (!a.Browser)
            {
                var proc = a.Proc;
                foot.Children.Add(Btn($"Ігнорувати {a.Name}", "Quiet",
                    () => Ignore(proc, a.Name), $"Ніколи не записувати {a.Name}"));
            }
            if (foot.Children.Count > 0) list.Children.Add(foot);
            box.Children.Add(list);
        }
        return new Border { BorderBrush = B("LineBrush"), BorderThickness = new Thickness(0, 1, 0, 0), Child = box };
    }

    /// <summary>One compact line; a click on it shows "→ Проєкт" and "Ігнорувати".</summary>
    UIElement VisitRow(TimelineItem t, bool showApp = false)
    {
        var label = T(showApp ? $"{t.App} · {t.Label}" : t.Label, 12.5, t.Moved != null ? "MutedBrush" : "TextBrush");
        label.ToolTip = t.Label;
        var dur = t.Moved != null ? T($"→ {t.Moved}", 12, "MintBrush") : T(TimeUtil.FormatExact(t.Ms), 12, "MutedBrush");
        var line = Cols("42,*,Auto", T(TimeUtil.Clock(t.S), 12, "FaintBrush"), label, dur.M(8, 0, 0, 0));
        var lineBox = new Border { Background = Brushes.Transparent, Padding = new Thickness(0, 7, 0, 7), Child = line };   // 32 px rows
        Clickable(lineBox, () => { _openVisit = _openVisit == t.Key ? null : t.Key; Render(); }, "visit:" + t.Key, t.Label);

        var col = new StackPanel();
        col.Children.Add(Cols("Auto,*", Tick(t), lineBox));
        if (_openVisit == t.Key)
        {
            var acts = new WrapPanel { Margin = new Thickness(70, 2, 0, 6) };
            if (t.Moved != null)
                acts.Children.Add(Btn("скасувати перенесення", "Quiet", () => UpdateDay(st => st.Moved.Remove(t.Key))));
            else
            {
                acts.Children.Add(Picker("→ Проєкт", "Tonal", ProjectNames(), v => MoveOne(t, v)));
                if (t.Browser)
                    acts.Children.Add(Btn("Ігнорувати сторінку", "Quiet",
                        () => Ignore(t.Label, "Сторінка"),
                        $"Ніколи не записувати вкладки з «{t.Label}» у заголовку").M(6, 0, 0, 0));
            }
            col.Children.Add(acts);
        }
        return col;
    }
}
