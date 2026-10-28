using System.Text.RegularExpressions;
using Kobian.Domain;
using Kobian.Providers.Sources.MsSql;
using Kobian.Providers.Sources.MsSql.SqlClient;

namespace Kobian.Agent.Jobs;

/// <summary>
/// Żądanie discovery z kreatora S1-db (bez sekretów — AC-01.3): login SQL tylko jako <c>SecretRef</c> magazynu Agenta,
/// pin certyfikatu tylko jako NAZWA pliku .cer w <c>&lt;stan&gt;/certs</c> (klient nie wskazuje Agentowi dowolnej ścieżki).
/// </summary>
public sealed record DatabaseDiscoveryRequest(
    string Server,
    string? Auth = null,
    string? SecretRef = null,
    string? Certificate = null,
    bool Strict = false,
    bool AllowLocalhostUntrusted = false);

/// <summary>Wynik: kod KB przy porażce albo wynik discovery + skrypt uprawnień (D5.7) dla baz bez BACKUP DATABASE (tylko uwierzytelnienie Windows).</summary>
public sealed record DatabaseDiscoveryOutcome(string? Code, MsSqlDiscoveryResult? Result, string? PermissionScript)
{
    public static DatabaseDiscoveryOutcome Failed(string code) => new(code, null, null);
}

/// <summary>
/// DiscoverDatabases (BL-40 cz. 3, SCR-03 S1-db, R2.5.2): połączenie na koncie Agenta → <see cref="MsSqlDiscoveryService"/> (tylko odczyt).
/// Niczego nie zapisuje — ani na serwerze SQL, ani w katalogu. Walidacja wejścia przed jakimkolwiek połączeniem (KB-0103).
/// // PROWIZORYCZNIE Qb30: pin certyfikatu = nazwa pliku w &lt;stan&gt;/certs (zapis pliku: <see cref="ServerCertificateService"/>, it. 74; alternatywa: odcisk SHA-256 w żądaniu).
/// </summary>
public sealed partial class DatabaseDiscoveryService(string certsDir, Func<MsSqlConnectionOptions, string?, CancellationToken, ValueTask<IMsSqlSession>> sessions)
{
    public const int MaxSecretRefLength = 256;

    /// <summary>RD-39 (PROWIZORYCZNIE Qb35): serwery dozwolone dla discovery i zapisu zadania (JobDraftValidator używa <see cref="Validate"/>); Program.cs ustawia z konfiguracji.</summary>
    public SqlServerAllowList AllowedServers { get; init; } = SqlServerAllowList.Any;

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_.-]{0,63}\\.cer$", RegexOptions.CultureInvariant, 100)]
    private static partial Regex CertificateName();

    /// <summary>Nazwa pliku pinu w &lt;stan&gt;/certs: litery/cyfry/<c>_.-</c>, rozszerzenie .cer, bez „..” (wspólne z zatwierdzaniem certyfikatu).</summary>
    public static bool IsValidCertificateName(string? name) =>
        !string.IsNullOrEmpty(name) && CertificateName().IsMatch(name) && !name.Contains("..", StringComparison.Ordinal);

    /// <summary>Walidacja kształtu i budowa opcji połączenia (bez sieci). Null w <c>Code</c> = poprawne.</summary>
    public (string? Code, MsSqlConnectionOptions? Options) Validate(DatabaseDiscoveryRequest r)
    {
        ArgumentNullException.ThrowIfNull(r);
        var auth = string.IsNullOrEmpty(r.Auth) ? "integrated" : r.Auth;
        MsSqlAuthentication mode;
        switch (auth)
        {
            case "integrated":
                mode = MsSqlAuthentication.Integrated;
                if (!string.IsNullOrEmpty(r.SecretRef)) return (KbCode.JobDefinitionInvalid, null);
                break;
            case "sql":
                mode = MsSqlAuthentication.SqlLogin;
                if (string.IsNullOrWhiteSpace(r.SecretRef) || r.SecretRef.Length > MaxSecretRefLength || r.SecretRef.Any(char.IsControl))
                    return (KbCode.JobDefinitionInvalid, null);
                break;
            default:
                return (KbCode.JobDefinitionInvalid, null);
        }

        string? pin = null;
        if (!string.IsNullOrEmpty(r.Certificate))
        {
            if (!IsValidCertificateName(r.Certificate)) return (KbCode.JobDefinitionInvalid, null);
            pin = Path.Combine(Path.GetFullPath(certsDir), r.Certificate);
            if (!File.Exists(pin)) return (KbCode.JobDefinitionInvalid, null);
        }

        var options = new MsSqlConnectionOptions
        {
            Server = r.Server ?? "",
            Authentication = mode,
            UserName = mode == MsSqlAuthentication.SqlLogin ? "placeholder" : null, // tylko do walidacji kształtu; login z magazynu sekretów
            ServerCertificatePath = pin,
            Strict = r.Strict,
            AllowLocalhostUntrusted = r.AllowLocalhostUntrusted,
        };
        try
        {
            MsSqlConnectionFactory.Build(options);
        }
        catch (KobianException e)
        {
            return (e.Code, null);
        }
        if (!AllowedServers.IsAllowed(options.Server)) return (KbCode.SqlServerNotAllowed, null); // przed jakimkolwiek połączeniem
        return (null, options with { UserName = null });
    }

    public async Task<DatabaseDiscoveryOutcome> DiscoverAsync(DatabaseDiscoveryRequest r, CancellationToken ct)
    {
        var (bad, options) = Validate(r);
        if (bad is not null) return DatabaseDiscoveryOutcome.Failed(bad);
        IMsSqlSession? session = null;
        try
        {
            session = await sessions(options!, string.IsNullOrEmpty(r.SecretRef) ? null : r.SecretRef, ct).ConfigureAwait(false);
            var result = await new MsSqlDiscoveryService(session).DiscoverAsync(ct).ConfigureAwait(false);
            string? script = null;
            var missing = result.Databases.Where(d => !d.CanBackup).Select(d => d.Name).ToList();
            // Skrypt (CREATE LOGIN … FROM WINDOWS) dotyczy konta usługi; dla loginu SQL uprawnienia nadaje administrator po swojej stronie.
            if (missing.Count > 0 && options!.Authentication == MsSqlAuthentication.Integrated)
                script = PermissionScript.Generate(missing, PermissionVariant.Minimal);
            return new DatabaseDiscoveryOutcome(null, result, script);
        }
        catch (KobianException e)
        {
            return DatabaseDiscoveryOutcome.Failed(e.Code);
        }
        catch (MsSqlServerException e)
        {
            return DatabaseDiscoveryOutcome.Failed(SqlErrorMap.MapMany(e.ErrorNumbers).Code);
        }
        finally
        {
            if (session is IAsyncDisposable d) await d.DisposeAsync().ConfigureAwait(false);
        }
    }
}
