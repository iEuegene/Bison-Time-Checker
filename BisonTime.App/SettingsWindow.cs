using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BisonTime.Core;
using static BisonTime.App.Ui;

namespace BisonTime.App;

/// <summary>Settings split into pages, like Bison Switcher: projects, rules, ignore, tracking, data.</summary>
internal sealed class SettingsWindow : Window
{
    static readonly (string Title, string Glyph)[] Tabs =
    {
        ("Проєкти", ""), ("Правила", ""), ("Ігнор", ""), ("Облік", ""), ("Дані", ""), ("Довідка", ""),
    };

    int _tab;
    readonly StackPanel _nav = new() { Margin = new Thickness(0, 22, 0, 0) };
    readonly StackPanel _page = new() { Margin = new Thickness(36, 30, 36, 30), MaxWidth = 780, HorizontalAlignment = HorizontalAlignment.Left };
    Settings _s = new();
    UpdatePanel? _update;

    public SettingsWindow()
    {
        Title = "Bison Time Checker — налаштування";
        Width = 1000;
        Height = 660;
        MinWidth = 820;
        MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = B("BgBrush");
        Foreground = B("TextBrush");
        FontFamily = TextFont;
        FontSize = 13;
        UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
        try { Icon = BitmapFrame.Create(new Uri("pack://application:,,,/Assets/bt-64.png")); } catch { /* cosmetic */ }

        var logo = new Image { Source = new BitmapImage(new Uri("pack://application:,,,/Assets/bt-64.png")), Width = 40, Height = 40 };
        var brand = HRow(logo, Col(0, T("Bison Time", 13, display: true, weight: FontWeights.Bold), T("Checker", 13, display: true, weight: FontWeights.Bold),
            T($"v{typeof(App).Assembly.GetName().Version?.ToString(3)}", 12, "MutedBrush")).M(10, 0, 0, 0));
        var note = T("Усе зберігається лише на цьому комп'ютері. В інтернет BT ходить лише по оновлення на GitHub і нічого не надсилає.", 12, "FaintBrush", wrap: true);
        var side = new DockPanel { Margin = new Thickness(14, 22, 14, 18), LastChildFill = true };
        DockPanel.SetDock(brand, Dock.Top);
        DockPanel.SetDock(note, Dock.Bottom);
        side.Children.Add(brand);
        side.Children.Add(note);
        side.Children.Add(_nav);
        var sideBox = new Border { Background = B("SideBrush"), BorderBrush = B("LineBrush"), BorderThickness = new Thickness(0, 0, 1, 0), Child = side };

        var scroll = new ScrollViewer { Content = _page, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Content = Cols("240,*", sideBox, scroll);
        Closed += (_, _) => _update?.Detach();
        Render();
    }

    public void Select(int tab)
    {
        _tab = Math.Clamp(tab, 0, Tabs.Length - 1);
        Render();
    }

    void Save(Action<Settings> change)
    {
        var s = App.Store.LoadSettings();
        change(s);
        App.Store.SaveSettings(s);
        Render();
    }

    void Render()
    {
        _s = App.Store.LoadSettings();
        _nav.Children.Clear();
        for (var i = 0; i < Tabs.Length; i++)
        {
            var idx = i;
            var on = i == _tab;
            var item = Cols("14,*", on ? Mark(18) : null, T(Tabs[i].Title, 14, on ? "TextBrush" : "MutedBrush", FontWeights.Medium).M(6, 0, 0, 0));
            var box = new Border
            {
                Background = on ? B("CardHiBrush") : Brushes.Transparent,
                BorderBrush = on ? new SolidColorBrush(Color.FromRgb(0x24, 0x53, 0x3F)) : Brushes.Transparent,
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(8, 9, 12, 9),
                Margin = new Thickness(0, 0, 0, 2), Child = item,
            };
            Clickable(box, () => { _tab = idx; Render(); });
            _nav.Children.Add(box);
        }

        _page.Children.Clear();
        switch (_tab)
        {
            case 0: ProjectsPage(); break;
            case 1: RulesPage(); break;
            case 2: IgnorePage(); break;
            case 3: TrackingPage(); break;
            case 4: DataPage(); break;
            default: HelpPage(); break;
        }
    }

    void H1(string title, string lead)
    {
        _page.Children.Add(T(title, 24, display: true, weight: FontWeights.Bold));
        _page.Children.Add(T(lead, 14, "MutedBrush", wrap: true).M(0, 6, 0, 20));
    }

    void H2(string title, string lead)
    {
        _page.Children.Add(T(title, 16, display: true, weight: FontWeights.Bold).M(0, 30, 0, 0));
        _page.Children.Add(T(lead, 13, "MutedBrush", wrap: true).M(0, 6, 0, 14));
    }

    static Border ListCard(IEnumerable<UIElement> items, string empty)
    {
        var p = new StackPanel();
        var first = true;
        foreach (var it in items)
        {
            p.Children.Add(new Border
            {
                BorderBrush = B("LineBrush"), BorderThickness = new Thickness(0, first ? 0 : 1, 0, 0),
                Padding = new Thickness(0, 10, 0, 10), Child = it,
            });
            first = false;
        }
        if (first) p.Children.Add(T(empty, 13, "MutedBrush", wrap: true).M(0, 12, 0, 12));
        return Card(p, pad: 16);
    }

    /// <summary>"Видалити" that asks "Точно?" first instead of a modal dialog.</summary>
    static Button Confirm(string label, Action action)
    {
        Button? b = null;
        var armed = false;
        b = Btn(label, "Quiet", () =>
        {
            if (armed) { action(); return; }
            armed = true;
            b!.Content = T("Точно?", 12, "OrangeBrush", FontWeights.SemiBold);
            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            t.Tick += (_, _) => { t.Stop(); armed = false; b.Content = T(label, 12, "MutedBrush"); };
            t.Start();
        });
        return b;
    }

    UIElement AddRow(string hint, string button, Action<string> add, FrameworkElement? extra = null)
    {
        var box = Input();
        void Go() { var v = box.Text.Trim(); if (v != "") add(v); }
        box.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) Go(); };
        return Cols("*,Auto,Auto", WithHint(box, hint), extra?.M(8, 0, 0, 0), Btn(button, "Primary", Go).M(8, 0, 0, 0)).M(0, 12, 0, 0);
    }

    // ---------------- pages ----------------

    void ProjectsPage()
    {
        H1("Проєкти", "Проєкт = домен клієнта, як у формі звіту. Нові проєкти з'являються самі: з назв документів «домен / документ / задача» " +
            "і з задач Planfix, що починаються з домену. Псевдоніми зливають кілька доменів в один проєкт: старий домен після міграції, мовні версії.");
        var names = _s.Projects.Keys.OrderBy(n => n == "Inweb").ThenBy(n => n, StringComparer.CurrentCultureIgnoreCase).ToList();
        var items = names.Select(n =>
        {
            var p = _s.Projects[n];
            var title = HRow(T(n, 13, weight: FontWeights.SemiBold),
                p.Internal ? TagChip("внутрішній", "LimeBrush", "GreenBrush") : null,
                p.Archived ? TagChip("архів", "FaintBrush", "BgBrush") : null);
            UIElement below;
            if (p.Internal) below = T($"Документи «{n} / …» ідуть сюди", 12, "MutedBrush");
            else
            {
                var aliases = Input(string.Join(", ", p.Aliases));
                aliases.LostFocus += (_, _) => Save(s =>
                    s.Projects[n].Aliases = aliases.Text.Split(',').Select(x => Classifier.NormalizeDomain(x) is { Length: > 0 } d ? d : x.Trim())
                        .Where(x => x != "").Distinct().ToList());
                below = WithHint(aliases, "псевдоніми через кому: old-domain.com.ua, site.lv");
            }
            return (UIElement)Cols("*,Auto,Auto",
                Col(6, title, below),
                Btn(p.Archived ? "Повернути" : "В архів", "Quiet", () => Save(s => s.Projects[n].Archived = !p.Archived)).M(10, 0, 0, 0),
                Confirm("Видалити", () => Save(s => { s.Projects.Remove(n); s.Rules.RemoveAll(r => r.Project == n); })));
        });
        _page.Children.Add(ListCard(items, "Ще немає проєктів."));
        _page.Children.Add(AddRow("домен клієнта (shop.com.ua) або назва внутрішнього проєкту", "Додати проєкт", v =>
        {
            var d = Classifier.NormalizeDomain(v);
            Save(s => s.Projects[d != "" ? d : v] = d != "" ? new ProjectCfg() : new ProjectCfg { Internal = true });
        }));
    }

    static Border TagChip(string text, string bg, string fg) => new()
    {
        Background = B(bg), CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 1, 6, 1), Margin = new Thickness(8, 0, 0, 0),
        Child = T(text, 11, fg, FontWeights.SemiBold), VerticalAlignment = VerticalAlignment.Center,
    };

    void RulesPage()
    {
        H1("Правила", "Коли домену клієнта немає в заголовку вікна. Фрагмент заголовка («Looker Studio», «Звіт Ahrefs») або назва програми " +
            "(figma, excel) → проєкт. Правило за заголовком перевіряється першим; правило на всю програму поступається домену клієнта в заголовку.");
        var items = _s.Rules.Select((r, i) => (UIElement)Cols("*,Auto",
            Col(2, T(r.Match, 13, weight: FontWeights.SemiBold), T($"→ {r.Project}", 12, "MutedBrush")),
            Btn("Видалити", "Quiet", () => Save(s => s.Rules.RemoveAt(i)))));
        _page.Children.Add(ListCard(items, "Правил немає."));

        var chosen = "";
        Button? pick = null;
        pick = Picker("Проєкт…", "Ghost", _s.Projects.Where(p => !p.Value.Archived).Select(p => p.Key).OrderBy(x => x), v =>
        {
            chosen = v;
            pick!.Content = new TextBlock { Text = v, FontFamily = TextFont };
        });
        _page.Children.Add(AddRow("фрагмент заголовка або програма (figma)", "Додати правило",
            v => { if (chosen != "") Save(s => s.Rules.Add(new Rule { Match = v, Project = chosen })); }, pick));

        H2("Таск-менеджери", "Фрагменти заголовка, за якими BT впізнає таск-менеджер. Задача з доменом клієнта в назві йде в цей проєкт; " +
            "сторінка без нього (планувальник, списки) — у проєкт, над яким ти працював у межах 15 хв до або після.");
        ListEditor(_s.WorkTools, "Planfix", "Список порожній.", (s, list) => s.WorkTools = list);
    }

    void IgnorePage()
    {
        H1("Ігнор", "Що BT не записує взагалі: програма (telegram, spotify) або фрагмент заголовка (YouTube, Rozetka). " +
            "Перевіряється за входженням, без урахування регістру.");
        ListEditor(_s.Ignore, "telegram або YouTube", "Список порожній.", (s, list) => s.Ignore = list);
    }

    void ListEditor(List<string> list, string hint, string empty, Action<Settings, List<string>> set)
    {
        var items = list.Select(x => (UIElement)Cols("*,Auto", T(x, 13, weight: FontWeights.Medium),
            Btn("Прибрати", "Quiet", () => Save(s => set(s, list.Where(y => y != x).ToList())))));
        _page.Children.Add(ListCard(items, empty));
        _page.Children.Add(AddRow(hint, "Додати", v => Save(s => set(s, list.Append(v).Distinct(StringComparer.OrdinalIgnoreCase).ToList()))));
    }

    void TrackingPage()
    {
        H1("Облік", "Коли BT ставить облік на паузу і як округлює підсумок проєкту.");
        var idle = Input(_s.IdleMin.ToString());
        idle.Width = 70;
        idle.TextAlignment = TextAlignment.Right;
        idle.LostFocus += (_, _) =>
        {
            if (int.TryParse(idle.Text, out var v)) Save(s => s.IdleMin = Math.Clamp(v, 1, 30));
            else idle.Text = _s.IdleMin.ToString();
        };
        var steps = HRow(new[] { 1, 5, 10, 15 }.Select(n =>
            (UIElement)Btn(n == 1 ? "до хвилини" : $"до {n} хв", _s.RoundStep == n ? "Primary" : "Ghost", () => Save(s => s.RoundStep = n)).M(0, 0, 6, 0)).ToArray());
        var auto = Toggle(_s.Autostart, on => { Save(s => s.Autostart = on); Autostart.Apply(on); });
        var upd = Toggle(_s.AutoUpdateCheck, on => Save(s => s.AutoUpdateCheck = on));

        UIElement Field(string title, string help, FrameworkElement control) =>
            Cols("*,Auto", Col(3, T(title, 14, weight: FontWeights.Medium), T(help, 12, "MutedBrush", wrap: true)), control.M(16, 0, 0, 0));
        _page.Children.Add(ListCard(new[]
        {
            Field("Простій, хв", "Без миші й клавіатури довше — облік на паузі, потім питання «додати чи викинути». Хвилини до цього моменту рахуються.", idle),
            Field("Округлення підсумку", "Лише підсумок проєкту. Кожен рядок рахується щонайменше як 1 хв.", steps),
            Field("Запускати з Windows", "BT стартує тихо в треї після входу в систему.", auto),
            Field("Перевіряти оновлення щодня", "Раз на добу BT питає GitHub, чи є нова версія, і показує її в треї. Оновлення ставиться лише після твого кліку.", upd),
            Field("Пауза", "Alt+Shift+P з будь-якої програми або кнопка у вікні BT. Іконка в треї стає помаранчевою.", T("Alt+Shift+P", 13, "LimeBrush", FontWeights.SemiBold)),
        }, ""));
    }

    void DataPage()
    {
        H1("Дані", "Усе лежить у папці BisonTime в AppData цього користувача: файл на день, налаштування, журнал. Сирі записи вікон зберігаються 7 днів, підсумки — 30.");
        var status = T("", 13, "LimeBrush", wrap: true);
        _page.Children.Add(HRow(
            Btn("Відкрити папку з даними", "Primary", () => App.OpenFolder(App.Store.Dir)),
            Btn("Копіювати журнал", "Ghost", () =>
            {
                var lines = App.Store.RecentLog().Select(x =>
                    string.Join(' ', new[] { TimeUtil.Local(x.T).ToString("HH:mm:ss"), x.Ev, x.Where, x.Why, x.Sec is { } s ? $"{s}s" : null }.Where(y => !string.IsNullOrEmpty(y))));
                try { Clipboard.SetText(string.Join(Environment.NewLine, lines)); status.Text = "Журнал скопійовано: надішли його розробнику, якщо час рахується дивно."; }
                catch { status.Text = "Буфер обміну зайнятий, спробуй ще раз."; }
            }, "Останні 400 подій трекера: програма й початок заголовка, без повних назв").M(8, 0, 0, 0)));
        _page.Children.Add(status.M(0, 10, 0, 0));
        H2("Очистити історію", "Видаляє всі записи часу, коментарі та позначки. Проєкти, правила й ігнор лишаються.");
        _page.Children.Add(Confirm("Очистити історію", () =>
        {
            App.Store.ClearHistory();
            App.Tracker.Reset();
            status.Text = "Історію очищено.";
        }));
    }

    static void Open(string url)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); } catch { /* no browser */ }
    }

    void HelpPage()
    {
        var version = typeof(App).Assembly.GetName().Version?.ToString(3) ?? "";
        H1("Довідка", HelpContent.Made);
        // The panel survives re-renders (a download keeps its progress): unhook it from the old card.
        if (_update?.Parent is Border old) old.Child = null;

        // About: version, copyright, links
        _page.Children.Add(ListCard(new UIElement[]
        {
            Cols("*,Auto", Col(3, T($"Bison Time Checker {version}", 14, weight: FontWeights.SemiBold), T(HelpContent.Copyright + " · ліцензія MIT", 12, "MutedBrush")),
                HRow(Btn("GitHub", "Ghost", () => Open(HelpContent.Repo), HelpContent.Repo),
                     Btn("Релізи", "Ghost", () => Open(HelpContent.Releases), "Усі версії на GitHub").M(6, 0, 0, 0),
                     Btn("Повідомити про проблему", "Ghost", () => Open(HelpContent.Issues)).M(6, 0, 0, 0))),
            _update ??= new UpdatePanel(),
        }, ""));

        foreach (var (title, lines) in HelpContent.Guide)
        {
            _page.Children.Add(T(title, 16, display: true, weight: FontWeights.Bold).M(0, 28, 0, 8));
            foreach (var line in lines)
            {
                if (line.StartsWith("• "))
                    _page.Children.Add(Cols("18,*", T("•", 13, "LimeBrush"), T(line[2..], 13, wrap: true)).M(0, 0, 0, 6));
                else
                    _page.Children.Add(T(line, 13, wrap: true).M(0, 0, 0, 8));
            }
        }

        _page.Children.Add(T("Що нового", 16, display: true, weight: FontWeights.Bold).M(0, 28, 0, 8));
        _page.Children.Add(ListCard(HelpContent.Changes.Select(c =>
            (UIElement)Cols("70,*", T(c.Version, 13, "LimeBrush", FontWeights.SemiBold), T(c.Text, 13, wrap: true))), ""));

        _page.Children.Add(T("Правова інформація", 16, display: true, weight: FontWeights.Bold).M(0, 28, 0, 8));
        foreach (var line in HelpContent.Legal) _page.Children.Add(T(line, 12, "MutedBrush", wrap: true).M(0, 0, 0, 8));
    }
}
