using System.Text.RegularExpressions;
using BisonTime.Core;

// keygen                                    -> prints a new key pair (do this once; private key goes to the BT_SIGNING_KEY secret)
// publish <appDir> <outDir> <version> <keyFile.cs>
//     writes outDir/manifest.json, manifest.json.sig and outDir/files/<sha256> (one copy per distinct file);
//     signs with $BT_SIGNING_KEY and checks the signature against the public key compiled into the app.
if (args.Length >= 1 && args[0] == "keygen")
{
    var (pub, priv) = Updates.NewKeys();
    Console.WriteLine($"PUBLIC  {pub}");
    Console.WriteLine($"PRIVATE {priv}");
    return 0;
}
if (args.Length == 5 && args[0] == "publish")
{
    var (appDir, outDir, version, keyFile) = (args[1], args[2], args[3], args[4]);
    var priv = Environment.GetEnvironmentVariable("BT_SIGNING_KEY");
    if (string.IsNullOrWhiteSpace(priv)) { Console.Error.WriteLine("BT_SIGNING_KEY is not set: refusing to publish an unsigned update"); return 2; }
    var pub = Regex.Match(File.ReadAllText(keyFile), "PublicKey\\s*=\\s*\"(?<k>[A-Za-z0-9+/=]+)\"").Groups["k"].Value;

    var manifest = Updates.Build(appDir, version);
    var bytes = Updates.Serialize(manifest);
    var sig = Updates.Sign(bytes, priv.Trim());
    if (!Updates.Verify(bytes, sig, pub)) { Console.Error.WriteLine("signing key does not match the public key in the app"); return 3; }

    Directory.CreateDirectory(Path.Combine(outDir, "files"));
    File.WriteAllBytes(Path.Combine(outDir, Updates.ManifestName), bytes);
    File.WriteAllText(Path.Combine(outDir, Updates.SignatureName), sig);
    var root = Path.GetFullPath(appDir);
    foreach (var f in manifest.Files)
    {
        var dst = Path.Combine(outDir, "files", f.Sha256);
        if (!File.Exists(dst)) File.Copy(Path.Combine(root, f.Path), dst);
    }
    Console.WriteLine($"manifest {version}: {manifest.Files.Count} files, {Directory.GetFiles(Path.Combine(outDir, "files")).Length} distinct");
    return 0;
}
Console.Error.WriteLine("usage: keygen | publish <appDir> <outDir> <version> <keyFile.cs>");
return 1;
