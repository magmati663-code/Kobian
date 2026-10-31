using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Kobian.Application.Storage;
using Kobian.Catalog.Sqlite;
using Kobian.Crypto;
using Kobian.Domain;
using Kobian.Engine.Repo;
using Kobian.Scheduler.Runs;

namespace Kobian.Agent.Jobs;

/// <summary>Wynik planu prune w Agencie: <c>Code</c> ≠ null = plan się nie powiódł (brak zadania, cel niedostępny) albo prune wstrzymany (KB-1601/KB-1602 w <see cref="PrunePlan.BlockedCode"/>).</summary>
public sealed record PruneOutcome(string? Code, PrunePlan? Plan);

/// <summary>
/// Wynik prune wykonawczego: <c>Code</c> ≠ null = nie wykonano (brak zadania, cel/blokada niedostępne, prune wstrzymany) albo zatrzymano w fazie 2 (KB-1603)
/// lub znaleziono fałszywe blokady (KB-1201, ostrzeżenie). <c>Marked</c>/<c>Unmarked</c> = zmiany oznaczeń fazy 1; <c>Deleted</c> = faza 2;
/// <c>Consolidation</c> = krok konsolidacji indeksów przed spisem (D3.16; null = nie uruchomiono).
/// </summary>
public sealed record PruneRunOutcome(string? Code, PrunePlan? Plan, int Marked, int Unmarked, PruneDeletion? Deleted, int LocksCleaned, string? RunId,
    IndexConsolidation? Consolidation = null);

/// <summary>
/// Plan prune repozytorium zadania (BL-25 cz. 3, D3.7, D3.12 „Prune (MVP)”) — wyłącznie PODGLĄD (dry-run): zadanie z katalogu → definicja → klucz →
/// dostawca celu z poświadczeniem <see cref="CredentialRole.Backup"/> (tylko odczyt) → <see cref="PrunePlanner.CollectAsync"/> → <see cref="PrunePlanner.Plan"/>.
/// Zapomniane = <c>snap_snapshots.forgotten_at</c> dla CAŁEGO repozytorium (wszystkie zadania w nim — CE-18); snapshot nieznany katalogowi zostaje żywy.
/// Niczego nie zapisuje ani na celu, ani w katalogu (bez wiersza run_runs — jak PreviewRetention).
/// Oznaczenia fazy 1 (<c>repo_prune_marks</c>) są uwzględniane w podglądzie — <see cref="PrunePlan.Deletable"/> pokazuje, co usunąłby następny przebieg.
/// <see cref="PruneAsync"/> (BL-25 cz. 4): sesja Maintenance → blokada WYŁĄCZNA (D3.13) → spis + plan → faza 1 (oznaczenia/zdjęcia w katalogu) →
/// faza 2 (<see cref="PruneExecutor"/>: ponowny spis, usuwanie po karencji, stop przy pierwszej niezgodności) → sprzątnięcie przeterminowanych plików blokad.
/// Każdy przebieg = wiersz run_runs (cause 'manual', reason 'prune').
/// D3.16 (BL-25 dokończenie): po uzyskaniu blokady wyłącznej i przed spisem — konsolidacja indeksów (<see cref="IndexConsolidator"/>, tylko nowe pliki).
/// Zastąpione indeksy stają się w tym samym przebiegu nieżywe → faza 1 je oznacza, a usuwa je faza 2 kolejnego przebiegu po karencji. Nieudana konsolidacja
/// (KB-1604) nie przerywa prune — wynik ma ostrzeżenie; repozytorium pozostaje kompletne.
/// </summary>
public sealed class CatalogPruneService
{
    public const int MaxJobNameLength = 200;

    private readonly string _catalogPath;
    private readonly IReadOnlyDictionary<string, IStorageProviderFactory> _providers;
    private readonly TimeProvider _time;
    private readonly CatalogWriter? _writer;
    private readonly byte[] _hostId;
    private readonly Func<TimeSpan, CancellationToken, Task>? _lockDelay;
    private readonly RepoLockOptions _lockOptions;

    public CatalogPruneService(string catalogPath, IReadOnlyDictionary<string, IStorageProviderFactory> providers, TimeProvider time,
        CatalogWriter? writer = null, byte[]? hostId = null, Func<TimeSpan, CancellationToken, Task>? lockDelay = null, RepoLockOptions? lockOptions = null)
    {
        _catalogPath = catalogPath ?? throw new ArgumentNullException(nameof(catalogPath));
        _providers = providers ?? throw new ArgumentNullException(nameof(providers));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _writer = writer;
        _hostId = hostId is { Length: 16 } ? hostId : CatalogJobService.HostIdFor(Environment.MachineName);
        _lockDelay = lockDelay;
        _lockOptions = lockOptions ?? new RepoLockOptions();
    }

