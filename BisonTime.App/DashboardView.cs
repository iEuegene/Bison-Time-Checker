using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using BisonTime.Core;
using static BisonTime.App.Ui;

namespace BisonTime.App;

/// <summary>
/// Day / week / month at a glance: tracked time, projects vs Інше, programs and browser services.
/// Read-only: corrections happen in the day view, a click on a day opens it there.
/// </summary>
internal sealed class DashboardView : ContentControl
{
    static readonly CultureInfo Uk = new("uk-UA");

    // Categorical colours on the dark Inweb green: lime first, no orange (that one means "warning").
    static readonly Color[] Palette =
    {
        Rgb(0xF0, 0xFC, 0x7E), Rgb(0x7F, 0xE3, 0xB5), Rgb(0x3B, 0xB8, 0xA0), Rgb(0x8C, 0xC8, 0xF2),
        Rgb(0xC3, 0xB1, 0xF5), Rgb(0xE6, 0xC5, 0x8F), Rgb(0xF2, 0xA7, 0xC3),
    };
    static readonly Color RestColor = Rgb(0x4A, 0x6B, 0x5F);

    Period _period = Period.Week;
    string _day = Today;
    readonly StackPanel _page = new() { Margin = new Thickness(32, 26, 32, 30) };
    readonly Action<string> _openDay;

    static long Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    static string Today => TimeUtil.DayKey(Now);
    static Color Rgb(byte r, byte g, byte b) => Color.FromRgb(r, g, b);

