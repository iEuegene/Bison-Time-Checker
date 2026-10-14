using System.Windows.Automation;
using BisonTime.Core;

namespace BisonTime.App;

/// <summary>
/// Reads the address bar of Chromium browsers (Chrome, Edge, Brave, Opera, Vivaldi) through UI Automation,
/// the same interface screen readers use. No browser extension, no hooks.
/// Runs on its own thread: a read can take tens of milliseconds, the tracker must not wait for it.
/// The result is reduced to domains right away (<see cref="Classifier.ReduceUrl"/>); the full address is never kept.
/// </summary>
internal sealed class UrlReader
{
    readonly object _lock = new();
    readonly Dictionary<(IntPtr, string), (string? Url, long At)> _cache = new();   // window + title → reduced url (null = none)
    const long Fresh = 10_000;   // single-page apps change the address without the title: re-read every 10 s
    readonly Dictionary<(IntPtr, string), int> _misses = new();
    readonly Dictionary<IntPtr, AutomationElement> _bars = new();       // worker thread only
    (IntPtr Hwnd, string Title)? _queued;
    readonly AutoResetEvent _signal = new(false);

    public UrlReader()
    {
        var t = new Thread(Loop) { IsBackground = true, Name = "BT url reader" };
        t.Start();
    }

    /// <summary>The reduced address for this window and title if already read; otherwise asks for it and returns null.</summary>
    public string? Get(IntPtr hwnd, string title)
    {
        lock (_lock)
        {
            if (_cache.TryGetValue((hwnd, title), out var known))
            {
                if (Environment.TickCount64 - known.At < Fresh) return known.Url;
                _queued = (hwnd, title);   // refresh in the background, meanwhile the last known address
                _signal.Set();
                return known.Url;
            }
            _queued = (hwnd, title);
        }
        _signal.Set();
        return null;
    }

    void Loop()
    {
        while (true)
        {
            _signal.WaitOne();
            (IntPtr Hwnd, string Title)? job;
            lock (_lock) { job = _queued; _queued = null; }
            if (job is not { } j) continue;
            string? url;
            bool final;
            try { (url, final) = Read(j.Hwnd); }
            catch { (url, final) = (null, false); }
            lock (_lock)
            {
                if (!final)
                {
                    // Busy (user typing in the bar) or not ready: try again on a later tick, give up after a few.
                    var n = _misses.TryGetValue((j.Hwnd, j.Title), out var m) ? m + 1 : 1;
                    _misses[(j.Hwnd, j.Title)] = n;
                    if (n < 5) continue;
                }
                if (_cache.Count > 500) { _cache.Clear(); _misses.Clear(); }
                _cache[(j.Hwnd, j.Title)] = (url, Environment.TickCount64);
            }
        }
    }

    /// <summary>(reduced url, final). Not final while the bar has focus: then it holds what is being typed.</summary>
    (string?, bool) Read(IntPtr hwnd)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (!_bars.TryGetValue(hwnd, out var bar))
            {
                bar = FindBar(hwnd)!;
                if (bar == null) return (null, true);
                if (_bars.Count > 50) _bars.Clear();
                _bars[hwnd] = bar;
            }
            try
            {
                if (bar.Current.HasKeyboardFocus) return (null, false);
                if (bar.GetCurrentPattern(ValuePattern.Pattern) is not ValuePattern vp) return (null, true);
                return (Classifier.ReduceUrl(vp.Current.Value), true);
            }
            catch (ElementNotAvailableException)
            {
                _bars.Remove(hwnd);   // the window was rebuilt: look the bar up again
            }
        }
        return (null, false);
    }

    /// <summary>
    /// Breadth-first through the browser's own UI, never into page content (Document subtrees):
    /// walking web content would be slow and would switch on the page's accessibility tree.
    /// </summary>
    static AutomationElement? FindBar(IntPtr hwnd)
    {
        var root = AutomationElement.FromHandle(hwnd);
        var walker = TreeWalker.ControlViewWalker;
        var queue = new Queue<AutomationElement>();
        queue.Enqueue(root);
        AutomationElement? fallback = null;
        var seen = 0;
        while (queue.Count > 0 && seen++ < 400)
        {
            var el = queue.Dequeue();
            var info = el.Current;
            if (info.ClassName == "OmniboxViewViews") return el;            // Chromium address bar
            if (info.ControlType == ControlType.Edit && fallback == null && IsAddressName(info.Name)) fallback = el;
            if (info.ControlType == ControlType.Document) continue;          // page content: skip
            for (var c = walker.GetFirstChild(el); c != null; c = walker.GetNextSibling(c)) queue.Enqueue(c);
        }
        return fallback;
    }

    static bool IsAddressName(string name) =>
        name.Contains("address", StringComparison.OrdinalIgnoreCase) || name.Contains("адрес", StringComparison.OrdinalIgnoreCase);
}
