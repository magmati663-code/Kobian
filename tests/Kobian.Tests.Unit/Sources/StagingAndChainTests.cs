using System.Security.Cryptography;
using Kobian.Providers.Sources.MsSql;
using Xunit;

namespace Kobian.Tests.Unit.Sources;

public sealed class StagingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "kobian-staging-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6)));

    public StagingTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private string MakeRun(string runId, TimeSpan age)
    {
        var d = Directory.CreateDirectory(Path.Combine(_root, runId));
        var f = Path.Combine(d.FullName, Guid.NewGuid().ToString("N") + ".bak");
        File.WriteAllBytes(f, [1, 2, 3]);
        var t = DateTime.UtcNow - age;
        File.SetLastWriteTimeUtc(f, t);
        Directory.SetCreationTimeUtc(d.FullName, t);
        Directory.SetLastWriteTimeUtc(d.FullName, t);
        return d.FullName;
    }

    [Fact, Trait("D", "D5.10")]
    public void File_name_is_random_and_does_not_reveal_database()
    {
        var p = Staging.FilePath(_root, "01j9runabc", Guid.NewGuid());
        Assert.Matches(@"[0-9a-f]{32}\.bak$", p);
        Assert.Throws<ArgumentException>(() => Staging.FilePath(_root, "../x", Guid.NewGuid()));
        Assert.Throws<ArgumentException>(() => Staging.FilePath(_root, "ERP", Guid.NewGuid()));
    }

    [Fact, Trait("D", "D5.10")]
    public void Cleanup_removes_only_stale_inactive_run_directories()
    {
        var old = new string('a', 32);
        var active = new string('b', 32);
        var fresh = new string('c', 32);
        MakeRun(old, TimeSpan.FromHours(3));
        MakeRun(active, TimeSpan.FromHours(3));
        MakeRun(fresh, TimeSpan.FromMinutes(5));
        Directory.CreateDirectory(Path.Combine(_root, "Inny Folder"));
        var removed = Staging.CleanupStale(_root, new HashSet<string> { active }, DateTimeOffset.UtcNow);
        Assert.Equal([old], removed);
        Assert.True(Directory.Exists(Path.Combine(_root, active)));
        Assert.True(Directory.Exists(Path.Combine(_root, fresh)));
        Assert.True(Directory.Exists(Path.Combine(_root, "Inny Folder")));
    }

    /// <summary>
    /// Katalog stagingu z definicji zadania bywa dowolną ścieżką (np. korzeń dysku z dokumentami): sprzątanie nie może ruszyć folderów o „podobnych” nazwach
    /// (documents, downloads), ani katalogu z cudzymi plikami w środku, nawet gdy nazwa wygląda jak runId.
    /// </summary>
    [Fact, Trait("SEC", "SEC-5.2")]
    public void Cleanup_never_touches_foreign_folders_even_with_lookalike_names_or_foreign_content()
    {
        foreach (var name in new[] { "documents", "downloads", "backup2026", "restore-backups" })
        {
            var d = Directory.CreateDirectory(Path.Combine(_root, name));
            File.WriteAllText(Path.Combine(d.FullName, "wazne.docx"), "x");
            Directory.SetCreationTimeUtc(d.FullName, DateTime.UtcNow.AddDays(-30));
            Directory.SetLastWriteTimeUtc(d.FullName, DateTime.UtcNow.AddDays(-30));
        }
        var mixed = MakeRun(new string('d', 32), TimeSpan.FromHours(5)); // nazwa jak runId, ale jest w środku cudzy plik
        File.WriteAllText(Path.Combine(mixed, "notatki.txt"), "x");

        var removed = Staging.CleanupStale(_root, new HashSet<string>(), DateTimeOffset.UtcNow);

        Assert.Empty(removed);
        Assert.True(File.Exists(Path.Combine(_root, "documents", "wazne.docx")));
        Assert.True(File.Exists(Path.Combine(_root, "restore-backups", "wazne.docx")));
        Assert.True(File.Exists(Path.Combine(mixed, "notatki.txt")));
    }

    [Fact, Trait("SEC", "SEC-5.2")]
    public void Cleanup_does_not_follow_links_out_of_staging()
    {
        var outside = _root + "-outside";
        Directory.CreateDirectory(outside);
        try
        {
            File.WriteAllText(Path.Combine(outside, "waznyplik.bak"), "x");
            Directory.CreateSymbolicLink(Path.Combine(_root, "linkrun0001"), outside);
            Staging.CleanupStale(_root, new HashSet<string>(), DateTimeOffset.UtcNow.AddDays(1));
            Assert.True(File.Exists(Path.Combine(outside, "waznyplik.bak")));
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact, Trait("AC", "AC-02.1"), Trait("K", "K16.1")]
    public void Delete_and_verify_leaves_no_bak_file()
    {
        var dir = MakeRun("run0000001", TimeSpan.Zero);
        var file = Directory.GetFiles(dir).Single();
        Assert.True(Staging.DeleteAndVerify(file));
        Assert.False(Directory.Exists(dir));
    }
}

public sealed class ChainAndControlTests
{
    private static readonly Guid A = Guid.NewGuid(), B = Guid.NewGuid();

    [Theory, Trait("D", "D5.2")]
    [InlineData(false, false, false, ChainVerdict.Clean)]    // brak pełnych
    [InlineData(true, true, true, ChainVerdict.Clean)]       // łańcuch Kobian
    [InlineData(true, true, false, ChainVerdict.ForeignFullBackup)]  // pełna obca (np. plan konserwacji SQL)
    [InlineData(true, false, true, ChainVerdict.ForeignFullBackup)]  // baza różnicowa przesunięta
    public void Chain_rule_R2_5_3(bool hasFull, bool baseMatches, bool kobianOwns, ChainVerdict expected)
    {
        Guid? last = hasFull ? A : null;
        Guid? diffBase = baseMatches ? A : B;
        Guid? kobian = kobianOwns ? A : B;
        Assert.Equal(expected, ChainEvaluator.Evaluate(last, diffBase, kobian));
    }

    [Theory, Trait("D", "D5.11")]
    [InlineData("range", 100, 105, 103, true)]
    [InlineData("range", 100, 105, 99, false)]
    [InlineData("range", 100, 105, 106, false)]
    [InlineData("exact", 7, 7, 7, true)]
    [InlineData("exact", 7, 8, 7, false)]
    [InlineData("approx", 1_000_000, 1_000_000, 1_000_900, true)]
    [InlineData("approx", 1_000_000, 1_000_000, 1_002_000, false)]
    public void Control_query_tolerances(string tol, long before, long after, long restored, bool ok) =>
        Assert.Equal(ok, ControlComparison.Matches(tol, before, after, restored));

    [Fact]
    public void Date_tolerance_requires_max_not_earlier_than_reference()
    {
        var t = new DateTimeOffset(2026, 10, 12, 20, 0, 0, TimeSpan.Zero);
        Assert.True(ControlComparison.DateMatches(t, t.AddMinutes(1)));
        Assert.False(ControlComparison.DateMatches(t, t.AddDays(-1)));
        Assert.False(ControlComparison.DateMatches(t, null));
    }
}
