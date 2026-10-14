namespace BisonTime.Core;

public interface IStore
{
    Settings LoadSettings();
    void SaveSettings(Settings s);
    DayFile LoadDay(string day);
    void SaveDay(string day, DayFile f);
    IEnumerable<string> Days();
    void DeleteDay(string day);
    Runtime LoadRuntime();
    void SaveRuntime(Runtime r);
    void Log(LogEntry e);
    Dictionary<string, DayStats> LoadStats();
    void SaveStats(Dictionary<string, DayStats> stats);
}

/// <summary>Why nothing is being counted, as shown under "Поза обліком".</summary>
public static class OffReasons
{
    public static readonly Dictionary<string, string> Names = new()
    {
        ["focus"] = "робочий стіл і вікна BT", ["idle"] = "простій", ["lock"] = "екран заблоковано",
        ["pause"] = "пауза", ["untracked"] = "ігноровані програми", ["done"] = "день завершено",
    };
}

/// <summary>
/// Turns a stream of "what is in front of the user" samples (one per second) into segments.
/// Single-threaded: call everything from one thread (the UI dispatcher).
/// </summary>
public sealed class Tracker
{
    public const long Gap = 2 * TimeUtil.Min;          // no tick for longer = asleep, app closed, crashed
    public const long MinSegment = 5_000;               // flicks shorter than 5 s are not recorded
    public const long MinIdlePrompt = TimeUtil.Min;     // idle under a minute is not worth asking about
    public const long MaxIdlePrompt = 4 * 60 * TimeUtil.Min;   // longer than that is not "a meeting away from the desk"
    public const long MinOff = TimeUtil.Min;            // untracked stretches under a minute are not listed
    public const int KeepSegmentsDays = 7;
    public const int KeepDaysDays = 30;
    const long SaveEvery = 30_000;

    readonly IStore _store;
    readonly Func<long> _now;
    readonly Runtime _rt;
    long _lastSave;

    public event Action? Changed;

    public Tracker(IStore store, Func<long>? now = null)
    {
        _store = store;
        _now = now ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        _rt = store.LoadRuntime();
    }

    public bool Paused => _rt.PausedSince != null;
    public bool Done => _rt.DoneUntil != null;
    public long? DoneUntil => _rt.DoneUntil;
    public long? PausedSince => _rt.PausedSince;
    public OpenSeg? Open => _rt.Open;

    void Log(string ev, string? where = null, string? why = null, int? sec = null) =>
        _store.Log(new LogEntry { T = _now(), Ev = ev, Where = where, Why = why, Sec = sec });

    static string Where(string proc, string title)
    {
        // Diagnostics keep the program and only the start of the title.
        var t = Classifier.Clean(title, Classifier.IsBrowser(proc));
        return t.Length > 24 ? $"{proc}:{t[..24]}…" : $"{proc}:{t}";
    }

    void Save(bool force = false)
    {
        var t = _now();
        if (!force && t - _lastSave < SaveEvery) return;
        _store.SaveRuntime(_rt);
        _lastSave = t;
    }

    void Close(OpenSeg open, long end, string why)
    {
        var e = Math.Max(open.S, Math.Min(end, _now()));
        Log("close", Where(open.Proc, open.Title), why, (int)((e - open.S) / 1000));
        if (e - open.S < MinSegment) return;
        foreach (var (day, s, pe) in TimeUtil.SplitByDay(open.S, e))
        {
            var f = _store.LoadDay(day);
            f.Segments.Add(new Segment { S = s, E = pe, Proc = open.Proc, Title = open.Title, Url = open.Url });
            _store.SaveDay(day, f);
        }
        var settings = _store.LoadSettings();
        var c = Classifier.Classify(new WindowInfo(open.Proc, open.Title, open.Url), settings);
        // A URL in some window title becomes a project only after a minute of real work there.
        if (c.AutoProject && c.Project != null && !settings.Projects.ContainsKey(c.Project)
            && (!c.AutoAfterMinute || e - open.S >= TimeUtil.Min))
        {
            settings.Projects[c.Project] = new ProjectCfg();
            _store.SaveSettings(settings);
            Log("new-project", c.Project);
        }
        Changed?.Invoke();
    }

    void StartOff(string reason, long t)
    {
        if (_rt.Off is { } off && off.Reason == reason) return;
        EndOff(t);
        _rt.Off = new OffState { S = t, Reason = reason };
    }