    public DashboardView(Action<string> openDay)
    {
        _openDay = openDay;
        Content = new ScrollViewer { Content = _page, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    }

    // ---------------- data ----------------

    static bool Editable(string day) =>
        string.CompareOrdinal(day, TimeUtil.ShiftDay(Today, -(Tracker.KeepSegmentsDays - 1))) >= 0 && string.CompareOrdinal(day, Today) <= 0;

    /// <summary>Days with raw segments are recomputed (they may have been edited); older ones come from the saved stats.</summary>
    static Dictionary<string, DayStats> Load(List<string> days)
    {
        var saved = App.Store.LoadStats();
        var settings = App.Store.LoadSettings();
        var res = new Dictionary<string, DayStats>();
        foreach (var d in days)
        {
            if (string.CompareOrdinal(d, Today) > 0) continue;
            var f = App.Store.LoadDay(d);
            if (f.Segments.Count > 0 || d == Today)
                res[d] = Stats.Save(App.Store, d, Report.Build(App.Tracker.DaySegments(d), f.State, settings));
            else if (saved.TryGetValue(d, out var st))
                res[d] = st;
        }
        return res;
    }

    // ---------------- render ----------------

    public void Render()
    {
        var focused = FocusedId();
        if (focused != null)
            Dispatcher.BeginInvoke(() => RestoreFocus(this, focused), System.Windows.Threading.DispatcherPriority.Loaded);
        var days = Stats.Days(_period, _day);
        var data = Load(days);
        var sum = Stats.Sum(data.Values);
        _page.Children.Clear();

        // header: title, period tabs, navigation
        var tabs = Segmented(new[] { (Period.Day, "День"), (Period.Week, "Тиждень"), (Period.Month, "Місяць") });
        var canNext = string.CompareOrdinal(Stats.Days(_period, Stats.Shift(_period, _day, 1))[0], Today) <= 0;
        var prev = IconBtn(G.Left, () => { _day = Stats.Shift(_period, _day, -1); Render(); }, "Назад");
        var next = IconBtn(G.Right, () => { _day = Stats.Shift(_period, _day, 1); if (string.CompareOrdinal(_day, Today) > 0) _day = Today; Render(); }, "Вперед");
        next.IsEnabled = canNext;
        var label = T(PeriodLabel(days), 15, weight: FontWeights.SemiBold);
        label.MinWidth = 210;
        label.TextAlignment = TextAlignment.Center;
        var nav = HRow(days.Contains(Today) ? null : Btn("Сьогодні", "Quiet", () => { _day = Today; Render(); }).M(0, 0, 6, 0), prev, label, next);
        _page.Children.Add(Cols("*,Auto,Auto", T("Дашборд", 26, display: true, weight: FontWeights.Bold), tabs, nav.M(16, 0, 0, 0)));

        if (sum.TrackedMs == 0 && sum.Apps.Count == 0)
        {
            _page.Children.Add(Card(Col(6,
                T("За цей період даних немає", 15, display: true, weight: FontWeights.Bold),
                T("Дашборд рахує дні з версії 1.4.0. Тиждень і місяць наповнюватимуться з кожним робочим днем.", 13, "MutedBrush", wrap: true)), pad: 20).M(0, 24, 0, 0));
            return;
        }

        // tiles: the one that goes to the report is the lime one
        var share = sum.TrackedMs > 0 ? sum.ProjectMs / (double)sum.TrackedMs : 0;
        _page.Children.Add(Cols("*,*,*,*",
            Tile("Робота трекера", TimeUtil.FormatExact(sum.TrackedMs), Average(data, days)),
            Tile("Проєкти", TimeUtil.FormatExact(sum.ProjectMs), "йде у звіт", lime: true).M(14, 0, 0, 0),
            Tile("Інше", TimeUtil.FormatExact(sum.OtherMs), "поза проєктами").M(14, 0, 0, 0),
            Tile("Частка проєктів", share.ToString("P0", Uk), "проєкти / весь час").M(14, 0, 0, 0)).M(0, 24, 0, 0));

        if (_period == Period.Day)
        {
            if (Editable(_day))
                _page.Children.Add(HRow(Btn("Відкрити день для виправлень", "Tonal", () => _openDay(_day),
                    "Перемикачі рядків, перенос з «Інше», +5 хв — у вікні дня")).M(0, 12, 0, 0));
        }
        else
            _page.Children.Add(DaysChart(days, data).M(0, 14, 0, 0));

        // programs and browser side by side
        _page.Children.Add(Cols("*,*",
            Breakdown("Програми", "Час у кожній програмі: проєкти та «Інше» разом.", sum.Apps),
            Breakdown("Браузер", "Вкладки за сервісом у заголовку. Адреси сторінок BT не бачить.", sum.Browser).M(12, 0, 0, 0)).M(0, 14, 0, 0));

        // projects
        _page.Children.Add(ProjectsCard(sum).M(0, 14, 0, 0));
    }

    string PeriodLabel(List<string> days)
    {
        var a = DateTime.ParseExact(days[0], "yyyy-MM-dd", null);
        var b = DateTime.ParseExact(days[^1], "yyyy-MM-dd", null);
        return _period switch
        {
            Period.Day => days[0] == Today ? "Сьогодні" : a.ToString("dddd, d MMMM", Uk),
            Period.Week => a.Month == b.Month ? $"{a.Day}–{b.ToString("d MMMM", Uk)}" : $"{a.ToString("d MMM", Uk)} – {b.ToString("d MMM", Uk)}",
            _ => Uk.TextInfo.ToTitleCase(a.ToString("MMMM yyyy", Uk)),
        };
    }

    static string Average(Dictionary<string, DayStats> data, List<string> days)
    {
        var worked = data.Values.Where(d => d.TrackedMs >= 30 * TimeUtil.Min).ToList();
        if (days.Count == 1 || worked.Count == 0) return "весь записаний час";
        return $"у середньому {TimeUtil.FormatExact(worked.Sum(d => d.TrackedMs) / worked.Count)} на робочий день";
    }

    static Border Tile(string title, string value, string note, bool lime = false) => Card(Col(6,
        T(title, 13, lime ? "GreenBrush" : "MutedBrush", FontWeights.Medium),
        T(value, 22, lime ? "GreenBrush" : "TextBrush", FontWeights.Bold, display: true),
        T(note, 12, lime ? "GreenBrush" : "FaintBrush", wrap: true)), lime ? "LimeBrush" : "CardBrush", null, 18);

    /// <summary>M3 segmented button: outlined group, the selected segment tonal with a check.</summary>
    UIElement Segmented((Period P, string Title)[] items)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        for (var i = 0; i < items.Count(); i++)
        {
            var (p, title) = items[i];
            var on = p == _period;
            var content = HRow(on ? Glyph(G.Check, 12, "LimeBrush").M(0, 0, 6, 0) : null, T(title, 13.5, on ? "LimeBrush" : "TextBrush", FontWeights.Medium));
            var n = items.Count();
            var seg = new Border
            {
                Background = on ? B("TonalBrush") : Brushes.Transparent, Padding = new Thickness(16, 8, 16, 8),
                // WPF does not clip to the rounded group: end segments round their own outer corners.
                CornerRadius = new CornerRadius(i == 0 ? 19 : 0, i == n - 1 ? 19 : 0, i == n - 1 ? 19 : 0, i == 0 ? 19 : 0),
                BorderBrush = B("EdgeBrush"), BorderThickness = new Thickness(i == 0 ? 0 : 1, 0, 0, 0), Child = content,
            };
            Clickable(seg, () => { _period = p; Render(); }, "period:" + p, title);
            row.Children.Add(seg);
        }
        return new Border { BorderBrush = B("EdgeBrush"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(20), Child = row, ClipToBounds = true, VerticalAlignment = VerticalAlignment.Center };
    }

    /// <summary>One stacked bar per day: projects in lime, Інше muted. A click opens the day while it is editable.</summary>
    FrameworkElement DaysChart(List<string> days, Dictionary<string, DayStats> data)
    {
        const double H = 130;
        var max = Math.Max(data.Values.Select(d => d.TrackedMs).DefaultIfEmpty(0).Max(), TimeUtil.Min);
        var grid = new Grid { Height = H + 22 };
        for (var i = 0; i < days.Count; i++) grid.ColumnDefinitions.Add(new ColumnDefinition());
        for (var i = 0; i < days.Count; i++)
        {
            var d = days[i];
            data.TryGetValue(d, out var st);
            var date = DateTime.ParseExact(d, "yyyy-MM-dd", null);
            var col = new Grid { Background = Brushes.Transparent, Margin = new Thickness(days.Count > 10 ? 2 : 8, 0, days.Count > 10 ? 2 : 8, 0) };
            col.RowDefinitions.Add(new RowDefinition { Height = new GridLength(H) });
            col.RowDefinitions.Add(new RowDefinition { Height = new GridLength(22) });
            var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom };
            if (st != null && st.TrackedMs > 0)
            {
                var other = Math.Max(0, st.OtherMs * H / max);
                var proj = Math.Max(0, st.ProjectMs * H / max);
                // Rounded column: the top piece rounds the top, the lime piece the bottom.
                var top = other >= 1;
                if (top) stack.Children.Add(new Border { Height = other, Background = B("TonalBrush"), CornerRadius = new CornerRadius(8, 8, proj >= 1 ? 0 : 8, proj >= 1 ? 0 : 8) });
                if (proj >= 1) stack.Children.Add(new Border { Height = proj, Background = B("LimeBrush"), CornerRadius = new CornerRadius(top ? 0 : 8, top ? 0 : 8, 8, 8) });
                col.ToolTip = $"{date.ToString("ddd, d MMM", Uk)}\nУсього {TimeUtil.FormatExact(st.TrackedMs)}\nПроєкти {TimeUtil.FormatExact(st.ProjectMs)} · Інше {TimeUtil.FormatExact(st.OtherMs)}"
                    + (Editable(d) ? "\nКлік — відкрити день" : "");
            }
            col.Children.Add(stack);
            var weekend = date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
            var caption = T(_period == Period.Week ? date.ToString("ddd d", Uk) : date.Day.ToString(), 12, d == Today ? "LimeBrush" : weekend ? "FaintBrush" : "MutedBrush");
            caption.HorizontalAlignment = HorizontalAlignment.Center;
            caption.VerticalAlignment = VerticalAlignment.Bottom;
            Grid.SetRow(caption, 1);
            col.Children.Add(caption);
            if (st != null && Editable(d)) Clickable(col, () => _openDay(d));
            Grid.SetColumn(col, i);
            grid.Children.Add(col);
        }
        var legend = HRow(Swatch(B("LimeBrush")), T("Проєкти", 12, "MutedBrush").M(6, 0, 16, 0), Swatch(B("TonalBrush")), T("Інше", 12, "MutedBrush").M(6, 0, 0, 0));
        return Card(Col(12, Cols("*,Auto", Col(2, T("По днях", 15, weight: FontWeights.SemiBold), T("Клік по дню — відкрити для виправлень (останні 7 днів)", 12, "FaintBrush")), legend), grid), pad: 20);
    }

