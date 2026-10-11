using BisonTime.Core;

internal sealed class MemStore : IStore
{
    public Settings Settings = new();
    public Dictionary<string, DayFile> DaysMap = new();
    public Runtime Rt = new();
    public List<LogEntry> Logs = new();
    public Settings LoadSettings() => Settings;
    public void SaveSettings(Settings s) => Settings = s;
    public DayFile LoadDay(string day) => DaysMap.TryGetValue(day, out var f) ? f : DaysMap[day] = new DayFile();
    public void SaveDay(string day, DayFile f) => DaysMap[day] = f;
    public IEnumerable<string> Days() => DaysMap.Keys.OrderBy(x => x);
    public void DeleteDay(string day) => DaysMap.Remove(day);
    public Runtime LoadRuntime() => Rt;
    public void SaveRuntime(Runtime r) => Rt = r;
    public void Log(LogEntry e) => Logs.Add(e);
}
