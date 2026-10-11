namespace BisonTime.Core;

public static class TimeUtil
{
    public const long Min = 60_000;

    public static DateTime Local(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime().DateTime;

    public static long ToMs(DateTime local) => new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Local)).ToUnixTimeMilliseconds();

    /// <summary>Local calendar day as yyyy-MM-dd.</summary>
    public static string DayKey(long ms) => Local(ms).ToString("yyyy-MM-dd");

    public static string ShiftDay(string key, int delta) =>
        DateTime.ParseExact(key, "yyyy-MM-dd", null).AddDays(delta).ToString("yyyy-MM-dd");

    public static long NextMidnight(long ms) => ToMs(Local(ms).Date.AddDays(1));

    /// <summary>Split [s, e) at local midnights.</summary>
    public static IEnumerable<(string Day, long S, long E)> SplitByDay(long s, long e)
    {
        var cur = s;
        while (cur < e)
        {
            var cut = Math.Min(NextMidnight(cur), e);
            yield return (DayKey(cur), cur, cut);
            cur = cut;
        }
    }

    /// <summary>Nearest multiple of <paramref name="step"/> minutes, exact half up: 152 → 150, 153 → 155.</summary>
    public static int RoundMinutes(long ms, int step = 5)
    {
        if (step <= 1) return (int)Math.Round(ms / (double)Min, MidpointRounding.AwayFromZero);
        return (int)Math.Floor(ms / (double)(step * Min) + 0.5) * step;
    }

    /// <summary>"2 год 35 хв" — both parts, like the two fields of the report form.</summary>
    public static string FormatReport(int totalMin)
    {
        var t = Math.Max(0, totalMin);
        return $"{t / 60} год {t % 60} хв";
    }

    /// <summary>"&lt;1 хв", "12 хв", "1 год 05 хв".</summary>
    public static string FormatExact(long ms)
    {
        var min = (int)(ms / Min);
        if (min < 1) return "<1 хв";
        return min >= 60 ? $"{min / 60} год {min % 60:00} хв" : $"{min} хв";
    }

    public static string Clock(long ms) => Local(ms).ToString("HH:mm");
}
