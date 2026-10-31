using System.Text;
using Kobian.Crypto;
using Kobian.Domain;
using Xunit;

namespace Kobian.Tests.Unit.Crypto;

/// <summary>BL-24 (D3.4, D3.11, D8.3): init repozytorium i unieważnienie klucza. Hasła losowe w teście (NFR-11).</summary>
public sealed class RepoProvisionerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kobian-prov-" + Guid.NewGuid().ToString("N"));
    private static string RandomPassword() => Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(12));
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-13T08:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

    public RepoProvisionerTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private string PwFile(string pw) { var p = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".pw"); File.WriteAllText(p, pw, new UTF8Encoding(false)); return p; }
    private string Write(string name, string content) { var p = Path.Combine(_dir, name.Replace('/', '_')); File.WriteAllText(p, content, new UTF8Encoding(false)); return p; }

    [Fact]
    public void Created_repo_loads_with_password_and_header_verifies()
    {
        var pw = RandomPassword();
        var r = RepoProvisioner.Create(pw, Now);
        Assert.Equal("keys/" + r.KeyIdHex + ".key", r.KeyFileName);
        using var keys = KeyFileLoader.Load(Write("k.key", r.KeyFileJson), PwFile(pw), Write("kobian.repo", r.HeaderJson));
        Assert.Equal(r.RepoIdHex, Convert.ToHexStringLower(keys.RepoId));
        Assert.True(RepoHeader.IsKeyListed(RepoHeader.Verify(r.HeaderJson, keys), Convert.FromHexString(r.KeyIdHex)));
    }

    [Fact]
    public void Two_creations_differ_and_wrong_password_fails()
    {
        var pw = RandomPassword();
        var a = RepoProvisioner.Create(pw, Now);
        var b = RepoProvisioner.Create(pw, Now);
        Assert.NotEqual(a.RepoIdHex, b.RepoIdHex);
        Assert.NotEqual(a.KeyIdHex, b.KeyIdHex);
        var ex = Assert.Throws<KobianException>(() => KeyFileLoader.Load(Write("k.key", a.KeyFileJson), PwFile(RandomPassword()), Write("kobian.repo", a.HeaderJson)));
        Assert.Equal(KbCode.KeyUnwrapFailed, ex.Code);
    }

    [Fact]
    public void Revoked_key_is_rejected_by_loader_and_header_no_longer_lists_it()
    {
        // repo z dwoma kluczami: A (hasło pa) i B (hasło pb) opakowują ten sam MK
        var mk = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        var repoId = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);
        var ka = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);
        var kb = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);
        string pa = RandomPassword(), pb = RandomPassword();
        using var keys = RepoKeys.Derive(mk, repoId);
        var header = RepoHeader.Create(keys, Now, [ka, kb]);
        var fileA = KeyFiles.WrapWithPassword(mk, pa, ka, Now);
        var fileB = KeyFiles.WrapWithPassword(mk, pb, kb, Now);

        var rev = RepoProvisioner.Revoke(header, keys, kb, fileB);
        Assert.Equal("keys/" + Convert.ToHexStringLower(kb) + ".key", rev.RevokedKeyFileName);

        // B nie odblokuje się nawet z poprawnym hasłem
        Assert.Equal(KbCode.KeyUnwrapFailed, Assert.Throws<KobianException>(() => KeyFiles.UnwrapWithPassword(rev.RevokedKeyFileJson, pb)).Code);
        // nowy nagłówek: MAC poprawny, lista tylko A, createdAt zachowane
        var info = RepoHeader.Verify(rev.HeaderJson, keys);
        Assert.Single(info.KeyIds);
        Assert.True(RepoHeader.IsKeyListed(info, ka));
        Assert.False(RepoHeader.IsKeyListed(info, kb));
        Assert.Equal("2026-10-13T08:00:00Z", info.CreatedAt);
        // A nadal działa z nowym nagłówkiem
        using var loaded = KeyFileLoader.Load(Write("a.key", fileA), PwFile(pa), Write("kobian.repo", rev.HeaderJson));
        Assert.Equal(repoId, loaded.RepoId.ToArray());
        // stary plik B (bez revoked) z nowym nagłówkiem: klucz spoza listy → odrzucony (atak „przywrócenia” starego pliku)
        Assert.Equal(KbCode.KeyUnwrapFailed, Assert.Throws<KobianException>(() => KeyFileLoader.Load(Write("b.key", fileB), PwFile(pb), Write("kobian2.repo", rev.HeaderJson))).Code);
    }

    [Fact]
    public void Cannot_revoke_last_key_unlisted_key_or_with_forged_header()
    {
        var pw = RandomPassword();
        var r = RepoProvisioner.Create(pw, Now);
        using var keys = KeyFileLoader.Load(Write("k.key", r.KeyFileJson), PwFile(pw), Write("kobian.repo", r.HeaderJson));
        var kid = Convert.FromHexString(r.KeyIdHex);
        Assert.Equal(KbCode.KeyUnwrapFailed, Assert.Throws<KobianException>(() => RepoProvisioner.Revoke(r.HeaderJson, keys, kid, r.KeyFileJson)).Code);
        Assert.Equal(KbCode.KeyUnwrapFailed, Assert.Throws<KobianException>(() => RepoProvisioner.Revoke(r.HeaderJson, keys, new byte[16], r.KeyFileJson)).Code);
        var forged = r.HeaderJson.Replace(r.KeyIdHex, new string('0', 32));
        Assert.Equal(KbCode.RepoHeaderInvalid, Assert.Throws<KobianException>(() => RepoProvisioner.Revoke(forged, keys, kid, r.KeyFileJson)).Code);
    }

    [Fact]
    public void MarkRevoked_rejects_non_key_files()
    {
        Assert.Equal(KbCode.KeyUnwrapFailed, Assert.Throws<KobianException>(() => KeyFiles.MarkRevoked("{\"a\":1}")).Code);
        Assert.Equal(KbCode.KeyUnwrapFailed, Assert.Throws<KobianException>(() => KeyFiles.MarkRevoked("not json")).Code);
    }
}
