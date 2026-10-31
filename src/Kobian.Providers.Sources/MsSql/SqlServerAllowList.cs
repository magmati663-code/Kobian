using Kobian.Domain;
using Kobian.Providers.Sources.MsSql.Tls;

namespace Kobian.Providers.Sources.MsSql;

/// <summary>
/// Lista serwerów SQL, z którymi Agent może się łączyć (RD-39, PROWIZORYCZNIE Qb35, ADR-0023). Chroni konto usługi przed przekazaniem
/// poświadczeń Windows (NTLM/Kerberos) do dowolnego hosta wskazanego przez Operatora — discovery, pobranie certyfikatu, zapis i uruchomienie zadania.
/// Wpisy (rozdzielane „;” lub „,” w zmiennej środowiskowej — przecinek tylko między wpisami, port przez „:”):
/// <c>host</c> (dowolna instancja/port na hoście), <c>host\instancja</c>, <c>host:port</c>, <c>*</c> (świadome wyłączenie ochrony).
/// Porównanie nazw bez rozróżniania wielkości liter, BEZ rozwiązywania DNS (nazwa ma być taka jak w zadaniu). Pusta konfiguracja = tylko ten komputer
/// (localhost, „.”, 127.0.0.1, ::1) — Agent na hoście SQL (D5.10).
/// </summary>
public sealed class SqlServerAllowList
{
    private readonly bool _any;
    private readonly IReadOnlyList<(string Host, string? Instance, int? Port)> _entries;

    private SqlServerAllowList(bool any, IReadOnlyList<(string, string?, int?)> entries)
    {
        _any = any;
        _entries = entries;
    }

    /// <summary>Bez ograniczeń — tylko dla testów i jawnego „*”.</summary>
    public static SqlServerAllowList Any { get; } = new(true, []);

    /// <summary>Tylko serwer na tym komputerze (domyślnie, gdy administrator niczego nie skonfigurował).</summary>
    public static SqlServerAllowList LocalOnly { get; } = new(false, [("localhost", null, null)]);

    public bool AllowsAny => _any;

    public int Count => _entries.Count;

    /// <summary>Konfiguracja z wartości zmiennej <c>KOBIAN_SQL_ALLOWED_SERVERS</c>; null/pusta = <see cref="LocalOnly"/>. Niepoprawny wpis = wyjątek przy starcie (KB-0103), nie ciche pominięcie.</summary>
    public static SqlServerAllowList Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return LocalOnly;
        var list = new List<(string, string?, int?)>();
        foreach (var raw in value.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (raw == "*") return Any;
            var s = raw;
            var colon = s.LastIndexOf(':');
            if (colon > 0 && !s.StartsWith('[') && s.IndexOf(':', StringComparison.Ordinal) == colon) s = s[..colon] + "," + s[(colon + 1)..]; // host:port → host,port
            var ep = SqlEndpoint.Parse(s);
            list.Add((Normalize(ep.Host), ep.Instance, ep.Port));
        }
        return list.Count == 0 ? LocalOnly : new SqlServerAllowList(false, list);
    }

    /// <summary>Czy wolno łączyć się z serwerem (wartość pola „server” zadania/żądania). Niepoprawny adres = nie wolno.</summary>
    public bool IsAllowed(string? server)
    {
        if (_any) return true;
        SqlEndpoint ep;
        try
        {
            ep = SqlEndpoint.Parse(server);
        }
        catch (KobianException)
        {
            return false;
        }
        var host = Normalize(ep.Host);
        foreach (var (h, inst, port) in _entries)
        {
            if (!string.Equals(h, host, StringComparison.OrdinalIgnoreCase)) continue;
            if (inst is not null && !string.Equals(inst, ep.Instance, StringComparison.OrdinalIgnoreCase)) continue;
            if (port is not null && port != (ep.Port ?? (ep.Instance is null ? SqlEndpoint.DefaultPort : null))) continue;
            return true;
        }
        return false;
    }

    /// <summary>Rzuca KB-3009, gdy serwer spoza listy (komunikat bez nazwy serwera — NFR-11).</summary>
    public void Demand(string? server)
    {
        if (!IsAllowed(server))
            throw new KobianException(KbCode.SqlServerNotAllowed, ErrorClass.Security, "The SQL Server is not on the allowed list of this Agent.");
    }

    private static string Normalize(string host) =>
        host is "127.0.0.1" or "::1" || string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ? "localhost" : host;

    public override string ToString() => _any ? "SqlServerAllowList(*)" : $"SqlServerAllowList({_entries.Count})";
}
