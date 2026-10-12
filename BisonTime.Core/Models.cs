using System.Text.Json.Serialization;

namespace BisonTime.Core;

/// <summary>What is in front of the user right now: process name (lowercase, no .exe) + window title.</summary>
public sealed record WindowInfo(string Proc, string Title);

/// <summary>A stretch of time spent in one window. Times are Unix ms.</summary>
public sealed class Segment
{
    public long S { get; set; }
    public long E { get; set; }
    public string Proc { get; set; } = "";
    public string Title { get; set; } = "";
}

public sealed class ProjectCfg
{
    public bool Internal { get; set; }
    public bool Archived { get; set; }
    public List<string> Aliases { get; set; } = new();
}

/// <summary>"Window title (or program) contains <see cref="Match"/> → <see cref="Project"/>".</summary>
public sealed class Rule
{
    public string Match { get; set; } = "";
    public string Project { get; set; } = "";
}

public sealed class Settings
{
    public int IdleMin { get; set; } = 3;
    public int RoundStep { get; set; } = 5;
    public Dictionary<string, ProjectCfg> Projects { get; set; } = new() { ["Inweb"] = new ProjectCfg { Internal = true } };
    public List<Rule> Rules { get; set; } = new();
    /// <summary>Program names or title fragments that are never recorded.</summary>
    public List<string> Ignore { get; set; } = new();
    /// <summary>Title fragments of task managers serving every project (Planfix…).</summary>
    public List<string> WorkTools { get; set; } = new() { "Planfix", "Планувальник" };
    public bool Autostart { get; set; } = true;
    /// <summary>Once a day ask GitHub Releases whether a newer version exists.</summary>
    public bool AutoUpdateCheck { get; set; } = true;
    /// <summary>Unix ms of the last automatic check.</summary>
    public long LastUpdateCheck { get; set; }
}

/// <summary>Untracked stretch and why: focus | idle | lock | pause | sleep.</summary>
public sealed class OffPiece
{
    public long S { get; set; }
    public long E { get; set; }
    public string Reason { get; set; } = "";
}

public sealed class IdleItem
{
    public string Id { get; set; } = "";
    public long S { get; set; }
    public long E { get; set; }
    public string? Project { get; set; }
}

public sealed class ManualItem
{
    public string Id { get; set; } = "";
    public string Project { get; set; } = "";
    public int Min { get; set; }
    public string Label { get; set; } = "";
}

/// <summary>What the user decided about a day: toggles, moves, manual minutes, comments, marks.</summary>
public sealed class DayState
{
    public Dictionary<string, bool> Disabled { get; set; } = new();
    public List<ManualItem> Manual { get; set; } = new();
    public Dictionary<string, string> Moved { get; set; } = new();
    public Dictionary<string, string> Comments { get; set; } = new();
    public Dictionary<string, string> CommentBase { get; set; } = new();
    public Dictionary<string, int> Done { get; set; } = new();
    public List<IdleItem> Idle { get; set; } = new();
}

public sealed class DayFile
{
    public List<Segment> Segments { get; set; } = new();
    public List<OffPiece> Off { get; set; } = new();
    public DayState State { get; set; } = new();
}

/// <summary>Tracker state that must survive a crash or reboot.</summary>
/// <summary>
/// A day boiled down for the dashboard: minutes per project, program and browser service.
/// No window titles, so it is kept for a year while raw segments go after 7 days.
/// </summary>
public sealed class DayStats
{
    public long ProjectMs { get; set; }
    public long OtherMs { get; set; }
    public Dictionary<string, long> Projects { get; set; } = new();
    public Dictionary<string, long> Apps { get; set; } = new();
    public Dictionary<string, long> Browser { get; set; } = new();
    [System.Text.Json.Serialization.JsonIgnore] public long TrackedMs => ProjectMs + OtherMs;
}

public sealed class Runtime
{
    public OpenSeg? Open { get; set; }
    public long? PausedSince { get; set; }
    /// <summary>"Завершити роботу": nothing is counted until this moment (next morning, 05:00).</summary>
    public long? DoneUntil { get; set; }
    public OffState? Off { get; set; }
    public IdlePending? Idle { get; set; }
    public long LastTick { get; set; }
}

public sealed class OpenSeg
{
    public long S { get; set; }
    public long Beat { get; set; }
    public string Proc { get; set; } = "";
    public string Title { get; set; } = "";
}

public sealed class OffState
{
    public long S { get; set; }
    public string Reason { get; set; } = "";
}

public sealed class IdlePending
{
    public long S { get; set; }
    public string? Project { get; set; }
}

public sealed class LogEntry
{
    public long T { get; set; }
    public string Ev { get; set; } = "";
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Where { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Why { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? Sec { get; set; }
}