    /// <summary>Karencja między oznaczeniem a usunięciem (D3.7). Stała 24 h; zmiana = ADR.</summary>
    public TimeSpan Grace { get; init; } = PrunePlanner.DefaultGrace;

    /// <summary>Progi konsolidacji indeksów (D3.16); null = konsolidacja wyłączona.</summary>
    public IndexConsolidationOptions? Consolidation { get; init; } = new();

    public async Task<PruneOutcome> PlanAsync(string job, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(job) || job.Length > MaxJobNameLength) return new PruneOutcome(KbCode.JobNotFound, null);
        var row = FindJob(job);
        if (row is null) return new PruneOutcome(KbCode.JobNotFound, null);
        try
        {
            var def = JobDefinition.Parse(row.Value.Type, row.Value.Definition);
            if (!_providers.TryGetValue(def.DestinationScheme, out var factory))
                throw new KobianException(KbCode.JobDefinitionInvalid, ErrorClass.Permanent, "Destination provider is not available.");
            using var keys = KeyFileLoader.Load(def.KeyFile, def.PasswordFile, def.RepoHeader);
            var destId = def.DestinationId;
            await using var session = await factory.OpenAsync(def.ToDestination(destId), CredentialRole.Backup, ct).ConfigureAwait(false);
            var forgotten = Forgotten(Convert.ToHexStringLower(keys.RepoId));
            var inv = await PrunePlanner.CollectAsync(session, keys, forgotten, ct).ConfigureAwait(false);
            var plan = PrunePlanner.Plan(inv, Marks(Convert.ToHexStringLower(keys.RepoId)), _time.GetUtcNow(), Grace);
            return new PruneOutcome(plan.BlockedCode, plan);
        }
        catch (KobianException ex)
        {
            return new PruneOutcome(ex.Code, null);
        }
        catch (StorageException ex)
        {
            return new PruneOutcome(ex.Code, null);
        }
    }

    /// <summary>Wstrzymanie retencji po skoku zegara (SEC-17.4); przy aktywnym wstrzymaniu <see cref="PruneAsync"/> nic nie usuwa.</summary>
    public RetentionHoldService? Hold { get; init; }

    public async Task<PruneRunOutcome> PruneAsync(string job, CancellationToken ct)
    {
        if (_writer is null) return new PruneRunOutcome(KbCode.OperationNotAvailable, null, 0, 0, null, 0, null);
        if (string.IsNullOrWhiteSpace(job) || job.Length > MaxJobNameLength) return new PruneRunOutcome(KbCode.JobNotFound, null, 0, 0, null, 0, null);
        var row = FindJob(job);
        if (row is null) return new PruneRunOutcome(KbCode.JobNotFound, null, 0, 0, null, 0, null);
        var runId = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        await RunRowAsync(runId, row.Value.JobId, null, null, ct).ConfigureAwait(false);
        PruneRunOutcome result;
        try
        {
            // SEC-17.4: po skoku zegara retencja jest wstrzymana (nic nie jest zapominane), ale fizyczny prune mierzy 24-godzinną karencję przesuniętym zegarem — oznaczenia sprzed chwili
            // spełniałyby ją i faza 2 kasowałaby dane, które wstrzymanie ma chronić. Do zwolnienia wstrzymania przez Administratora prune nie rusza niczego (KB-6117, przebieg Skipped).
            result = Hold?.Current() is not null
                ? new PruneRunOutcome(KbCode.AlertClockJump, null, 0, 0, null, 0, runId)
                : await PruneCoreAsync(row.Value.Type, row.Value.Definition, runId, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await RunRowAsync(runId, null, RunState.Cancelled, null, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex) when (ex is KobianException or StorageException)
        {
            var code = ex is KobianException k ? k.Code : ((StorageException)ex).Code;
            result = new PruneRunOutcome(code, null, 0, 0, null, 0, runId);
        }
        catch (InvalidOperationException)
        {
            result = new PruneRunOutcome(KbCode.OperationNotAvailable, null, 0, 0, null, 0, runId); // dostawca bez sesji Maintenance (osobne poświadczenie — D4.1)
        }
#pragma warning disable CA1031 // granica wykonawcy: nieoczekiwany wyjątek = wiersz Failed (KB-0001), bez zostawiania przebiegu w stanie Running
        catch
        {
            await RunRowAsync(runId, null, RunState.Failed, KbCode.Internal, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
#pragma warning restore CA1031
        var state = result.Code switch
        {
            null => RunState.Succeeded,
            KbCode.RepoLockInvalid or KbCode.PruneStoppedInconsistent or KbCode.IndexConsolidationIncomplete => RunState.Warning,
            KbCode.RepoLocked or KbCode.AlertClockJump => RunState.Skipped,
            _ => RunState.Failed,
        };
        await RunRowAsync(runId, null, state, result.Code, CancellationToken.None).ConfigureAwait(false);
        return result;
    }

    private async Task<PruneRunOutcome> PruneCoreAsync(string type, byte[] definition, string runId, CancellationToken ct)
    {
        var def = JobDefinition.Parse(type, definition);
        if (!_providers.TryGetValue(def.DestinationScheme, out var factory))
            throw new KobianException(KbCode.JobDefinitionInvalid, ErrorClass.Permanent, "Destination provider is not available.");
        using var keys = KeyFileLoader.Load(def.KeyFile, def.PasswordFile, def.RepoHeader);
        var repoHex = Convert.ToHexStringLower(keys.RepoId);
        var destId = def.DestinationId;
        await using var session = await factory.OpenAsync(def.ToDestination(destId), CredentialRole.Maintenance, ct).ConfigureAwait(false);
        await using var lease = await RepoLocks.AcquireExclusiveAsync(session, keys, RepoLockOwner.For(_hostId), _time, _lockOptions, _lockDelay, ct).ConfigureAwait(false);

        var forgotten = Forgotten(repoHex);
        var marks = Marks(repoHex);
        // D3.16: konsolidacja pod tą samą blokadą wyłączną; jej nowe indeksy są żywe, a zastąpione — nieżywe już w spisie poniżej.
        // PROWIZORYCZNIE Qb100: konsolidacja jako krok ręcznego prune (bez osobnego harmonogramu).
        IndexConsolidation? consolidation = null;
        if (Consolidation is { } copt)
            consolidation = await IndexConsolidator.RunAsync(session, keys, lease, forgotten, _hostId, copt, ct).ConfigureAwait(false);
        var inv = await PrunePlanner.CollectAsync(session, keys, forgotten, ct).ConfigureAwait(false);
        var plan = PrunePlanner.Plan(inv, marks, _time.GetUtcNow(), Grace);
        if (plan.BlockedCode is not null) return new PruneRunOutcome(plan.BlockedCode, plan, 0, 0, null, 0, runId, consolidation);

        // Faza 1: nowe oznaczenia z czasem teraz, zdjęcie oznaczeń obiektów znów żywych lub nieobecnych (katalog = jedyne miejsce oznaczeń).
        var markedAt = Iso(_time.GetUtcNow());
        await _writer!.ExecuteControlAsync((c, tx) =>
        {
            foreach (var o in plan.NewMarks)
                Exec(c, tx, "INSERT OR IGNORE INTO repo_prune_marks(repo_id,object_key,marked_at) VALUES ($r,$k,$t);", ("$r", repoHex), ("$k", o.Key), ("$t", markedAt));
            foreach (var k in plan.Unmark)
                Exec(c, tx, "DELETE FROM repo_prune_marks WHERE repo_id=$r AND object_key=$k;", ("$r", repoHex), ("$k", k));
            return 0;
        }, ct).ConfigureAwait(false);

        // Faza 2: tylko obiekty oznaczone ≥ karencja temu i nadal nieżywe w ponownym spisie.
        PruneDeletion? deletion = null;
        if (plan.Deletable.Count > 0)
        {
            deletion = await PruneExecutor.DeleteAsync(session, keys, lease, forgotten, marks, plan.Deletable, _time, Grace, ct).ConfigureAwait(false);
            if (deletion.Deleted.Count > 0)
            {
                await _writer.ExecuteControlAsync((c, tx) =>
                {
                    foreach (var o in deletion.Deleted)
                        Exec(c, tx, "DELETE FROM repo_prune_marks WHERE repo_id=$r AND object_key=$k;", ("$r", repoHex), ("$k", o.Key));
                    return 0;
                }, CancellationToken.None).ConfigureAwait(false);
            }
        }

        // Sprzątanie przeterminowanych plików blokad (append-only cele zostawiają po jednym pliku na odświeżenie).
        var cleaned = 0;
        var scan = await RepoLocks.ScanAsync(session, keys, _time.GetUtcNow(), _lockOptions, ct).ConfigureAwait(false);
        foreach (var f in scan.ExpiredFiles)
        {
            try { await session.DeleteAsync(f, ct).ConfigureAwait(false); cleaned++; }
            catch (StorageException) { /* następnym razem */ }
        }

        var code = deletion?.StopCode ?? ((lease.LastScan?.Invalid.Count ?? 0) + scan.Invalid.Count > 0 ? KbCode.RepoLockInvalid : null)
                   ?? (consolidation?.Code == KbCode.IndexConsolidationIncomplete ? KbCode.IndexConsolidationIncomplete : null);
        return new PruneRunOutcome(code, plan, plan.NewMarks.Count, plan.Unmark.Count, deletion, cleaned, runId, consolidation);
    }

    /// <summary>Oznaczenia fazy 1 dla repozytorium: klucz „rodzaj:hex” → czas oznaczenia. Nieczytelny czas = oznaczenie pomijane (zostanie zapisane od nowa — karencja liczy się od nowa).</summary>
    private Dictionary<string, DateTimeOffset> Marks(string repoHex)
    {
        var map = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        using var c = CatalogBootstrap.Open(_catalogPath, readOnly: true);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT object_key, marked_at FROM repo_prune_marks WHERE repo_id = $r;";
        cmd.Parameters.AddWithValue("$r", repoHex);
        using var rd = cmd.ExecuteReader();
        while (rd.Read())
            if (DateTimeOffset.TryParse(rd.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at))
                map[rd.GetString(0)] = at;
        return map;
    }

    /// <summary>Wiersz run_runs przebiegu prune: <paramref name="jobId"/> ≠ null = start (Running), inaczej zakończenie ze stanem <paramref name="state"/>.</summary>
    private Task<int> RunRowAsync(string runId, string? jobId, RunState? state, string? code, CancellationToken ct)
    {
        var now = Iso(_time.GetUtcNow());
        return _writer!.ExecuteControlAsync((c, tx) =>
        {
            if (jobId is not null)
            {
                Exec(c, tx, "INSERT INTO run_runs(run_id,job_id,cause,state,attempt,queued_at,started_at) VALUES ($i,$j,'manual','Running',0,$t,$t);", ("$i", runId), ("$j", jobId), ("$t", now));
                Exec(c, tx, "INSERT INTO run_transitions(run_id,seq,from_state,to_state,at_utc,reason) VALUES ($i,0,NULL,'Running',$t,'prune');", ("$i", runId), ("$t", now));
            }
            else
            {
                var s = state!.Value.ToString();
                Exec(c, tx, "UPDATE run_runs SET state=$s, finished_at=$t, kb_code=$k WHERE run_id=$i;", ("$s", s), ("$t", now), ("$k", code), ("$i", runId));
                Exec(c, tx, "INSERT INTO run_transitions(run_id,seq,from_state,to_state,at_utc,reason,kb_code) VALUES ($i,1,'Running',$s,$t,'prune',$k);", ("$i", runId), ("$s", s), ("$t", now), ("$k", code));
            }
            return 0;
        }, ct);
    }

    private static string Iso(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private static void Exec(Microsoft.Data.Sqlite.SqliteConnection c, Microsoft.Data.Sqlite.SqliteTransaction tx, string sql, params (string Name, object? Value)[] args)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    private (string JobId, string Type, byte[] Definition)? FindJob(string name)
    {
        using var c = CatalogBootstrap.Open(_catalogPath, readOnly: true);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT job_id, type, definition FROM job_jobs WHERE name = $n;";
        cmd.Parameters.AddWithValue("$n", name);
        using var rd = cmd.ExecuteReader();
        return rd.Read() ? (rd.GetString(0), rd.GetString(1), (byte[])rd[2]) : null;
    }

    /// <summary>Snapshoty repozytorium zapomniane w katalogu (wszystkie zadania). Id w katalogu to hex małymi literami.</summary>
    private HashSet<string> Forgotten(string repoHex)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        using var c = CatalogBootstrap.Open(_catalogPath, readOnly: true);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT snap_id FROM snap_snapshots WHERE repo_id = $r AND forgotten_at IS NOT NULL;";
        cmd.Parameters.AddWithValue("$r", repoHex);
        using var rd = cmd.ExecuteReader();
        while (rd.Read()) set.Add(rd.GetString(0));
        return set;
    }
}
