namespace BisonTime.Core;

public enum Period { Day, Week, Month }

/// <summary>One slice of a top list: a name and its share of the time.</summary>
public sealed record Slice(string Name, long Ms, double Share);

/// <summary>Dashboard numbers: per-day stats kept for a year, summed over a day, a week or a month.</summary>
public static class Stats
{
    public const int KeepDays = 400;
    public const string Rest = "Решта";

    public static DayStats From(DayReport r) => new()
    {
        ProjectMs = r.Projects.Sum(p => p.ExactMs),
        OtherMs = r.OtherMs,
        Projects = r.Projects.Where(p => p.ExactMs > 0).ToDictionary(p => p.Name, p => p.ExactMs),
        Apps = new(r.AppMs),
        Browser = new(r.BrowserMs),
    };

    /// <summary>Recompute a day from its segments and store it; prune what is older than a year.</summary>
    public static DayStats Save(IStore store, string day, DayReport r)
    {
        var all = store.LoadStats();
        var st = From(r);
        if (st.TrackedMs == 0 && st.Apps.Count == 0) all.Remove(day); else all[day] = st;
        var today = TimeUtil.DayKey(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var limit = TimeUtil.ShiftDay(today, -KeepDays);
        foreach (var old in all.Keys.Where(d => string.CompareOrdinal(d, limit) < 0).ToList()) all.Remove(old);
        store.SaveStats(all);
        return st;
    }

    /// <summary>Days of the period that contains <paramref name="day"/>. Weeks start on Monday.</summary>
    public static List<string> Days(Period p, string day)
    {
        var d = DateTime.ParseExact(day, "yyyy-MM-dd", null);
        DateTime from, to;
        switch (p)
        {
            case Period.Week:
                from = d.AddDays(-(((int)d.DayOfWeek + 6) % 7));
                to = from.AddDays(6);
                break;
            case Period.Month:
                from = new DateTime(d.Year, d.Month, 1);
                to = from.AddMonths(1).AddDays(-1);
                break;
            default:
                from = to = d;
                break;
        }
        var res = new List<string>();
        for (var x = from; x <= to; x = x.AddDays(1)) res.Add(x.ToString("yyyy-MM-dd"));
        return res;
    }

    /// <summary>The same period one step back or forward.</summary>
    public static string Shift(Period p, string day, int delta)
    {
        var d = DateTime.ParseExact(day, "yyyy-MM-dd", null);
        d = p switch { Period.Week => d.AddDays(7 * delta), Period.Month => d.AddMonths(delta), _ => d.AddDays(delta) };
        return d.ToString("yyyy-MM-dd");
    }

    public static DayStats Sum(IEnumerable<DayStats> days)
    {
        var res = new DayStats();
        static void Merge(Dictionary<string, long> into, Dictionary<string, long> from)
        {
            foreach (var (k, v) in from) into[k] = (into.TryGetValue(k, out var x) ? x : 0) + v;
        }
        foreach (var d in days)
        {
            res.ProjectMs += d.ProjectMs;
            res.OtherMs += d.OtherMs;
            Merge(res.Projects, d.Projects);
            Merge(res.Apps, d.Apps);
            Merge(res.Browser, d.Browser);
        }
        return res;
    }

    /// <summary>Biggest <paramref name="n"/> entries, the rest folded into one "Решта" slice.</summary>
    public static List<Slice> Top(Dictionary<string, long> d, int n)
    {
        var total = d.Values.Where(v => v > 0).Sum();
        if (total == 0) return new();
        var sorted = d.Where(x => x.Value > 0).OrderByDescending(x => x.Value).ThenBy(x => x.Key, StringComparer.Ordinal).ToList();
        var res = sorted.Take(sorted.Count > n ? n - 1 : n).Select(x => new Slice(x.Key, x.Value, x.Value / (double)total)).ToList();
        var rest = total - res.Sum(x => x.Ms);
        if (rest > 0) res.Add(new Slice(Rest, rest, rest / (double)total));
        return res;
    }
}
