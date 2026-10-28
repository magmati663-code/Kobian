using System.Security.Cryptography;
using Kobian.Application.Storage;
using Kobian.Crypto;
using Kobian.Domain;
using Kobian.Engine.Repo;
using Kobian.Format.Metadata;
using Kobian.Format.Repo;

namespace Kobian.Engine.Restore;

/// <summary>
/// <c>kobian-restore list|restore --repo &lt;katalog&gt; --key-file &lt;plik&gt; [--snapshot &lt;id|latest&gt;] [--path &lt;wzorzec&gt;] --to &lt;katalog&gt;</c>
/// oraz <c>kobian-restore check … [--sample &lt;0–100&gt;] [--rotation &lt;n&gt;]</c> (BL-26: V2, z <c>--sample</c> także V3 — <see cref="RepoChecker"/>).
/// Kody wyjścia jak 6.8: 0 OK, 1 Warning, 2 Failed, 64 składnia. Repo otwierane poświadczeniem Backup (bez usuwania).
/// </summary>
public static class OfflineRestore
{
    public static Task<int> RunAsync(string[] args, IStorageProviderFactory local, TextWriter o, TextWriter e, CancellationToken ct)
        => RunAsync(args, local, null, o, e, ct);

    /// <param name="remote">
    /// Fabryka zdalnych dostawców (argumenty: katalog sekretów, schemat) dla <c>--dest-scheme sftp|ftps|webdav</c>; null = tylko cel lokalny.
    /// Cel zdalny: <c>--dest-scheme sftp --host H [--port N] --hostkey SHA256:… --secrets-dir D --secret-ref file:nazwa --repo-header kobian.repo --repo /ścieżka</c>
    /// (FTPS: <c>--spki SHA256:…[,…]</c> zamiast <c>--hostkey</c>, opcjonalnie <c>--tls implicit</c>). Nagłówek <c>kobian.repo</c> musi być lokalną kopią.
    /// </param>
    public static async Task<int> RunAsync(string[] args, IStorageProviderFactory local, Func<string, string, IStorageProviderFactory?>? remote,
        TextWriter o, TextWriter e, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(o);
        ArgumentNullException.ThrowIfNull(e);
        string? Opt(string n) { var i = Array.IndexOf(args, n); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        var verb = args.Length > 0 ? args[0] : null;
        var repo = Opt("--repo");
        var keyFile = Opt("--key-file");
        if (verb is not ("list" or "restore" or "check") || repo is null || keyFile is null || Opt("--password-file") is null || (verb == "restore" && Opt("--to") is null))
        {
            await e.WriteLineAsync("usage: kobian-restore list|restore|check --repo <dir> --key-file <file> --password-file <file> [--repo-header <kobian.repo>] [--snapshot <id|latest>] [--path <pattern>] --to <dir> [--sample <0-100>] [--rotation <n>] [remote: --dest-scheme sftp|ftps|webdav --host <h> --hostkey|--spki <pin> --secrets-dir <dir> --secret-ref file:<name>]").ConfigureAwait(false);
            return 64;
        }
        var sample = 0;
        long rotation = 0;
        if (verb == "check" && ((Opt("--sample") is { } sp && (!int.TryParse(sp, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out sample) || sample > 100))
                                || (Opt("--rotation") is { } rt && !long.TryParse(rt, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out rotation))))
        {
            await e.WriteLineAsync("usage: --sample must be 0..100, --rotation an integer").ConfigureAwait(false);
            return 64;
        }
        var scheme = Opt("--dest-scheme") ?? "local";
        IStorageProviderFactory factory = local;
        DestinationConfig config;
        if (scheme == "local")
        {
            config = new DestinationConfig("offline", "local", Path.GetFullPath(repo));
        }
        else
        {
            var secretsDir = Opt("--secrets-dir");
            var secretRef = Opt("--secret-ref");
            var host = Opt("--host");
            var pin = scheme == "sftp" ? Opt("--hostkey") : Opt("--spki");
            if (remote is null || remote(secretsDir ?? "-", scheme) is not { } rf)
            {
                await e.WriteLineAsync($"unsupported --dest-scheme '{scheme}' (supported: local, sftp, ftps, webdav)").ConfigureAwait(false);
                return 64;
            }
            if (secretsDir is null || secretRef is null || host is null || Opt("--repo-header") is null || (scheme == "sftp" && pin is null))
            {
                await e.WriteLineAsync("usage (remote): --dest-scheme sftp|ftps|webdav --host <h> [--port <n>] (--hostkey|--spki) <SHA256:…> --secrets-dir <dir> --secret-ref file:<name> --repo-header <local kobian.repo> --repo <path on server>").ConfigureAwait(false);
                return 64;
            }
            var options = new Dictionary<string, string>(StringComparer.Ordinal) { ["host"] = host };
            if (Opt("--port") is { } port) options["port"] = port;
            if (pin is not null) options[scheme == "sftp" ? "hostkey" : "spki"] = pin;
            if (scheme == "ftps" && Opt("--tls") is { } tls) options["tls"] = tls;
            factory = rf;
            config = new DestinationConfig("offline", scheme, repo, secretRef, options);
        }
        try
        {
            // Nagłówek: jawny --repo-header albo <repo>/kobian.repo, jeśli istnieje (D3.11); brak nagłówka lub hasła = KB-1102 (bez --repo-id, it. 48).
            var header = Opt("--repo-header") ?? (scheme == "local" && File.Exists(Path.Combine(repo, "kobian.repo")) ? Path.Combine(repo, "kobian.repo") : null);
            using var keys = KeyFileLoader.Load(keyFile, Opt("--password-file"), header);
            await using var s = await factory.OpenAsync(config, CredentialRole.Backup, ct).ConfigureAwait(false);
            if (verb == "check") return await CheckAsync(s, keys, sample, rotation, o, e, ct).ConfigureAwait(false);
            var snaps = new List<SnapshotObject>();
            await foreach (var st in s.ListAsync(RepoPaths.SnapshotsDir, ct).ConfigureAwait(false))
            {
                var id = RepoPaths.TryParseId(st.Path, RepoPaths.SnapshotsDir, ".snap");
                if (id is null) continue;
                try { snaps.Add(await MetadataStore.LoadSnapshotAsync(s, keys, id, ct).ConfigureAwait(false)); }
                catch (KobianException ex) { await e.WriteLineAsync($"{ex.Code}: unreadable snapshot {Convert.ToHexStringLower(id)} skipped").ConfigureAwait(false); }
            }
            snaps.Sort((a, b) => a.Time.CompareTo(b.Time));
            if (verb == "list")
            {
                foreach (var sn in snaps)
                    await o.WriteLineAsync($"{Convert.ToHexStringLower(sn.SnapId)}  {sn.Time.UtcDateTime:yyyy-MM-dd HH:mm:ss}Z  {sn.JobId}  {sn.Mode}  {sn.Stats.Files} files").ConfigureAwait(false);
                return 0;
            }
            var want = Opt("--snapshot") ?? "latest";
            var snap = want == "latest" ? snaps.LastOrDefault() : snaps.FirstOrDefault(x => string.Equals(Convert.ToHexStringLower(x.SnapId), want, StringComparison.OrdinalIgnoreCase));
            if (snap is null)
            {
                await e.WriteLineAsync("snapshot not found").ConfigureAwait(false);
                return 2;
            }
            using var reader = new RepoReader(s, keys);
            await reader.LoadIndexesAsync(snap, ct).ConfigureAwait(false);
            var r = await new RestoreExecutor(reader).RestoreAsync(snap, Opt("--to")!, Opt("--path"), ct).ConfigureAwait(false);
            foreach (var g in r.Issues.GroupBy(i => i.Code)) await e.WriteLineAsync($"{g.Key}: {g.Count()}").ConfigureAwait(false);
            await o.WriteLineAsync($"{r.Status}: {r.Files} files, {r.Bytes} bytes").ConfigureAwait(false);
            return (int)r.Status;
        }
        catch (KobianException ex)
        {
            await e.WriteLineAsync($"{ex.Code}: {ex.Message}").ConfigureAwait(false);
            return 2;
        }
        catch (StorageException ex)
        {
            await e.WriteLineAsync($"{ex.Code}: {ex.Message}").ConfigureAwait(false);
            return 2;
        }
    }

    /// <summary>Raport <c>check</c>: znaleziska (kod, obiekt) na stderr, podsumowanie i uszkodzone snapshoty na stdout; kod wyjścia = status (0/1/2).</summary>
    private static async Task<int> CheckAsync(IStorageSession s, RepoKeys keys, int sample, long rotation, TextWriter o, TextWriter e, CancellationToken ct)
    {
        var r = await new RepoChecker(s, keys).CheckAsync(new CheckOptions(sample, rotation), ct).ConfigureAwait(false);
        foreach (var i in r.Issues) await e.WriteLineAsync($"{i.Code}: {i.Target}").ConfigureAwait(false);
        if (r.IssuesTruncated) await e.WriteLineAsync("(issue list truncated)").ConfigureAwait(false);
        foreach (var d in r.DamagedSnapshots) await o.WriteLineAsync($"damaged snapshot {d}").ConfigureAwait(false);
        await o.WriteLineAsync($"{r.Status}: V{r.Level}, {r.Snapshots} snapshots, {r.Indexes} indexes, {r.Packs} packs, {r.PacksRead} packs read, {r.BlocksVerified} blocks verified").ConfigureAwait(false);
        return (int)r.Status;
    }
}

