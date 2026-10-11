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
    bool WorkTool = false);

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
        ["screamingfrogseospider"] = "Screaming Frog", ["acrord32"] = "Acrobat", ["acrobat"] = "Acrobat",
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

    /// <summary>"https://WWW.Ti.ua/path" → "ti.ua"; "" when not a domain.</summary>
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

    /// <summary>"ti.ua / ТЗ №123 / Виправлення помилок" → (ti.ua, ТЗ №123, Виправлення помилок). Needs two segments.</summary>
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

    /// <summary>"paramedic.ua - SEO-комплекс / ТЗ" → "SEO-комплекс / ТЗ".</summary>
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
            if (m == "") continue;
            var isApp = string.Equals(m, w.Proc, StringComparison.OrdinalIgnoreCase)
                || string.Equals(m, w.Proc + ".exe", StringComparison.OrdinalIgnoreCase);
            if (appRules ? isApp : (!isApp && clean.Contains(m, StringComparison.OrdinalIgnoreCase))) return r;
        }
        return null;
    }

    public static Cls Classify(WindowInfo w, Settings s, Dictionary<string, string>? idx = null)
    {
        idx ??= Index(s);
        var browser = IsBrowser(w.Proc);
        var app = AppName(w.Proc);
        var clean = Clean(w.Title, browser);
        var shown = StripGoogle(clean);
        var group = browser ? "web" : "apps";
        string Key(string g, string label) => $"{g}:{(g == "apps" ? w.Proc + "|" : "")}{label.ToLowerInvariant()}";

        // 1. A title rule is the user's explicit word: it wins.
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

        // 4. A title that starts with a domain and a separator names a client: "covere2.eu - SEO-комплекс".
        var lead = LeadingDomain.Match(shown);
        if (lead.Success)
        {
            var d = NormalizeDomain(lead.Groups["d"].Value);
            if (d != "")
            {
                var project = ResolveDomain(d, idx) ?? d;
                var label = CutDomain(shown, d);
                if (label == "") label = app;
                return new Cls(group, project, Key(group, label), label, "", w.Proc, app, browser,
                    AutoProject: !s.Projects.ContainsKey(project));
            }
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
