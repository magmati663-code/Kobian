using System.Security.Cryptography;
using Kobian.Domain;
using Kobian.Providers.Sources.MsSql;
using Kobian.Providers.Sources.MsSql.Tls;

namespace Kobian.Agent.Jobs;

/// <summary>Wynik pobrania certyfikatu: kod KB albo dane do zatwierdzenia + proponowana nazwa pliku.</summary>
public sealed record ServerCertificateOutcome(string? Code, SqlServerCertificateInfo? Certificate, string? SuggestedName);

/// <summary>Wynik zatwierdzenia: kod KB albo nazwa zapisanego pliku .cer (do pola „certificate” w S1-db).</summary>
public sealed record ServerCertificateApproval(string? Code, string? FileName);

/// <summary>
/// Zatwierdzanie certyfikatu serwera SQL (BL-40 cz. 5, warunek pinu z Qb30; PROWIZORYCZNIE Qb32 — TOFU z potwierdzeniem Operatora):
/// <list type="number">
/// <item><see cref="FetchAsync"/> — Agent pobiera certyfikat z handshake TLS (bez logowania) i zwraca odcisk SHA-256 do porównania
/// z administratorem serwera SQL; niczego nie zapisuje.</item>
/// <item><see cref="ApproveAsync"/> — Agent pobiera certyfikat PONOWNIE, porównuje odcisk stałoczasowo z tym, który Operator zatwierdził
/// (KB-3007 przy zmianie) i zapisuje DER do <c>&lt;stan&gt;/certs/&lt;nazwa&gt;.cer</c> bez nadpisywania innej treści (KB-3008).</item>
/// </list>
/// Klient nigdy nie przesyła bajtów certyfikatu — Agent ufa tylko temu, co sam odebrał od serwera.
/// </summary>
public sealed class ServerCertificateService(string certsDir, ISqlServerCertificateFetcher fetcher)
{
    /// <summary>RD-39 (PROWIZORYCZNIE Qb35): serwery, z którymi Agent może się łączyć; spoza listy → KB-3009 PRZED połączeniem. Program.cs ustawia z konfiguracji.</summary>
    public SqlServerAllowList AllowedServers { get; init; } = SqlServerAllowList.Any;

    public async Task<ServerCertificateOutcome> FetchAsync(string server, bool strict, CancellationToken ct)
    {
        try
        {
            AllowedServers.Demand(server);
            var info = await fetcher.FetchAsync(server, strict, ct).ConfigureAwait(false);
            return new ServerCertificateOutcome(null, info, SuggestName(SqlEndpoint.Parse(server), info.Sha256Hex));
        }
        catch (KobianException e)
        {
            return new ServerCertificateOutcome(e.Code, null, null);
        }
    }

    public async Task<ServerCertificateApproval> ApproveAsync(string server, bool strict, string? sha256Hex, string? fileName, CancellationToken ct)
    {
        if (!DatabaseDiscoveryService.IsValidCertificateName(fileName) || !TryParseSha256(sha256Hex, out var expected))
            return new ServerCertificateApproval(KbCode.JobDefinitionInvalid, null);
        SqlServerCertificateInfo info;
        try
        {
            SqlEndpoint.Parse(server); // walidacja przed połączeniem
            AllowedServers.Demand(server);
            info = await fetcher.FetchAsync(server, strict, ct).ConfigureAwait(false);
        }
        catch (KobianException e)
        {
            return new ServerCertificateApproval(e.Code, null);
        }

        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(info.RawData), expected))
            return new ServerCertificateApproval(KbCode.SqlCertificateChanged, null);

        var dir = Path.GetFullPath(certsDir);
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, fileName!);
        if (File.Exists(target))
        {
            var existing = await File.ReadAllBytesAsync(target, ct).ConfigureAwait(false);
            return existing.AsSpan().SequenceEqual(info.RawData)
                ? new ServerCertificateApproval(null, fileName) // idempotentnie: ten sam certyfikat pod tą samą nazwą
                : new ServerCertificateApproval(KbCode.SqlCertificateNameTaken, null);
        }

        var tmp = Path.Combine(dir, "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await File.WriteAllBytesAsync(tmp, info.RawData, ct).ConfigureAwait(false);
            File.Move(tmp, target, overwrite: false); // równoległe zatwierdzenie pod tą samą nazwą → IOException → KB-3008
        }
        catch (IOException)
        {
            return new ServerCertificateApproval(KbCode.SqlCertificateNameTaken, null);
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
        return new ServerCertificateApproval(null, fileName);
    }

    /// <summary>Proponowana nazwa: host (znaki spoza <c>[A-Za-z0-9_.-]</c> → „-”, ≤ 40) + 8 znaków odcisku + „.cer”.</summary>
    internal static string SuggestName(SqlEndpoint ep, string sha256Hex)
    {
        var host = new string([.. ep.Host.Select(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' ? c : '-')]).Trim('-');
        if (host.Length > 40) host = host[..40];
        if (ep.Instance is { } inst) host += "-" + inst;
        if (host.Length == 0 || !char.IsAsciiLetterOrDigit(host[0])) host = "sql" + host;
        var name = host + "-" + sha256Hex[..8].ToLowerInvariant() + ".cer";
        return DatabaseDiscoveryService.IsValidCertificateName(name) ? name : "sql-" + sha256Hex[..8].ToLowerInvariant() + ".cer";
    }

    internal static bool TryParseSha256(string? hex, out byte[] value)
    {
        value = [];
        if (hex is null) return false;
        var clean = hex.Replace(":", "", StringComparison.Ordinal).Replace(" ", "", StringComparison.Ordinal);
        if (clean.Length != 64) return false;
        try
        {
            value = Convert.FromHexString(clean);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
