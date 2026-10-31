using System.Runtime;

namespace Kobian.Agent.Jobs;

/// <summary>
/// Po uruchomieniu (kopia, odtwarzanie, weryfikacja) usługa trzyma bufory packów, tablicę dedup i cache indeksów w stercie — bez sprzątania proces zostaje
/// przy ~120–190 MB prywatnej pamięci przez godziny (pomiar tools/perf: 78 MB w spoczynku). Jedno kompaktujące sprzątanie (z LOH) kończy to w ułamku sekundy.
/// Sygnałem jest pamięć ZATWIERDZONA przez GC po przebiegu (to ona zostaje w RSS, nawet gdy sterta jest pusta), nie ilość przeczytanych danych:
/// przyrost bez zmian czyta „1,2 GB” logicznie, a prawie nic nie alokuje.
/// </summary>
public static class MemoryTrim
{
    /// <summary>Od takiej pamięci zatwierdzonej przez GC (bajty) po przebiegu robimy sprzątanie; w spoczynku Agent ma ~10–20 MB.</summary>
    public const long HeapThresholdBytes = 32L << 20;

    private static long _lastTicks;

    public static bool ShouldTrim(long committedBytes) => committedBytes >= HeapThresholdBytes;

    /// <summary>Sprzątanie z kompaktowaniem LOH; najwyżej cztery razy na sekundę (równoległe zakończenia przebiegów nie kolejkują sprzątań).</summary>
    public static bool TrimNow(long nowTicks)
    {
        var prev = Interlocked.Read(ref _lastTicks);
        if (nowTicks - prev < TimeSpan.FromMilliseconds(250).Ticks || Interlocked.CompareExchange(ref _lastTicks, nowTicks, prev) != prev) return false;
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        // Aggressive = pełne, blokujące, kompaktujące sprzątanie + oddanie systemowi zatwierdzonej, ale pustej pamięci (zwykłe Forced zostawiało 50–100 MB committed przy 1 MB sterty).
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        return true;
    }

    /// <summary>Po przebiegu: sprzątanie, gdy GC trzyma dużo pamięci.</summary>
    public static void AfterRun()
    {
        var before = GC.GetTotalMemory(false);
        var did = ShouldTrim(GC.GetGCMemoryInfo().TotalCommittedBytes) && TrimNow(Environment.TickCount64 * TimeSpan.TicksPerMillisecond);
        if (Environment.GetEnvironmentVariable("KOBIAN_MEM_LOG") == "1")
        {
            var i = GC.GetGCMemoryInfo();
            Console.Error.WriteLine($"memtrim before={before >> 20}MB trimmed={did} after={GC.GetTotalMemory(false) >> 20}MB heapSize={i.HeapSizeBytes >> 20}MB committed={i.TotalCommittedBytes >> 20}MB frag={i.FragmentedBytes >> 20}MB");
        }
    }
}
