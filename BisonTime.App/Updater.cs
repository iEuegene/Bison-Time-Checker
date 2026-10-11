using System.Diagnostics;
using System.IO;
using System.Net.Http;
using BisonTime.Core;

namespace BisonTime.App;

/// <summary>
/// Checks GitHub Releases for a newer version and installs only the files that changed.
/// The only network traffic BT ever makes; it never sends any data, only downloads.
/// </summary>
internal static class Updater
{
    const string Releases = "https://github.com/iEuegene/Bison-Time-Checker/releases";

    static readonly HttpClient Http = CreateClient();

    static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd($"BisonTimeChecker/{Current}");
        return c;
    }

    public static string Current => typeof(App).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    public static string AppDir => AppContext.BaseDirectory;
    public static string DownloadDir => Path.Combine(AppDir, ".update");

    public sealed record Available(UpdateManifest Manifest, List<UpdateFile> Changed)
    {
        public long Bytes => Changed.Sum(f => f.Size);
    }

    /// <summary>Latest release if it is newer than this one; null when up to date. Throws on network or signature errors.</summary>
    public static async Task<Available?> CheckAsync()
    {
        byte[] manifest;
        try { manifest = await Http.GetByteArrayAsync($"{Releases}/latest/download/{Updates.ManifestName}"); }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            throw new InvalidDataException("Остання версія на GitHub вийшла без файлів для оновлення. Завантаж її вручну: кнопка «Релізи».");
        }
        var signature = await Http.GetStringAsync($"{Releases}/latest/download/{Updates.SignatureName}");
        var m = Updates.ParseVerified(manifest, signature, UpdateKey.PublicKey)
            ?? throw new InvalidDataException("Підпис оновлення не збігається. Оновлення відхилено.");
        if (!Updates.IsNewer(m.Version, Current)) return null;
        var changed = await Task.Run(() => Updates.Changed(m, AppDir));
        return new Available(m, changed);
    }

    static void EnsureWritable()
    {
        var probe = Path.Combine(AppDir, ".write-test");
        try { File.WriteAllText(probe, ""); File.Delete(probe); }
        catch (Exception)
        {
            throw new IOException("У папку програми не можна писати (можливо, вона в Program Files). Перенеси папку BisonTimeChecker у свою теку, наприклад Документи, і запусти звідти.");
        }
    }

    /// <summary>Download the changed files, verify each hash, swap them in. Call <see cref="Restart"/> afterwards.</summary>
    public static async Task InstallAsync(Available a, Action<string> progress)
    {
        EnsureWritable();
        Directory.CreateDirectory(DownloadDir);
        var items = new List<(UpdateFile, string)>();
        var i = 0;
        foreach (var f in a.Changed)
        {
            i++;
            progress($"Завантаження {i} з {a.Changed.Count}…");
            var tmp = Path.Combine(DownloadDir, f.Sha256);
            if (!File.Exists(tmp) || Updates.Sha256File(tmp) != f.Sha256)
            {
                var bytes = await Http.GetByteArrayAsync($"{Releases}/download/v{a.Manifest.Version}/{f.Sha256}");
                if (Updates.Sha256(bytes) != f.Sha256) throw new InvalidDataException($"Файл {f.Path} пошкоджено під час завантаження.");
                await File.WriteAllBytesAsync(tmp, bytes);
            }
            items.Add((f, tmp));
        }
        progress("Встановлення…");
        await Task.Run(() => Updates.Apply(AppDir, items));
    }

    /// <summary>Start the updated copy and exit this one; the new process waits until we are gone.</summary>
    public static void Restart()
    {
        // Not Environment.ProcessPath: the running exe was just renamed to *.old.
        var exe = Path.Combine(AppDir, "BisonTimeChecker.exe");
        Process.Start(new ProcessStartInfo(exe, "--after-update") { UseShellExecute = false, WorkingDirectory = AppDir });
        App.Current.Shutdown();
    }

    public static string Size(long bytes) => bytes >= 1_000_000 ? $"{bytes / 1_000_000.0:0.0} МБ" : $"{Math.Max(1, bytes / 1000)} КБ";
}
