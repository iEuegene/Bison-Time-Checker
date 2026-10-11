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
    Eq("covere2.eu - SEO-комплекс", Classifier.Clean("(3) covere2.eu - SEO-комплекс - Google Chrome", true));
    Eq("Inbox", Classifier.Clean("Inbox - Робочий - Microsoft​ Edge", true).Replace(" - Робочий", ""));
    Eq("ti.ua outreach.xlsx", Classifier.Clean("ti.ua outreach.xlsx - Excel", false));
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
    var s = S("paramedic.ua", "ti.ua");
    var a = C("chrome", "paramedic.ua - SEO-комплекс(KPI) / Технічне завдання №57 - Google Chrome", s);
    Eq("paramedic.ua", a.Project); Eq("web", a.Group); Eq("SEO-комплекс(KPI) / Технічне завдання №57", a.Label);
    var x = C("excel", "ti.ua outreach.xlsx - Excel", s);
    Eq("ti.ua", x.Project); Eq("apps", x.Group);
    Eq(null, C("chrome", "myparamedic.ua news - Google Chrome", s).Project);
    Eq(null, C("chrome", "paramedic.uax - Google Chrome", s).Project);
});
Test("leading domain + separator names a client even if new; file names never do", () =>
{
    var c = C("chrome", "covere2.eu - SEO-комплекс (Перший етап) - Google Chrome", S());
    Eq("covere2.eu", c.Project); True(c.AutoProject, "auto");
    Eq(null, C("excel", "report.xlsx - Excel", S()).Project);
    Eq(null, C("winword", "brief.docx - Word", S()).Project);
});
Test("aliases and subdomains resolve to the project", () =>
{
    var s = S("aquarum.ua");
    s.Projects["aquarum.ua"].Aliases.Add("aqua-room.com.ua");
    Eq("aquarum.ua", C("chrome", "aqua-room.com.ua / Редиректи - Google Docs - Google Chrome", s).Project);
    Eq("aquarum.ua", C("chrome", "test.aquarum.ua - Адмінка - Google Chrome", s).Project);
});
Test("rules: title rule wins, app rule loses to a domain in the title", () =>
{
    var s = S("shop.com.ua", "ti.ua");
    s.Rules.Add(new Rule { Match = "Looker Studio", Project = "ti.ua" });
    s.Rules.Add(new Rule { Match = "figma", Project = "shop.com.ua" });
    Eq("ti.ua", C("chrome", "Звіт — Looker Studio - Google Chrome", s).Project);
    Eq("shop.com.ua", C("figma", "Головна — макет", s).Project);
    Eq("ti.ua", C("figma", "ti.ua — банер", s).Project);
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
    True(!Classifier.IsIgnored(new WindowInfo("chrome", "ti.ua - Google Chrome"), s), "not ignored");
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
Test("report: every row counts at least a minute", () =>
{
    var r = Report.Build(new[] { Sg(0, 0.3, "chrome", DocT), Sg(1, 1.2, "excel", "shop.com.ua.xlsx - Excel") }, new DayState(), S("shop.com.ua"));
    Eq(2 * Min, r.Projects.Single().ExactMs);
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
    var s = fs.LoadSettings(); s.Projects["ti.ua"] = new ProjectCfg(); fs.SaveSettings(s);
    var d = fs.LoadDay("2026-10-12"); d.Segments.Add(new Segment { S = 1, E = 2, Proc = "chrome", Title = "ТЗ №5" }); fs.SaveDay("2026-10-12", d);
    var fs2 = new FileStore(dir);
    True(fs2.LoadSettings().Projects.ContainsKey("ti.ua"), "settings");
    Eq("ТЗ №5", fs2.LoadDay("2026-10-12").Segments[0].Title);
    True(File.ReadAllText(Path.Combine(dir, "days", "2026-10-12.json")).Contains("ТЗ №5"), "readable json");
    Directory.Delete(dir, true);
});

Console.WriteLine($"{passed} passed, {failed} failed");
return failed == 0 ? 0 : 1;