    static Border Swatch(Brush b) => new() { Width = 10, Height = 10, CornerRadius = new CornerRadius(5), Background = b, VerticalAlignment = VerticalAlignment.Center };

    /// <summary>Donut plus the list it stands for, biggest first, the tail folded into "Решта".</summary>
    static FrameworkElement Breakdown(string title, string note, Dictionary<string, long> d)
    {
        var slices = Stats.Top(d, 8);
        var head = Col(2, T(title, 15, weight: FontWeights.SemiBold), T(note, 12, "FaintBrush", wrap: true));
        if (slices.Count == 0)
            return Card(Col(12, head, T("Немає даних", 13, "MutedBrush")), pad: 20);
        var colors = slices.Select((s, i) => s.Name == Stats.Rest ? RestColor : Palette[i % Palette.Length]).ToList();
        var total = slices.Sum(s => s.Ms);

        var list = new StackPanel();
        for (var i = 0; i < slices.Count; i++)
        {
            var s = slices[i];
            var name = T(s.Name, 13, s.Name == Stats.Rest ? "MutedBrush" : "TextBrush");
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            list.Children.Add(Cols("14,*,Auto,Auto",
                Swatch(new SolidColorBrush(colors[i])),
                name.M(4, 0, 6, 0),
                T(s.Share.ToString("P0", Uk), 12, "FaintBrush").M(0, 0, 10, 0),
                T(TimeUtil.FormatExact(s.Ms), 12.5, "MutedBrush", FontWeights.Medium)).M(0, 4, 0, 4));
        }
        var donut = Donut(slices, colors, total);
        return Card(Col(16, head, Cols("Auto,*", donut, list.M(20, 0, 0, 0))), pad: 20);
    }

