using BisonTime.Core;
using static BisonTime.Core.TimeUtil;

var failed = 0;
var passed = 0;
void Test(string name, Action body)
{
    try { body(); passed++; }
    catch (Exception e) { failed++; Console.WriteLine($"FAIL {name}\n  {e.Message}"); }
}
void Eq<T>(T expected, T actual, string what = "")
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new Exception($"{what} expected <{expected}> got <{actual}>");
}
void True(bool v, string what) { if (!v) throw new Exception(what); }

Settings S(params string[] domains)
{
    var s = new Settings();
    foreach (var d in domains) s.Projects[d] = new ProjectCfg();
    return s;
}
Cls C(string proc, string title, Settings s) => Classifier.Classify(new WindowInfo(proc, title), s);

// ---------------- time ----------------
Test("rounding to nearest 5, half up", () =>
{
    Eq(150, RoundMinutes(152 * Min));
    Eq(155, RoundMinutes(153 * Min));
    Eq(155, RoundMinutes(152 * Min + 30_000));
    Eq(0, RoundMinutes(2 * Min));
});
Test("formats", () =>
{
    Eq("2 год 35 хв", FormatReport(155));
    Eq("0 год 30 хв", FormatReport(30));
    Eq("<1 хв", FormatExact(30_000));
    Eq("1 год 05 хв", FormatExact(65 * Min));
});
Test("midnight split", () =>
{
    var s = ToMs(new DateTime(2026, 10, 11, 23, 50, 0));
    var e = ToMs(new DateTime(2026, 10, 12, 0, 20, 0));
    var parts = SplitByDay(s, e).ToList();
    Eq(2, parts.Count);
    Eq(10 * Min, parts[0].E - parts[0].S);
    Eq("2026-10-12", parts[1].Day);
});

// ---------------- classify ----------------
Test("browser suffix and counters are cut", () =>
{
    Eq("gamma2.eu - SEO-комплекс", Classifier.Clean("(3) gamma2.eu - SEO-комплекс - Google Chrome", true));
    Eq("Inbox", Classifier.Clean("Inbox - Робочий - Microsoft​ Edge", true).Replace(" - Робочий", ""));
    Eq("beta.ua outreach.xlsx", Classifier.Clean("beta.ua outreach.xlsx - Excel", false));
});
Test("Google Docs convention in Chrome: project, doc, task", () =>
{
    var c = C("chrome", "shop.com.ua / ТЗ №123 / Виправлення помилок - Google Документи - Google Chrome", S("shop.com.ua"));
    Eq("docs", c.Group); Eq("shop.com.ua", c.Project); Eq("ТЗ №123", c.Label); Eq("Виправлення помилок", c.Task);
});
Test("new domain in a doc title names a new project", () =>
{
    var c = C("chrome", "newclient.com / Аудит - Google Docs - Google Chrome", S());
    Eq("newclient.com", c.Project); True(c.AutoProject, "auto");
});
Test("internal Inweb doc; random first segment is other", () =>
{
    Eq("Inweb", C("chrome", "Inweb / Навчання / GEO - Google Docs - Google Chrome", S()).Project);
    Eq(null, C("chrome", "Нотатки / мітинг - Google Docs - Google Chrome", S()).Project);
});
Test("known project domain anywhere in a title (Planfix task, Excel file)", () =>
{
    var s = S("alpha-med.ua", "beta.ua");
    var a = C("chrome", "alpha-med.ua - SEO-комплекс(KPI) / Технічне завдання №57 - Google Chrome", s);
    Eq("alpha-med.ua", a.Project); Eq("web", a.Group); Eq("SEO-комплекс(KPI) / Технічне завдання №57", a.Label);
    var x = C("excel", "beta.ua outreach.xlsx - Excel", s);
    Eq("beta.ua", x.Project); Eq("apps", x.Group);
    Eq(null, C("chrome", "myalpha-med.ua news - Google Chrome", s).Project);
    Eq(null, C("chrome", "alpha-med.uax - Google Chrome", s).Project);
});
Test("leading domain + separator names a client even if new; file names never do", () =>
{
    var c = C("chrome", "gamma2.eu - SEO-комплекс (Перший етап) - Google Chrome", S());
    Eq("gamma2.eu", c.Project); True(c.AutoProject, "auto");
    Eq(null, C("excel", "report.xlsx - Excel", S()).Project);
    Eq(null, C("winword", "brief.docx - Word", S()).Project);
});
Test("aliases and subdomains resolve to the project", () =>
{
    var s = S("new-brand.ua");
    s.Projects["new-brand.ua"].Aliases.Add("old-brand.com.ua");
    Eq("new-brand.ua", C("chrome", "old-brand.com.ua / Редиректи - Google Docs - Google Chrome", s).Project);
    Eq("new-brand.ua", C("chrome", "test.new-brand.ua - Адмінка - Google Chrome", s).Project);
});
Test("rules: title rule wins, app rule loses to a domain in the title", () =>
{
    var s = S("shop.com.ua", "beta.ua");
    s.Rules.Add(new Rule { Match = "Looker Studio", Project = "beta.ua" });
    s.Rules.Add(new Rule { Match = "figma", Project = "shop.com.ua" });
    Eq("beta.ua", C("chrome", "Звіт — Looker Studio - Google Chrome", s).Project);
    Eq("shop.com.ua", C("figma", "Головна — макет", s).Project);
    Eq("beta.ua", C("figma", "beta.ua — банер", s).Project);
});
Test("Planfix pages without a client are flagged for context", () =>
{
    var c = C("chrome", "Bison / Планувальник / inweb - Google Chrome", S());
    Eq(null, c.Project); True(c.WorkTool, "work tool");
});
Test("ignore by program or title fragment", () =>
{
    var s = S(); s.Ignore.Add("telegram"); s.Ignore.Add("YouTube");
    True(Classifier.IsIgnored(new WindowInfo("telegram", "Чати"), s), "app");
    True(Classifier.IsIgnored(new WindowInfo("chrome", "Музика - YouTube - Google Chrome"), s), "title");
    True(!Classifier.IsIgnored(new WindowInfo("chrome", "beta.ua - Google Chrome"), s), "not ignored");
});

