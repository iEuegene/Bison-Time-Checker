using System.Security.Cryptography;
using System.Text.Json;

namespace BisonTime.Core;

public sealed class UpdateFile
{
    public string Path { get; set; } = "";
    public long Size { get; set; }
    public string Sha256 { get; set; } = "";
}

/// <summary>Every file of a release with its SHA-256. Published next to a signature of its exact bytes.</summary>
public sealed class UpdateManifest
{
    public string Version { get; set; } = "";
    public List<UpdateFile> Files { get; set; } = new();
}

/// <summary>
/// Delta updates: compare the signed manifest of the latest release with the files on disk,
/// fetch only what differs, swap it in with rollback. No Windows API here, so it is tested on any OS.
/// </summary>
public static class Updates
{
    public const string ManifestName = "manifest.json";
    public const string SignatureName = "manifest.json.sig";

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string Sha256File(string path)
    {
        using var s = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant();
    }

    public static string Sha256(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    /// <summary>Manifest of a published app folder. Paths use '/', sorted, so the bytes are reproducible.</summary>
    public static UpdateManifest Build(string dir, string version)
    {
        var root = System.IO.Path.GetFullPath(dir);
        var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(f => new UpdateFile
            {
                Path = System.IO.Path.GetRelativePath(root, f).Replace('\\', '/'),
                Size = new FileInfo(f).Length,
                Sha256 = Sha256File(f),
            })
            .OrderBy(f => f.Path, StringComparer.Ordinal)
            .ToList();
        return new UpdateManifest { Version = version, Files = files };
    }

    public static byte[] Serialize(UpdateManifest m) => JsonSerializer.SerializeToUtf8Bytes(m, Json);

    // ---- signatures: ECDSA P-256 / SHA-256 (built into .NET on every OS) ----

    public static (string PublicKey, string PrivateKey) NewKeys()
    {
        using var k = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (Convert.ToBase64String(k.ExportSubjectPublicKeyInfo()), Convert.ToBase64String(k.ExportPkcs8PrivateKey()));
    }

    public static string Sign(byte[] data, string privateKeyBase64)
    {
        using var k = ECDsa.Create();
        k.ImportPkcs8PrivateKey(Convert.FromBase64String(privateKeyBase64), out _);
        return Convert.ToBase64String(k.SignData(data, HashAlgorithmName.SHA256));
    }

    public static bool Verify(byte[] data, string signatureBase64, string publicKeyBase64)
    {
        try
        {
            using var k = ECDsa.Create();
            k.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeyBase64), out _);
            return k.VerifyData(data, Convert.FromBase64String(signatureBase64.Trim()), HashAlgorithmName.SHA256);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>The manifest only if the signature matches our public key and every path is safe.</summary>
    public static UpdateManifest? ParseVerified(byte[] manifestBytes, string signature, string publicKey)
    {
        if (!Verify(manifestBytes, signature, publicKey)) return null;
        UpdateManifest? m;
        try { m = JsonSerializer.Deserialize<UpdateManifest>(manifestBytes, Json); }
        catch (JsonException) { return null; }
        if (m is null || m.Files.Count == 0 || !System.Version.TryParse(m.Version, out _)) return null;
        if (m.Files.Any(f => !SafePath(f.Path) || f.Sha256.Length != 64 || f.Size < 0)) return null;
        return m;
    }

    /// <summary>A relative path that stays inside the app folder: no drive, no root, no "..".</summary>
    public static bool SafePath(string rel)
    {
        if (string.IsNullOrWhiteSpace(rel) || rel.Contains(':') || rel.StartsWith('/') || rel.StartsWith('\\')) return false;
        var parts = rel.Split('/', '\\');
        return parts.All(p => p != "" && p != "." && p != "..");
    }

    /// <summary>Highest "vX.Y.Z" among published (non-draft) releases in a GitHub API releases list.</summary>
    public static string? NewestVersion(string releasesJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(releasesJson);
            return doc.RootElement.EnumerateArray()
                .Where(r => !(r.TryGetProperty("draft", out var d) && d.ValueKind == JsonValueKind.True))
                .Select(r => r.TryGetProperty("tag_name", out var t) ? t.GetString()?.TrimStart('v', 'V') : null)
                .Select(v => System.Version.TryParse(v, out var x) ? x : null)
                .Where(v => v != null)
                .Max()?.ToString();
        }
        catch (Exception) { return null; }
    }

    public static bool IsNewer(string remote, string local) =>
        System.Version.TryParse(remote, out var r) && System.Version.TryParse(local, out var l) && r > l;

    /// <summary>Files of the manifest that are missing or different in <paramref name="baseDir"/>.</summary>
    public static List<UpdateFile> Changed(UpdateManifest m, string baseDir)
    {
        var res = new List<UpdateFile>();
        foreach (var f in m.Files)
        {
            var local = System.IO.Path.Combine(baseDir, f.Path.Replace('/', System.IO.Path.DirectorySeparatorChar));
            if (!File.Exists(local) || new FileInfo(local).Length != f.Size || Sha256File(local) != f.Sha256) res.Add(f);
        }
        return res;
    }

    /// <summary>
    /// Swap downloaded files in. Each target is first renamed to "*.old" (Windows allows renaming a
    /// running exe or loaded DLL, not overwriting it), then the new file moves into place.
    /// Any failure restores everything already swapped.
    /// </summary>
    public static void Apply(string baseDir, IReadOnlyList<(UpdateFile File, string Downloaded)> items)
    {
        // Verify before touching anything.
        foreach (var (f, tmp) in items)
            if (!SafePath(f.Path) || Sha256File(tmp) != f.Sha256)
                throw new InvalidDataException($"hash mismatch: {f.Path}");

        var done = new List<(string Target, string? Old)>();
        try
        {
            foreach (var (f, tmp) in items)
            {
                var target = System.IO.Path.Combine(baseDir, f.Path.Replace('/', System.IO.Path.DirectorySeparatorChar));
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
                string? old = null;
                if (File.Exists(target))
                {
                    old = target + ".old";
                    if (File.Exists(old)) File.Delete(old);
                    File.Move(target, old);
                }
                done.Add((target, old));
                File.Move(tmp, target);
            }
        }
        catch
        {
            foreach (var (target, old) in Enumerable.Reverse(done))
            {
                try
                {
                    if (old != null)
                    {
                        if (File.Exists(target)) File.Delete(target);
                        File.Move(old, target);
                    }
                    else if (File.Exists(target)) File.Delete(target);
                }
                catch { /* best effort: the next update run repairs by hash */ }
            }
            throw;
        }
    }

    /// <summary>Remove leftovers of a finished update: *.old files and the download folder.</summary>
    public static void Cleanup(string baseDir, string downloadDir)
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(baseDir, "*.old", SearchOption.AllDirectories))
                try { File.Delete(f); } catch { /* still loaded: next start */ }
            if (Directory.Exists(downloadDir)) Directory.Delete(downloadDir, recursive: true);
        }
        catch { /* best effort */ }
    }
}
