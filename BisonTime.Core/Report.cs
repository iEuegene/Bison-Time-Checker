namespace BisonTime.Core;

public sealed class Row
{
    public string Key { get; init; } = "";      // "project|rowKey": what toggles and moves refer to
    public string RowKey { get; init; } = "";
    public string Group { get; init; } = "";
    public string Label { get; set; } = "";
    public string Task { get; set; } = "";
    public string App { get; set; } = "";
    public long Ms { get; set; }
    public long RawMs { get; set; }
    public bool Disabled { get; init; }
    /// <summary>When the time was spent, pieces closer than a minute glued together: the "розбір" of a row.</summary>
    public List<Piece> Pieces { get; } = new();
    /// <summary>Ms this row had when the project was marked "Внесено"; null = the row appeared after (or no snapshot).</summary>
    public long? EnteredMs { get; set; }
    /// <summary>The project was marked with a per-row snapshot, so "new since" can be told row by row.</summary>
    public bool HasSnapshot { get; set; }
    /// <summary>Time that is not in the report yet: the whole row if it is new, the growth otherwise.</summary>
    public long NewMs => !HasSnapshot || Disabled ? 0 : Math.Max(0, Ms - (EnteredMs ?? 0));
    public bool IsNew => NewMs >= 30_000;   // half a minute: the open row ticking on is not news
}

public sealed class Piece
{
    public long S { get; set; }
    public long E { get; set; }
    public long Ms { get; set; }
}

public sealed class Group
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public List<Row> Rows { get; init; } = new();
}

public sealed class CommentInfo
{
    public string Text { get; init; } = "";
    public string Auto { get; init; } = "";
    public bool Edited { get; init; }
    public bool Stale { get; init; }
}

public sealed class DoneInfo
{
    public bool Marked { get; init; }
    public bool Stale { get; init; }
    public int Diff { get; init; }
}

public sealed class ProjectReport
{
    public string Name { get; init; } = "";
    public long ExactMs { get; init; }
    public int Rounded { get; init; }
    public List<Group> Groups { get; init; } = new();
    public CommentInfo Comment { get; set; } = new();
    public DoneInfo Done { get; set; } = new();
}

/// <summary>One line of "Інше": a project session (already counted) or an uncounted visit.</summary>
public sealed class TimelineItem
{
    public bool IsSession { get; init; }
    public string? Project { get; init; }
    public string Key { get; init; } = "";      // "o:<start>" for visits: stable id for moves
    public string RowKey { get; init; } = "";
    public long S { get; init; }
    public long E { get; set; }
    public long Ms { get; set; }
    public string Label { get; set; } = "";
    public string App { get; init; } = "";
    public string Proc { get; init; } = "";
    public bool Browser { get; init; }
    public string? Url { get; init; }
    public string? Moved { get; set; }
}

/// <summary>"Інше" of one program: its uncounted visits in time order and how long they took.</summary>
public sealed class OtherApp
{
    public string Name { get; init; } = "";
    public bool Browser { get; init; }
    public string Proc { get; init; } = "";
    /// <summary>Visits not moved into a project.</summary>
    public long Ms { get; init; }
    public List<TimelineItem> Visits { get; init; } = new();
}

public sealed class DayReport
{
    public List<OtherApp> OtherApps { get; init; } = new();
    /// <summary>All recorded time per program, projects and Інше together.</summary>
    public Dictionary<string, long> AppMs { get; init; } = new();
    /// <summary>Browser time per web service (Google Docs, Planfix, YouTube…).</summary>
    public Dictionary<string, long> BrowserMs { get; init; } = new();
    public List<ProjectReport> Projects { get; init; } = new();
    public List<TimelineItem> Timeline { get; init; } = new();
    public long OtherMs { get; init; }
    public int DayRounded { get; init; }
}

public static class Report
{
    public static readonly (string Id, string Title)[] Groups =
    {
        ("docs", "Документи"), ("web", "Вкладки"), ("apps", "Програми"), ("linked", "За правилом"),
        ("ctx", "За контекстом"), ("manual", "Вручну"),
    };

    const long MergeGap = 2 * TimeUtil.Min;    // pieces closer than this read as one visit
    const long CommentMin = 5 * TimeUtil.Min;  // a source enters the comment draft from 5 min a day
    const long MinProject = TimeUtil.Min;      // a project worked on at all counts at least a minute a day
    const long PieceGap = TimeUtil.Min;        // row pieces closer than this read as one stretch
    const long ContextGap = 15 * TimeUtil.Min; // task-manager pages follow the project worked on within 15 min

