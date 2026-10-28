using Kobian.Domain;
using Microsoft.Data.SqlClient;

namespace Kobian.Providers.Sources.MsSql.SqlClient;

/// <summary>Sposób uwierzytelnienia do SQL Server (R2.5.1).</summary>
public enum MsSqlAuthentication
{
    /// <summary>Uwierzytelnienie Windows konta usługi (konto wirtualne — D2.12). Domyślne.</summary>
    Integrated = 0,
    /// <summary>Login SQL; hasło wyłącznie przez <see cref="SqlCredential"/> (nigdy w łańcuchu połączenia).</summary>
    SqlLogin = 1,
}

/// <summary>
/// Parametry połączenia ze źródłem MSSQL (BL-40 cz. 2, SEC-5.6). Bez sekretów — hasło loginu SQL przychodzi osobno
/// (<see cref="SqlClientMsSqlSession"/> przyjmuje dostawcę hasła). <see cref="ToString"/> nie ujawnia ścieżek ani nazw (NFR-11).
/// </summary>
public sealed record MsSqlConnectionOptions
{
    /// <summary>Serwer: <c>host</c>, <c>host\instancja</c> lub <c>host,port</c> (bez prefiksów protokołu <c>np:</c>/<c>lpc:</c>/<c>admin:</c>).</summary>
    public required string Server { get; init; }

    public MsSqlAuthentication Authentication { get; init; } = MsSqlAuthentication.Integrated;

    /// <summary>Login SQL (tylko <see cref="MsSqlAuthentication.SqlLogin"/>).</summary>
    public string? UserName { get; init; }

    /// <summary>
    /// Plik .cer zatwierdzonego certyfikatu serwera (pin, plan 5.x „Połączenie”). Bez pinu walidacja łańcucha systemowego;
    /// <c>TrustServerCertificate</c> wyłącznie dla localhost i tylko po jawnym <see cref="AllowLocalhostUntrusted"/> (ostrzeżenie).
    /// // PROWIZORYCZNIE Qb29: pin = ścieżka pliku .cer w katalogu stanu (alternatywa: odcisk SHA-256 w definicji zadania).
    /// </summary>
    public string? ServerCertificatePath { get; init; }

    /// <summary>Tylko localhost: zgoda na certyfikat niezaufany (self-signed instancji Express) — z ostrzeżeniem KB-3004 w UI.</summary>
    public bool AllowLocalhostUntrusted { get; init; }

    /// <summary><see langword="true"/> = TDS 8 / <c>Encrypt=Strict</c> (SQL 2022+). Domyślnie <c>Mandatory</c> (SQL 2014 SP3+ z TLS 1.2 — SP-07).</summary>
    public bool Strict { get; init; }

    /// <summary>Limit nawiązania połączenia (s). Zapytania: <see cref="QueryTimeoutSeconds"/>; BACKUP/RESTORE: bez limitu (anulowanie tokenem).</summary>
    public int ConnectTimeoutSeconds { get; init; } = 15;

    /// <summary>Limit zwykłych zapytań discovery/msdb (s).</summary>
    public int QueryTimeoutSeconds { get; init; } = 60;

    public override string ToString() => $"MsSqlConnectionOptions(auth={Authentication}, pinned={ServerCertificatePath is not null}, strict={Strict})";
}

/// <summary>
/// Budowa <see cref="SqlConnectionStringBuilder"/> z <see cref="MsSqlConnectionOptions"/> — czysta funkcja testowalna bez serwera.
/// Gwarancje (SEC-5.6, NFR-07, NFR-11): szyfrowanie zawsze, <c>ApplicationName = "Kobian Backup"</c>, brak hasła w łańcuchu,
/// <c>TrustServerCertificate</c> tylko localhost + jawna zgoda, brak <c>AttachDBFilename</c>/<c>User Instance</c>/MARS.
/// </summary>
public static class MsSqlConnectionFactory
{
    public const string ApplicationName = "Kobian Backup";

