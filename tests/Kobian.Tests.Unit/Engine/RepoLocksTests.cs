using System.Security.Cryptography;
using System.Text;
using Kobian.Application.Storage;
using Kobian.Crypto;
using Kobian.Domain;
using Kobian.Engine.Repo;
using Kobian.Format.Repo;
using Kobian.Tests.Unit.TestDoubles;
using Xunit;

namespace Kobian.Tests.Unit.Engine;

/// <summary>Zegar testowy przesuwany ręcznie (odczekanie blokady wyłącznej = przesunięcie zegara, bez czekania naprawdę).</summary>
public sealed class ManualTime(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;

    public override DateTimeOffset GetUtcNow() => Now;

    public Task Advance(TimeSpan by)
    {
        Now += by;
        return Task.CompletedTask;
    }
}

/// <summary>
/// D3.13 (BL-25 cz. 4, it. 68): plik blokady z MAC (KB-1201) i dzierżawy współdzielone/wyłączne na magazynie w pamięci zgodnym z D4.6 (Backup nie usuwa).
/// Klucze i identyfikatory losowe w teście (NFR-11).
/// </summary>
public sealed class RepoLocksTests : IDisposable
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private readonly RepoKeys _keys = RepoKeys.Derive(RandomNumberGenerator.GetBytes(32), RandomNumberGenerator.GetBytes(16));
    private readonly ManualTime _time = new(new DateTimeOffset(2026, 10, 14, 12, 0, 0, TimeSpan.Zero));
    private readonly MemoryStorage _store = new();
    private readonly RepoLockOwner _a = RepoLockOwner.For(RandomNumberGenerator.GetBytes(16));
    private readonly RepoLockOwner _b = RepoLockOwner.For(RandomNumberGenerator.GetBytes(16));

    public void Dispose() => _keys.Dispose();

    private static string Hex() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

    private RepoLockInfo Info(string id, bool exclusive = false) => new(id, _a.HostId, _a.InstallationId, exclusive, _time.Now, _time.Now, 1800);

    private string[] LockFiles() => [.. _store.Objects.Keys.Where(k => k.StartsWith(RepoPaths.LocksDir + "/", StringComparison.Ordinal))];

    private Func<TimeSpan, CancellationToken, Task> Delay(Action? during = null) => (t, _) =>
    {
        during?.Invoke();
        return _time.Advance(t);
    };

    [Fact, Trait("D", "D3.13")]
    public void Lock_file_round_trips_and_any_change_is_rejected_with_KB_1201()
    {
        var id = Hex();
        var json = RepoLockFile.Create(_keys, Info(id, exclusive: true));
        var back = RepoLockFile.Verify(Encoding.UTF8.GetBytes(json), _keys, id);
        Assert.True(back.Exclusive);
        Assert.Equal(_time.Now, back.RenewedAt);
        Assert.Equal(1800, back.TtlSec);

        string[] forged =
        [
            json.Replace("\"exclusive\"", "\"shared\"", StringComparison.Ordinal),
            json.Replace("\"ttlSec\":1800", "\"ttlSec\":86400", StringComparison.Ordinal),
            json.Replace("2026-10-14T12:00:00Z\",\"ttlSec", "2030-10-14T12:00:00Z\",\"ttlSec", StringComparison.Ordinal),
            json[..^1] + ",\"kind\":\"shared\"}", // duplikat pola
            json[..^1] + ",\"extra\":1}",
            "[]",
            "",
            new string(' ', RepoLockFile.MaxBytes + 1),
        ];
        foreach (var f in forged)
            Assert.Equal(KbCode.RepoLockInvalid, Assert.Throws<KobianException>(() => RepoLockFile.Verify(Encoding.UTF8.GetBytes(f), _keys, id)).Code);
        // przeniesienie pod inną nazwę pliku i inny klucz repozytorium
        Assert.Equal(KbCode.RepoLockInvalid, Assert.Throws<KobianException>(() => RepoLockFile.Verify(Encoding.UTF8.GetBytes(json), _keys, Hex())).Code);
        using var other = RepoKeys.Derive(RandomNumberGenerator.GetBytes(32), RandomNumberGenerator.GetBytes(16));
        Assert.Equal(KbCode.RepoLockInvalid, Assert.Throws<KobianException>(() => RepoLockFile.Verify(Encoding.UTF8.GetBytes(json), other, id)).Code);
    }

    [Fact, Trait("D", "D3.13"), Trait("SEC", "SEC-13.6")]
    public void Repository_header_signed_with_the_same_key_is_not_accepted_as_a_lock()
    {
        // ten sam K_mac podpisuje kobian.repo i blokady — rozdział domen przez „magic” i kształt obiektu
        var header = RepoHeader.Create(_keys, _time.Now, [RandomNumberGenerator.GetBytes(16)]);
        Assert.Equal(KbCode.RepoLockInvalid, Assert.Throws<KobianException>(() => RepoLockFile.Verify(Encoding.UTF8.GetBytes(header), _keys, Hex())).Code);
    }

    [Fact, Trait("D", "D3.13")]
    public async Task Shared_locks_coexist_and_block_exclusive_until_they_expire()
    {
        var s1 = await RepoLocks.AcquireSharedAsync(_store.Open(), _keys, _a, _time, null, _ct);
        var s2 = await RepoLocks.AcquireSharedAsync(_store.Open(), _keys, _b, _time, null, _ct);
        await s1.ReleaseAsync(_ct);
        await s2.ReleaseAsync(_ct);
        Assert.Equal(2, LockFiles().Length); // sesja Backup nie usuwa (D4.1) — pliki wygasną

        var busy = await Assert.ThrowsAsync<KobianException>(() => RepoLocks.AcquireExclusiveAsync(_store.Open(CredentialRole.Maintenance), _keys, _a, _time, null, Delay(), _ct));
        Assert.Equal((KbCode.RepoLocked, ErrorClass.Transient), (busy.Code, busy.ErrorClass));

        await _time.Advance(TimeSpan.FromSeconds(1801));
        var scan = await RepoLocks.ScanAsync(_store.Open(), _keys, _time.Now, null, _ct);
        Assert.Empty(scan.Live);
        Assert.Equal(2, scan.ExpiredFiles.Count);
        await using var ex = await RepoLocks.AcquireExclusiveAsync(_store.Open(CredentialRole.Maintenance), _keys, _a, _time, null, Delay(), _ct);
        Assert.True(ex.Exclusive);
        Assert.Equal(KbCode.RepoLocked, (await Assert.ThrowsAsync<KobianException>(() => RepoLocks.AcquireSharedAsync(_store.Open(), _keys, _b, _time, null, _ct))).Code);
    }

    [Fact, Trait("D", "D3.13"), Trait("PM", "PM-18")]
    public async Task Exclusive_gives_up_when_a_shared_lock_appears_during_the_settle_wait()
    {
        var maint = _store.Open(CredentialRole.Maintenance);
        // kopia sprawdziła locks/ PRZED zapisaniem wyłącznej, a swoją współdzieloną zapisuje w trakcie odczekania (wyścig z D3.13)
        var sharedId = Hex();
        var race = Delay(() => _store.Objects[RepoPaths.Lock(Convert.FromHexString(sharedId))] =
            Encoding.UTF8.GetBytes(RepoLockFile.Create(_keys, new RepoLockInfo(sharedId, _b.HostId, _b.InstallationId, false, _time.Now, _time.Now, 1800))));
        var e = await Assert.ThrowsAsync<KobianException>(() => RepoLocks.AcquireExclusiveAsync(maint, _keys, _a, _time, new RepoLockOptions(), race, _ct));
        Assert.Equal(KbCode.RepoLocked, e.Code);
        // własny plik wyłącznej usunięty (Maintenance), cudzy współdzielony został
        var left = await RepoLocks.ScanAsync(_store.Open(), _keys, _time.Now, null, _ct);
        Assert.Equal([sharedId], left.Live.Select(l => l.LockId));
        Assert.False(left.Live[0].Exclusive);
    }

    [Fact, Trait("D", "D3.13")]
    public async Task Shared_lock_written_while_exclusive_holds_is_refused_after_the_recheck()
    {
        await using var ex = await RepoLocks.AcquireExclusiveAsync(_store.Open(CredentialRole.Maintenance), _keys, _a, _time, null, Delay(), _ct);
        await Assert.ThrowsAsync<KobianException>(() => RepoLocks.AcquireSharedAsync(_store.Open(), _keys, _b, _time, null, _ct));
        await ex.ReleaseAsync(_ct);
        Assert.Empty(LockFiles()); // wyłączna usunięta; współdzielona nie została zapisana (odmowa przed zapisem)
        await using var ok = await RepoLocks.AcquireSharedAsync(_store.Open(), _keys, _b, _time, null, _ct);
        Assert.Single(LockFiles());
    }

    [Fact, Trait("D", "D3.13"), Trait("SEC", "SEC-13.6"), Trait("KB", "KB-1201")]
    public async Task Forged_or_garbage_lock_files_are_ignored_and_reported()
    {
        var forgedId = Hex();
        using (var attacker = RepoKeys.Derive(RandomNumberGenerator.GetBytes(32), _keys.RepoId))
            _store.Objects[RepoPaths.Lock(Convert.FromHexString(forgedId))] = Encoding.UTF8.GetBytes(RepoLockFile.Create(attacker, Info(forgedId, exclusive: true)));
        _store.Objects[RepoPaths.Lock(RandomNumberGenerator.GetBytes(16))] = "{not json"u8.ToArray();
        _store.Objects[RepoLockPathWithBadName()] = "x"u8.ToArray(); // nazwa spoza wzorca — nie jest blokadą
        var scan = await RepoLocks.ScanAsync(_store.Open(), _keys, _time.Now, null, _ct);
        Assert.Empty(scan.Live);
        Assert.Equal(2, scan.Invalid.Count);
        await using var ex = await RepoLocks.AcquireExclusiveAsync(_store.Open(CredentialRole.Maintenance), _keys, _a, _time, null, Delay(), _ct);
        Assert.Equal(2, ex.LastScan!.Invalid.Count);
    }

    [Fact, Trait("D", "D3.13")]
    public async Task Destination_write_failure_keeps_its_own_code_instead_of_looking_like_a_busy_repository()
    {
        // cel odrzuca zapis blokady (tu: brak miejsca, jak cel tylko do odczytu → KB-2105): to nie jest „repozytorium zajęte”, więc nie może być KB-1202 (przejściowy, ponawiany)
        var e = await Assert.ThrowsAsync<KobianException>(() =>
            RepoLocks.AcquireSharedAsync(_store.Open(faultAt: 1, fault: FaultKind.NoSpaceOnCreate), _keys, _a, _time, null, _ct));
        Assert.Equal(KbCode.DestinationFull, e.Code);
        Assert.NotEqual(KbCode.RepoLocked, e.Code);
    }

    private static string RepoLockPathWithBadName() => RepoPaths.LocksDir + "/README.txt";

    /// <summary>
    /// Sesja Backup nie usuwa swoich blokad, więc po miesiącach kopii bez prune locks/ przekracza limit. Limit chroni kopię (zalanie katalogu), ale NIE może obowiązywać sesji
    /// Maintenance — to prune sprząta locks/; gdyby sam dostawał KB-1202, repozytorium blokowało się na stałe.
    /// </summary>
    [Fact, Trait("D", "D3.13")]
    public async Task Maintenance_scan_is_not_limited_by_the_backup_lock_file_cap()
    {
        for (var i = 0; i < 5; i++) _store.Objects[RepoPaths.Lock(RandomNumberGenerator.GetBytes(16))] = "{}"u8.ToArray();
        var scan = await RepoLocks.ScanAsync(_store.Open(CredentialRole.Maintenance), _keys, _time.Now, new RepoLockOptions(MaxLockFiles: 4), _ct);
        Assert.Equal(5, scan.Invalid.Count); // wszystkie przeczytane, żaden wyjątek
    }

    [Fact, Trait("D", "D3.13")]
    public async Task Too_many_lock_files_count_as_locked()
    {
        for (var i = 0; i < 5; i++) _store.Objects[RepoPaths.Lock(RandomNumberGenerator.GetBytes(16))] = "{}"u8.ToArray();
        var e = await Assert.ThrowsAsync<KobianException>(() => RepoLocks.ScanAsync(_store.Open(), _keys, _time.Now, new RepoLockOptions(MaxLockFiles: 4), _ct));
        Assert.Equal(KbCode.RepoLocked, e.Code);
    }

    [Fact, Trait("D", "D3.13")]
    public async Task Renewal_writes_a_new_file_and_maintenance_removes_the_old_one()
    {
        var shared = await RepoLocks.AcquireSharedAsync(_store.Open(), _keys, _a, _time, null, _ct);
        var first = shared.LockId;
        await shared.RenewIfDueAsync(_ct);
        Assert.Equal(first, shared.LockId); // jeszcze nie pora
        await _time.Advance(TimeSpan.FromMinutes(5));
        await shared.RenewIfDueAsync(_ct);
        Assert.NotEqual(first, shared.LockId);
        Assert.True(shared.Owns(first) && shared.Owns(shared.LockId));
        Assert.Equal(2, LockFiles().Length); // Backup: stary plik zostaje i wygasa
        await _time.Advance(TimeSpan.FromMinutes(29));
        var scan = await RepoLocks.ScanAsync(_store.Open(), _keys, _time.Now, null, _ct);
        Assert.Equal([shared.LockId], scan.Live.Select(l => l.LockId)); // odświeżona żyje dłużej niż pierwotna
        await shared.ReleaseAsync(_ct);

        _store.Objects.Clear();
        await using var ex = await RepoLocks.AcquireExclusiveAsync(_store.Open(CredentialRole.Maintenance), _keys, _a, _time, new RepoLockOptions(Refresh: TimeSpan.FromMinutes(5), ExclusiveSettle: TimeSpan.FromMinutes(6)), Delay(), _ct);
        Assert.Single(LockFiles()); // odnowiona w trakcie odczekania; stary plik usunięty (Maintenance)
        await ex.ReleaseAsync(_ct);
        Assert.Empty(LockFiles());
    }

    [Fact, Trait("D", "D3.13"), Trait("D", "D4.1")]
    public async Task Exclusive_lock_requires_a_maintenance_session()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => RepoLocks.AcquireExclusiveAsync(_store.Open(), _keys, _a, _time, null, Delay(), _ct));
        Assert.Empty(LockFiles());
    }

    [Fact, Trait("D", "D3.13")]
    public async Task Lock_from_a_host_with_clock_ahead_stays_live()
    {
        var id = Hex();
        var ahead = Info(id) with { RenewedAt = _time.Now.AddHours(2), CreatedAt = _time.Now.AddHours(2) };
        _store.Objects[RepoPaths.Lock(Convert.FromHexString(id))] = Encoding.UTF8.GetBytes(RepoLockFile.Create(_keys, ahead));
        var scan = await RepoLocks.ScanAsync(_store.Open(), _keys, _time.Now, null, _ct);
        Assert.Single(scan.Live);
        await Assert.ThrowsAsync<KobianException>(() => RepoLocks.AcquireExclusiveAsync(_store.Open(CredentialRole.Maintenance), _keys, _a, _time, null, Delay(), _ct));
    }
}