Test("Screaming Frog crawl title: dashed URL names the client", () =>
{
    var c = C("javaw", "https-coffee-shop-com-ua- - Screaming Frog SEO Spider 20.2 (Licensed)", S());
    Eq("coffee-shop.com.ua", c.Project); True(c.AutoProject && c.AutoAfterMinute, "auto after a minute");
    Eq("Screaming Frog", c.App); Eq("Screaming Frog SEO Spider 20.2 (Licensed)", c.Label);
    // a dash inside the name, and a known project with its exact spelling
    Eq("old-brand.com.ua", C("javaw", "https-www-old-brand-com-ua- - Screaming Frog SEO Spider", S()).Project);
    Eq("shop.com.ua", C("javaw", "https-test-shop-com-ua- - Screaming Frog SEO Spider", S("shop.com.ua")).Project);
    Eq("gamma2.eu", C("javaw", "https-gamma2-eu-de- - Screaming Frog SEO Spider", S()).Project);
});
Test("URL written in a title names the client", () =>
{
    var c = C("chrome", "https://newclient.com.ua/catalog/ - Google Chrome", S());
    Eq("newclient.com.ua", c.Project); True(c.AutoAfterMinute, "after a minute");
    Eq(null, C("chrome", "https-only - Google Chrome", S()).Project);
});

// ---------------- report ----------------
var T0 = ToMs(new DateTime(2026, 10, 12, 10, 0, 0));
Segment Sg(double from, double to, string proc, string title) =>
    new() { S = T0 + (long)(from * Min), E = T0 + (long)(to * Min), Proc = proc, Title = title };
const string DocT = "shop.com.ua / ТЗ №123 / Виправлення помилок - Google Docs - Google Chrome";

