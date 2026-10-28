using Kobian.Engine.Backup;
using Xunit;

namespace Kobian.Tests.Unit.Engine;

/// <summary>
/// RD-22 / NFR-22 (it. 88, rola 13): strażnik pamięci dedup — ≤ 48 B na blok bazowy przy 1 mln bloków (it. 87 słownik: 73 B/blok; it. 102 tablica posortowana: 32 B/blok).
/// Category=Perf — poza ci.sh (pomiar GC zależny od środowiska); uruchamiany ręcznie przy zmianach DedupTable i przy przeglądzie rundy.
/// </summary>
public sealed class DedupMemoryPerfTests
{
    [Fact, Trait("Category", "Perf"), Trait("NFR", "NFR-22"), Trait("RD", "RD-22")]
    public void One_million_base_blocks_fit_in_48_bytes_each()
    {
        const int n = 1_000_000;
        var rnd = new Random(88);
        var id = new byte[32];
        static long Mem()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            return GC.GetTotalMemory(true);
        }
        var before = Mem();
        var t = new DedupTable(n);
        for (var i = 0; i < n; i++)
        {
            rnd.NextBytes(id);
            t.AddConfirmed(id); // wzorzec potoku: najpierw cała baza …
        }
        t.Reference(id); // … potem odwołania (zamrożenie do tablicy posortowanej, it. 102)
        var perBlock = (Mem() - before) / (double)n;
        GC.KeepAlive(t);
        Assert.Equal(n, t.Count);
        Assert.True(perBlock <= 48, $"{perBlock:F0} B/blok > 48"); // it. 102: zmierzone 32 B/blok (było 73 — słownik flag)
        TestContext.Current.SendDiagnosticMessage($"DedupTable: {perBlock:F0} B/blok przy {n:N0} blokach");
    }
}
