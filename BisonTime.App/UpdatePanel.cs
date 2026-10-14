using System.Windows;
using System.Windows.Controls;
using static BisonTime.App.Ui;

namespace BisonTime.App;

/// <summary>
/// "Перевірити оновлення" → "Оновити (N файлів)" → restart. One instance lives as long as the
/// settings window, so a download keeps its progress when the page re-renders.
/// </summary>
internal sealed class UpdatePanel : ContentControl
{
    enum Stage { Idle, Checking, UpToDate, Available, Installing, Failed }

    Stage _stage = Stage.Idle;
    string _message = "";

    public UpdatePanel()
    {
        if (App.Instance.Update != null) _stage = Stage.Available;
        App.Instance.UpdateChanged += OnUpdateChanged;
        Build();
    }

    public void Detach() => App.Instance.UpdateChanged -= OnUpdateChanged;

    void OnUpdateChanged()
    {
        if (_stage is Stage.Checking or Stage.Installing) return;
        _stage = App.Instance.Update != null ? Stage.Available : _stage;
        Build();
    }

    async void Check()
    {
        _stage = Stage.Checking;
        Build();
        try
        {
            var a = await Updater.CheckAsync();
            _stage = a != null ? Stage.Available : Stage.UpToDate;
            App.Instance.SetUpdate(a);
        }
        catch (Exception ex)
        {
            _stage = Stage.Failed;
            _message = ex is System.Net.Http.HttpRequestException or TaskCanceledException
                ? "Не вдалося зв'язатися з GitHub. Перевір інтернет і спробуй ще раз."
                : ex.Message;
        }
        Build();
    }

    async void Install()
    {
        if (App.Instance.Update is not { } a) return;
        _stage = Stage.Installing;
        _message = "Підготовка…";
        Build();
        try
        {
            await Updater.InstallAsync(a, m => Dispatcher.Invoke(() => { _message = m; Build(); }));
            _message = "Готово, перезапускаю…";
            Build();
            Updater.Restart();
        }
        catch (Exception ex)
        {
            _stage = Stage.Failed;
            _message = ex is System.Net.Http.HttpRequestException or TaskCanceledException
                ? "Завантаження перервалося. Нічого не змінено — спробуй ще раз."
                : ex.Message;
            Build();
        }
    }

    void Build()
    {
        var current = Updater.Current;
        var a = App.Instance.Update;
        string status, brush = "MutedBrush";
        UIElement? action;
        switch (_stage)
        {
            case Stage.Checking:
                status = "Перевіряю…";
                action = null;
                break;
            case Stage.UpToDate:
                status = $"У тебе остання версія ({current}).";
                brush = "MintBrush";
                action = Btn("Перевірити ще раз", "Ghost", Check);
                break;
            case Stage.Available when a != null:
                status = $"Вийшла версія {a.Manifest.Version}. Завантажиться лише те, що змінилося ({a.Changed.Count} файлів, {Updater.Size(a.Bytes)}). Твої дані й налаштування залишаться.";
                brush = "TextBrush";
                action = Btn($"Оновити до {a.Manifest.Version}", "Primary", Install);
                break;
            case Stage.Installing:
                status = _message;
                brush = "TextBrush";
                action = null;
                break;
            case Stage.Failed:
                status = _message;
                brush = "OrangeBrush";
                action = Btn("Спробувати ще раз", "Ghost", a != null ? Install : Check);
                break;
            default:
                status = $"У тебе версія {current}.";
                action = Btn("Перевірити оновлення", "Ghost", Check);
                break;
        }
        Content = Cols("*,Auto",
            Col(3, T("Оновлення", 14, weight: FontWeights.SemiBold), T(status, 12, brush, wrap: true)),
            action is FrameworkElement fe ? fe.M(12, 0, 0, 0) : null);
    }
}
