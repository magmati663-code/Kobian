using System.Text.Json;
using Kobian.Crypto;
using Kobian.Domain;
using Xunit;

namespace Kobian.Tests.Unit.Crypto;

/// <summary>V-09 (D3.11, SEC-7.3): kobian.repo + JCS + MAC. Oczekiwany MAC policzony niezależnie (Python: HKDF/HMAC/JSON kanoniczny).</summary>
public sealed class RepoHeaderTests
{
    private const string ExpectedMac = "c4a24f83dcbeb18bdabae53c11829e29cb0e68d0673cb14b524600d8c476a991";
    private static readonly byte[] Mk = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
    private static readonly byte[] RepoId = Convert.FromHexString("00112233445566778899aabbccddeeff");
    private static readonly byte[] K1 = Convert.FromHexString("a1a2a3a4a5a6a7a8a9aaabacadaeafb0");
    private static readonly byte[] K2 = Convert.FromHexString("c3c4c5c6c7c8c9cacbcccdcecfd0d1d2");

    private static string Header(RepoKeys k) => RepoHeader.Create(k, DateTimeOffset.Parse("2026-10-13T07:00:00Z", System.Globalization.CultureInfo.InvariantCulture), [K1, K2]);

    [Fact]
    public void V09_mac_matches_independent_reference()
    {
        using var keys = RepoKeys.Derive(Mk, RepoId);
        using var doc = JsonDocument.Parse(Header(keys));
        Assert.Equal(ExpectedMac, doc.RootElement.GetProperty("mac").GetString());
    }

    [Fact]
    public void Verify_roundtrip_returns_keys_and_peek_gives_repoId()
    {
        using var keys = RepoKeys.Derive(Mk, RepoId);
        var json = Header(keys);
        Assert.Equal(RepoId, RepoHeader.PeekRepoId(json));
        var info = RepoHeader.Verify(json, keys);
        Assert.Equal(2, info.KeyIds.Count);
        Assert.True(RepoHeader.IsKeyListed(info, K2));
        Assert.False(RepoHeader.IsKeyListed(info, new byte[16]));
    }

    [Theory]
    [InlineData("\"createdAt\":\"2026-10-13T07:00:00Z\"", "\"createdAt\":\"2026-10-13T07:00:01Z\"")]
    [InlineData("\"size\":4194304", "\"size\":4194305")]
    [InlineData("c3c4c5c6c7c8c9cacbcccdcecfd0d1d2", "d3c4c5c6c7c8c9cacbcccdcecfd0d1d2")]
    public void Any_body_change_breaks_mac(string from, string to)
    {
        using var keys = RepoKeys.Derive(Mk, RepoId);
        var json = Header(keys);
        Assert.Contains(from, json);
        var ex = Assert.Throws<KobianException>(() => RepoHeader.Verify(json.Replace(from, to), keys));
        Assert.Equal(KbCode.RepoHeaderInvalid, ex.Code);
    }

    [Fact]
    public void Removing_a_key_from_list_breaks_mac_dos_not_silent_downgrade()
    {
        using var keys = RepoKeys.Derive(Mk, RepoId);
        var json = Header(keys).Replace(",\"c3c4c5c6c7c8c9cacbcccdcecfd0d1d2\"", "");
        Assert.Equal(KbCode.RepoHeaderInvalid, Assert.Throws<KobianException>(() => RepoHeader.Verify(json, keys)).Code);
    }

    [Fact]
    public void Header_forged_with_other_key_is_rejected_SEC_7_3()
    {
        using var real = RepoKeys.Derive(Mk, RepoId);
        using var fake = RepoKeys.Derive(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray(), RepoId);
        var forged = Header(fake);
        Assert.Equal(KbCode.RepoHeaderInvalid, Assert.Throws<KobianException>(() => RepoHeader.Verify(forged, real)).Code);
    }

    [Fact]
    public void Header_of_another_repo_is_rejected_even_with_valid_mac_for_same_keys()
    {
        using var other = RepoKeys.Derive(Mk, new byte[16]);
        var json = Header(other);
        using var real = RepoKeys.Derive(Mk, RepoId);
        Assert.Equal(KbCode.RepoHeaderInvalid, Assert.Throws<KobianException>(() => RepoHeader.Verify(json, real)).Code);
    }

