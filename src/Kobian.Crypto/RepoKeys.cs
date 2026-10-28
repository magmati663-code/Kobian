using System.Security.Cryptography;
using System.Text;

namespace Kobian.Crypto;

/// <summary>
/// Hierarchia kluczy repozytorium wg D3.9 (HKDF-SHA256, etykiety „kobian/v1/…”).
/// Przechowuje wyłącznie klucze pośrednie; <c>MK</c> nie jest zatrzymywany. Bufory zerowane w <see cref="Dispose"/>.
/// </summary>
public sealed class RepoKeys : IDisposable
{
    public const int KeySize = 32;
    public const int IdSize = 16;

    internal static readonly byte[] LabelData = Encoding.ASCII.GetBytes("kobian/v1/data");
    internal static readonly byte[] LabelMeta = Encoding.ASCII.GetBytes("kobian/v1/meta");
    internal static readonly byte[] LabelChunkId = Encoding.ASCII.GetBytes("kobian/v1/chunk-id");
    internal static readonly byte[] LabelHeaderMac = Encoding.ASCII.GetBytes("kobian/v1/header-mac");
    internal static readonly byte[] LabelPack = Encoding.ASCII.GetBytes("kobian/v1/pack");
    internal static readonly byte[] LabelPackCommit = Encoding.ASCII.GetBytes("kobian/v1/pack-commit");
    internal static readonly byte[] LabelCommit = Encoding.ASCII.GetBytes("kobian/v1/commit");
    internal const string LabelObjPrefix = "kobian/v1/obj/";

    private readonly byte[] _repoId;
    private readonly byte[] _kData;
    private readonly byte[] _kMeta;
    private readonly byte[] _kId;
    private readonly byte[] _kMac;
    private bool _disposed;

    private RepoKeys(byte[] repoId, byte[] kData, byte[] kMeta, byte[] kId, byte[] kMac)
    {
        _repoId = repoId;
        _kData = kData;
        _kMeta = kMeta;
        _kId = kId;
        _kMac = kMac;
    }

    /// <summary>Wyprowadza klucze z <paramref name="masterKey"/> (32 B) i <paramref name="repoId"/> (16 B, sól HKDF).</summary>
    public static RepoKeys Derive(ReadOnlySpan<byte> masterKey, ReadOnlySpan<byte> repoId)
    {
        if (masterKey.Length != KeySize) throw new ArgumentException("MK must be 32 bytes.", nameof(masterKey));
        if (repoId.Length != IdSize) throw new ArgumentException("repoId must be 16 bytes.", nameof(repoId));
        return new RepoKeys(
            repoId.ToArray(),
            Hkdf(masterKey, repoId, LabelData),
            Hkdf(masterKey, repoId, LabelMeta),
            Hkdf(masterKey, repoId, LabelChunkId),
            Hkdf(masterKey, repoId, LabelHeaderMac));
    }

    public ReadOnlySpan<byte> RepoId => Live(_repoId);

    /// <summary>Tylko dla wektorów testowych V-01 (nie używać w kodzie produktu).</summary>
    internal ReadOnlySpan<byte> KData => Live(_kData);
    internal ReadOnlySpan<byte> KMeta => Live(_kMeta);
    internal ReadOnlySpan<byte> KId => Live(_kId);
    internal ReadOnlySpan<byte> KMac => Live(_kMac);

    /// <summary><c>K_pack</c> = HKDF(K_data, packId, "kobian/v1/pack"). Wywołujący zeruje wynik po użyciu.</summary>
    public byte[] DerivePackKey(ReadOnlySpan<byte> packId)
    {
        RequireId(packId, nameof(packId));
        return Hkdf(Live(_kData), packId, LabelPack);
    }

    /// <summary><c>commitTag</c> = HMAC-SHA256(K_commit(pack), "kobian/v1/commit" ‖ packId).</summary>
    public byte[] CommitTag(ReadOnlySpan<byte> packId)
    {
        RequireId(packId, nameof(packId));
        var kCommit = Hkdf(Live(_kData), packId, LabelPackCommit);
        try
        {
            Span<byte> msg = stackalloc byte[LabelCommit.Length + IdSize];
            LabelCommit.CopyTo(msg);
            packId.CopyTo(msg[LabelCommit.Length..]);
            return HMACSHA256.HashData(kCommit, msg);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(kCommit);
        }
    }

    /// <summary><c>K_obj</c> = HKDF(K_meta, objectId, "kobian/v1/obj/" + typ).</summary>
    public byte[] DeriveObjectKey(ReadOnlySpan<byte> objectId, MetadataObjectType type)
    {
        RequireId(objectId, nameof(objectId));
        var label = Encoding.ASCII.GetBytes(LabelObjPrefix + type switch
        {
            MetadataObjectType.Index => "index",
            MetadataObjectType.Snapshot => "snapshot",
            _ => throw new ArgumentOutOfRangeException(nameof(type)),
        });
        return Hkdf(Live(_kMeta), objectId, label);
    }

    /// <summary><c>chunkId</c>/<c>treeId</c> = HMAC-SHA256(K_id, plaintext).</summary>
    public byte[] ChunkId(ReadOnlySpan<byte> plaintext) => HMACSHA256.HashData(Live(_kId), plaintext);

    /// <summary>MAC nagłówka repo i blokad: HMAC-SHA256(K_mac, data).</summary>
    public byte[] HeaderMac(ReadOnlySpan<byte> data) => HMACSHA256.HashData(Live(_kMac), data);

    /// <summary>Weryfikacja MAC w czasie stałym.</summary>
    public bool VerifyHeaderMac(ReadOnlySpan<byte> data, ReadOnlySpan<byte> mac)
    {
        Span<byte> expected = stackalloc byte[32];
        HMACSHA256.HashData(Live(_kMac), data, expected);
        return mac.Length == 32 && CryptographicOperations.FixedTimeEquals(expected, mac);
    }

    public void Dispose()
    {
        if (_disposed) return;
        CryptographicOperations.ZeroMemory(_kData);
        CryptographicOperations.ZeroMemory(_kMeta);
        CryptographicOperations.ZeroMemory(_kId);
        CryptographicOperations.ZeroMemory(_kMac);
        _disposed = true;
    }

    internal static byte[] Hkdf(ReadOnlySpan<byte> ikm, ReadOnlySpan<byte> salt, ReadOnlySpan<byte> info)
    {
        var okm = new byte[KeySize];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, okm, salt, info);
        return okm;
    }

    private static void RequireId(ReadOnlySpan<byte> id, string name)
    {
        if (id.Length != IdSize) throw new ArgumentException("Identifier must be 16 bytes.", name);
    }

    private ReadOnlySpan<byte> Live(byte[] b)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return b;
    }
}

public enum MetadataObjectType
{
    Index = 1,
    Snapshot = 2,
}
