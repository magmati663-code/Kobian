using System.Runtime;

namespace Kobian.Agent.Jobs;

/// <summary>
/// Po dużym uruchomieniu (kopia, odtwarzanie, weryfikacja) usługa trzyma bufory packów, tablicę dedup i cache indeksów w stercie — bez sprzątania proces zostaje
/// przy ~190 MB przez godziny (pomiar tools/perf: 1,2 GB danych → 193 MB po kopii, 77 MB w spoczynku). Jedno kompaktujące sprzątanie (z LOH) kończy to w ułamku sekundy.
/// Małe uruchomienia (przyrost bez zmian, SQL) nie wywołują sprzątania.
/// </summary>
public static class MemoryTrim
{
    /// <summary>Od tylu bajtów przeczytanych danych uruchomienie uznajemy za „duże”.</summary>
    public const long LargeRunBytes = 64L << 20;

    private static long _lastTicks;

    public static bool ShouldTrim(long bytesProcessed) => bytesProcessed >= LargeRunBytes;

    /// <summary>Sprzątanie z kompaktowaniem LOH; najwyżej raz na 5 s (równoległe zakończenia uruchomień nie kolejkują sprzątań).</summary>
    public static bool TrimNow(long nowTicks)
    {
        var prev = Interlocked.Read(ref _lastTicks);
        if (nowTicks - prev < TimeSpan.FromSeconds(5).Ticks || Interlocked.CompareExchange(ref _lastTicks, nowTicks, prev) != prev) return false;
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        return true;
    }

    /// <summary>Po uruchomieniu: sprzątanie tylko gdy było duże.</summary>
    public static void AfterRun(long bytesProcessed)
    {
        if (ShouldTrim(bytesProcessed)) TrimNow(Environment.TickCount64 * TimeSpan.TicksPerMillisecond);
    }
}