Test("report: 30 min in a doc -> 0 год 30 хв + comment", () =>
{
    var r = Report.Build(new[] { Sg(0, 30, "chrome", DocT) }, new DayState(), S("shop.com.ua"));
    var p = r.Projects.Single();
    Eq(30, p.Rounded); Eq("ТЗ №123 — виправлення помилок", p.Comment.Text);
});
Test("report: a project counts at least a minute, rows count what they took", () =>
{
    var r = Report.Build(new[] { Sg(0, 0.3, "chrome", DocT), Sg(1, 1.2, "excel", "shop.com.ua.xlsx - Excel") }, new DayState(), S("shop.com.ua"));
    Eq(Min, r.Projects.Single().ExactMs);
    // twenty 10-second task pages: ~3.3 min, not 20
    var segs = Enumerable.Range(0, 20).Select(i => Sg(i, i + 1.0 / 6, "chrome", $"shop.com.ua — задача {i} - Planfix - Google Chrome")).ToArray();
    var p = Report.Build(segs, new DayState(), S("shop.com.ua")).Projects.Single();
    True(p.ExactMs >= 3 * Min && p.ExactMs < 4 * Min, $"got {p.ExactMs / 1000}s");
});
Test("report: a row keeps its pieces for the breakdown", () =>
{
    var segs = new[] { Sg(0, 10, "chrome", DocT), Sg(10.5, 20, "chrome", DocT), Sg(40, 45, "chrome", DocT) };
    var row = Report.Build(segs, new DayState(), S("shop.com.ua")).Projects[0].Groups[0].Rows[0];
    Eq(2, row.Pieces.Count);
    Eq(T0 + 20 * Min, row.Pieces[0].E);
    Eq(5 * Min, row.Pieces[1].Ms);
});
Test("report: disabled rows drop out; manual minutes count", () =>
{
    var segs = new[] { Sg(0, 33, "chrome", DocT), Sg(33, 45, "excel", "shop.com.ua.xlsx - Excel") };
    var st = new DayState();
    var key = Report.Build(segs, st, S("shop.com.ua")).Projects[0].Groups.First(g => g.Id == "apps").Rows[0].Key;
    st.Disabled[key] = true;
    st.Manual.Add(new ManualItem { Id = "m1", Project = "shop.com.ua", Min = 15, Label = "Дзвінок" });
    Eq(50, Report.Build(segs, st, S("shop.com.ua")).Projects[0].Rounded); // 33 + 15 = 48 -> 50
});
Test("report: Інше chronological with sessions; moves count into the project", () =>
{
    var segs = new[] { Sg(0, 20, "chrome", DocT), Sg(20, 32, "telegram", "Робочий чат"), Sg(32, 40, "chrome", DocT) };
    var r = Report.Build(segs, new DayState(), S("shop.com.ua"));
    Eq(3, r.Timeline.Count); True(r.Timeline[0].IsSession && !r.Timeline[1].IsSession, "order");
    Eq(12 * Min, r.OtherMs);
    var st = new DayState(); st.Moved[r.Timeline[1].Key] = "shop.com.ua";
    var r2 = Report.Build(segs, st, S("shop.com.ua"));
    Eq(40, r2.Projects[0].Rounded); Eq(0L, r2.OtherMs);
});
Test("report: Інше grouped by program, biggest first, time order inside", () =>
{
    var segs = new[]
    {
        Sg(0, 5, "telegram", "Робочий чат"), Sg(5, 6, "chrome", "Новини - Google Chrome"),
        Sg(6, 20, "chrome", DocT), Sg(20, 30, "telegram", "Інший чат"), Sg(30, 33, "sharex", "ShareX - Image Editor"),
    };
    var r = Report.Build(segs, new DayState(), S("shop.com.ua"));
    Eq(3, r.OtherApps.Count);
    var tg = r.OtherApps[0];
    Eq(15 * Min, tg.Ms); Eq(2, tg.Visits.Count); True(tg.Visits[0].S < tg.Visits[1].S, "chronological");
    Eq(r.OtherMs, r.OtherApps.Sum(a => a.Ms));
    var st = new DayState(); st.Moved[tg.Visits[1].Key] = "shop.com.ua";
    var moved = Report.Build(segs, st, S("shop.com.ua")).OtherApps;
    Eq(5 * Min, moved.Single(a => a.Visits.Any(v => v.Moved != null)).Ms);
});
Test("report: Planfix page goes to the project around it, not further than 15 min", () =>
{
    const string PF = "Bison / Планувальник / inweb - Google Chrome";
    var r = Report.Build(new[] { Sg(0, 20, "chrome", DocT), Sg(20, 25, "chrome", PF), Sg(25, 30, "chrome", DocT) }, new DayState(), S("shop.com.ua"));
    Eq(30, r.Projects[0].Rounded); Eq(0L, r.OtherMs);
    var far = Report.Build(new[] { Sg(0, 5, "chrome", PF), Sg(40, 50, "chrome", DocT) }, new DayState(), S("shop.com.ua"));
    Eq(5 * Min, far.OtherMs);
});
Test("report: edited comment kept, flagged stale on new sources; done mark", () =>
{
    var segs = new List<Segment> { Sg(0, 30, "chrome", DocT) };
    var auto = Report.Build(segs, new DayState(), S("shop.com.ua")).Projects[0].Comment.Auto;
    var st = new DayState();
    st.Comments["shop.com.ua"] = "Свій"; st.CommentBase["shop.com.ua"] = auto; st.Done["shop.com.ua"] = 30;
    var p = Report.Build(segs, st, S("shop.com.ua")).Projects[0];
    Eq("Свій", p.Comment.Text); True(!p.Comment.Stale, "fresh"); True(p.Done.Marked, "marked");
    segs.Add(Sg(30, 40, "chrome", "shop.com.ua / Семантика - Google Docs - Google Chrome"));
    var p2 = Report.Build(segs, st, S("shop.com.ua")).Projects[0];
    True(p2.Comment.Stale, "stale"); True(p2.Done.Stale, "done stale"); Eq(10, p2.Done.Diff);
});

