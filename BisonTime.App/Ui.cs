using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace BisonTime.App;

/// <summary>Small factory for the Inweb-styled elements both windows build in code.</summary>
internal static class Ui
{
    public static Brush B(string key) => (Brush)Application.Current.FindResource(key);
    public static FontFamily TextFont => (FontFamily)Application.Current.FindResource("Text");
    public static FontFamily Display => (FontFamily)Application.Current.FindResource("Display");
    public static readonly FontFamily Icons = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    public static TextBlock T(string text, double size = 13, string brush = "TextBrush", FontWeight? weight = null,
        bool display = false, bool wrap = false, bool ellipsis = true)
    {
        var t = new TextBlock
        {
            Text = text,
            FontSize = size,
            Foreground = B(brush),
            FontWeight = weight ?? FontWeights.Normal,
            FontFamily = display ? Display : TextFont,
            VerticalAlignment = VerticalAlignment.Center,
        };
        // Tabular digits: times in a column line up ("1 год 05 хв" over "<1 хв").
        System.Windows.Documents.Typography.SetNumeralAlignment(t, FontNumeralAlignment.Tabular);
        if (wrap) t.TextWrapping = TextWrapping.Wrap;
        else if (ellipsis) t.TextTrimming = TextTrimming.CharacterEllipsis;
        if (!wrap && ellipsis) t.ToolTip = text.Length > 30 ? text : null;
        return t;
    }

    public static TextBlock Glyph(string glyph, double size = 14, string brush = "MutedBrush") => new()
    {
        Text = glyph, FontFamily = Icons, FontSize = size, Foreground = B(brush),
        VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
    };

    public static Button Btn(object content, string style, Action onClick, string? tip = null)
    {
        var b = new Button
        {
            Content = content is string s ? new TextBlock { Text = s, FontFamily = TextFont } : content,
            Style = (Style)Application.Current.FindResource(style),
            ToolTip = tip,
        };
        b.Click += (_, _) => onClick();
        return b;
    }

    public static CheckBox Toggle(bool on, Action<bool> changed, string? tip = null)
    {
        var c = new CheckBox { IsChecked = on, Style = (Style)Application.Current.FindResource("Toggle"), ToolTip = tip };
        c.Click += (_, _) => changed(c.IsChecked == true);
        return c;
    }

    public static TextBox Input(string text = "", bool multiline = false) => new()
    {
        Text = text,
        Style = (Style)Application.Current.FindResource("Input"),
        FontFamily = TextFont,
        AcceptsReturn = multiline,
        TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
    };

    /// <summary>Text box with a grey hint shown while it is empty (WPF has none built in).</summary>
    public static Grid WithHint(TextBox box, string hint)
    {
        var h = new TextBlock
        {
            Text = hint, Foreground = B("FaintBrush"), FontFamily = TextFont, FontSize = 12,
            Margin = new Thickness(9, 6, 9, 0), IsHitTestVisible = false, TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = box.AcceptsReturn ? VerticalAlignment.Top : VerticalAlignment.Center,
        };
        void Update() => h.Visibility = string.IsNullOrEmpty(box.Text) ? Visibility.Visible : Visibility.Collapsed;
        box.TextChanged += (_, _) => Update();
        Update();
        var g = new Grid();
        g.Children.Add(box);
        g.Children.Add(h);
        return g;
    }

    public static Border Card(UIElement child, string bg = "CardBrush", string? border = null, double pad = 12) => new()
    {
        Background = B(bg),
        BorderBrush = border == null ? Brushes.Transparent : B(border),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(16),
        Padding = new Thickness(pad),
        Child = child,
    };

    // Segoe Fluent / MDL2 glyphs used across the app.
    public static class G
    {
        public const string Today = "\uE823", Dashboard = "\uE9D2", Projects = "\uE8B7", Rules = "\uE71C", Tracking = "\uE916",
            Help = "\uE897", Info = "\uE946", Pause = "\uE769", Stop = "\uE71A", Play = "\uE768", Copy = "\uE8C8",
            Left = "\uE76B", Right = "\uE76C", Up = "\uE70E", Down = "\uE70D", Add = "\uE710", Update = "\uE895",
            Settings = "\uE713", Check = "\uE73E";
    }

    /// <summary>Round icon-only button (M3 standard icon button).</summary>
    public static Button IconBtn(string glyph, Action onClick, string? tip = null, double size = 16)
    {
        var b = Btn(Glyph(glyph, size), "Icon", onClick, tip);
        if (tip != null) System.Windows.Automation.AutomationProperties.SetName(b, tip);   // screen readers
        return b;
    }

