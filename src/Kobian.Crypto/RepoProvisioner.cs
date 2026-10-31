using System.Globalization;
using System.Security.Cryptography;
using Kobian.Domain;

namespace Kobian.Crypto;

/// <summary>Pliki nowego repozytorium: <c>kobian.repo</c> oraz <c>keys/&lt;keyId&gt;.key</c> (D3.11). Brak materiału tajnego w wyniku.</summary>
public sealed record ProvisionedRepo(string RepoIdHex, string KeyIdHex, string HeaderJson, string KeyFileName, string KeyFileJson);

/// <summary>
/// Repozytorium z drugim kluczem — kluczem odzyskiwania (K14.1, AC-28.1): plik <c>keys/&lt;id&gt;.key</c> typu „recovery” na liście nagłówka.
/// <see cref="RecoveryKey"/> to SEKRET (55 znaków Base32 w grupach) — zwracany jednorazowo do pokazania użytkownikowi, nigdy zapisywany ani logowany.
/// </summary>
public sealed record ProvisionedRepoWithRecovery(ProvisionedRepo Repo, string RecoveryKeyIdHex, string RecoveryKeyFileName, string RecoveryKeyFileJson, string RecoveryKey);

/// <summary>Wynik unieważnienia: nowy nagłówek (bez klucza na liście) i plik klucza z <c>revoked:true</c>.</summary>
public sealed record RevocationResult(string HeaderJson, string RevokedKeyFileName, string RevokedKeyFileJson);

/// <summary>
/// Tworzenie repozytorium i unieważnianie klucza (BL-24, D3.4, D3.11, D8.3).
/// Init: losowy <c>MK</c> (32 B) i <c>repoId</c> (16 B), opakowanie hasłem (PBKDF2 600 000), nagłówek z MAC; MK jest zerowany.
/// Unieważnienie: usunięcie keyId z uwierzytelnionej listy <c>body.keys</c> (nowy MAC) + <c>revoked:true</c> w pliku klucza;
/// ostatniego ważnego klucza nie wolno unieważnić (repozytorium stałoby się nieodczytywalne).
/// </summary>
public static class RepoProvisioner
{
    public static ProvisionedRepo Create(string password, DateTimeOffset createdAt)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);
        var mk = RandomNumberGenerator.GetBytes(RepoKeys.KeySize);
        var repoId = RandomNumberGenerator.GetBytes(RepoKeys.IdSize);
        var keyId = RandomNumberGenerator.GetBytes(RepoKeys.IdSize);
        try
        {
            using var keys = RepoKeys.Derive(mk, repoId);
            var header = RepoHeader.Create(keys, createdAt, [keyId]);
            var keyFile = KeyFiles.WrapWithPassword(mk, password, keyId, createdAt);
            var keyIdHex = Convert.ToHexStringLower(keyId);
            return new ProvisionedRepo(Convert.ToHexStringLower(repoId), keyIdHex, header, "keys/" + keyIdHex + ".key", keyFile);
        }
        finally { CryptographicOperations.ZeroMemory(mk); }
    }

    /// <summary>Jak <see cref="Create"/>, plus klucz odzyskiwania opakowujący ten sam MK (drugi keyId na uwierzytelnionej liście nagłówka).</summary>
    public static ProvisionedRepoWithRecovery CreateWithRecovery(string password, DateTimeOffset createdAt)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);
        var mk = RandomNumberGenerator.GetBytes(RepoKeys.KeySize);
        var rk = RandomNumberGenerator.GetBytes(RecoveryKey.KeyLength);
        var repoId = RandomNumberGenerator.GetBytes(RepoKeys.IdSize);
        var keyId = RandomNumberGenerator.GetBytes(RepoKeys.IdSize);
        var recoveryId = RandomNumberGenerator.GetBytes(RepoKeys.IdSize);
        try
        {
            using var keys = RepoKeys.Derive(mk, repoId);
            var header = RepoHeader.Create(keys, createdAt, [keyId, recoveryId]);
            var keyFile = KeyFiles.WrapWithPassword(mk, password, keyId, createdAt);
            var recoveryFile = KeyFiles.WrapWithRecoveryKey(mk, rk, recoveryId, createdAt);
            var keyIdHex = Convert.ToHexStringLower(keyId);
            var recoveryIdHex = Convert.ToHexStringLower(recoveryId);
            var repo = new ProvisionedRepo(Convert.ToHexStringLower(repoId), keyIdHex, header, "keys/" + keyIdHex + ".key", keyFile);
            return new ProvisionedRepoWithRecovery(repo, recoveryIdHex, "keys/" + recoveryIdHex + ".key", recoveryFile, RecoveryKey.Encode(rk));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(mk);
            CryptographicOperations.ZeroMemory(rk);
        }
    }

    /// <summary>
    /// Unieważnia <paramref name="keyIdToRevoke"/>. <paramref name="keys"/> musi pochodzić z odblokowania KTÓREGOŚ INNEGO ważnego klucza
    /// (ten sam MK; K_mac jest wspólny) — nagłówek jest najpierw weryfikowany (MAC), dopiero potem przepisywany.
    /// </summary>
    public static RevocationResult Revoke(string headerJson, RepoKeys keys, ReadOnlySpan<byte> keyIdToRevoke, string keyFileJson)
    {
        ArgumentNullException.ThrowIfNull(keys);
        if (keyIdToRevoke.Length != RepoKeys.IdSize) throw new ArgumentException("keyId must be 16 bytes.", nameof(keyIdToRevoke));
        var info = RepoHeader.Verify(headerJson, keys); // KB-1104 przy podróbce
        var hex = Convert.ToHexStringLower(keyIdToRevoke);
        if (!RepoHeader.IsKeyListed(info, keyIdToRevoke))
            throw new KobianException(KbCode.KeyUnwrapFailed, ErrorClass.Security, "The key is not listed in the repository header.");
        if (info.KeyIds.Count < 2)
            throw new KobianException(KbCode.KeyUnwrapFailed, ErrorClass.Permanent, "The last valid key cannot be revoked.");
        var remaining = info.KeyIds.Where(k => !string.Equals(k, hex, StringComparison.Ordinal)).Select(Convert.FromHexString).ToList();
        var created = DateTimeOffset.ParseExact(info.CreatedAt, "yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
        return new RevocationResult(RepoHeader.Create(keys, created, remaining), "keys/" + hex + ".key", KeyFiles.MarkRevoked(keyFileJson));
    }
}
