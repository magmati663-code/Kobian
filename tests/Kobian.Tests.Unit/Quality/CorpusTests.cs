using System.Security.Cryptography;
using Kobian.Tests.Support;
using Xunit;

namespace Kobian.Tests.Unit.Quality;

/// <summary>D10.9: generator deterministyczny; brak danych osobowych; treści mieszane (kompresowalne + losowe).</summary>
public sealed class CorpusTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "kobian-corpus-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6)));

    public void Dispose()
    {
        if (Directory.Exists(_tmp)) Directory.Delete(_tmp, recursive: true);
    }

    private static CorpusSpec Spec() => CorpusSpec.Load(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "corpus", "small-mixed.json")));

    [Fact, Trait("D", "D10.9")]
    public void Same_spec_and_seed_give_byte_identical_tree()
    {
        var a = new CorpusGenerator(Spec()).Generate(Path.Combine(_tmp, "a"));
        var b = new CorpusGenerator(Spec()).Generate(Path.Combine(_tmp, "b"));
        Assert.Equal(CorpusGenerator.Manifest(a), CorpusGenerator.Manifest(b));
        Assert.Equal(300, a.Count);
        // weryfikacja zapisanego na dysku (nie tylko manifestu)
        foreach (var f in a.Take(30))
        {
            var bytes = File.ReadAllBytes(Path.Combine(_tmp, "b", f.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
            Assert.Equal(f.Sha256, Convert.ToHexStringLower(SHA256.HashData(bytes)));
        }
    }

    [Fact, Trait("D", "D10.9")]
    public void Different_seed_gives_different_tree()
    {
        var s2 = Spec() with { Seed = "00112233" };
        var a = new CorpusGenerator(Spec()).Generate(Path.Combine(_tmp, "a"));
        var b = new CorpusGenerator(s2).Generate(Path.Combine(_tmp, "b"));
        Assert.NotEqual(CorpusGenerator.Manifest(a), CorpusGenerator.Manifest(b));
    }

    [Fact, Trait("US", "US-22a")]
    public void Corpus_contains_duplicates_empty_files_and_polish_names()
    {
        var files = new CorpusGenerator(Spec()).Generate(Path.Combine(_tmp, "a"));
        Assert.Contains(files, f => f.Size == 0);
        Assert.Contains(files.GroupBy(f => f.Sha256), g => g.Count() > 1 && g.First().Size > 0);
        Assert.Contains(files, f => f.RelativePath.Contains('ż', StringComparison.Ordinal) || f.RelativePath.Contains('ł', StringComparison.Ordinal) || f.RelativePath.Contains('Ł', StringComparison.Ordinal));
    }

    [Fact]
    public void Generator_refuses_to_overwrite_existing_file()
    {
        var root = Path.Combine(_tmp, "a");
        new CorpusGenerator(Spec()).Generate(root);
        Assert.ThrowsAny<IOException>(() => new CorpusGenerator(Spec()).Generate(root));
    }
}
