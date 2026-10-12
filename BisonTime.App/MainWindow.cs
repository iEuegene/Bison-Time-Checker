using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BisonTime.Core;
using static BisonTime.App.Ui;

namespace BisonTime.App;

/// <summary>The tray flyout: today's projects, pause, idle prompts, "Інше". Rebuilt from data on every change.</summary>
internal sealed class MainWindow : Window
{
    string _day = TimeUtil.DayKey(Now);
    readonly HashSet<string> _open = new();
    bool _otherOpen, _showShort;
    readonly HashSet<string> _openApps = new(StringComparer.OrdinalIgnoreCase);
    string? _openVisit;
    readonly StackPanel _body = new() { Margin = new Thickness(10, 10, 10, 14) };
    readonly ScrollViewer _scroll;
    readonly TextBlock _dayLabel, _total;
    readonly Button _prev, _next;
    readonly DispatcherTimer _refresh;
    DispatcherTimer? _commentSave;

    Settings _settings = new();
    DayFile _file = new();
    DayReport _report = new();
    string? _current;
    Dictionary<string, long> _off = new();

    public DateTime HiddenAt { get; private set; } = DateTime.MinValue;
    static long Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    string Today => TimeUtil.DayKey(Now);

    public MainWindow()
    {
        Title = "Bison Time Checker";
        Width = 420;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        Background = B("BgBrush");
        BorderBrush = B("LineBrush");
        BorderThickness = new Thickness(1);
        FontFamily = TextFont;
        UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);

