using System.Security.Cryptography;
using Kobian.Engine.Backup;
using Xunit;

namespace Kobian.Tests.Unit.Engine;

/// <summary>RD-22 (it. 87): DedupTable zachowuje niezmienniki I-2/I-3 poprzedniej implementacji (słownik + zbiór ciągów hex).</summary>
public sealed class DedupTableTests
{
    private static byte[] Id() => RandomNumberGenerator.GetBytes(32);

    [Fact, Trait("RD", "RD-22"), Trait("D", "D3.15")]
    public void Baseline_blocks_are_not_new_and_new_blocks_need_confirmation()
    {
        var t = new DedupTable();
        var baseId = Id();
        var fresh = Id();
        t.AddConfirmed(baseId);
        Assert.False(t.Reference(baseId));          // z bazy — nie wysyłamy
        Assert.True(t.Reference(fresh));            // nowy — do packu
        Assert.False(t.Reference(fresh));           // drugi raz w tym samym uruchomieniu — już w packu
        Assert.False(t.AllReferencedConfirmed());   // I-3: pack jeszcze niezweryfikowany
        t.ConfirmPending();
        Assert.True(t.AllReferencedConfirmed());
        Assert.Equal(2, t.Count);
    }

    [Fact, Trait("RD", "RD-22")]
    public void Unreferenced_unconfirmed_entries_do_not_break_I3_and_keys_compare_by_value()
    {
        var t = new DedupTable();
        var a = Id();
        t.AddConfirmed(a);
        Assert.True(t.AllReferencedConfirmed());
        Assert.Equal(new ChunkKey(a), new ChunkKey((byte[])a.Clone()));
        Assert.NotEqual(new ChunkKey(a), new ChunkKey(Id()));
        Assert.Throws<ArgumentException>(() => new ChunkKey(new byte[31]));
    }

    [Theory, Trait("RD", "RD-22")]
    [InlineData(int.MaxValue)] // tablica posortowana bez filtra (domyślnie)
    [InlineData(0)]            // tablica + filtr Blooma (it. 102) — ten sam wynik
    public void Model_check_against_reference_implementation(int bloomThreshold)
    {
        var rnd = new Random(8701);
        var pool = Enumerable.Range(0, 300).Select(_ => Id()).ToArray();
        for (var round = 0; round < 50; round++)
        {
            var t = new DedupTable(0, bloomThreshold);
            var known = new Dictionary<string, bool>(StringComparer.Ordinal);
            var refd = new HashSet<string>(StringComparer.Ordinal);
            var pending = new List<string>();
            foreach (var b in pool.Where(_ => rnd.Next(4) == 0)) { t.AddConfirmed(b); known[Convert.ToHexString(b)] = true; }
            for (var op = 0; op < 400; op++)
            {
                if (rnd.Next(10) == 0)
                {
                    t.ConfirmPending();
                    foreach (var h in pending) known[h] = true;
                    pending.Clear();
                    continue;
                }
                var id = pool[rnd.Next(pool.Length)];
                var hex = Convert.ToHexString(id);
                refd.Add(hex);
                var expectedNew = !known.ContainsKey(hex);
                if (expectedNew) { known[hex] = false; pending.Add(hex); }
                Assert.Equal(expectedNew, t.Reference(id));
            }
            var expectedI3 = refd.All(h => known.TryGetValue(h, out var ok) && ok);
            Assert.Equal(expectedI3, t.AllReferencedConfirmed());
        }
    }

    [Fact, Trait("RD", "RD-22"), Trait("D", "D3.15")]
    public void Base_is_frozen_sorted_and_deduplicated_and_late_confirmations_still_count()
    {
        var a = Id();
        var b = Id();
        var t = new DedupTable(3, bloomThreshold: 2);
        t.AddConfirmed(a);
        t.AddConfirmed(b);
        t.AddConfirmed((byte[])a.Clone()); // ten sam blok w dwóch indeksach bazowych
        Assert.Equal(3, t.Count); // przed zamrożeniem — surowa lista
        Assert.False(t.Reference(b));
        Assert.Equal((2, true), t.Shape); // po zamrożeniu: 2 unikalne, filtr (próg 2)
        Assert.Equal(2, t.Count);
        var late = Id();
        t.AddConfirmed(late); // po zamrożeniu (poza potokiem) — słownik, nadal „znany i potwierdzony”
        Assert.False(t.Reference(late));
        t.AddConfirmed(a); // już w bazie — bez duplikatu
        Assert.Equal(3, t.Count);
        Assert.True(t.AllReferencedConfirmed());
        Assert.True(t.Reference(Id()));
        Assert.False(t.AllReferencedConfirmed());
    }

    [Fact, Trait("RD", "RD-22"), Trait("D", "D3.15")]
    public void Bloom_filter_has_no_false_negatives_and_rare_false_positives()
    {
        var keys = Enumerable.Range(0, 20_000).Select(_ => new ChunkKey(Id())).ToArray();
        var f = new ChunkBloomFilter(keys.Length);
        foreach (var k in keys) f.Add(k);
        Assert.All(keys, k => Assert.True(f.MayContain(k)));
        var fp = Enumerable.Range(0, 20_000).Count(_ => f.MayContain(new ChunkKey(Id())));
        Assert.InRange(fp, 0, 20_000 * 3 / 100); // ≈ 1% z wzoru (10 bit/wpis, k = 7); próg 3% — test probabilistyczny z zapasem
        Assert.True(new ChunkKey(Id()).CompareTo(new ChunkKey(Id())) != 0);
    }
}
