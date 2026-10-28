using System.Globalization;
using Kobian.UI.Dashboard;
using Kobian.UI.Localization;

namespace Kobian.UI.Jobs;

/// <summary>Harmonogram zadania z usługi (ListSchedules — RD-27): wyrażenie cron i stan włączenia. Wyłączony harmonogram zachowuje wyrażenie.</summary>
public sealed record JobSchedule(string Job, string Cron, bool Enabled);

/// <summary>Dane SCR-02: stan zadań (jak na pulpicie) + harmonogramy + rola wywołującego.</summary>
public sealed record JobsData(IReadOnlyList<JobStatusInfo> Jobs, IReadOnlyList<JobSchedule> Schedules, CallerRole Role, DateTimeOffset AsOfUtc);

/// <summary>
/// Port SCR-02 do usługi (GetStatus + ListSnapshots + ListSchedules; RunJob; SetSchedule). Adapter: <c>GrpcDashboardSource</c>.
/// Metody akcji zwracają kod KB odmowy usługi albo null (sukces). Brak połączenia → <see cref="IOException"/>.
/// </summary>
public interface IJobsSource
{
    Task<JobsData> LoadJobsAsync(CancellationToken ct);

    Task<string?> StartJobAsync(string job, CancellationToken ct);

    /// <summary><paramref name="cron"/> = null wyłącza harmonogram (wyrażenie zostaje w katalogu); inaczej ustawia/wznawia.</summary>
    Task<string?> SetScheduleAsync(string job, string? cron, CancellationToken ct);
}

/// <summary>
/// Opis harmonogramu dla ludzi (6.9) i oczekiwany cykl (kontrola zaległości 6.3) dla najczęstszych wzorców cron
/// (co godzinę, codziennie, dni robocze, raz w tygodniu). Inne wyrażenia: surowy cron, cykl nieznany (bez kontroli zaległości).
/// </summary>
public static class CronText
{
    public static string Describe(string cron, CultureInfo? culture = null)
    {
        var c = culture ?? CultureInfo.CurrentUICulture;
        if (!TryParse(cron, out var p)) return string.Format(c, Strings.Get("Cron.Custom", c), cron);
        return p.Kind switch
        {
            Kind.Hourly => p.Minute == 0 ? Strings.Get("Cron.Hourly", c) : string.Format(c, Strings.Get("Cron.HourlyAt", c), p.Minute.ToString("00", c)),
            Kind.Daily => string.Format(c, Strings.Get("Cron.Daily", c), p.Time(c)),
            Kind.Weekdays => string.Format(c, Strings.Get("Cron.Weekdays", c), p.Time(c)),
            _ => string.Format(c, Strings.Get("Cron.Weekly", c), c.DateTimeFormat.GetDayName((DayOfWeek)p.Day), p.Time(c)),
        };
    }

    /// <summary>Oczekiwany odstęp między kopiami (dni robocze = 3 dni, by weekend nie dawał fałszywej zaległości); null = nieznany.</summary>
    public static TimeSpan? Interval(string cron) =>
        !TryParse(cron, out var p) ? null : p.Kind switch
        {
            Kind.Hourly => TimeSpan.FromHours(1),
            Kind.Daily => TimeSpan.FromDays(1),
            Kind.Weekdays => TimeSpan.FromDays(3),
            _ => TimeSpan.FromDays(7),
        };

    private enum Kind
    {
        Hourly,
        Daily,
        Weekdays,
        Weekly,
    }

    private readonly record struct Pattern(Kind Kind, int Minute, int Hour, int Day)
    {
        public string Time(CultureInfo c) => new DateTime(2000, 1, 1, Hour, Minute, 0, DateTimeKind.Unspecified).ToString("HH:mm", c);
    }

    private static bool TryParse(string cron, out Pattern p)
    {
        p = default;
        var f = (cron ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (f.Length != 5 || f[2] != "*" || f[3] != "*") return false;
        if (!Num(f[0], 59, out var m)) return false;
        if (f[1] == "*") { p = new(Kind.Hourly, m, 0, 0); return f[4] == "*"; }
        if (!Num(f[1], 23, out var h)) return false;
        if (f[4] == "*") { p = new(Kind.Daily, m, h, 0); return true; }
        if (f[4] == "1-5") { p = new(Kind.Weekdays, m, h, 0); return true; }
        if (Num(f[4], 7, out var d)) { p = new(Kind.Weekly, m, h, d % 7); return true; }
        return false;
    }

    private static bool Num(string s, int max, out int v) =>
        int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out v) && v <= max;
}