    static UIElement Donut(List<Slice> slices, List<Color> colors, long totalMs)
    {
        const double Size = 160, Thick = 22;
        var r = Size / 2;
        var ri = r - Thick;
        var canvas = new Canvas { Width = Size, Height = Size, VerticalAlignment = VerticalAlignment.Top };
        double a0 = -90;
        for (var i = 0; i < slices.Count; i++)
        {
            var sweep = slices[i].Share * 360;
            Shape shape = sweep >= 359.99
                ? new Path { Data = new CombinedGeometry(GeometryCombineMode.Exclude, new EllipseGeometry(new Point(r, r), r, r), new EllipseGeometry(new Point(r, r), ri, ri)) }
                : new Path { Data = Ring(r, ri, a0, sweep) };
            shape.Fill = new SolidColorBrush(colors[i]);
            shape.Stroke = B("CardBrush");
            shape.StrokeThickness = slices.Count > 1 ? 1.5 : 0;
            shape.ToolTip = $"{slices[i].Name} · {slices[i].Share.ToString("P0", Uk)} · {TimeUtil.FormatExact(slices[i].Ms)}";
            canvas.Children.Add(shape);
            a0 += sweep;
        }
        var min = (int)(totalMs / TimeUtil.Min);
        var big = T(min >= 60 ? $"{min / 60} год" : $"{min} хв", 17, weight: FontWeights.Bold, display: true, ellipsis: false);
        var small = T(min >= 60 ? $"{min % 60:00} хв" : "усього", 12, "MutedBrush", FontWeights.Medium, ellipsis: false);
        foreach (var t in new[] { big, small }) { t.Width = Size; t.TextAlignment = TextAlignment.Center; }
        Canvas.SetTop(big, r - 17);
        Canvas.SetTop(small, r + 6);
        canvas.Children.Add(big);
        canvas.Children.Add(small);
        return canvas;
    }

    static Geometry Ring(double r, double ri, double startDeg, double sweepDeg)
    {
        Point P(double rad, double deg) => new(r + rad * Math.Cos(deg * Math.PI / 180), r + rad * Math.Sin(deg * Math.PI / 180));
        var end = startDeg + sweepDeg;
        var large = sweepDeg > 180;
        var f = new PathFigure { StartPoint = P(r, startDeg), IsClosed = true };
        f.Segments.Add(new ArcSegment(P(r, end), new Size(r, r), 0, large, SweepDirection.Clockwise, true));
        f.Segments.Add(new LineSegment(P(ri, end), true));
        f.Segments.Add(new ArcSegment(P(ri, startDeg), new Size(ri, ri), 0, large, SweepDirection.Counterclockwise, true));
        var g = new PathGeometry();
        g.Figures.Add(f);
        return g;
    }

    static FrameworkElement ProjectsCard(DayStats sum)
    {
        var head = Col(2, T("Проєкти", 15, weight: FontWeights.SemiBold),
            T("Точний час до округлення. Округлений підсумок для звіту — на сторінці «Сьогодні».", 12, "FaintBrush", wrap: true));
        var items = sum.Projects.Where(x => x.Value > 0).OrderByDescending(x => x.Value).ToList();
        if (items.Count == 0) return Card(Col(12, head, T("Часу на проєктах немає", 13, "MutedBrush")), pad: 20);
        var max = items[0].Value;
        var list = new StackPanel();
        foreach (var (name, ms) in items)
        {
            var bar = new Border
            {
                Height = 6, CornerRadius = new CornerRadius(3), Background = B("LimeBrush"),
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center,
            };
            var track = new Grid();
            track.Children.Add(bar);
            track.SizeChanged += (_, e) => bar.Width = Math.Max(2, e.NewSize.Width * ms / (double)max);
            var label = T(name, 13);
            label.TextTrimming = TextTrimming.CharacterEllipsis;
            list.Children.Add(Cols("220,*,Auto", label, track.M(10, 0, 14, 0), T(TimeUtil.FormatExact(ms), 12.5, "MutedBrush", FontWeights.Medium)).M(0, 5, 0, 5));
        }
        return Card(Col(12, head, list), pad: 20);
    }
}
