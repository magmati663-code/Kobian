using System.Globalization;
using Kobian.Application.Storage;
using Kobian.Domain;

namespace Kobian.Agent.Jobs;

/// <summary>Cel w żądaniu API (bez sekretów — AC-01.3): poświadczenie tylko jako odwołanie <c>SecretRef</c>, klucz hosta jako odcisk „SHA256:…”.</summary>
public sealed record DestinationRequest(string Scheme, string Root, string? Host = null, int? Port = null, string? SecretRef = null, string? HostKey = null);

public enum DestinationProbeStatus
{
    Ok,
    UnknownIdentity,
    Failed,
}

/// <summary>Wynik sprawdzenia celu: kod KB przy porażce, tożsamość serwera (publiczny odcisk), miejsce, istniejące repozytorium.</summary>
public sealed record DestinationProbeResult(DestinationProbeStatus Status, string? Code, ServerIdentity? Identity, long? FreeBytes, long? TotalBytes, bool RepositoryExists);

/// <summary>
/// ProbeDestination (BL-60, R2.6.1 SCR-03 S2, D4.11): otwiera sesję celu z poświadczeniem Backup (bez usuwania — D4.6), czyta tożsamość, wolne miejsce
/// i obecność <c>kobian.repo</c>. Niczego nie zapisuje — ani na celu, ani w katalogu. SFTP bez przypiętego klucza: tryb „identity_probe” (klucz nigdy nie jest
/// zaufany, poświadczenie nie jest wysyłane) → <see cref="DestinationProbeStatus.UnknownIdentity"/> z odciskiem do zatwierdzenia przez użytkownika (bez TOFU w tle).
/// </summary>
public sealed class DestinationProbeService(IReadOnlyDictionary<string, IStorageProviderFactory> providers)
{
    public const int MaxRootLength = 1024;
    public const int MaxHostLength = 253;

    /// <summary>Walidacja kształtu celu (bez dostępu do sieci/dysku). Null = poprawny, inaczej kod KB.</summary>
    public string? ValidateShape(DestinationRequest d)
    {
        ArgumentNullException.ThrowIfNull(d);
        if (string.IsNullOrEmpty(d.Scheme) || !providers.ContainsKey(d.Scheme)) return KbCode.JobDefinitionInvalid;
        if (string.IsNullOrWhiteSpace(d.Root) || d.Root.Length > MaxRootLength || d.Root.Any(char.IsControl)) return KbCode.DestinationNotFound;
        if (d.Scheme == "local" && !Path.IsPathFullyQualified(d.Root)) return KbCode.DestinationNotFound;
        if (d.Scheme == "smb")
        {
            // BL-73: \\serwer\udział[\ścieżka] bez urządzeń, ADS, „..” i udziałów administracyjnych (UncPath).
            try
            {
                _ = Kobian.Providers.Storage.Smb.UncPath.Parse(d.Root);
            }
            catch (StorageException ex)
            {
                return ex.Code;
            }
        }
        if (d.Scheme is "sftp" or "ftps" or "ftp" or "webdav")
        {
            if (string.IsNullOrEmpty(d.Host) || d.Host.Length > MaxHostLength) return KbCode.DestinationNotFound;
            if (d.Port is { } p && p is < 1 or > 65535) return KbCode.DestinationNotFound;
        }
        return null;
    }

    public async Task<DestinationProbeResult> ProbeAsync(DestinationRequest d, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(d);
        if (ValidateShape(d) is { } bad) return Failed(bad);
        var factory = providers[d.Scheme];
        try
        {
            await using var session = await factory.OpenAsync(ToConfig(d), CredentialRole.Backup, ct).ConfigureAwait(false);
            var free = await session.GetFreeSpaceAsync(ct).ConfigureAwait(false);
            var repo = await session.StatAsync(RepoHeaderName, ct).ConfigureAwait(false) is { IsDirectory: false };
            return new DestinationProbeResult(DestinationProbeStatus.Ok, null, session.PresentedIdentity, free, TotalBytes(d), repo);
        }
        catch (StorageException ex) when (ex.Code is KbCode.HostKeyMismatch or KbCode.TlsIdentityMismatch && ex.PresentedIdentity is { } id)
        {
            // Brak przypięcia → do zatwierdzenia; przypięty, ale inny klucz → porażka KB-2104/KB-2109 z odciskiem (zmiana klucza serwera = alarm, nie pytanie).
            // FTPS (BL-34): certyfikat niezaufany jest odrzucany w uzgadnianiu TLS, zanim padnie USER/PASS — tryb „identity_probe” nie jest potrzebny.
            return string.IsNullOrEmpty(d.HostKey)
                ? new DestinationProbeResult(DestinationProbeStatus.UnknownIdentity, ex.Code, id, null, null, false)
                : new DestinationProbeResult(DestinationProbeStatus.Failed, ex.Code, id, null, null, false);
        }
        catch (StorageException ex)
        {
            return Failed(ex.Code);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Failed(KbCode.AccessDenied);
        }
    }

