namespace Kobian.Format.Repo;

/// <summary>
/// Układ obiektów repozytorium Kobian Repo v1 (R2.3, D3.1): <c>data/&lt;pp&gt;/&lt;packId&gt;.pack</c>, <c>index/&lt;indexId&gt;.idx</c>,
/// <c>snapshots/&lt;snapId&gt;.snap</c>, <c>keys/&lt;keyId&gt;.key</c>, <c>locks/&lt;lockId&gt;.lock</c>. Identyfikatory 16 B jako hex małymi literami.
/// Ścieżki względne, separator „/” (kontrakt D4.6, <c>StoragePath</c>).
/// </summary>
public static class RepoPaths
{
    public const string DataDir = "data";
    public const string IndexDir = "index";
    public const string SnapshotsDir = "snapshots";
    public const string KeysDir = "keys";
    public const string LocksDir = "locks";
    public const string ConfigFile = "config";

    public static string Pack(ReadOnlySpan<byte> packId)
    {
        var hex = Hex(packId);
        return $"{DataDir}/{hex[..2]}/{hex}.pack";
    }

    public static string Index(ReadOnlySpan<byte> indexId) => $"{IndexDir}/{Hex(indexId)}.idx";

    public static string Snapshot(ReadOnlySpan<byte> snapId) => $"{SnapshotsDir}/{Hex(snapId)}.snap";

    /// <summary>Snapshot w trakcie zatwierdzania (D3.14 pkt 3) — rename do <see cref="Snapshot"/>.</summary>
    public static string SnapshotPart(ReadOnlySpan<byte> snapId) => Snapshot(snapId) + ".part";

    public static string Lock(ReadOnlySpan<byte> lockId) => $"{LocksDir}/{Hex(lockId)}.lock";

    /// <summary>Odczyt identyfikatora z nazwy pliku listingu (np. <c>snapshots/…snap</c>); null, gdy nazwa nie pasuje.</summary>
    public static byte[]? TryParseId(string path, string dir, string extension)
    {
        ArgumentNullException.ThrowIfNull(path);
        var prefix = dir + "/";
        if (!path.StartsWith(prefix, StringComparison.Ordinal) || !path.EndsWith(extension, StringComparison.Ordinal)) return null;
        var hex = path[prefix.Length..^extension.Length];
        if (hex.Length != 32) return null;
        foreach (var c in hex)
            if (c is not ((>= '0' and <= '9') or (>= 'a' and <= 'f'))) return null;
        return Convert.FromHexString(hex);
    }

    private static string Hex(ReadOnlySpan<byte> id)
    {
        if (id.Length != 16) throw new ArgumentException("Identifier must be 16 bytes.", nameof(id));
        return Convert.ToHexStringLower(id);
    }
}
