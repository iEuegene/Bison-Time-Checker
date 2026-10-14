using System.Text.RegularExpressions;

namespace BisonTime.Core;

/// <summary>Where a window's time goes and how it is shown.</summary>
/// <param name="Group">docs | web | apps | linked | other</param>
public sealed record Cls(
    string Group,
    string? Project,
    string RowKey,
    string Label,
    string Task,
    string Proc,
    string App,
    bool Browser,
    bool AutoProject = false,
    bool WorkTool = false,
    bool AutoAfterMinute = false);

/// <summary>
/// Maps (program, window title) to a project. Pure and deterministic: the same input
/// and settings always give the same answer, so a day can be re-classified at any time.
/// </summary>
public static class Classifier
{
    static readonly HashSet<string> Browsers = new(StringComparer.OrdinalIgnoreCase)
        { "chrome", "msedge", "firefox", "opera", "brave", "vivaldi", "arc", "zen", "chromium" };

    static readonly Dictionary<string, string> AppNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["chrome"] = "Chrome", ["msedge"] = "Edge", ["firefox"] = "Firefox", ["opera"] = "Opera", ["brave"] = "Brave",
        ["vivaldi"] = "Vivaldi", ["excel"] = "Excel", ["winword"] = "Word", ["powerpnt"] = "PowerPoint",
        ["outlook"] = "Outlook", ["olk"] = "Outlook", ["telegram"] = "Telegram", ["explorer"] = "Провідник",
        ["code"] = "VS Code", ["claude"] = "Claude", ["slack"] = "Slack", ["zoom"] = "Zoom", ["ms-teams"] = "Teams",
        ["teams"] = "Teams", ["figma"] = "Figma", ["notepad"] = "Блокнот", ["viber"] = "Viber",
        ["screamingfrogseospider"] = "Screaming Frog", ["sharex"] = "ShareX", ["photoshop"] = "Photoshop",
        ["whatsapp"] = "WhatsApp", ["discord"] = "Discord", ["obsidian"] = "Obsidian", ["notion"] = "Notion", ["acrord32"] = "Acrobat", ["acrobat"] = "Acrobat",
    };

    static readonly Regex BrowserSuffix = new(
        @"\s+[-–—]\s+(?:Google Chrome|Microsoft\s*Edge|Mozilla Firefox|Firefox|Opera|Brave|Vivaldi|Arc|Chromium)\b.*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex MorePages = new(@"\s+(?:і ще|and)\s+\d+\s+\S+$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex LeadingNoise = new(@"^(?:\(\d+\+?\)\s*|[●•*]\s*)+", RegexOptions.Compiled);
    static readonly Regex OfficeSuffix = new(@"\s+[-–—]\s+(?:Excel|Word|PowerPoint|Outlook)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    static readonly Regex GoogleSuffix = new(
        @"\s+[-–—]\s+Google\s+(?:Docs|Sheets|Slides|Forms|Drawings|Документи|Таблиці|Презентації|Форми|Документы|Таблицы|Презентации|Формы)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    static readonly Regex DomainRe = new(@"^(?:[\p{L}\p{N}](?:[\p{L}\p{N}-]*[\p{L}\p{N}])?\.)+\p{L}{2,}$", RegexOptions.Compiled);
    static readonly Regex Ipv4 = new(@"^\d{1,3}(?:\.\d{1,3}){3}$", RegexOptions.Compiled);
    static readonly Regex LeadingDomain = new(@"^\s*(?<d>[\p{L}\p{N}][\p{L}\p{N}.-]*\.\p{L}{2,})\s+(?:[-–—|:]|/)\s", RegexOptions.Compiled);

    /// <summary>File extensions look like TLDs ("outreach.xlsx"): never treat them as domains.</summary>
    static readonly HashSet<string> FileExt = new(StringComparer.OrdinalIgnoreCase)
    {
        "doc", "docx", "xls", "xlsx", "xlsm", "csv", "ppt", "pptx", "pdf", "txt", "rtf", "odt", "ods", "png", "jpg",
        "jpeg", "gif", "svg", "webp", "zip", "rar", "7z", "exe", "msi", "html", "htm", "json", "xml", "md", "js",
        "ts", "py", "cs", "mp4", "mp3", "mov", "log", "ini", "psd", "fig", "sql", "yml", "yaml",
    };

    public static bool IsBrowser(string proc) => Browsers.Contains(proc);

    /// <summary>Web services recognisable by the tab title, first match wins. The browser shows no URL to BT.</summary>
    static readonly (string Name, Regex Re)[] Services = new (string, string)[]
    {
        ("Google Meet", @"(?:^|\s)Meet\s+[-–—]|Google Meet"),
        ("Google Docs", @"[-–—]\s+Google\s+(?:Docs|Документи|Документы)$"),
        ("Google Sheets", @"[-–—]\s+Google\s+(?:Sheets|Таблиці|Таблицы)$"),
        ("Google Slides", @"[-–—]\s+Google\s+(?:Slides|Презентації|Презентации)$"),
        ("Google Drive", @"Google\s+(?:Drive|Диск)"),
        ("Gmail", @"\bGmail\b"),
        ("Google Calendar", @"Google\s+(?:Calendar|Календар|Календарь)"),
        ("Search Console", @"Search Console"),
        ("Google Analytics", @"\bAnalytics\b"),
        ("Looker Studio", @"Looker Studio|Data Studio"),
        ("Google Ads", @"Google Ads"),
        ("Planfix", @"Planfix|ПланФ[іи]кс"),
        ("YouTube", @"YouTube"),
        ("Telegram", @"^Telegram\b|Telegram Web"),
        ("WhatsApp", @"WhatsApp"),
        ("Claude", @"\bClaude\b"),
        ("ChatGPT", @"ChatGPT"),
        ("Gemini", @"\bGemini\b"),
        ("LinkedIn", @"LinkedIn"),
        ("Facebook", @"Facebook"),
        ("Instagram", @"Instagram"),
        ("Ahrefs", @"Ahrefs"),
        ("Serpstat", @"Serpstat"),
        ("Semrush", @"Semrush"),
        ("GitHub", @"GitHub"),
        ("Пошук Google", @"[-–—]\s+(?:Пошук Google|Google Search|Поиск в Google)$"),
    }.Select(x => (x.Item1, new Regex(x.Item2, RegexOptions.IgnoreCase | RegexOptions.Compiled))).ToArray();

    static readonly HashSet<string> CallApps = new(StringComparer.OrdinalIgnoreCase) { "zoom", "ms-teams", "teams", "webex", "ciscocollabhost" };
    static readonly Regex CallTitle = new(@"(?:^|\s)Meet\s+[-–—]|Google Meet|Zoom Meeting|Zoom Workplace|\|\s*Microsoft Teams|Webex", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>A call is on screen: listening without touching the mouse is not idle.</summary>
    public static bool IsCall(WindowInfo w) => CallApps.Contains(w.Proc) || CallTitle.IsMatch(w.Title ?? "");

    static readonly Regex TitleParts = new(@"\s+[|–—-]\s+|\s*\|\s*", RegexOptions.Compiled);

    /// <summary>
    /// The site or brand most of these titles share ("… | Caffitalia"), to offer it as a rule after a bulk move.
    /// Null when nothing is shared by at least half of them (and by two).
    /// </summary>
    public static string? CommonFragment(IEnumerable<string> titles)
    {
        var list = titles.ToList();
        if (list.Count < 2) return null;
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in list)
            foreach (var part in TitleParts.Split(t).Select(x => x.Trim()).Where(x => x.Length is >= 3 and <= 40).Distinct(StringComparer.OrdinalIgnoreCase))
                counts[part] = (counts.TryGetValue(part, out var n) ? n : 0) + 1;
        var best = counts.Where(x => x.Value >= 2 && x.Value * 2 >= list.Count)
            .OrderByDescending(x => x.Value).ThenBy(x => x.Key.Length).Select(x => x.Key).FirstOrDefault();
        return best;
    }

    static readonly Regex DomainToken = new(@"(?:sc-domain:|https?://)?((?:[\p{L}\p{N}](?:[\p{L}\p{N}-]*[\p{L}\p{N}])?\.)+\p{L}{2,})", RegexOptions.Compiled);

    /// <summary>
    /// Address bar text → "host other.domain …": the host plus domain-looking values from the path and query
    /// (Search Console resource, Ahrefs/Serpstat target). Nothing else of the URL is kept: paths can carry
    /// tokens and personal data. Null while the user is typing a search or the text is not a URL.
    /// </summary>
    public static string? ReduceUrl(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var t = raw.Trim();
        if (t.Contains(' ')) return null;                                   // a search being typed
        if (!Regex.IsMatch(t, "^[a-z][a-z0-9+.-]*://", RegexOptions.IgnoreCase)) t = "https://" + t;
        if (!Uri.TryCreate(t, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return null;
        var host = NormalizeDomain(uri.Host);
        if (host == "") return null;
        var parts = new List<string> { host };
        var rest = Uri.UnescapeDataString(uri.AbsolutePath + " " + uri.Query.Replace('&', ' ').Replace('=', ' '));
        foreach (Match m in DomainToken.Matches(rest))
        {
            var d = NormalizeDomain(m.Groups[1].Value);
            if (d != "" && !parts.Contains(d)) parts.Add(d);
            if (parts.Count >= 6) break;
        }
        // Tools that name the client by an id instead of a domain (Serpstat project, GA4 property, Planfix task,
        // Looker report): keep "host#id" so a rule can bind that id to a project. Ids only, never the path.
        var anchor = AnchorId.Match(Uri.UnescapeDataString(uri.AbsolutePath + uri.Fragment));
        if (anchor.Success) parts.Add($"{host}#{anchor.Groups["id"].Value.ToLowerInvariant()}");
        return string.Join(' ', parts);
    }

    static readonly Regex AnchorId = new(
        @"(?:^|[/#])(?:a\d+)?(?<id>p?\d{5,}|[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})(?=$|[/?#&])",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>The known project a reduced URL points to: the host first, then domains named inside it.</summary>
    public static string? UrlProject(string? reduced, Dictionary<string, string> idx)
    {
        if (string.IsNullOrWhiteSpace(reduced)) return null;
        foreach (var d in reduced.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            if (!d.Contains('#') && ResolveDomain(d, idx) is { } p) return p;
        return null;
    }

    public const string UrlRulePrefix = "url:";

    /// <summary>"serpstat.com#1291746" of a reduced URL, if it has one: what an address rule binds to a project.</summary>
    public static string? UrlAnchor(string? reduced) =>
        reduced?.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(x => x.Contains('#'));

    /// <summary>For people: "serpstat.com · 1291746".</summary>
    public static string DescribeAnchor(string anchor) => anchor.Replace("#", " · ");

    public const string ProjectSites = "Сайти й задачі проєктів";
    public const string OtherTabs = "Інші вкладки";

    /// <summary>Browser tab → service for the dashboard: Google Docs, Planfix, YouTube… or a fallback bucket.</summary>
    public static string Service(string title, Cls c, Settings s)
    {
        var t = Clean(title, browser: true);
        foreach (var (name, re) in Services)
            if (re.IsMatch(t)) return name;
        if (s.WorkTools.Any(w => w != "" && t.Contains(w, StringComparison.OrdinalIgnoreCase))) return "Planfix";
        return c.Project != null ? ProjectSites : OtherTabs;
    }

    public static string AppName(string proc) =>
        AppNames.TryGetValue(proc, out var n) ? n : (proc.Length > 0 ? char.ToUpper(proc[0]) + proc[1..] : "Програма");

    /// <summary>Window title → what the user sees: browser name, counters and Office suffixes removed.</summary>
    public static string Clean(string title, bool browser)
    {
        var t = (title ?? "").Replace("​", "").Trim();
        if (browser)
        {
            t = BrowserSuffix.Replace(t, "");
            t = MorePages.Replace(t, "");
        }
        t = LeadingNoise.Replace(t, "");
        t = OfficeSuffix.Replace(t, "");
        return t.Trim();
    }

    public static string StripGoogle(string title) => GoogleSuffix.Replace(title ?? "", "").Trim();

    public static bool IsDomainLike(string s)
    {
        if (string.IsNullOrEmpty(s) || s.Any(char.IsWhiteSpace) || Ipv4.IsMatch(s) || !DomainRe.IsMatch(s)) return false;
        return !FileExt.Contains(s[(s.LastIndexOf('.') + 1)..]);
    }

    /// <summary>"https://WWW.Beta.ua/path" → "beta.ua"; "" when not a domain.</summary>
    public static string NormalizeDomain(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var s = raw.Trim().ToLowerInvariant();
        s = Regex.Replace(s, "^sc-domain:", "");
        s = Regex.Replace(s, "^[a-z][a-z0-9+.-]*://", "");
        s = s.Split('/', '?', '#')[0];
        s = Regex.Replace(s, ":\\d+$", "");
        if (s.StartsWith("www.")) s = s[4..];
        s = s.TrimEnd('.');
        return IsDomainLike(s) ? s : "";
    }

    /// <summary>lowercase key (name, domain, alias) → canonical project name.</summary>
    public static Dictionary<string, string> Index(Settings s)
    {
        var idx = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, p) in s.Projects)
        {
            if (p.Archived) continue;
            idx[name.ToLowerInvariant()] = name;
            foreach (var a in p.Aliases)
            {
                var d = NormalizeDomain(a);
                var key = d != "" ? d : a.Trim().ToLowerInvariant();
                if (key != "") idx[key] = name;
            }
        }
        return idx;
    }

    /// <summary>Known project for a domain, walking up subdomains: test.shop.com.ua → shop.com.ua.</summary>
    public static string? ResolveDomain(string? domain, Dictionary<string, string> idx)
    {
        var h = NormalizeDomain(domain);
        if (h == "") return null;
        var labels = h.Split('.');
        for (var i = 0; i < labels.Length - 1; i++)
            if (idx.TryGetValue(string.Join('.', labels[i..]), out var hit)) return hit;
        return null;
    }

    public sealed record DocParts(string First, string Doc, string Task);

    /// <summary>"beta.ua / ТЗ №123 / Виправлення помилок" → (beta.ua, ТЗ №123, Виправлення помилок). Needs two segments.</summary>
    public static DocParts? ParseDocTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;
        var parts = Regex.Split(StripGoogle(title), @"\s+/\s+").Select(x => x.Trim()).Where(x => x != "").ToArray();
        if (parts.Length < 2) return null;
        return new DocParts(parts[0], parts[1], string.Join(" / ", parts[2..]));
    }

    /// <summary>Project named by a first segment: a domain (known or new) or an internal project like Inweb.</summary>
    public static string? ProjectFromSegment(string first, Settings s, Dictionary<string, string> idx)
    {
        var d = NormalizeDomain(first);
        if (d != "") return ResolveDomain(d, idx) ?? d;
        if (idx.TryGetValue(first.Trim().ToLowerInvariant(), out var name) && s.Projects.TryGetValue(name, out var p) && p.Internal)
            return name;
        return null;
    }

    static bool EdgeOk(char? c) => c is null || !(char.IsLetterOrDigit(c.Value) || c == '-' || c == '.');

    /// <summary>First known project domain written in a title as a whole token. Internal names are skipped: too common.</summary>
    public static (string Project, string Match)? ProjectInTitle(string? title, Dictionary<string, string> idx)
    {
        if (string.IsNullOrEmpty(title)) return null;
        var low = title.ToLowerInvariant();
        (int Index, string Match, string Project)? best = null;
        foreach (var (key, project) in idx)
        {
            if (!key.Contains('.')) continue;
            var from = 0;
            while (from <= low.Length)
            {
                var i = low.IndexOf(key, from, StringComparison.Ordinal);
                if (i < 0) break;
                char? before = i == 0 ? null : low[i - 1];
                var endAt = i + key.Length;
                char? after = endAt < low.Length ? low[endAt] : null;
                char? after2 = endAt + 1 < low.Length ? low[endAt + 1] : null;
                var okAfter = EdgeOk(after) || (after == '.' && (after2 is null || !char.IsLetterOrDigit(after2.Value)))
                    || (after == '.' && FileExt.Contains(new string(low.Skip(endAt + 1).TakeWhile(char.IsLetterOrDigit).ToArray())));
                if (EdgeOk(before) && okAfter)
                {
                    if (best is null || i < best.Value.Index || (i == best.Value.Index && key.Length > best.Value.Match.Length))
                        best = (i, key, project);
                    break;
                }
                from = i + 1;
            }
        }
        return best is null ? null : (best.Value.Project, best.Value.Match);
    }

    /// <summary>"alpha-med.ua - SEO-комплекс / ТЗ" → "SEO-комплекс / ТЗ".</summary>
    public static string CutDomain(string title, string match)
    {
        var i = title.ToLowerInvariant().IndexOf(match, StringComparison.Ordinal);
        var cut = i < 0 ? title : title[..i] + title[(i + match.Length)..];
        cut = Regex.Replace(cut, @"^[\s\-–—|/:·•,]+|[\s\-–—|/:·•,]+$", "");
        return Regex.Replace(cut, @"\s{2,}", " ").Trim();
    }

    /// <summary>Program name or title fragment on the ignore list.</summary>
    public static bool IsIgnored(WindowInfo w, Settings s) =>
        s.Ignore.Any(x => !string.IsNullOrWhiteSpace(x) &&
            (string.Equals(x.Trim(), w.Proc, StringComparison.OrdinalIgnoreCase) ||
             w.Title.Contains(x.Trim(), StringComparison.OrdinalIgnoreCase)));

    static Rule? MatchRule(WindowInfo w, string clean, Settings s, bool appRules)
    {
        foreach (var r in s.Rules)
        {
            var m = r.Match.Trim();
            if (m == "" || m.StartsWith(UrlRulePrefix, StringComparison.OrdinalIgnoreCase)) continue;
            var isApp = string.Equals(m, w.Proc, StringComparison.OrdinalIgnoreCase)
                || string.Equals(m, w.Proc + ".exe", StringComparison.OrdinalIgnoreCase);
            if (appRules ? isApp : (!isApp && clean.Contains(m, StringComparison.OrdinalIgnoreCase))) return r;
        }
        return null;
    }

    static readonly Regex UrlInTitle = new(@"https?://(?<h>[\p{L}\p{N}](?:[\p{L}\p{N}.-]*[\p{L}\p{N}])?)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex DashedUrl = new(@"(?<![\p{L}\p{N}])(?<t>https?-(?<s>[a-z0-9]+(?:-[a-z0-9]+)+)-*)(?![\p{L}\p{N}])", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Public suffixes seen in client work, as dash-separated tokens (Screaming Frog slugs lose the dots).</summary>
    static readonly string[] Suffix2 = { "com.ua", "net.ua", "org.ua", "in.ua", "kiev.ua", "kyiv.ua", "od.ua", "lviv.ua", "dp.ua", "kh.ua", "co.uk", "com.pl", "co.il", "com.cy", "com.tr" };
    static readonly HashSet<string> Suffix1 = new(StringComparer.OrdinalIgnoreCase)
    {
        // Only zones that are not also common words in names ("shop", "store", "online" stay out).
        "ua", "com", "net", "org", "eu", "info", "biz", "pro", "io", "pl", "lv", "lt", "ee", "de", "uk", "cy", "md", "ge", "cz", "sk",
        "at", "ch", "fr", "it", "es", "nl", "be", "us", "ca",
    };

    /// <summary>"coffee-shop-com-ua" → "coffee-shop.com.ua"; known projects win, then the first public suffix from the left.</summary>
    public static string? UndashDomain(string slug, Dictionary<string, string> idx)
    {
        var sl = slug.Trim('-').ToLowerInvariant();
        if (sl.StartsWith("www-")) sl = sl[4..];
        string? best = null;
        foreach (var key in idx.Keys)
        {
            if (!key.Contains('.')) continue;
            var dashed = key.Replace('.', '-');
            if (("-" + sl + "-").Contains("-" + dashed + "-") && (best is null || key.Length > best.Length)) best = key;
        }
        if (best != null) return best;
        var t = sl.Split('-', StringSplitOptions.RemoveEmptyEntries);
        // Two-level zones first anywhere (com-ua), then single zones: "best-com-ua" is best.com.ua, not best.com.
        for (var i = 1; i + 1 < t.Length; i++)
            if (Suffix2.Contains($"{t[i]}.{t[i + 1]}"))
                return NormalizeDomain($"{string.Join('-', t[..i])}.{t[i]}.{t[i + 1]}") is { Length: > 0 } d2 ? d2 : null;
        for (var i = 1; i < t.Length; i++)
            if (Suffix1.Contains(t[i]))
                return NormalizeDomain($"{string.Join('-', t[..i])}.{t[i]}") is { Length: > 0 } d1 ? d1 : null;
        return null;
    }

    /// <summary>A domain taken from a URL written in a title. Returns the domain and the exact text it came from.</summary>
    public static (string Domain, string Token)? DomainFromUrl(string title, Dictionary<string, string> idx)
    {
        var m = UrlInTitle.Match(title);
        if (m.Success && NormalizeDomain(m.Groups["h"].Value) is { Length: > 0 } d) return (d, m.Value);
        var dm = DashedUrl.Match(title);
        if (dm.Success && UndashDomain(dm.Groups["s"].Value, idx) is { } u) return (u, dm.Groups["t"].Value);
        return null;
    }

    public static Cls Classify(WindowInfo w, Settings s, Dictionary<string, string>? idx = null)
    {
        idx ??= Index(s);
        var browser = IsBrowser(w.Proc);
        var app = w.Title.Contains("Screaming Frog", StringComparison.OrdinalIgnoreCase) ? "Screaming Frog" : AppName(w.Proc);
        var clean = Clean(w.Title, browser);
        var shown = StripGoogle(clean);
        var group = browser ? "web" : "apps";
        string Key(string g, string label) => $"{g}:{(g == "apps" ? w.Proc + "|" : "")}{label.ToLowerInvariant()}";

        // 1. A rule is the user's explicit word: it wins. Address rules ("url:serpstat.com#1291746") first.
        var urlRule = s.ReadUrls && UrlAnchor(w.Url) is { } anchor
            ? s.Rules.FirstOrDefault(r => r.Match.StartsWith(UrlRulePrefix, StringComparison.OrdinalIgnoreCase)
                && string.Equals(r.Match[UrlRulePrefix.Length..].Trim(), anchor, StringComparison.OrdinalIgnoreCase))
            : null;
        if (urlRule != null && idx.TryGetValue(urlRule.Project.ToLowerInvariant(), out var up))
            return new Cls("linked", up, $"link:{w.Proc}|{shown.ToLowerInvariant()}", shown == "" ? app : shown, "", w.Proc, app, browser);
        var rule = MatchRule(w, clean, s, appRules: false);
        if (rule != null && idx.TryGetValue(rule.Project.ToLowerInvariant(), out var rp))
            return new Cls("linked", rp, $"link:{w.Proc}|{shown.ToLowerInvariant()}", shown == "" ? app : shown, "", w.Proc, app, browser);

        // 2. "domain / document / task" (Google Docs and anything named the same way).
        var doc = ParseDocTitle(clean);
        var docProject = doc is null ? null : ProjectFromSegment(doc.First, s, idx);
        if (doc != null && docProject != null)
        {
            var g = GoogleSuffix.IsMatch(clean) ? "docs" : group;
            return new Cls(g, docProject, Key(g, $"{doc.Doc}|{doc.Task}"), doc.Doc, doc.Task, w.Proc, app, browser,
                AutoProject: !s.Projects.ContainsKey(docProject) && NormalizeDomain(docProject) != "");
        }

        // 3. A known project's domain anywhere in the title (Planfix task, CRM, Excel file, search).
        var named = ProjectInTitle(clean, idx);
        if (named != null)
        {
            var label = CutDomain(StripGoogle(clean), named.Value.Match);
            if (label == "") label = app;
            var g = GoogleSuffix.IsMatch(clean) ? "docs" : group;
            return new Cls(g, named.Value.Project, Key(g, label), label, "", w.Proc, app, browser);
        }

        // 3b. The browser's address: the client's site under a brand title ("… | Coffee Shop"), or a tool
        //     that carries the client's domain in its URL (Search Console resource, Ahrefs target…). Known projects only.
        if (browser && s.ReadUrls && UrlProject(w.Url, idx) is { } byUrl)
        {
            var label = shown == "" ? app : shown;
            var g = GoogleSuffix.IsMatch(clean) ? "docs" : group;
            return new Cls(g, byUrl, Key(g, label), label, "", w.Proc, app, browser);
        }

        // 4. A title that starts with a domain and a separator names a client: "gamma2.eu - SEO-комплекс".
        var lead = LeadingDomain.Match(shown);
        if (lead.Success)
        {
            var d = NormalizeDomain(lead.Groups["d"].Value);
            // "coffee-shop.com.ua — інтернет-магазин" on that site itself is a site naming itself, not a client:
            // a new project only when the domain is named somewhere else (task manager, document, file).
            var ownSite = browser && w.Url != null && ResolveDomain(w.Url.Split(' ')[0], new Dictionary<string, string> { [d] = d }) != null;
            if (d != "" && !(ownSite && !s.Projects.ContainsKey(ResolveDomain(d, idx) ?? d)))
            {
                var project = ResolveDomain(d, idx) ?? d;
                var label = CutDomain(shown, d);
                if (label == "") label = app;
                return new Cls(group, project, Key(group, label), label, "", w.Proc, app, browser,
                    AutoProject: !s.Projects.ContainsKey(project));
            }
        }

        // 4b. A URL in the title — written normally ("https://site.com/") or the way Screaming Frog
        //     names a crawl ("https-coffee-shop-com-ua-"). A new client is added after a minute of work.
        var fromUrl = DomainFromUrl(clean, idx);
        if (fromUrl != null)
        {
            var project = ResolveDomain(fromUrl.Value.Domain, idx) ?? fromUrl.Value.Domain;
            var label = CutDomain(shown.Replace(fromUrl.Value.Token, fromUrl.Value.Domain), fromUrl.Value.Domain);
            label = Regex.Replace(label, @"^(?:https?://|https?-)", "").Trim(' ', '-', '/');
            if (label == "") label = app;
            return new Cls(group, project, Key(group, label), label, "", w.Proc, app, browser,
                AutoProject: !s.Projects.ContainsKey(project), AutoAfterMinute: true);
        }

        // 5. A whole program assigned to a project ("figma → shop.com.ua").
        var appRule = MatchRule(w, clean, s, appRules: true);
        if (appRule != null && idx.TryGetValue(appRule.Project.ToLowerInvariant(), out var ap))
            return new Cls("linked", ap, $"link:{w.Proc}", app, "", w.Proc, app, browser);

        // 6. Task manager pages without a client in the title: the report infers from context.
        var work = s.WorkTools.Any(k => !string.IsNullOrWhiteSpace(k) && clean.Contains(k.Trim(), StringComparison.OrdinalIgnoreCase));
        return new Cls("other", null, $"o:{w.Proc}|{shown.ToLowerInvariant()}", shown == "" ? app : shown, "", w.Proc, app, browser,
            WorkTool: work);
    }
}
