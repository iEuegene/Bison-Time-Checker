using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BisonTime.Core;
using static BisonTime.App.Ui;

namespace BisonTime.App;

/// <summary>Pages of the main window, in rail order.</summary>
public enum AppPage { Today, Dashboard, Projects, Rules, Tracking, Help, About }

/// <summary>
/// The main window (variant A, Material 3): a navigation rail on the left, one page on the right.
/// Today and the dashboard live here next to the settings; the tray flyout stays for a quick look.
/// </summary>
internal sealed class AppWindow : Window
{
    static readonly (AppPage Page, string Title, string Glyph)[] Rail =
    {
        (AppPage.Today, "Сьогодні", G.Today), (AppPage.Dashboard, "Дашборд", G.Dashboard), (AppPage.Projects, "Проєкти", G.Projects),
        (AppPage.Rules, "Правила", G.Rules), (AppPage.Tracking, "Облік", G.Tracking),
    };
    static readonly (AppPage Page, string Title, string Glyph)[] RailBottom =
    {
        (AppPage.Help, "Довідка", G.Help), (AppPage.About, "Про BT", G.Info),
    };

    AppPage _tab = AppPage.Today;
    readonly StackPanel _nav = new();
    readonly StackPanel _navBottom = new();
    readonly StackPanel _page = new() { Margin = new Thickness(36, 30, 36, 36), MaxWidth = 820, HorizontalAlignment = HorizontalAlignment.Left };
    readonly ScrollViewer _scroll;
    readonly ContentControl _host = new();
    DayView? _today;
    DashboardView? _dashboard;
    Settings _s = new();
    UpdatePanel? _update;

    public AppWindow()
    {
        Title = "Bison Time Checker";
        // Fit the work area: a 1080p laptop at 150% leaves ~1280×680 above the taskbar.
        var area = SystemParameters.WorkArea;
        Width = Math.Min(1120, area.Width - 40);
        Height = Math.Min(780, area.Height - 40);
        MinWidth = Math.Min(900, area.Width - 40);
        MinHeight = Math.Min(560, area.Height - 40);
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = B("BgBrush");
        Foreground = B("TextBrush");
        FontFamily = TextFont;
        FontSize = 13;
        UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
        try { Icon = BitmapFrame.Create(new Uri("pack://application:,,,/Assets/bt-64.png")); } catch { /* cosmetic */ }

        var logo = Plate("BT").M(0, 0, 0, 22);
        logo.HorizontalAlignment = HorizontalAlignment.Center;
        var rail = new DockPanel { Margin = new Thickness(0, 20, 0, 16), LastChildFill = true };
        DockPanel.SetDock(logo, Dock.Top);
        DockPanel.SetDock(_navBottom, Dock.Bottom);
        rail.Children.Add(logo);
        rail.Children.Add(_navBottom);
        rail.Children.Add(_nav);
        var railBox = new Border { Background = B("SideBrush"), BorderBrush = B("LineBrush"), BorderThickness = new Thickness(0, 0, 1, 0), Child = rail };

        _scroll = new ScrollViewer { Content = _page, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Content = Cols("96,*", railBox, _host);
        App.Instance.UpdateChanged += OnUpdateChanged;
        Closed += (_, _) => { _update?.Detach(); App.Instance.UpdateChanged -= OnUpdateChanged; };
        // Ctrl+1…7 jumps between rail pages, in rail order.
        PreviewKeyDown += (_, e) =>
        {
            if (System.Windows.Input.Keyboard.Modifiers != System.Windows.Input.ModifierKeys.Control) return;
            var n = e.Key - System.Windows.Input.Key.D1;
            var all = Rail.Concat(RailBottom).ToArray();
            if (n >= 0 && n < all.Length) { _tab = all[n].Page; Render(); e.Handled = true; }
        };
        Render();
    }

    void OnUpdateChanged() => Dispatcher.BeginInvoke(RenderRail);

    public void Select(AppPage page)
    {
        _tab = page;
        Render();
    }

    /// <summary>Open a given day on the "Сьогодні" page (from a click in the dashboard).</summary>
    public void ShowDay(string day)
    {
        _tab = AppPage.Today;
        Render();
        _today!.Open(day);
    }

    void Save(Action<Settings> change)
    {
        var s = App.Store.LoadSettings();
        change(s);
        App.Store.SaveSettings(s);
        Render();
    }

    /// <summary>M3 navigation rail item: icon in a pill (lime when active) with the label under it.</summary>
    UIElement RailItem((AppPage Page, string Title, string Glyph) it, bool badge)
    {
        var on = it.Page == _tab;
        var pill = new Grid { Width = 56, Height = 32 };
        pill.Children.Add(new Border { CornerRadius = new CornerRadius(16), Background = on ? B("LimeBrush") : Brushes.Transparent });
        pill.Children.Add(Glyph(it.Glyph, 18, on ? "GreenBrush" : "MutedBrush"));
        if (badge)
            pill.Children.Add(new System.Windows.Shapes.Ellipse
            {
                Width = 8, Height = 8, Fill = B("OrangeBrush"), HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 3, 12, 0),
            });
        var label = T(it.Title, 12, on ? "LimeBrush" : "MutedBrush", on ? FontWeights.SemiBold : FontWeights.Medium);
        label.HorizontalAlignment = HorizontalAlignment.Center;
        var box = new StackPanel { Background = Brushes.Transparent, Margin = new Thickness(0, 4, 0, 10) };
        box.Children.Add(pill);
        box.Children.Add(label.M(0, 4, 0, 0));
        if (!on)
        {
            box.MouseEnter += (_, _) => ((Border)pill.Children[0]).Background = B("CardHiBrush");
            box.MouseLeave += (_, _) => ((Border)pill.Children[0]).Background = Brushes.Transparent;
        }
        var index = Array.FindIndex(Rail.Concat(RailBottom).ToArray(), x => x.Page == it.Page) + 1;
        box.ToolTip = $"{it.Title} (Ctrl+{index})";
        Clickable(box, () => { _tab = it.Page; Render(); }, "rail:" + it.Page, it.Title);
        return box;
    }

