using System.Security.Cryptography;
using Kobian.Application.Storage;
using Kobian.Domain;
using Kobian.Engine.Repo;
using Kobian.Tests.Unit.TestDoubles;
using Xunit;

namespace Kobian.Tests.Unit.Engine;

/// <summary>Runda 10/11: zerwane połączenie w trakcie wysyłki packa → ponowienie na świeżej sesji; błędy trwałe i niezgodność skrótu nie są ponawiane.</summary>
public sealed class UploadRetryTests
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    private static VerifiedUploader Uploader(IStorageSession first, MemoryStorage store, List<int> delays, Func<IStorageSession>? next = null, int max = 4) =>
        new(first)
        {
            Reopen = _ => ValueTask.FromResult(next?.Invoke() ?? store.Open()),
            MaxAttempts = max,
            Delay = (n, _) => { delays.Add(n); return Task.CompletedTask; },
        };

    [Fact]
    public async Task Connection_drop_mid_write_is_retried_on_a_fresh_session_and_the_object_is_complete()
    {
        var store = new MemoryStorage(atomicRename: true); // tymczasowy obiekt + rename: nieudana próba nie zostawia śladu pod docelową nazwą
        var delays = new List<int>();
        await using var up = Uploader(store.Open(faultAt: 1, fault: FaultKind.DisconnectMidWrite), store, delays);
        var data = RandomNumberGenerator.GetBytes(300_000);
        var sha = await up.UploadAsync("data/aa/pack1", data, _ct);
        Assert.Equal(SHA256.HashData(data), sha);
        Assert.Equal(data, store.Objects["data/aa/pack1"]);
        Assert.Equal(1, up.Retries);
        Assert.Equal([1], delays); // jedno opóźnienie przed pierwszym ponowieniem
    }

    [Fact]
    public async Task Direct_final_partial_object_blocks_the_retry_because_backup_role_cannot_delete()
    {
        // Cel bez atomowego rename (DirectFinal): po zerwaniu częściowy obiekt zostaje, a rola Backup nie usuwa (D4.6) — ponowienie tego packa jest niemożliwe,
        // przebieg kończy się błędem zapisu jak dotąd (ponowi go wykonawca przebiegów). Dotyczy tylko takich celów; SFTP/FTP/WebDAV/SMB/Local mają rename.
        var store = new MemoryStorage(atomicRename: false);
        var delays = new List<int>();
        await using var up = Uploader(store.Open(faultAt: 1, fault: FaultKind.DisconnectMidWrite), store, delays);
        await Assert.ThrowsAsync<RepoWriteException>(() => up.UploadAsync("data/aa/packd", RandomNumberGenerator.GetBytes(300_000), _ct));
        Assert.Equal(1, up.Retries);
    }

    [Fact]
    public async Task Transient_failures_are_retried_up_to_the_limit_then_reported_as_a_write_failure()
    {
        var store = new MemoryStorage();
        var delays = new List<int>();
        // każda sesja (także otwierana ponownie) zrywa pierwszy zapis
        await using var up = Uploader(store.Open(faultAt: 1, fault: FaultKind.DisconnectMidWrite), store, delays, () => store.Open(faultAt: 1, fault: FaultKind.DisconnectMidWrite), max: 3);
        await Assert.ThrowsAsync<RepoWriteException>(() => up.UploadAsync("data/aa/pack2", RandomNumberGenerator.GetBytes(10_000), _ct));
        Assert.Equal(2, up.Retries); // 3 próby = 2 ponowienia
        Assert.Equal([1, 2], delays); // narastające opóźnienie
        Assert.DoesNotContain("data/aa/pack2", store.Objects.Keys); // brak częściowego obiektu (tryb atomowy)
    }

    [Fact]
    public async Task Full_destination_is_not_retried_at_pack_level()
    {
        var store = new MemoryStorage();
        var delays = new List<int>();
        await using var up = Uploader(store.Open(faultAt: 1, fault: FaultKind.NoSpaceOnCreate), store, delays);
        await Assert.ThrowsAsync<RepoWriteException>(() => up.UploadAsync("data/aa/packf", RandomNumberGenerator.GetBytes(10_000), _ct));
        Assert.Equal(0, up.Retries);
        Assert.Empty(delays);
    }

    [Fact]
    public async Task Silent_corruption_and_process_kill_are_never_retried()
    {
        var store = new MemoryStorage();
        var delays = new List<int>();
        await using var up = Uploader(store.Open(faultAt: 1, fault: FaultKind.SilentTruncate), store, delays);
        await Assert.ThrowsAsync<RepoWriteException>(() => up.UploadAsync("data/aa/pack3", RandomNumberGenerator.GetBytes(10_000), _ct)); // V1-a: rozmiar
        Assert.Equal(0, up.Retries);

        await using var killed = Uploader(store.Open(faultAt: 1, fault: FaultKind.ProcessKill), store, delays);
        await Assert.ThrowsAsync<SimulatedProcessKillException>(() => killed.UploadAsync("data/aa/pack4", RandomNumberGenerator.GetBytes(10_000), _ct));
        Assert.Empty(delays);
    }

    [Fact]
    public async Task Without_reopen_behaviour_is_unchanged()
    {
        var store = new MemoryStorage();
        var plain = new VerifiedUploader(store.Open(faultAt: 1, fault: FaultKind.DisconnectMidWrite));
        await Assert.ThrowsAsync<RepoWriteException>(() => plain.UploadAsync("data/aa/pack5", RandomNumberGenerator.GetBytes(50_000), _ct));
        Assert.Equal(0, plain.Retries);
    }

    /// <summary>Sesja, której pierwsze Stat zrywa połączenie PO zatwierdzeniu zapisu (obiekt jest na celu, potwierdzenie zgubione).</summary>
    private sealed class LostAckSession(IStorageSession inner) : IStorageSession
    {
        private bool _failed;
        public CredentialRole Role => inner.Role;
        public ProviderCapabilities Capabilities => inner.Capabilities;
        public ServerIdentity? PresentedIdentity => inner.PresentedIdentity;

        public ValueTask<StatResult?> StatAsync(string path, CancellationToken ct)
        {
            if (_failed) return inner.StatAsync(path, ct);
            _failed = true;
            throw new StorageException(ErrorClass.Transient, KbCode.TransientIo, "Connection reset after commit (simulated).");
        }

        public IAsyncEnumerable<StatResult> ListAsync(string prefix, CancellationToken ct) => inner.ListAsync(prefix, ct);
        public ValueTask<Stream> OpenReadAsync(string path, long offset, long? length, CancellationToken ct) => inner.OpenReadAsync(path, offset, length, ct);
        public ValueTask<IObjectWriter> CreateAsync(string path, long expectedLength, WriteMode mode, long resumeFrom, CancellationToken ct) => inner.CreateAsync(path, expectedLength, mode, resumeFrom, ct);
        public ValueTask<long?> GetFreeSpaceAsync(CancellationToken ct) => inner.GetFreeSpaceAsync(ct);
        public ValueTask DeleteAsync(string path, CancellationToken ct) => inner.DeleteAsync(path, ct);
        public ValueTask<byte[]?> ServerHashAsync(string path, HashAlgorithmName algorithm, CancellationToken ct) => inner.ServerHashAsync(path, algorithm, ct);
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Lost_acknowledgement_is_recognised_by_size_and_hash_and_counts_as_success(bool atomicRename)
    {
        var store = new MemoryStorage(atomicRename);
        var delays = new List<int>();
        var data = RandomNumberGenerator.GetBytes(80_000);
        await using var up = Uploader(new LostAckSession(store.Open()), store, delays);
        Assert.Equal(SHA256.HashData(data), await up.UploadAsync("data/aa/packl", data, _ct));
        Assert.Equal(1, up.Retries);
        Assert.Equal(data, store.Objects["data/aa/packl"]);
    }
}