    void EndOff(long t)
    {
        if (_rt.Off is not { } off) return;
        _rt.Off = null;
        // Overnight, after "Завершити роботу" and anything under a minute is noise, not information.
        if (off.Reason != "done" && t - off.S >= MinOff && TimeUtil.DayKey(off.S) == TimeUtil.DayKey(t))
        {
            var day = TimeUtil.DayKey(off.S);
            var f = _store.LoadDay(day);
            f.Off.Add(new OffPiece { S = off.S, E = t, Reason = off.Reason });
            _store.SaveDay(day, f);
        }
    }

    void FinishIdle(long end, bool offer)
    {
        if (_rt.Idle is not { } p) return;
        _rt.Idle = null;
        if (!offer || end - p.S < MinIdlePrompt) return;
        // A computer left on overnight is not idle work: never offer hours across midnight or half a day back.
        if (end - p.S > MaxIdlePrompt || TimeUtil.DayKey(p.S) != TimeUtil.DayKey(end - 1)) return;
        var day = TimeUtil.DayKey(p.S);
        var f = _store.LoadDay(day);
        f.State.Idle.Add(new IdleItem { Id = $"i{p.S}", S = p.S, E = end, Project = p.Project });
        _store.SaveDay(day, f);
        Changed?.Invoke();
    }

    string? ProjectOf(OpenSeg o) =>
        Classifier.Classify(new WindowInfo(o.Proc, o.Title, o.Url), _store.LoadSettings()).Project;

    /// <summary>
    /// One sample. <paramref name="w"/> = foreground window (null when there is none worth reading);
    /// <paramref name="idle"/> = no input for the idle threshold; <paramref name="locked"/> = session locked.
    /// </summary>
    public void Tick(WindowInfo? w, bool idle, bool locked)
    {
        var t = _now();
        if (idle && w != null && Classifier.IsCall(w)) idle = false;

        // Asleep, BT closed or crashed: what was open ends where the last tick saw it.
        if (_rt.LastTick > 0 && t - _rt.LastTick > Gap)
        {
            var last = _rt.LastTick;
            if (_rt.Open is { } o) { Close(o, Math.Min(o.Beat, last), "sleep"); _rt.Open = null; }
            FinishIdle(last, offer: true);
            EndOff(last);
            Log("gap", why: $"{(t - last) / 1000}s");
            Save(force: true);
        }
        _rt.LastTick = t;

        // A pause left on overnight is forgotten, not intended: it ends at the morning boundary like "Завершити роботу".
        if (_rt.PausedSince is { } since && t >= NextMorning(since))
        {
            _rt.PausedSince = null;
            EndOff(t);
            Log("pause-expired");
            Save(force: true);
            Changed?.Invoke();
        }

        if (_rt.DoneUntil is { } until && t >= until)
        {
            _rt.DoneUntil = null;
            EndOff(t);
            Log("day-start");
            Save(force: true);
            Changed?.Invoke();
        }

        if (_rt.Idle != null && (locked || !idle)) FinishIdle(t, offer: !locked);

        var settings = _store.LoadSettings();
        string? reason =
            _rt.DoneUntil != null ? "done" :
            _rt.PausedSince != null ? "pause" :
            locked ? "lock" :
            idle ? "idle" :
            w is null ? "focus" :
            Classifier.IsIgnored(w, settings) ? "untracked" : null;

        if (reason is null)
        {
            // Same window = same program and title, and the address did not move to another site
            // (single-page apps like Search Console switch the property without changing the title).
            if (_rt.Open is { } o && o.Proc == w!.Proc && o.Title == w.Title && (w.Url == null || o.Url == null || o.Url == w.Url))
            {
                o.Beat = t;
                // The address is read in the background and may arrive a moment after the window does.
                if (o.Url == null && w.Url != null) { o.Url = w.Url; Save(force: true); Changed?.Invoke(); return; }
                Save();
                return;
            }
            if (_rt.Open is { } prev) Close(prev, t, "switch");
            EndOff(t);
            _rt.Open = new OpenSeg { S = t, Beat = t, Proc = w!.Proc, Title = w.Title, Url = w.Url };
            Log("open", Where(w.Proc, w.Title));
            Save(force: true);
            Changed?.Invoke();
            return;
        }

        if (_rt.Open is { } cur)
        {
            // Minutes before the idle threshold stay counted: reading without touching the mouse is work.
            Close(cur, t, reason);
            if (reason == "idle") _rt.Idle = new IdlePending { S = t, Project = ProjectOf(cur) };
            _rt.Open = null;
            Log("off", why: reason);
        }
        StartOff(reason, t);
        Save(force: true);
    }