    void RenderRail()
    {
        _nav.Children.Clear();
        foreach (var it in Rail) _nav.Children.Add(RailItem(it, false));
        _navBottom.Children.Clear();
        foreach (var it in RailBottom) _navBottom.Children.Add(RailItem(it, it.Page == AppPage.About && App.Instance.Update != null));
    }

    void Render()
    {
        var focused = FocusedId();
        if (focused != null)
            Dispatcher.BeginInvoke(() => RestoreFocus(this, focused), System.Windows.Threading.DispatcherPriority.Loaded);
        _s = App.Store.LoadSettings();
        RenderRail();
        Title = "Bison Time Checker — " + Rail.Concat(RailBottom).First(x => x.Page == _tab).Title;
        switch (_tab)
        {
            case AppPage.Today:
                if (_today == null) { _today = new DayView(flyout: false); _today.Open(); }
                _host.Content = _today;
                return;
            case AppPage.Dashboard:
                _dashboard ??= new DashboardView(day => ShowDay(day));
                _dashboard.Render();
                _host.Content = _dashboard;
                return;
        }
        _host.Content = _scroll;
        _page.Children.Clear();
        switch (_tab)
        {
            case AppPage.Projects: ProjectsPage(); break;
            case AppPage.Rules: RulesPage(); break;
            case AppPage.Tracking: TrackingPage(); break;
            case AppPage.Help: HelpPage(); break;
            default: AboutPage(); break;
        }
        _scroll.ScrollToTop();
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
    /// <summary>Two-step button for the irreversible: the second press names what goes ("Так, видалити shop.com.ua").</summary>
    static Button Confirm(string label, Action action, string? armedText = null)
    {
        Button? b = null;
        var armed = false;
        b = Btn(label, "Quiet", () =>
        {
            if (armed) { action(); return; }
            armed = true;
            b!.Content = T(armedText ?? "Точно?", 12.5, "OrangeBrush", FontWeights.SemiBold);
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
                Confirm("Видалити", () => Save(s => { s.Projects.Remove(n); s.Rules.RemoveAll(r => r.Project == n); }), $"Так, видалити {n}"));
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
        Child = T(text, 12, fg, FontWeights.SemiBold), VerticalAlignment = VerticalAlignment.Center,
    };

    void RulesPage()
    {
        H1("Правила", "Коли домену клієнта немає в заголовку вікна. Фрагмент заголовка («Looker Studio», «Звіт Ahrefs») або назва програми " +
            "(figma, excel) → проєкт. Правило за заголовком перевіряється першим; правило на всю програму поступається домену клієнта в заголовку.");
        var items = _s.Rules.Select((r, i) => (UIElement)Cols("*,Auto",
            Col(2, T(r.Match.StartsWith(Classifier.UrlRulePrefix) ? "Адреса: " + Classifier.DescribeAnchor(r.Match[Classifier.UrlRulePrefix.Length..]) : r.Match,
                13, weight: FontWeights.SemiBold), T($"→ {r.Project}", 12, "MutedBrush")),
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

        H2("Ігнор", "Що BT не записує взагалі: програма (telegram, spotify) або фрагмент заголовка (YouTube, Rozetka). " +
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
        H1("Облік", "Коли BT ставить облік на паузу, як округлює підсумок і де лежать дані.");
        // Stepper instead of a text box: the number reads at a glance and cannot be typed wrong.
        var minus = IconBtn("\uE738", () => Save(s => s.IdleMin = Math.Max(1, s.IdleMin - 1)), "Менше на 1 хв", 13);
        var plus = IconBtn(G.Add, () => Save(s => s.IdleMin = Math.Min(30, s.IdleMin + 1)), "Більше на 1 хв", 13);
        minus.IsEnabled = _s.IdleMin > 1;
        plus.IsEnabled = _s.IdleMin < 30;
        var value = T($"{_s.IdleMin} хв", 15, weight: FontWeights.SemiBold, ellipsis: false);
        value.MinWidth = 52;
        value.TextAlignment = TextAlignment.Center;
        var idle = new Border
        {
            BorderBrush = B("EdgeBrush"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(20),
            Padding = new Thickness(2), VerticalAlignment = VerticalAlignment.Center, Child = HRow(minus, value, plus),
        };
        var steps = HRow(new[] { 1, 5, 10, 15 }.Select(n =>
            (UIElement)Btn(n == 1 ? "до хвилини" : $"до {n} хв", _s.RoundStep == n ? "Primary" : "Ghost", () => Save(s => s.RoundStep = n)).M(0, 0, 6, 0)).ToArray());
        var auto = Toggle(_s.Autostart, on => { Save(s => s.Autostart = on); Autostart.Apply(on); });
        var upd = Toggle(_s.AutoUpdateCheck, on => Save(s => s.AutoUpdateCheck = on));
        var urls = Toggle(_s.ReadUrls, on => Save(s => s.ReadUrls = on));

        UIElement Field(string title, string help, FrameworkElement control)
        {
            control.VerticalAlignment = VerticalAlignment.Center;   // never stretch to the height of a long description
            return Cols("*,Auto", Col(3, T(title, 14, weight: FontWeights.Medium), T(help, 12, "MutedBrush", wrap: true)), control.M(16, 0, 0, 0));
        }
        _page.Children.Add(ListCard(new[]
        {
            Field("Простій", "Без миші й клавіатури довше — облік на паузі, потім питання «додати чи викинути». Хвилини до цього моменту рахуються. Під час дзвінка (Meet, Zoom, Teams) простій не настає.", idle),
            Field("Округлення підсумку", "Лише підсумок проєкту. Проєкт за день — щонайменше 1 хв, рядки — скільки зайняли.", steps),
            Field("Запускати з Windows", "BT стартує тихо в треї після входу в систему.", auto),
            Field("Читати адресу вкладки", "Chrome, Edge, Brave, Opera, Vivaldi: коли в заголовку немає проєкту, BT дивиться в адресний рядок. Береться лише домен (і домен клієнта з адреси Search Console, Ahrefs тощо), шлях сторінки не зберігається.", urls),
            Field("Перевіряти оновлення щодня", "Раз на добу BT питає GitHub, чи є нова версія, і показує її в треї. Оновлення ставиться лише після твого кліку.", upd),
            Field("Пауза", "Alt+Shift+P з будь-якої програми або кнопка у вікні дня. Іконка в треї стає помаранчевою.", T("Alt+Shift+P", 13, "MutedBrush", FontWeights.SemiBold)),
            Field("Завершити роботу", "Кнопка у вікні дня або в меню трею: облік зупиняється до 05:00 і вмикається сам. Іконка в треї сіра.", T("до 05:00", 13, "MutedBrush", FontWeights.SemiBold)),
        }, ""));

        H2("Дані", "Усе лежить у папці BisonTime в AppData цього користувача: файл на день, налаштування, журнал. Сирі записи вікон зберігаються 7 днів, дні — 30, цифри для дашборда без назв вікон — рік.");
        var status = T("", 13, "MintBrush", wrap: true);
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
        H1("Довідка", "Як BT рахує час і як ним користуватися. " + HelpContent.Made);
        foreach (var (title, lines) in HelpContent.Guide)
        {
            var body = new StackPanel();
            foreach (var line in lines)
            {
                var num = System.Text.RegularExpressions.Regex.Match(line, @"^(\d+)\. ");
                if (line.StartsWith("• "))
                    body.Children.Add(Cols("18,*", T("•", 13, "MutedBrush"), T(line[2..], 13, wrap: true)).M(0, 0, 0, 7));
                else if (num.Success)
                {
                    var badge = new Border
                    {
                        Width = 22, Height = 22, CornerRadius = new CornerRadius(11), Background = B("TonalBrush"), VerticalAlignment = VerticalAlignment.Top,
                        Child = new TextBlock { Text = num.Groups[1].Value, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = B("TextBrush"),
                            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontFamily = TextFont },
                    };
                    body.Children.Add(Cols("32,*", badge, T(line[num.Length..], 13, wrap: true).M(0, 2, 0, 0)).M(0, 0, 0, 9));
                }
                else
                    body.Children.Add(T(line, 13, wrap: true).M(0, 0, 0, 8));
            }
            _page.Children.Add(Card(Col(10, T(title, 15, display: true, weight: FontWeights.Bold), body), pad: 20).M(0, 0, 0, 12));
        }
    }

    void AboutPage()
    {
        var version = typeof(App).Assembly.GetName().Version?.ToString(3) ?? "";
        H1("Про BT", HelpContent.Made);
        // The panel survives re-renders (a download keeps its progress): unhook it from the old card.
        if (_update?.Parent is Border old) old.Child = null;

        _page.Children.Add(ListCard(new UIElement[]
        {
            Cols("*,Auto", Col(3, T($"Bison Time Checker {version}", 15, weight: FontWeights.SemiBold), T(HelpContent.Copyright + " · ліцензія MIT", 12, "MutedBrush")),
                HRow(Btn("GitHub", "Ghost", () => Open(HelpContent.Repo), HelpContent.Repo),
                     Btn("Релізи", "Ghost", () => Open(HelpContent.Releases), "Усі версії на GitHub").M(6, 0, 0, 0),
                     Btn("Повідомити про проблему", "Ghost", () => Open(HelpContent.Issues)).M(6, 0, 0, 0))),
            _update ??= new UpdatePanel(),
        }, ""));

        H2("Що нового", "Кожна версія одним рядком.");
        _page.Children.Add(ListCard(HelpContent.Changes.Select(c =>
            (UIElement)Cols("70,*", T(c.Version, 13, "MutedBrush", FontWeights.SemiBold), T(c.Text, 13, wrap: true))), ""));

        H2("Правова інформація", "");
        foreach (var line in HelpContent.Legal) _page.Children.Add(T(line, 12.5, "MutedBrush", wrap: true).M(0, 0, 0, 8));
        _page.Children.Add(T("Усе зберігається лише на цьому комп'ютері. В інтернет BT ходить лише по оновлення на GitHub і нічого не надсилає.", 12.5, "FaintBrush", wrap: true).M(0, 8, 0, 0));
    }
}