    public static SqlConnectionStringBuilder Build(MsSqlConnectionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var server = ValidateServer(options.Server);
        if (options.ConnectTimeoutSeconds is < 1 or > 300) throw Invalid("connect timeout");
        if (options.QueryTimeoutSeconds is < 1 or > 3600) throw Invalid("query timeout");
        if (options.Authentication == MsSqlAuthentication.SqlLogin)
        {
            if (string.IsNullOrWhiteSpace(options.UserName) || options.UserName.Length > 128 || options.UserName.Any(char.IsControl))
                throw Invalid("user name");
        }
        else if (options.UserName is not null)
        {
            throw Invalid("user name with integrated authentication");
        }

        var local = IsLocalhost(server);
        if (options.AllowLocalhostUntrusted && !local) throw Invalid("untrusted certificate allowed only for localhost");
        if (options.ServerCertificatePath is { } pin)
        {
            if (!Path.IsPathFullyQualified(pin) || pin.Contains('\0', StringComparison.Ordinal) || pin.Contains(';', StringComparison.Ordinal))
                throw Invalid("server certificate path");
            if (options.AllowLocalhostUntrusted) throw Invalid("pinned certificate and untrusted certificate are exclusive");
        }

        var b = new SqlConnectionStringBuilder
        {
            DataSource = server,
            InitialCatalog = "master",
            ApplicationName = ApplicationName,
            Encrypt = options.Strict ? SqlConnectionEncryptOption.Strict : SqlConnectionEncryptOption.Mandatory,
            TrustServerCertificate = local && options.AllowLocalhostUntrusted,
            IntegratedSecurity = options.Authentication == MsSqlAuthentication.Integrated,
            PersistSecurityInfo = false,
            MultipleActiveResultSets = false,
            ConnectTimeout = options.ConnectTimeoutSeconds,
            CommandTimeout = options.QueryTimeoutSeconds,
            Pooling = true,
            MultiSubnetFailover = false,
        };
        if (options.ServerCertificatePath is { } path) b.ServerCertificate = path;
        return b;
    }

    /// <summary>Serwer z kreatora/definicji: tylko nazwa hosta/IPv4/[IPv6], opcjonalnie <c>\instancja</c> albo <c>,port</c>.</summary>
    internal static string ValidateServer(string? server)
    {
        if (string.IsNullOrWhiteSpace(server) || server.Length > 255) throw Invalid("server");
        var s = server.Trim();
        foreach (var c in s)
        {
            var ok = char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or '\\' or ',' or ':' or '[' or ']';
            if (!ok) throw Invalid("server");
        }

        // Prefiksy protokołu (tcp:/np:/lpc:/admin:) — DAC i potoki nazwane wyłączone (SEC: brak połączenia administracyjnego).
        if (s.Contains(':', StringComparison.Ordinal) && !s.StartsWith('[')) throw Invalid("server protocol prefix");
        if (s.StartsWith('['))
        {
            var close = s.IndexOf(']', StringComparison.Ordinal);
            if (close < 3) throw Invalid("server");
            var rest = s[(close + 1)..];
            if (rest.Length > 0 && rest[0] is not (',' or '\\')) throw Invalid("server");
        }

        var parts = s.Split(',');
        if (parts.Length > 2) throw Invalid("server");
        if (parts.Length == 2 && (!int.TryParse(parts[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var port) || port is < 1 or > 65535))
            throw Invalid("server port");
        if (parts[0].Count(c => c == '\\') > 1 || parts[0].EndsWith('\\') || parts[0].StartsWith('\\')) throw Invalid("server instance");
        return s;
    }

    internal static bool IsLocalhost(string server)
    {
        var host = server.Split(',')[0].Split('\\')[0];
        return host is "." or "127.0.0.1" or "[::1]"
            || string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase);
    }

    private static KobianException Invalid(string what) =>
        new(KbCode.JobDefinitionInvalid, ErrorClass.Permanent, "Invalid MSSQL connection option: " + what + ".");
}