    [Theory]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"body\":{},\"mac\":\"zz\"}")]
    [InlineData("{\"body\":1,\"mac\":\"00\"}")]
    [InlineData("not json")]
    public void Malformed_input_gives_uniform_error(string json)
    {
        using var keys = RepoKeys.Derive(Mk, RepoId);
        Assert.Equal(KbCode.RepoHeaderInvalid, Assert.Throws<KobianException>(() => RepoHeader.Verify(json, keys)).Code);
    }

    [Fact]
    public void Oversized_input_is_rejected_before_parsing()
    {
        using var keys = RepoKeys.Derive(Mk, RepoId);
        var big = "{\"body\":{\"x\":\"" + new string('a', 70_000) + "\"},\"mac\":\"00\"}";
        Assert.Equal(KbCode.RepoHeaderInvalid, Assert.Throws<KobianException>(() => RepoHeader.Verify(big, keys)).Code);
        Assert.Equal(KbCode.RepoHeaderInvalid, Assert.Throws<KobianException>(() => RepoHeader.PeekRepoId(big)).Code);
    }

    [Fact]
    public void Authenticated_unknown_feature_gives_KB_1002_not_garbage()
    {
        using var keys = RepoKeys.Derive(Mk, RepoId);
        // Nagłówek z features=["cdc"] poprawnie podpisany (symulacja repo v2): MAC policzony tym samym kodem.
        var body = "{\"chunker\":{\"alg\":\"fixed\",\"size\":4194304,\"smallFileMax\":1048576},\"compression\":{\"default\":\"zstd\",\"level\":3},\"createdAt\":\"2026-10-13T07:00:00Z\",\"features\":[\"cdc\"],\"formatVersion\":1,\"keys\":[\"a1a2a3a4a5a6a7a8a9aaabacadaeafb0\"],\"magic\":\"kobian-repo\",\"minReaderVersion\":1,\"pack\":{\"targetSize\":16777216},\"repoId\":\"00112233445566778899aabbccddeeff\"}";
        var mac = Convert.ToHexStringLower(keys.HeaderMac(System.Text.Encoding.UTF8.GetBytes(body)));
        var ex = Assert.Throws<KobianException>(() => RepoHeader.Verify("{\"body\":" + body + ",\"mac\":\"" + mac + "\"}", keys));
        Assert.Equal(KbCode.UnsupportedFeature, ex.Code);
    }

    [Fact]
    public void Create_validates_key_ids()
    {
        using var keys = RepoKeys.Derive(Mk, RepoId);
        Assert.Throws<ArgumentException>(() => RepoHeader.Create(keys, DateTimeOffset.UnixEpoch, []));
        Assert.Throws<ArgumentException>(() => RepoHeader.Create(keys, DateTimeOffset.UnixEpoch, [new byte[3]]));
    }

    private sealed class Dir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "kobian-rh-" + Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(6)));
        public Dir() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }

    [Fact, Trait("BL", "BL-24")]
    public void Loader_takes_repoId_from_header_and_requires_key_to_be_listed()
    {
        using var d = new Dir();
        var mk = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        var repoId = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);
        var keyId = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);
        var otherKeyId = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);
        var pw = "pw-" + Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(6));
        var kf = System.IO.Path.Combine(d.Path, "a.key");
        var pf = System.IO.Path.Combine(d.Path, "pw.txt");
        var hf = System.IO.Path.Combine(d.Path, "kobian.repo");
        File.WriteAllText(kf, KeyFiles.WrapWithPassword(mk, pw, keyId, DateTimeOffset.UtcNow));
        File.WriteAllText(pf, pw);
        using var keys = RepoKeys.Derive(mk, repoId);

        File.WriteAllText(hf, RepoHeader.Create(keys, DateTimeOffset.UtcNow, [keyId, otherKeyId]));
        using (var viaHeader = KeyFileLoader.Load(kf, pf, hf))
            Assert.Equal(keys.KData.ToArray(), viaHeader.KData.ToArray());

        // klucz unieważniony (poza uwierzytelnioną listą) → odmowa
        File.WriteAllText(hf, RepoHeader.Create(keys, DateTimeOffset.UtcNow, [otherKeyId]));
        Assert.Equal(KbCode.KeyUnwrapFailed, Assert.Throws<KobianException>(() => KeyFileLoader.Load(kf, pf, hf).Dispose()).Code);

        // nagłówek podrobiony (inny MK, to samo repoId) → KB-1104
        using var fake = RepoKeys.Derive(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32), repoId);
        File.WriteAllText(hf, RepoHeader.Create(fake, DateTimeOffset.UtcNow, [keyId]));
        Assert.Equal(KbCode.RepoHeaderInvalid, Assert.Throws<KobianException>(() => KeyFileLoader.Load(kf, pf, hf).Dispose()).Code);

        // nagłówek uszkodzony
        File.WriteAllText(hf, "{broken");
        Assert.Equal(KbCode.RepoHeaderInvalid, Assert.Throws<KobianException>(() => KeyFileLoader.Load(kf, pf, hf).Dispose()).Code);
    }
}