// ---------------- tracker ----------------

(MemStore st, Tracker tr, Func<long> clock, Action<double, WindowInfo?, bool, bool> run, string day) Setup(DateTime? start = null)
{
    var now = ToMs(start ?? new DateTime(2026, 10, 12, 10, 0, 0));
    var store = new MemStore();
    long t = now;
    var tr = new Tracker(store, () => t);
    // feed one sample per second for `min` minutes
    void Run(double min, WindowInfo? w, bool idle, bool locked)
    {
        var steps = (int)(min * 60);
        for (var i = 0; i < steps; i++) { tr.Tick(w, idle, locked); t += 1000; }
    }
    return (store, tr, () => t, Run, DayKey(now));
}
long Total(DayFile f) => f.Segments.Sum(s => s.E - s.S);
var DOC = new WindowInfo("chrome", DocT);
var TG = new WindowInfo("telegram", "Чати");

Test("tracker: switching windows makes segments; doc names a new project", () =>
{
    var (st, tr, _, run, day) = Setup();
    run(30, DOC, false, false);
    run(10, TG, false, false);
    tr.Shutdown();
    var segs = st.DaysMap[day].Segments;
    Eq(2, segs.Count);
    True(Math.Abs(segs[0].E - segs[0].S - 30 * Min) <= 1000, "30 min doc");
    True(st.Settings.Projects.ContainsKey("shop.com.ua"), "project created");
});
Test("tracker: a URL in a title becomes a project only after a minute", () =>
{
    var (st, tr, _, run, _) = Setup();
    var sf = new WindowInfo("javaw", "https-coffee-shop-com-ua- - Screaming Frog SEO Spider 20.2 (Licensed)");
    run(0.5, sf, false, false);
    run(0.5, TG, false, false);
    True(!st.Settings.Projects.ContainsKey("coffee-shop.com.ua"), "30 s is not enough");
    run(2, sf, false, false);
    run(0.5, TG, false, false);
    True(st.Settings.Projects.ContainsKey("coffee-shop.com.ua"), "2 min creates it");
});
Test("tracker: idle keeps the minutes before the threshold, prompts after", () =>
{
    var (st, tr, _, run, day) = Setup();
    st.Settings.Projects["shop.com.ua"] = new ProjectCfg();
    run(13, DOC, false, false);       // 10 min work + 3 min reading until the idle threshold
    run(7, DOC, true, false);         // idle
    run(1, DOC, false, false);        // back
    tr.Shutdown();
    var f = st.DaysMap[day];
    True(Math.Abs(Total(f) - 14 * Min) <= 2000, $"counted {Total(f) / 1000}s");
    Eq(1, f.State.Idle.Count);
    True(Math.Abs(f.State.Idle[0].E - f.State.Idle[0].S - 7 * Min) <= 2000, "idle 7 min");
    Eq("shop.com.ua", f.State.Idle[0].Project);
});
Test("tracker: a call on screen is never idle", () =>
{
    var (st, tr, _, run, day) = Setup();
    var meet = new WindowInfo("chrome", "Meet – abc-defg-hij - Google Chrome");
    run(20, meet, true, false);    // 20 min of listening, no input at all
    tr.Shutdown();
    True(Math.Abs(Total(st.DaysMap[day]) - 20 * Min) <= 2000, "all 20 min counted");
    Eq(0, st.DaysMap[day].State.Idle.Count);
    True(Classifier.IsCall(new WindowInfo("zoom", "Zoom")), "zoom");
    True(!Classifier.IsCall(new WindowInfo("chrome", "Meeting notes - Google Docs - Google Chrome")), "doc about a meeting");
});
Test("tracker: locked screen is not offered back", () =>
{
    var (st, tr, _, run, day) = Setup();
    run(5, DOC, false, false);
    run(30, DOC, true, true);
    run(1, DOC, false, false);
    tr.Shutdown();
    Eq(0, st.DaysMap[day].State.Idle.Count);
    True(Math.Abs(Total(st.DaysMap[day]) - 6 * Min) <= 2000, "5 + 1 min");
});
Test("tracker: sleep closes at the last tick", () =>
{
    var (st, tr, clock, run, day) = Setup();
    run(20, DOC, false, false);
    // two hours without ticks: a fresh tracker over the same store sees the jump
    long t = clock() + 120 * Min;
    var tr2 = new Tracker(st, () => t);
    tr2.Tick(DOC, false, false);
    for (var i = 0; i < 300; i++) { t += 1000; tr2.Tick(DOC, false, false); }
    tr2.Shutdown();
    var segs = st.DaysMap[day].Segments;
    True(segs.Count == 2, $"segments {segs.Count}");
    True(Math.Abs(segs[0].E - segs[0].S - 20 * Min) <= 31_000, "first ~20 min (runtime saved every 30 s)");
    True(Math.Abs(segs[1].E - segs[1].S - 5 * Min) <= 2000, "second 5 min");
});
Test("tracker: pause stops counting whatever the windows do", () =>
{
    var (st, tr, _, run, day) = Setup();
    run(10, DOC, false, false);
    tr.Pause();
    run(30, DOC, false, false);
    tr.Resume();
    run(5, DOC, false, false);
    tr.Shutdown();
    True(Math.Abs(Total(st.DaysMap[day]) - 15 * Min) <= 2000, "10 + 5");
    var off = tr.DayOff(day);
    True(Math.Abs(off["pause"] - 30 * Min) <= 2000, "pause 30");
});
Test("tracker: flicks under 5 s dropped; ignored never recorded", () =>
{
    var (st, tr, _, run, day) = Setup();
    st.Settings.Ignore.Add("telegram");
    run(3.0 / 60, DOC, false, false);
    run(5, TG, false, false);
    tr.Shutdown();
    Eq(0, st.LoadDay(day).Segments.Count);
});
Test("tracker: midnight split", () =>
{
    var (st, tr, _, run, _) = Setup(new DateTime(2026, 10, 12, 23, 50, 0));
    run(30, DOC, false, false);
    tr.Shutdown();
    True(Math.Abs(Total(st.DaysMap["2026-10-12"]) - 10 * Min) <= 1000, "10 today");
    True(Math.Abs(Total(st.DaysMap["2026-10-13"]) - 20 * Min) <= 1000, "20 tomorrow");
});
Test("tracker: cleanup keeps 7 days of segments, 30 days of state", () =>
{
    var (st, tr, _, _, _) = Setup();
    st.DaysMap["2026-10-06"] = new DayFile { Segments = { new Segment { S = 1, E = 2 } } };
    st.DaysMap["2026-10-05"] = new DayFile { Segments = { new Segment { S = 1, E = 2 } } };
    st.DaysMap["2026-09-13"] = new DayFile();
    st.DaysMap["2026-09-12"] = new DayFile();
    tr.Cleanup();
    Eq(1, st.DaysMap["2026-10-06"].Segments.Count);
    Eq(0, st.DaysMap["2026-10-05"].Segments.Count);
    True(st.DaysMap.ContainsKey("2026-09-13") && !st.DaysMap.ContainsKey("2026-09-12"), "30 days");
});