        // ---- header: logo, day switcher, total for the report, settings ----
        var logo = new Image
        {
            Source = new BitmapImage(new Uri("pack://application:,,,/Assets/bt-64.png")),
            Width = 28, Height = 28, VerticalAlignment = VerticalAlignment.Center,
        };
        _prev = Btn(Glyph("", 12), "Quiet", () => Go(-1), "Попередній день");
        _next = Btn(Glyph("", 12), "Quiet", () => Go(1), "Наступний день");
        _dayLabel = T("Сьогодні", 13, "MutedBrush", FontWeights.Medium);
        _dayLabel.MinWidth = 74;
        _dayLabel.TextAlignment = TextAlignment.Center;
        _total = T("0 год 0 хв", 15, display: true, weight: FontWeights.Bold);
        _total.HorizontalAlignment = HorizontalAlignment.Right;
        var totalBox = Col(0, _total, T("до звіту", 11, "MutedBrush"));
        ((TextBlock)totalBox.Children[1]).HorizontalAlignment = HorizontalAlignment.Right;
        var gear = Btn(Glyph("", 15), "Quiet", () => App.Instance.ShowSettings(), "Налаштування");
        var header = new Border
        {
            Background = B("SideBrush"),
            BorderBrush = B("LineBrush"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(14, 10, 8, 10),
            Child = Cols("Auto,Auto,*,Auto", logo, HRow(_prev, _dayLabel, _next).M(8, 0, 0, 0), totalBox.M(0, 0, 4, 0), gear),
        };
        // the header doubles as a drag handle
        header.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 1) try { DragMove(); } catch { /* not pressed */ } };

        _scroll = new ScrollViewer
        {
            Content = _body,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(_scroll, 1);
        root.Children.Add(header);
        root.Children.Add(_scroll);
        Content = root;

        Deactivated += (_, _) => { HiddenAt = DateTime.UtcNow; Hide(); };
        KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) Hide(); };

        _refresh = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _refresh.Tick += (_, _) => { if (IsVisible && !EditingText) Refresh(); };
        App.Tracker.Changed += () => Dispatcher.BeginInvoke(() => { if (IsVisible && !EditingText) Refresh(); });
    }

    bool EditingText => System.Windows.Input.Keyboard.FocusedElement is TextBox;

    public void ShowNearTray()
    {
        _day = Today;
        var area = SystemParameters.WorkArea;
        Height = Math.Min(680, area.Height - 24);
        Left = area.Right - Width - 12;
        Top = area.Bottom - Height - 12;
        if (_current == null && App.Tracker.Open is { } o)
            _current = Classifier.Classify(new WindowInfo(o.Proc, o.Title), App.Store.LoadSettings()).Project;
        Refresh();
        if (_current != null) _open.Add(_current);
        Render();
        Show();
        Activate();
        _refresh.Start();
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
        _settings = App.Store.LoadSettings();
        _file = App.Store.LoadDay(_day);
        _report = Report.Build(App.Tracker.DaySegments(_day), _file.State, _settings);
        _current = null;
        if (_day == Today && App.Tracker.Open is { } o)
            _current = Classifier.Classify(new WindowInfo(o.Proc, o.Title), _settings).Project;
        _off = App.Tracker.DayOff(_day);
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

    // ---------------- render ----------------

    string DayLabel()
    {
        if (_day == Today) return "Сьогодні";
        if (_day == TimeUtil.ShiftDay(Today, -1)) return "Вчора";
        var d = DateTime.ParseExact(_day, "yyyy-MM-dd", null);
        return d.ToString("ddd, d MMM", new System.Globalization.CultureInfo("uk-UA"));
    }

    void Render()
    {
        _dayLabel.Text = DayLabel();
        _total.Text = TimeUtil.FormatReport(_report.DayRounded);
        _next.IsEnabled = string.CompareOrdinal(_day, Today) < 0;
        _prev.IsEnabled = string.CompareOrdinal(_day, TimeUtil.ShiftDay(Today, -(Tracker.KeepSegmentsDays - 1))) > 0;

        var offset = _scroll.VerticalOffset;
        _body.Children.Clear();
        void Add(UIElement? e) { if (e is FrameworkElement fe) { fe.Margin = new Thickness(0, 0, 0, 8); _body.Children.Add(fe); } }

        Add(PauseBar());
        Add(UpdateBar());
        foreach (var it in _file.State.Idle.ToList()) Add(IdleBanner(it));
        foreach (var p in _report.Projects) Add(ProjectCard(p));
        if (_report.Projects.Count == 0)
            Add(Card(Col(6,
                T("Поки порожньо", 14, display: true, weight: FontWeights.Bold),
                T("Відкрий документ «домен / документ / задача» або вкладку з доменом клієнта в заголовку — BT підхопить проєкт сам.", 12, "MutedBrush", wrap: true)), pad: 16));
        Add(OffLine());
        Add(Other());
        _scroll.ScrollToVerticalOffset(offset);
    }

    UIElement? PauseBar()
    {
        if (_day != Today) return null;
        if (App.Tracker.PausedSince is { } since)
        {
            var min = (Now - since) / TimeUtil.Min;
            var text = Col(2,
                T("На паузі", 14, "GreenBrush", FontWeights.Bold, display: true),
                T($"з {TimeUtil.Clock(since)} · {TimeUtil.FormatExact(min * TimeUtil.Min)} · час не рахується", 12, "GreenBrush"));
            var btn = Btn("▶  Продовжити", "Big", () => App.Instance.TogglePause(), "Alt+Shift+P");
            return Card(Cols("*,Auto", text, btn), "OrangeBrush", "OrangeBrush", 12);
        }
        var dot = new System.Windows.Shapes.Ellipse { Width = 8, Height = 8, Fill = B("LimeBrush"), VerticalAlignment = VerticalAlignment.Center };
        var label = T(_current != null ? $"Облік іде · {_current}" : "Облік іде", 13, "MutedBrush");
        var pause = Btn("❚❚  Пауза", "Ghost", () => App.Instance.TogglePause(), "Пауза (Alt+Shift+P)");
        pause.Padding = new Thickness(14, 8, 14, 8);
        return Card(Cols("Auto,*,Auto", dot, label.M(10, 0, 8, 0), pause), "SideBrush", "LineBrush", 8);
    }

    UIElement? UpdateBar()
    {
        if (_day != Today || App.Instance.Update is not { } u) return null;
        var text = Col(2,
            T($"Нова версія {u.Manifest.Version}", 13, weight: FontWeights.SemiBold),
            T($"{u.Changed.Count} файлів · {Updater.Size(u.Bytes)} · дані не зачіпаються", 12, "MutedBrush"));
        var btn = Btn("Оновити", "Primary", () => { Hide(); App.Instance.ShowSettings(5); });
        return Card(Cols("*,Auto", text, btn.M(10, 0, 0, 0)), "CardBrush", "LineBrush", 10);
    }

    UIElement IdleBanner(IdleItem it)
    {
        var min = (int)Math.Round((it.E - it.S) / (double)TimeUtil.Min);
        var msg = new TextBlock { FontFamily = TextFont, FontSize = 13, Foreground = B("TextBrush"), VerticalAlignment = VerticalAlignment.Center };
        msg.Inlines.Add(new System.Windows.Documents.Run($"{min} хв простою") { FontWeight = FontWeights.SemiBold });
        msg.Inlines.Add(new System.Windows.Documents.Run($" о {TimeUtil.Clock(it.S)}"));
        void AddTo(string project) => UpdateDay(st =>
        {
            st.Idle.RemoveAll(x => x.Id == it.Id);
            st.Manual.Add(new ManualItem { Id = "m" + it.Id, Project = project, Min = min, Label = $"Простій {TimeUtil.Clock(it.S)}–{TimeUtil.Clock(it.E)}" });
        });
        UIElement add = it.Project != null && _settings.Projects.ContainsKey(it.Project)
            ? Btn($"Додати до {it.Project}", "Primary", () => AddTo(it.Project!))
            : Picker("Додати до…", "Primary", ProjectNames(), AddTo);
        var drop = Btn("Викинути", "Quiet", () => UpdateDay(st => st.Idle.RemoveAll(x => x.Id == it.Id)));
        return Card(Col(8, msg, HRow(add, drop.M(6, 0, 0, 0))), "CardBrush", "OrangeBrush", 10);
    }

    UIElement ProjectCard(ProjectReport p)
    {
        var open = _open.Contains(p.Name);
        var head = new Grid { Background = Brushes.Transparent };
        var cols = Cols("10,*,Auto,Auto,Auto,22",
            p.Name == _current ? Mark() : null,
            T(p.Name, 13, weight: FontWeights.Medium).M(4, 0, 8, 0),
            p.Done.Marked ? Chip("внесено", "MutedBrush") : null,
            p.Done.Stale ? Chip($"змінилось {(p.Done.Diff > 0 ? "+" : "")}{p.Done.Diff} хв", "OrangeBrush") : null,
            Plate(TimeUtil.FormatReport(p.Rounded), p.Rounded == 0 ? "FaintBrush" : "LimeBrush", p.Rounded == 0 ? "BgBrush" : "GreenBrush").M(6, 0, 4, 0),
            Glyph(open ? "" : "", 11));
        head.Children.Add(cols);
        head.Margin = new Thickness(0, 2, 0, 2);
        Clickable(head, () => { if (!_open.Add(p.Name)) _open.Remove(p.Name); Render(); });

        var content = new StackPanel();
        content.Children.Add(head);
        if (open)
        {
            foreach (var g in p.Groups)
            {
                content.Children.Add(T(g.Title.ToUpperInvariant(), 10.5, "MutedBrush", FontWeights.SemiBold).M(4, 10, 0, 2));
                var first = true;
                foreach (var r in g.Rows)
                {
                    content.Children.Add(RowEl(r, first));
                    first = false;
                }
            }
            var plus = HRow(
                Btn("+5 хв", "Ghost", () => AddManual(p.Name, 5)),
                Btn("+15 хв", "Ghost", () => AddManual(p.Name, 15)).M(6, 0, 0, 0));
            content.Children.Add(Cols("*,Auto", T($"До округлення: {TimeUtil.FormatExact(p.ExactMs)}", 12, "MutedBrush"), plus).M(4, 10, 0, 0));
            content.Children.Add(CommentBox(p).M(4, 12, 0, 0));
        }
        var card = Card(content, open ? "CardHiBrush" : "CardBrush", null, 8);
        if (p.Rounded == 0 || p.Done.Marked) card.Opacity = open ? 1 : 0.62;
        return card;
    }

    static Border Chip(string text, string brush) => new()
    {
        BorderBrush = B(brush), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6),
        Padding = new Thickness(6, 1, 6, 1), Margin = new Thickness(4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
        Child = T(text, 11, brush),
    };

    void AddManual(string project, int min) => UpdateDay(st =>
        st.Manual.Add(new ManualItem { Id = $"m{Now}", Project = project, Min = min, Label = $"Вручну +{min} хв" }));

    UIElement RowEl(Row r, bool first)
    {
        var label = T(r.Label, 13, r.Disabled ? "FaintBrush" : "TextBrush");
        if (r.Disabled) label.TextDecorations = TextDecorations.Strikethrough;
        var sub = r.Task != "" ? r.Task : (r.Group is "apps" or "linked" or "ctx" ? r.App : "");
        var what = sub == "" ? (UIElement)label : Col(1, label, T(sub, 11.5, "MutedBrush"));
        var dur = T(TimeUtil.FormatExact(r.Ms), 12, r.Disabled ? "FaintBrush" : "MutedBrush");
        var key = r.Key;
        var off = r.Disabled;
        var sw = Toggle(!off, on => UpdateDay(st => { if (on) st.Disabled.Remove(key); else st.Disabled[key] = true; }),
            off ? "Не враховується" : "Враховується");
        var grid = Cols("*,Auto,Auto", what, dur.M(8, 0, 10, 0), sw);
        return new Border
        {
            BorderBrush = B("LineBrush"), BorderThickness = new Thickness(0, first ? 0 : 1, 0, 0),
            Padding = new Thickness(4, 5, 0, 5), Child = grid,
        };
    }

    FrameworkElement CommentBox(ProjectReport p)
    {
        var c = p.Comment;
        var box = Input(c.Text, multiline: true);
        box.MinHeight = 54;
        box.MaxHeight = 140;
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
            ? HRow(T("є нове — ", 12, "OrangeBrush"), Btn("оновити", "Quiet", () => UpdateDay(st => { st.Comments.Remove(name); st.CommentBase.Remove(name); })))
            : T(c.Edited ? "відредаговано" : "чернетка", 12, "MutedBrush");
        var copy = Btn("Копіювати", "Ghost", () =>
        {
            try { Clipboard.SetText(box.Text ?? ""); } catch { /* clipboard busy */ }
        });
        var done = p.Done.Marked;
        var mark = Btn(done ? "Внесено ✓" : "Внесено", done ? "Ghost" : "Primary", () => UpdateDay(st =>
        {
            if (done) st.Done.Remove(name); else st.Done[name] = p.Rounded;
        }), "Позначити, що час уже перенесено у звіт");
        return Col(6, WithHint(box, "Що зроблено — допиши, що BT не бачить"), Cols("*,Auto,Auto", hint, copy, mark.M(6, 0, 0, 0)));
    }

    UIElement? OffLine()
    {
        var parts = _off.Where(x => x.Value >= TimeUtil.Min).OrderByDescending(x => x.Value)
            .Select(x => $"{(OffReasons.Names.TryGetValue(x.Key, out var n) ? n : x.Key)} {TimeUtil.FormatExact(x.Value)}").ToList();
        if (parts.Count == 0) return null;
        var t = T("Поза обліком: " + string.Join(" · ", parts), 12, "FaintBrush", wrap: true);
        t.ToolTip = "Час, коли BT нічого не рахував, і чому";
        return t.M(4, 0, 0, 0);
    }

    UIElement? Other()
    {
        if (_report.OtherApps.Count == 0) return null;
        var head = Cols("*,Auto,22", T("Інше", 13, "FaintBrush"), T(TimeUtil.FormatExact(_report.OtherMs), 12, "FaintBrush"),
            Glyph(_otherOpen ? "\uE70E" : "\uE70D", 11, "FaintBrush"));
        var headBox = new Border { Background = Brushes.Transparent, Child = head };
        Clickable(headBox, () => { _otherOpen = !_otherOpen; Render(); });
        var panel = new StackPanel();
        panel.Children.Add(headBox);
        if (_otherOpen)
        {
            panel.Children.Add(T("По програмах, найбільше часу зверху. У звіт не йде, поки не перенесеш у проєкт.", 11.5, "FaintBrush", wrap: true).M(0, 2, 0, 6));
            foreach (var a in _report.OtherApps) panel.Children.Add(OtherAppEl(a));
        }
        return Card(panel, "BgBrush", "LineBrush", 10);
    }

    UIElement OtherAppEl(OtherApp a)
    {
        var open = _openApps.Contains(a.Name);
        var count = a.Visits.Count;
        var head = Cols("*,Auto,Auto,22",
            T(a.Name, 13, a.Ms > 0 ? "TextBrush" : "MutedBrush", FontWeights.Medium),
            T($"{count} {Plural(count, "запис", "записи", "записів")}", 11.5, "FaintBrush").M(8, 0, 10, 0),
            T(TimeUtil.FormatExact(a.Ms), 12.5, a.Ms > 0 ? "LimeBrush" : "FaintBrush", FontWeights.SemiBold),
            Glyph(open ? "\uE70E" : "\uE70D", 10, "FaintBrush"));
        var headBox = new Border { Background = Brushes.Transparent, Padding = new Thickness(0, 7, 0, 7), Child = head };
        Clickable(headBox, () => { if (!_openApps.Add(a.Name)) _openApps.Remove(a.Name); Render(); });

        var box = new StackPanel();
        box.Children.Add(headBox);
        if (open)
        {
            var hidden = 0;
            var list = new StackPanel { Margin = new Thickness(10, 0, 0, 6) };
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

    static string Plural(int n, string one, string few, string many)
    {
        var d = n % 10;
        var dd = n % 100;
        return d == 1 && dd != 11 ? one : d is >= 2 and <= 4 && dd is < 12 or > 14 ? few : many;
    }

    /// <summary>One compact line; a click on it shows "→ Проєкт" and "Ігнорувати".</summary>
    UIElement VisitRow(TimelineItem t)
    {
        var label = T(t.Label, 12.5, t.Moved != null ? "MutedBrush" : "TextBrush");
        label.TextTrimming = TextTrimming.CharacterEllipsis;
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
                acts.Children.Add(Picker("→ Проєкт", "Ghost", ProjectNames(), v => UpdateDay(st => st.Moved[t.Key] = v)));
                if (t.Browser)
                    acts.Children.Add(Btn("Ігнорувати сторінку", "Quiet",
                        () => UpdateSettings(s => { if (!s.Ignore.Contains(t.Label)) s.Ignore.Add(t.Label); }),
                        $"Ніколи не записувати вкладки з «{t.Label}» у заголовку").M(6, 0, 0, 0));
            }
            col.Children.Add(acts);
        }
        return col;
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // The flyout lives as long as the app: closing just hides it.
        e.Cancel = true;
        Hide();
    }
}
