using Cronos;
using Kobian.Domain;

namespace Kobian.Scheduler.Timing;

/// <summary>
/// Harmonogram cron w strefie zadania (D7.2, CE-5: strefa zadania, nie systemu). DST przez Cronos:
/// luka → pierwsza poprawna chwila (E1), nakładka → jedno wystąpienie (E2).
/// </summary>
public sealed class CronSchedule
{
    public static readonly TimeSpan MinInterval = TimeSpan.FromMinutes(5);
    private const int MaxOccurrencesScan = 100_000;
    private readonly CronExpression _expr;

    private CronSchedule(CronExpression expr, TimeZoneInfo zone, string text)
    {
        _expr = expr;
        Zone = zone;
        Text = text;
    }

    public TimeZoneInfo Zone { get; }

    public string Text { get; }

    /// <summary>Parsuje wyrażenie 5-polowe i strefę IANA. Błąd → <see cref="KbCode.ScheduleInvalid"/> (AC-03.3).</summary>
    public static CronSchedule Parse(string expression, string timeZoneId)
    {
        ArgumentNullException.ThrowIfNull(expression);
        try
        {
            var expr = CronExpression.Parse(expression, CronFormat.Standard);
            var zone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
            return new CronSchedule(expr, zone, expression);
        }
        catch (Exception ex) when (ex is CronFormatException or TimeZoneNotFoundException or InvalidTimeZoneException or ArgumentException)
        {
            throw new KobianException(KbCode.ScheduleInvalid, ErrorClass.Permanent, "Invalid cron expression or time zone.", ex);
        }
    }

    public DateTimeOffset? NextAfter(DateTimeOffset utc) =>
        _expr.GetNextOccurrence(utc.UtcDateTime, Zone, inclusive: false) is { } d ? new DateTimeOffset(d, TimeSpan.Zero) : null;

    /// <summary>Wystąpienia w (fromExclusive, toInclusive] — do wykrywania pominiętych (E3, AC-03.1).</summary>
    public IReadOnlyList<DateTimeOffset> Between(DateTimeOffset fromExclusive, DateTimeOffset toInclusive)
    {
        var list = new List<DateTimeOffset>();
        // Zegar cofnięty (lastHandled w przyszłości) — brak zaległości; znalezisko symulacji RD-5 (it. 12): Cronos rzuca ArgumentException.
        if (fromExclusive >= toInclusive) return list;
        foreach (var d in _expr.GetOccurrences(fromExclusive.UtcDateTime, toInclusive.UtcDateTime, Zone, fromInclusive: false, toInclusive: true))
        {
            list.Add(new DateTimeOffset(d, TimeSpan.Zero));
            if (list.Count >= MaxOccurrencesScan) break;
        }
        return list;
    }

    /// <summary>Podgląd N najbliższych (AC-03.3).</summary>
    public IReadOnlyList<DateTimeOffset> Preview(DateTimeOffset utcNow, int count = 5)
    {
        var list = new List<DateTimeOffset>(count);
        var t = utcNow;
        while (list.Count < count && NextAfter(t) is { } n)
        {
            list.Add(n);
            t = n;
        }
        return list;
    }

    /// <summary>Reguła S3 (R2.6.2): ≥ 5 min między wystąpieniami w ciągu najbliższego tygodnia.</summary>
    public bool RespectsMinimumInterval(DateTimeOffset utcNow)
    {
        DateTimeOffset? prev = null;
        foreach (var o in Between(utcNow, utcNow.AddDays(7)))
        {
            if (prev is not null && o - prev.Value < MinInterval) return false;
            prev = o;
        }
        return true;
    }
}