// ---------------- file store ----------------
Test("file store round-trips settings and days with Cyrillic", () =>
{
    var dir = Path.Combine(Path.GetTempPath(), "bt-test-" + Guid.NewGuid().ToString("N"));
    var fs = new FileStore(dir);
    var s = fs.LoadSettings(); s.Projects["beta.ua"] = new ProjectCfg(); fs.SaveSettings(s);
    var d = fs.LoadDay("2026-10-12"); d.Segments.Add(new Segment { S = 1, E = 2, Proc = "chrome", Title = "ТЗ №5" }); fs.SaveDay("2026-10-12", d);
    var fs2 = new FileStore(dir);
    True(fs2.LoadSettings().Projects.ContainsKey("beta.ua"), "settings");
    Eq("ТЗ №5", fs2.LoadDay("2026-10-12").Segments[0].Title);
    True(File.ReadAllText(Path.Combine(dir, "days", "2026-10-12.json")).Contains("ТЗ №5"), "readable json");
    Directory.Delete(dir, true);
});

// ---------------- updates ----------------
string TmpDir() { var d = Path.Combine(Path.GetTempPath(), "bt-upd-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(d); return d; }
Test("updates: signed manifest verifies; tampered bytes or another key do not", () =>
{
    var app = TmpDir();
    File.WriteAllText(Path.Combine(app, "BisonTimeChecker.dll"), "v1");
    Directory.CreateDirectory(Path.Combine(app, "uk"));
    File.WriteAllText(Path.Combine(app, "uk", "res.dll"), "r");
    var (pub, priv) = Updates.NewKeys();
    var bytes = Updates.Serialize(Updates.Build(app, "1.2.0"));
    var sig = Updates.Sign(bytes, priv);
    var m = Updates.ParseVerified(bytes, sig, pub);
    True(m != null, "valid"); Eq(2, m!.Files.Count); Eq("uk/res.dll", m.Files[1].Path);
    var bad = (byte[])bytes.Clone(); bad[10] ^= 1;
    True(Updates.ParseVerified(bad, sig, pub) == null, "tampered");
    True(Updates.ParseVerified(bytes, sig, Updates.NewKeys().PublicKey) == null, "other key");
    Directory.Delete(app, true);
});
Test("updates: unsafe paths are rejected even when signed", () =>
{
    var (pub, priv) = Updates.NewKeys();
    foreach (var path in new[] { "../evil.dll", "C:/Windows/x.dll", "/etc/x", "a/../../b" })
    {
        var m = new UpdateManifest { Version = "9.9.9", Files = { new UpdateFile { Path = path, Size = 1, Sha256 = new string('a', 64) } } };
        var b = Updates.Serialize(m);
        True(Updates.ParseVerified(b, Updates.Sign(b, priv), pub) == null, path);
    }
});
Test("updates: only changed or missing files are downloaded; swap and rollback", () =>
{
    var app = TmpDir(); var next = TmpDir(); var dl = TmpDir();
    File.WriteAllText(Path.Combine(app, "a.dll"), "same");
    File.WriteAllText(Path.Combine(app, "b.dll"), "old");
    File.WriteAllText(Path.Combine(next, "a.dll"), "same");
    File.WriteAllText(Path.Combine(next, "b.dll"), "new");
    File.WriteAllText(Path.Combine(next, "c.dll"), "added");
    var m = Updates.Build(next, "1.2.0");
    var changed = Updates.Changed(m, app);
    Eq("b.dll,c.dll", string.Join(",", changed.Select(f => f.Path)));
    var items = changed.Select(f => { var t = Path.Combine(dl, f.Sha256); File.Copy(Path.Combine(next, f.Path), t); return (f, t); }).ToList();
    Updates.Apply(app, items);
    Eq("new", File.ReadAllText(Path.Combine(app, "b.dll"))); Eq("added", File.ReadAllText(Path.Combine(app, "c.dll")));
    True(File.Exists(Path.Combine(app, "b.dll.old")), "old kept until cleanup");
    Updates.Cleanup(app, dl);
    True(!File.Exists(Path.Combine(app, "b.dll.old")) && !Directory.Exists(dl), "cleaned");
    Eq(0, Updates.Changed(m, app).Count);

    // a corrupted download aborts before anything is touched
    File.WriteAllText(Path.Combine(next, "b.dll"), "newer");
    var m2 = Updates.Build(next, "1.3.0");
    var f2 = Updates.Changed(m2, app).Single();
    var bad = Path.Combine(TmpDir(), "x"); File.WriteAllText(bad, "corrupt");
    try { Updates.Apply(app, new[] { (f2, bad) }); throw new Exception("should fail"); } catch (InvalidDataException) { }
    Eq("new", File.ReadAllText(Path.Combine(app, "b.dll")));
    foreach (var d in new[] { app, next }) Directory.Delete(d, true);
});
Test("updates: version comparison", () =>
{
    True(Updates.IsNewer("1.10.0", "1.9.3"), "1.10 > 1.9");
    True(!Updates.IsNewer("1.2.0", "1.2.0"), "same");
    True(!Updates.IsNewer("garbage", "1.0.0"), "garbage");
});
Test("updates: newest release tag when nothing is signed yet", () =>
{
    var json = """[{"tag_name":"v1.2.0","draft":false,"prerelease":true},{"tag_name":"v1.10.0","draft":true},{"tag_name":"v1.1.0","draft":false},{"tag_name":"nightly"}]""";
    Eq("1.2.0", Updates.NewestVersion(json), "drafts and junk tags skipped");
    Eq(null, Updates.NewestVersion("[]"), "empty list");
    Eq(null, Updates.NewestVersion("<html>"), "not json");
});

// ---------------- finish day, dashboard ----------------
Test("tracker: finish day stops counting until 05:00, then resumes by itself", () =>
{
    var (st, tr, clock, run, day) = Setup(new DateTime(2026, 10, 12, 4, 40, 0));
    run(10, DOC, false, false);
    tr.FinishDay();
    True(tr.Done && !tr.Paused, "done, not paused");
    Eq(ToMs(new DateTime(2026, 10, 12, 5, 0, 0)), tr.DoneUntil!.Value);
    run(5, DOC, false, false);
    True(tr.Done, "still done at 04:55");
    Eq(1, st.DaysMap[day].Segments.Count);
    run(10, DOC, false, false);          // crosses 05:00
    True(!tr.Done, "resumed");
    tr.Shutdown();
    var segs = st.DaysMap[day].Segments;
    Eq(2, segs.Count);
    True(Math.Abs(segs[1].E - segs[1].S - 5 * Min) <= 2000, "counts again from 05:00");
    True(st.DaysMap[day].Off.All(o => o.Reason != "done"), "a finished evening is not 'поза обліком'");
    Eq(ToMs(new DateTime(2026, 10, 13, 5, 0, 0)), Tracker.NextMorning(ToMs(new DateTime(2026, 10, 12, 19, 30, 0))));
});
Test("tracker: resume right after finishing", () =>
{
    var (st, tr, _, run, day) = Setup();
    run(2, DOC, false, false);
    tr.FinishDay();
    tr.Resume();
    run(2, DOC, false, false);
    tr.Shutdown();
    True(!tr.Done, "resumed"); Eq(2, st.DaysMap[day].Segments.Count);
});
Test("bulk move suggests the shared site name as a rule", () =>
{
    Eq("Coffee Shop", Classifier.CommonFragment(new[]
    {
        "Харчові інгредієнти та інвентар | Coffee Shop", "Купити топери на торт — стильні | Coffee Shop",
        "Головна | Coffee Shop", "Перевірка розширених результатів – Google Search Console",
    }));
    Eq(null, Classifier.CommonFragment(new[] { "Новини дня", "Погода | Синоптик" }));
    Eq(null, Classifier.CommonFragment(new[] { "Головна | Coffee Shop" }));
});
Test("browser tabs map to services", () =>
{
    var s = S("shop.com.ua");
    string Sv(string title) => Classifier.Service(title, C("chrome", title, s), s);
    Eq("Google Docs", Sv(DocT));
    Eq("Google Sheets", Sv("Семантика - Google Таблиці - Google Chrome"));
    Eq("Google Meet", Sv("Meet – abc-defg-hij - Google Chrome"));
    Eq("Gmail", Sv("Вхідні (3) - me@example.com - Gmail - Google Chrome"));
    Eq("Planfix", Sv("Bison / Планувальник / inweb - Google Chrome"));
    Eq("YouTube", Sv("Як працює GEO - YouTube - Google Chrome"));
    Eq("Пошук Google", Sv("купити каву - Пошук Google - Google Chrome"));
    Eq(Classifier.ProjectSites, Sv("shop.com.ua - інтернет-магазин - Google Chrome"));
    Eq(Classifier.OtherTabs, Sv("Новини дня - Google Chrome"));
});
Test("dashboard: per-day stats, periods and top lists", () =>
{
    var segs = new[] { Sg(0, 30, "chrome", DocT), Sg(30, 40, "telegram", "Чат"), Sg(40, 45, "chrome", "Новини - Google Chrome") };
    var r = Report.Build(segs, new DayState(), S("shop.com.ua"));
    var d = Stats.From(r);
    Eq(30 * Min, d.ProjectMs); Eq(15 * Min, d.OtherMs); Eq(45 * Min, d.TrackedMs);
    Eq(35 * Min, d.Apps["Chrome"]); Eq(10 * Min, d.Apps["Telegram"]);
    Eq(30 * Min, d.Browser["Google Docs"]); Eq(5 * Min, d.Browser[Classifier.OtherTabs]);
    var week = Stats.Days(Period.Week, "2026-10-14");
    Eq("2026-10-12", week[0]); Eq("2026-10-18", week[^1]);
    var month = Stats.Days(Period.Month, "2026-02-10");
    Eq(28, month.Count);
    Eq("2026-09-14", Stats.Shift(Period.Week, "2026-09-21", -1));
    var sum = Stats.Sum(new[] { d, d });
    Eq(90 * Min, sum.TrackedMs); Eq(20 * Min, sum.Apps["Telegram"]);
    var top = Stats.Top(new Dictionary<string, long> { ["a"] = 50, ["b"] = 30, ["c"] = 15, ["d"] = 5 }, 3);
    Eq(3, top.Count); Eq(Stats.Rest, top[2].Name); Eq(20L, top[2].Ms);
    Eq(4, Stats.Top(new Dictionary<string, long> { ["a"] = 50, ["b"] = 30, ["c"] = 15, ["d"] = 5 }, 4).Count);
    var store = new MemStore();
    Stats.Save(store, DayKey(T0), r);
    Eq(45 * Min, store.Stats[DayKey(T0)].TrackedMs);
});

Console.WriteLine($"{passed} passed, {failed} failed");
return failed == 0 ? 0 : 1;
