using System.Windows;
using System.Windows.Media;
using BisonTime.Core;

namespace BisonTime.App;

/// <summary>The tray flyout: a <see cref="DayView"/> in a borderless window next to the tray.</summary>
internal sealed class MainWindow : Window
{
    readonly DayView _view = new(flyout: true);

    public DateTime HiddenAt { get; private set; } = DateTime.MinValue;

    public MainWindow()
    {
        Title = "Bison Time Checker";
        Width = 420;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        FontFamily = Ui.TextFont;
        UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);

        // Rounded M3 sheet: the window itself is transparent, the border carries the shape.
        Content = new System.Windows.Controls.Border
        {
            Background = Ui.B("BgBrush"),
            BorderBrush = Ui.B("LineBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(20),
            Child = _view,
        };
        _view.CloseRequested += Hide;

        Deactivated += (_, _) => { HiddenAt = DateTime.UtcNow; Hide(); };
        KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) Hide(); };
    }

    public void ShowNearTray(string? day = null)
    {
        var area = SystemParameters.WorkArea;
        Height = Math.Min(720, area.Height - 24);
        Left = area.Right - Width - 12;
        Top = area.Bottom - Height - 12;
        _view.Open(day);
        Ui.FadeIn((UIElement)Content, 160);
        Show();
        Activate();
    }

    public void Refresh() => _view.Refresh();

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // The flyout lives as long as the app: closing just hides it.
        e.Cancel = true;
        Hide();
    }
}