    static string LowerFirst(string s)
    {
        if (s.Length > 1 && char.IsLower(s[1])) return char.ToLower(s[0]) + s[1..];
        return s.Length == 1 ? s.ToLower() : s;
    }

    /// <summary>Rule-built comment: document names and tasks, biggest first. Only what BT knows for sure.</summary>
    public static string DraftComment(ProjectReport p)
    {
        var parts = new List<(string Phrase, long Ms)>();
        foreach (var g in p.Groups)
            foreach (var r in g.Rows)
            {
                if (r.Disabled || r.Ms < CommentMin) continue;
                if (g.Id == "docs" || (r.Task != "" && g.Id is "web" or "apps"))
                    parts.Add((r.Task != "" ? $"{r.Label} — {LowerFirst(r.Task)}" : r.Label, r.Ms));
            }
        var seen = new HashSet<string>();
        var outList = new List<string>();
        foreach (var (phrase, _) in parts.OrderByDescending(x => x.Ms))
            if (seen.Add(phrase.ToLowerInvariant())) outList.Add(outList.Count == 0 ? phrase : LowerFirst(phrase));
        var text = string.Join("; ", outList);
        return text.Length > 0 ? char.ToUpper(text[0]) + text[1..] : text;
    }

    sealed class Item
    {
        public Segment Seg = null!;
        public Cls C = null!;
        public bool Inferred;
    }

    static void InferFromContext(List<Item> items)
    {
        bool Real(int i) => items[i].C.Project != null && !items[i].Inferred;
        for (var i = 0; i < items.Count; i++)
        {
            var it = items[i];
            if (it.C.Project != null || !it.C.WorkTool) continue;
            string? project = null;
            for (var j = i - 1; j >= 0; j--)
            {
                if (it.Seg.S - items[j].Seg.E > ContextGap) break;
                if (Real(j)) { project = items[j].C.Project; break; }
            }
            if (project == null)
                for (var j = i + 1; j < items.Count; j++)
                {
                    if (items[j].Seg.S - it.Seg.E > ContextGap) break;
                    if (Real(j)) { project = items[j].C.Project; break; }
                }
            if (project != null)
            {
                it.C = it.C with { Group = "ctx", Project = project, RowKey = $"ctx:{it.C.Proc}", Label = $"{it.C.App}: сторінки без назви проєкту" };
                it.Inferred = true;
            }
        }
    }

