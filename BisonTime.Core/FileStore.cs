using System.Text.Encodings.Web;
using System.Text.Json;

namespace BisonTime.Core;

/// <summary>
/// JSON files in one folder (%LOCALAPPDATA%\BisonTime on Windows): settings.json, runtime.json,
/// days/yyyy-MM-dd.json, log.txt. Write-through cache; every write is temp file + rename.
/// </summary>
public sealed class FileStore : IStore
{
    public const int LogSize = 400;

    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // keep Cyrillic readable in the files
    };

    readonly string _dir;
    readonly Dictionary<string, DayFile> _days = new();
    Settings? _settings;
    readonly List<LogEntry> _log = new();
    bool _logLoaded;

    public string Dir => _dir;
    public event Action? SettingsChanged;

    public FileStore(string dir)
    {
        _dir = dir;
        Directory.CreateDirectory(Path.Combine(_dir, "days"));
    }

    string P(params string[] parts) => Path.Combine(new[] { _dir }.Concat(parts).ToArray());

    T Read<T>(string path) where T : new()
    {
        try
        {
            if (File.Exists(path)) return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json) ?? new T();
        }
        catch (Exception)
        {
            // A corrupt file must not take the whole day down: keep a copy, start fresh.
            try { File.Copy(path, path + ".broken", overwrite: true); } catch { /* best effort */ }
        }
        return new T();
    }

    void Write<T>(string path, T value)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, Json));
        File.Move(tmp, path, overwrite: true);
    }

    public Settings LoadSettings()
    {
        if (_settings != null) return _settings;
        var path = P("settings.json");
        _settings = File.Exists(path) ? Read<Settings>(path) : new Settings();
        return _settings;
    }

    public void SaveSettings(Settings s)
    {
        _settings = s;
        Write(P("settings.json"), s);
        SettingsChanged?.Invoke();
    }

    public DayFile LoadDay(string day)
    {
        if (_days.TryGetValue(day, out var f)) return f;
        f = Read<DayFile>(P("days", day + ".json"));
        _days[day] = f;
        return f;
    }

    public void SaveDay(string day, DayFile f)
    {
        _days[day] = f;
        Write(P("days", day + ".json"), f);
    }

    public IEnumerable<string> Days() =>
        Directory.EnumerateFiles(P("days"), "*.json").Select(Path.GetFileNameWithoutExtension).OfType<string>().OrderBy(x => x);

    public void DeleteDay(string day)
    {
        _days.Remove(day);
        var path = P("days", day + ".json");
        if (File.Exists(path)) File.Delete(path);
    }

    public Runtime LoadRuntime() => Read<Runtime>(P("runtime.json"));

    public void SaveRuntime(Runtime r) => Write(P("runtime.json"), r);

    void LoadLog()
    {
        if (_logLoaded) return;
        _logLoaded = true;
        var path = P("log.txt");
        if (!File.Exists(path)) return;
        foreach (var line in File.ReadLines(path).TakeLast(LogSize))
        {
            try { if (JsonSerializer.Deserialize<LogEntry>(line, Json) is { } e) _log.Add(e); } catch { /* skip */ }
        }
    }

    public void Log(LogEntry e)
    {
        LoadLog();
        _log.Add(e);
        var compact = new JsonSerializerOptions(Json) { WriteIndented = false };
        try
        {
            if (_log.Count > LogSize * 2)
            {
                _log.RemoveRange(0, _log.Count - LogSize);
                File.WriteAllLines(P("log.txt"), _log.Select(x => JsonSerializer.Serialize(x, compact)));
            }
            else
            {
                File.AppendAllText(P("log.txt"), JsonSerializer.Serialize(e, compact) + Environment.NewLine);
            }
        }
        catch { /* diagnostics must never break tracking */ }
    }

    Dictionary<string, DayStats>? _stats;

    public Dictionary<string, DayStats> LoadStats() => _stats ??= Read<Dictionary<string, DayStats>>(P("stats.json"));

    public void SaveStats(Dictionary<string, DayStats> stats)
    {
        _stats = stats;
        Write(P("stats.json"), stats);
    }

    public IReadOnlyList<LogEntry> RecentLog()
    {
        LoadLog();
        return _log.TakeLast(LogSize).ToList();
    }

    /// <summary>Delete all recorded time; projects, rules and ignore stay.</summary>
    public void ClearHistory()
    {
        foreach (var d in Days().ToList()) DeleteDay(d);
        _days.Clear();
        SaveStats(new Dictionary<string, DayStats>());
        Write(P("runtime.json"), new Runtime());
        _log.Clear();
        try { File.Delete(P("log.txt")); } catch { /* best effort */ }
    }
}
