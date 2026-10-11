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
        CornerRadius = new CornerRadius(10),
        Padding = new Thickness(pad),
        Child = child,
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
            menu.IsOpen = true;
        }, tip);
        return b;
    }

    public static void Clickable(UIElement el, Action onClick)
    {
        if (el is FrameworkElement fe) fe.Cursor = Cursors.Hand;
        el.MouseLeftButtonUp += (_, e) => { onClick(); e.Handled = true; };
    }
}