    /// <summary>Button with a leading icon and a label.</summary>
    public static Button IconText(string glyph, string text, string style, Action onClick, string? tip = null)
    {
        var fg = style == "Primary" ? "GreenBrush" : style == "Quiet" ? "MutedBrush" : "TextBrush";
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(Glyph(glyph, 14, fg));
        content.Children.Add(new TextBlock { Text = text, FontFamily = TextFont, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        return Btn(content, style, onClick, tip);
    }

    /// <summary>Rounded square with two letters of a project name.</summary>
    public static Border Avatar(string name, bool active = false)
    {
        var letters = new string(name.Where(char.IsLetterOrDigit).Take(2).ToArray()).ToUpperInvariant();
        return new Border
        {
            Width = 36, Height = 36, CornerRadius = new CornerRadius(12),
            Background = B(active ? "MintBrush" : "TonalBrush"), VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = letters, FontFamily = Display, FontSize = 11.5, FontWeight = FontWeights.Bold,
                Foreground = B(active ? "GreenBrush" : "MutedBrush"),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            },
        };
    }

    /// <summary>Small filled label: lime for time that goes to the report, tonal otherwise.</summary>
    public static Border Chip(string text, bool lime = true) => new()
    {
        Background = B(lime ? "LimeBrush" : "TonalBrush"), CornerRadius = new CornerRadius(8),
        Padding = new Thickness(9, 4, 9, 4), VerticalAlignment = VerticalAlignment.Center,
        Child = T(text, 12.5, lime ? "GreenBrush" : "TextBrush", FontWeights.SemiBold, ellipsis: false),
    };

    /// <summary>The Inweb parallelogram: a skewed lime plate with upright text.</summary>
    public static Border Plate(string text, string bg = "LimeBrush", string fg = "GreenBrush")
    {
        var inner = T(text, 12, fg, FontWeights.Bold, display: true, ellipsis: false);
        inner.RenderTransform = new SkewTransform(14, 0);
        inner.RenderTransformOrigin = new Point(0.5, 0.5);
        return new Border
        {
            Background = B(bg),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(10, 4, 10, 4),
            RenderTransform = new SkewTransform(-14, 0),
            RenderTransformOrigin = new Point(0.5, 0.5),
            Child = inner,
            VerticalAlignment = VerticalAlignment.Center,
        };
    }

    /// <summary>Thin lime stroke at -14°: "you are working here now" / selected item.</summary>
    public static Rectangle Mark(double h = 16) => new()
    {
        Width = 4, Height = h, Fill = B("LimeBrush"), RadiusX = 1, RadiusY = 1,
        RenderTransform = new SkewTransform(-14, 0), RenderTransformOrigin = new Point(0.5, 0.5),
        VerticalAlignment = VerticalAlignment.Center,
    };

    public static StackPanel HRow(params UIElement?[] kids)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var k in kids) if (k != null) p.Children.Add(k);
        return p;
    }

    public static StackPanel Col(double gap, params UIElement?[] kids)
    {
        var p = new StackPanel();
        foreach (var k in kids)
            if (k is FrameworkElement fe) { if (p.Children.Count > 0) fe.Margin = new Thickness(fe.Margin.Left, fe.Margin.Top + gap, fe.Margin.Right, fe.Margin.Bottom); p.Children.Add(fe); }
            else if (k != null) p.Children.Add(k);
        return p;
    }

    public static T M<T>(this T el, double l, double t, double r, double b) where T : FrameworkElement
    {
        el.Margin = new Thickness(l, t, r, b);
        return el;
    }

    /// <summary>Grid with given column widths ("*", "Auto", "120"); children placed in order.</summary>
    public static Grid Cols(string widths, params UIElement?[] kids)
    {
        var g = new Grid();
        foreach (var w in widths.Split(','))
        {
            var s = w.Trim();
            g.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = s == "*" ? new GridLength(1, GridUnitType.Star) : s == "Auto" ? GridLength.Auto : new GridLength(double.Parse(s)),
            });
        }
        var i = 0;
        foreach (var k in kids)
        {
            if (k != null) { Grid.SetColumn(k, i); g.Children.Add(k); }
            i++;
        }
        return g;
    }

    /// <summary>A button that opens a dark menu of choices.</summary>
    public static Button Picker(string label, string style, IEnumerable<string> items, Action<string> picked, string? tip = null)
    {
        Button? b = null;
        b = Btn(label, style, () =>
        {
            var menu = new ContextMenu { PlacementTarget = b, Placement = PlacementMode.Bottom };
            foreach (var it in items)
            {
                var mi = new MenuItem { Header = it };
                var v = it;
                mi.Click += (_, _) => picked(v);
                menu.Items.Add(mi);
            }
            if (menu.Items.Count == 0) menu.Items.Add(new MenuItem { Header = "Немає проєктів", IsEnabled = false });
            menu.Opened += (_, _) => MenuOpen = true;
            menu.Closed += (_, _) => MenuOpen = false;
            menu.IsOpen = true;
        }, tip);
        return b;
    }

    /// <summary>
    /// Mouse click, and keyboard too: the element takes Tab focus with the lime ring and runs on Enter or Space.
    /// <paramref name="id"/> lets a view put focus back on "the same" element after it rebuilds.
    /// </summary>
    public static void Clickable(UIElement el, Action onClick, string? id = null, string? name = null)
    {
        // Hover: a faint light layer on transparent surfaces, so every clickable row answers the pointer.
        if (el is Border hb && (hb.Background == null || hb.Background == Brushes.Transparent))
        {
            if (hb.CornerRadius == default) hb.CornerRadius = new CornerRadius(8);
            hb.Background = Brushes.Transparent;
            hb.MouseEnter += (_, _) => hb.Background = Hover;
            hb.MouseLeave += (_, _) => hb.Background = Brushes.Transparent;
        }
        if (el is FrameworkElement fe)
        {
            fe.Cursor = Cursors.Hand;
            fe.Focusable = true;
            fe.FocusVisualStyle = (Style)Application.Current.FindResource("FocusRingSquare");
            if (id != null) fe.Uid = id;
            if (name != null) System.Windows.Automation.AutomationProperties.SetName(fe, name);
        }
        el.MouseLeftButtonUp += (_, e) => { onClick(); e.Handled = true; };
        el.KeyDown += (_, e) =>
        {
            if (e.Key is Key.Enter or Key.Space && Keyboard.FocusedElement == el) { onClick(); e.Handled = true; }
        };
    }

    /// <summary>After a rebuild, focus the element that has the same id as the one focused before (keyboard users keep their place).</summary>
    public static void RestoreFocus(DependencyObject root, string? id)
    {
        if (string.IsNullOrEmpty(id)) return;
        var found = Find(root, id);
        found?.Focus();
        static UIElement? Find(DependencyObject d, string id)
        {
            if (d is UIElement u && d is FrameworkElement f && f.Uid == id) return u;
            var n = VisualTreeHelper.GetChildrenCount(d);
            for (var i = 0; i < n; i++)
                if (Find(VisualTreeHelper.GetChild(d, i), id) is { } r) return r;
            if (d is ContentControl { Content: DependencyObject c } && VisualTreeHelper.GetChildrenCount(d) == 0) return Find(c, id);
            return null;
        }
    }

    /// <summary>The Uid of whatever has keyboard focus now (empty when it has none).</summary>
    public static string? FocusedId() => (Keyboard.FocusedElement as FrameworkElement)?.Uid is { Length: > 0 } u ? u : null;

    static readonly SolidColorBrush Hover = Freeze(new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)));
    static T Freeze<T>(T f) where T : Freezable { f.Freeze(); return f; }

    /// <summary>
    /// Windows 11 title bar in the app's own dark green instead of the system white (no effect on Windows 10).
    /// </summary>
    public static void DarkTitleBar(Window w)
    {
        w.SourceInitialized += (_, _) =>
        {
            try
            {
                var hwnd = new System.Windows.Interop.WindowInteropHelper(w).Handle;
                var on = 1;
                DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int));          // DWMWA_USE_IMMERSIVE_DARK_MODE
                var caption = 0x0019220B;                                      // COLORREF of #0B2219 (BGR)
                DwmSetWindowAttribute(hwnd, 35, ref caption, sizeof(int));     // DWMWA_CAPTION_COLOR
                var border = 0x00313D1D;                                       // #1D3D31
                DwmSetWindowAttribute(hwnd, 34, ref border, sizeof(int));      // DWMWA_BORDER_COLOR
            }
            catch { /* older Windows: default title bar */ }
        };
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>Fade in over 150 ms (opacity only), unless Windows animations are switched off.</summary>
    public static void FadeIn(UIElement el, int ms = 150)
    {
        if (!SystemParameters.ClientAreaAnimation) { el.Opacity = 1; return; }
        el.BeginAnimation(UIElement.OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(ms))
        {
            EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut },
        });
    }

    /// <summary>True while one of our project menus is open: views hold off rebuilding under it.</summary>
    public static bool MenuOpen { get; private set; }
}
