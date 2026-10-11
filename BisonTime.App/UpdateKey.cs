namespace BisonTime.App;

/// <summary>
/// Public half of the release signing key (ECDSA P-256). The private half lives only in the
/// BT_SIGNING_KEY secret of the GitHub repository; CI signs every release manifest with it,
/// and the app refuses any update whose manifest signature does not verify against this key.
/// </summary>
internal static class UpdateKey
{
    public const string PublicKey = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEZHvIwclEUkCr8hBfzJPFDrkLTKu7yYyAQgFvlnloJK+9kU81nfR38Q6VCTIM8eFJ8NTWOcJ842B80JGImPQp+A==";
}