    public static DayReport Build(IEnumerable<Segment> segs, DayState state, Settings settings)
    {
        var idx = Classifier.Index(settings);
        var projects = new Dictionary<string, Dictionary<string, Row>>();
        var order = new List<string>();
        var timeline = new List<TimelineItem>();

        Dictionary<string, Row> Proj(string name)
        {
            if (!projects.TryGetValue(name, out var p)) { p = new(); projects[name] = p; order.Add(name); }
            return p;
        }
        Row AddRow(string project, string group, string rowKey, long ms, string label, string task, string app, long s = 0, long e = 0)
        {
            var rows = Proj(project);
            var key = $"{project}|{rowKey}";
            if (!rows.TryGetValue(key, out var r))
            {
                r = new Row { Key = key, RowKey = rowKey, Group = group, Label = label, Task = task, App = app,
                    Disabled = state.Disabled.TryGetValue(key, out var d) && d };
                rows[key] = r;
            }
            r.Ms += ms;
            if (e > s)
            {
                if (r.Pieces.Count > 0 && s - r.Pieces[^1].E <= PieceGap) { r.Pieces[^1].E = Math.Max(r.Pieces[^1].E, e); r.Pieces[^1].Ms += ms; }
                else r.Pieces.Add(new Piece { S = s, E = e, Ms = ms });
            }
            return r;
        }

        var items = segs.Where(x => x.E > x.S).OrderBy(x => x.S)
            .Select(x => new Item { Seg = x, C = Classifier.Classify(new WindowInfo(x.Proc, x.Title, x.Url), settings, idx) })
            .Where(x => !Classifier.IsIgnored(new WindowInfo(x.Seg.Proc, x.Seg.Title), settings))
            .ToList();
        InferFromContext(items);

        var appMs = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var browserMs = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        static void Add(Dictionary<string, long> d, string k, long v) => d[k] = (d.TryGetValue(k, out var x) ? x : 0) + v;

        foreach (var it in items)
        {
            var seg = it.Seg;
            var c = it.C;
            var ms = seg.E - seg.S;
            Add(appMs, c.App, ms);
            if (c.Browser) Add(browserMs, Classifier.Service(seg.Title, c, settings), ms);
            var last = timeline.Count > 0 ? timeline[^1] : null;
            if (c.Project != null)
            {
                AddRow(c.Project, c.Group, c.RowKey, ms, c.Label, c.Task, c.App, seg.S, seg.E);
                if (last is { IsSession: true } && last.Project == c.Project && seg.S - last.E <= MergeGap)
                    last.E = Math.Max(last.E, seg.E);
                else
                    timeline.Add(new TimelineItem { IsSession = true, Project = c.Project, S = seg.S, E = seg.E });
            }
            else if (last is { IsSession: false } && last.RowKey == c.RowKey && seg.S - last.E <= MergeGap)
            {
                last.E = Math.Max(last.E, seg.E);
                last.Ms += ms;
            }
            else
            {
                timeline.Add(new TimelineItem { Key = $"o:{seg.S}", RowKey = c.RowKey, S = seg.S, E = seg.E, Ms = ms,
                    Label = c.Label, App = c.App, Proc = c.Proc, Browser = c.Browser, Url = seg.Url });
            }
        }

        foreach (var t in timeline.Where(t => !t.IsSession))
        {
            if (state.Moved.TryGetValue(t.Key, out var target) && settings.Projects.ContainsKey(target))
            {
                t.Moved = target;
                AddRow(target, "manual", $"moved:{t.Key}", t.Ms, $"{t.Label} (перенесено)", "", t.App, t.S, t.E);
            }
        }
        foreach (var m in state.Manual)
            if (settings.Projects.ContainsKey(m.Project))
                AddRow(m.Project, "manual", $"man:{m.Id}", m.Min * TimeUtil.Min, m.Label == "" ? "Вручну" : m.Label, "", "");

        var result = new List<ProjectReport>();
        foreach (var name in order)
        {
            var rows = projects[name].Values.ToList();
            foreach (var r in rows) r.RawMs = r.Ms;
            var groups = Groups.Select(g => new Group
            {
                Id = g.Id, Title = g.Title,
                Rows = rows.Where(r => r.Group == g.Id).OrderByDescending(r => r.Ms).ToList(),
            }).Where(g => g.Rows.Count > 0).ToList();
            // Rows count what was really spent; only a project as a whole is lifted to a minute,
            // so twenty 10-second task pages add up to ~3 min, not 20.
            var exact = rows.Where(r => !r.Disabled).Sum(r => r.Ms);
            if (exact > 0 && exact < MinProject) exact = MinProject;
            var rounded = TimeUtil.RoundMinutes(exact, settings.RoundStep);
            var p = new ProjectReport { Name = name, ExactMs = exact, Rounded = rounded, Groups = groups };

            var auto = DraftComment(p);
            var edited = state.Comments.TryGetValue(name, out var txt);
            p.Comment = new CommentInfo
            {
                Text = edited ? txt! : auto, Auto = auto, Edited = edited,
                Stale = edited && (!state.CommentBase.TryGetValue(name, out var b) || b != auto),
            };
            if (state.DoneRows.TryGetValue(name, out var snap))
                foreach (var r in rows)
                {
                    r.HasSnapshot = true;
                    r.EnteredMs = snap.TryGetValue(r.Key, out var was) ? was : null;
                }
            var hasMark = state.Done.TryGetValue(name, out var mark);
            p.Done = new DoneInfo { Marked = hasMark && mark == rounded, Stale = hasMark && mark != rounded, Diff = hasMark ? rounded - mark : 0 };
            result.Add(p);
        }
        result = result.OrderByDescending(p => p.ExactMs).ThenBy(p => p.Name, StringComparer.Ordinal).ToList();

        var otherApps = timeline.Where(t => !t.IsSession)
            .GroupBy(t => t.App == "" ? t.Proc : t.App, StringComparer.OrdinalIgnoreCase)
            .Select(g => new OtherApp
            {
                Name = g.First().App == "" ? g.First().Proc : g.First().App,
                Browser = g.Any(t => t.Browser), Proc = g.First().Proc,
                Ms = g.Where(t => t.Moved == null).Sum(t => t.Ms),
                Visits = g.OrderBy(t => t.S).ToList(),
            })
            .OrderByDescending(a => a.Ms).ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new DayReport
        {
            OtherApps = otherApps,
            AppMs = appMs,
            BrowserMs = browserMs,
            Projects = result,
            Timeline = timeline,
            OtherMs = timeline.Where(t => !t.IsSession && t.Moved == null).Sum(t => t.Ms),
            DayRounded = result.Sum(p => p.Rounded),
        };
    }
}