    public const int MaxBrowseFolders = 2000;

    /// <summary>
    /// Podfoldery folderu <see cref="DestinationRequest.Root"/> na serwerze (wybór folderu w kreatorze zamiast wpisywania ścieżki). Tylko SFTP/FTPS/WebDAV
    /// i tylko z zaufaną tożsamością serwera (SFTP: zatwierdzony klucz; bez niego poświadczenie nie idzie do serwera — D4.11). Nic nie zapisuje.
    /// </summary>
    public async Task<(string? Code, IReadOnlyList<string> Folders, bool Truncated)> BrowseAsync(DestinationRequest d, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(d);
        if (d.Scheme is not ("sftp" or "ftps" or "webdav")) return (KbCode.JobDefinitionInvalid, [], false);
        if ((ValidateShape(d) ?? JobDestination.Validate(d)) is { } bad) return (bad, [], false);
        // SFTP bez przypiętego klucza = tryb identity_probe (bez logowania); FTPS/WebDAV bez pinu — tylko z certyfikatem zaufanym przez system.
        if (d.Scheme == "sftp" && string.IsNullOrEmpty(d.HostKey)) return (KbCode.HostKeyMismatch, [], false);
        try
        {
            await using var session = await providers[d.Scheme].OpenAsync(ToConfig(d), CredentialRole.Backup, ct).ConfigureAwait(false);
            if (session is not IRemoteSourceReader reader) return (KbCode.OperationNotAvailable, [], false);
            var all = (await reader.ListDirectoryAsync("", ct).ConfigureAwait(false))
                .Where(e => e.Kind == RemoteSourceKind.Directory).Select(e => e.Name).Order(StringComparer.CurrentCultureIgnoreCase).ToList();
            return (null, all.Take(MaxBrowseFolders).ToList(), all.Count > MaxBrowseFolders);
        }
        catch (StorageException ex)
        {
            return (ex.Code, [], false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (KbCode.AccessDenied, [], false);
        }
    }

    /// <summary>Nazwa nagłówka repozytorium na celu (D3.11).</summary>
    public const string RepoHeaderName = "kobian.repo";

    internal static DestinationConfig ToConfig(DestinationRequest d)
    {
        Dictionary<string, string>? options = null;
        if (d.Scheme == "sftp")
        {
            options = new(StringComparer.Ordinal) { ["host"] = d.Host ?? "" };
            if (d.Port is { } p) options["port"] = p.ToString(CultureInfo.InvariantCulture);
            if (!string.IsNullOrEmpty(d.HostKey)) options["hostkey"] = d.HostKey;
            else options["identity_probe"] = "1";
        }
        else if (d.Scheme == "ftps")
        {
            options = JobDestination.FtpsOptions(d);
        }
        else if (d.Scheme == "webdav")
        {
            options = JobDestination.WebDavOptions(d);
        }
        return new DestinationConfig("probe", d.Scheme, d.Root, string.IsNullOrEmpty(d.SecretRef) ? null : d.SecretRef, options);
    }

    /// <summary>Pojemność woluminu — tylko cel lokalny (dla SFTP brak statvfs do czasu W-13).</summary>
    private static long? TotalBytes(DestinationRequest d)
    {
        if (d.Scheme != "local") return null;
        try
        {
            return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(d.Root))!).TotalSize;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static DestinationProbeResult Failed(string code) => new(DestinationProbeStatus.Failed, code, null, null, null, false);
}