    public void Pause()
    {
        if (_rt.PausedSince != null) return;
        var t = _now();
        _rt.PausedSince = t;
        if (_rt.Open is { } o) { Close(o, t, "pause"); _rt.Open = null; }
        StartOff("pause", t);
        Log("pause");
        Save(force: true);
        Changed?.Invoke();
    }

    /// <summary>The next 05:00 local after <paramref name="t"/>: when a finished day starts counting again.</summary>
    public static long NextMorning(long t)
    {
        var local = TimeUtil.Local(t);
        var m = local.Date.AddHours(5);
        if (m <= local) m = m.AddDays(1);
        return TimeUtil.ToMs(m);
    }

    /// <summary>"Завершити роботу": stop counting until tomorrow morning; no "remember to unpause".</summary>
    public void FinishDay()
    {
        var t = _now();
        _rt.PausedSince = null;
        _rt.DoneUntil = NextMorning(t);
        if (_rt.Open is { } o) { Close(o, t, "done"); _rt.Open = null; }
        FinishIdle(t, offer: true);
        EndOff(t);
        StartOff("done", t);
        Log("done");
        Save(force: true);
        Changed?.Invoke();
    }

    /// <summary>Continue counting after a pause or a finished day.</summary>
    public void Resume()
    {
        if (_rt.PausedSince == null && _rt.DoneUntil == null) return;
        _rt.PausedSince = null;
        _rt.DoneUntil = null;
        EndOff(_now());
        Log("resume");
        Save(force: true);
        Changed?.Invoke();
    }

    /// <summary>Close the open segment (app exit). The next start continues cleanly.</summary>
    public void Shutdown()
    {
        var t = _now();
        if (_rt.Open is { } o) { Close(o, t, "exit"); _rt.Open = null; }
        EndOff(t);
        _rt.LastTick = 0;
        Save(force: true);
    }

    /// <summary>Forget the open segment and pending idle (after "clear history").</summary>
    public void Reset()
    {
        _rt.Open = null;
        _rt.Off = null;
        _rt.Idle = null;
        Save(force: true);
        Changed?.Invoke();
    }

    /// <summary>The day's segments with the still-open one clipped in.</summary>
    public List<Segment> DaySegments(string day)
    {
        var list = new List<Segment>(_store.LoadDay(day).Segments);
        if (_rt.Open is { } o)
            foreach (var (d, s, e) in TimeUtil.SplitByDay(o.S, Math.Max(o.S, _now())))
                if (d == day) list.Add(new Segment { S = s, E = e, Proc = o.Proc, Title = o.Title, Url = o.Url });
        return list;
    }

    /// <summary>Untracked time of a day by reason, the current stretch included.</summary>
    public Dictionary<string, long> DayOff(string day)
    {
        var res = new Dictionary<string, long>();
        void Add(string r, long ms) => res[r] = (res.TryGetValue(r, out var v) ? v : 0) + ms;
        foreach (var x in _store.LoadDay(day).Off) Add(x.Reason, x.E - x.S);
        var t = _now();
        if (_rt.Off is { } off && TimeUtil.DayKey(off.S) == day && t - off.S >= MinOff) Add(off.Reason, t - off.S);
        return res;
    }

    /// <summary>Raw segments go after 7 days, whole days after 30.</summary>
    public void Cleanup()
    {
        var today = TimeUtil.DayKey(_now());
        var segLimit = TimeUtil.ShiftDay(today, -(KeepSegmentsDays - 1));
        var dayLimit = TimeUtil.ShiftDay(today, -(KeepDaysDays - 1));
        foreach (var day in _store.Days().ToList())
        {
            if (string.CompareOrdinal(day, dayLimit) < 0) { _store.DeleteDay(day); continue; }
            if (string.CompareOrdinal(day, segLimit) < 0)
            {
                var f = _store.LoadDay(day);
                if (f.Segments.Count > 0 || f.Off.Count > 0)
                {
                    f.Segments.Clear();
                    f.Off.Clear();
                    _store.SaveDay(day, f);
                }
            }
        }
    }
}
