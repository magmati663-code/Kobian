using System.Security.Cryptography;
using Kobian.Application.Storage;
using Kobian.Domain;
using Kobian.Providers.Storage.Smb;
using Xunit;

namespace Kobian.Tests.Unit.Storage;

/// <summary>
/// BL-73 cz. 3 (it. 94, D4.13): watchdog łączenia SMB w <see cref="SharedSmbConnector"/> — blokujące wywołanie systemowe (model WNetAddConnection2)
/// nie wstrzymuje zadania dłużej niż limit, spóźnione połączenie jest zamykane, a bramki serwerów nie rosną bez końca. Poświadczenia syntetyczne.
/// </summary>
public sealed class SmbConnectWatchdogTests
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private readonly SmbCredential _cred = new(@"FIRMA\kobian-bk", Convert.ToHexString(RandomNumberGenerator.GetBytes(10)));

    /// <summary>Konektor blokujący wątek do czasu <see cref="Unblock"/> (jak wiszący WNetAddConnection2: DNS, nieosiągalny serwer).</summary>
    private sealed class BlockingConnector : ISmbConnector, IDisposable
    {
        private readonly ManualResetEventSlim _gate = new(false);
        public int Connects;
        public int Disconnects;
        public bool Block { get; set; } = true;
        public int? FailAfterUnblock { get; set; }

        public ValueTask<ISmbConnection> ConnectAsync(SmbShare share, SmbCredential credential, CancellationToken ct)
        {
            Interlocked.Increment(ref Connects);
            if (Block) _gate.Wait(TimeSpan.FromSeconds(30), CancellationToken.None); // jak WNet: wywołanie systemowe nie zna tokenu
            if (FailAfterUnblock is { } e) throw new SmbConnectException(e, "fake failure");
            return ValueTask.FromResult<ISmbConnection>(new Conn(this, "/fake/" + share.Server + "/" + share.Share));
        }

        public void Unblock() => _gate.Set();

        public void Dispose() => _gate.Dispose();

        private sealed class Conn(BlockingConnector owner, string root) : ISmbConnection
        {
            public string LocalRoot { get; } = root;

            public ValueTask DisposeAsync()
            {
                Interlocked.Increment(ref owner.Disconnects);
                return ValueTask.CompletedTask;
            }
        }
    }

    private static async Task WaitUntil(Func<bool> cond)
    {
        for (var i = 0; i < 500 && !cond(); i++) await Task.Delay(10);
        Assert.True(cond());
    }

    [Fact, Trait("Decision", "D4.13")]
    public async Task Hanging_connect_times_out_as_transient_and_the_late_connection_is_closed()
    {
        using var inner = new BlockingConnector();
        using var shared = new SharedSmbConnector(inner, TimeSpan.FromMilliseconds(150));
        var ex = await Assert.ThrowsAsync<SmbConnectException>(async () => await shared.ConnectAsync(UncPath.Parse(@"\\nas\kopie"), _cred, _ct));
        Assert.Equal(SharedSmbConnector.TimeoutWin32Error, ex.Win32Error);
        var mapped = SmbErrorMap.MapConnect(ex.Win32Error);
        Assert.Equal(ErrorClass.Transient, mapped.ErrorClass); // ponowienie wg D4.10, nie błąd trwały
        Assert.Equal(KbCode.TransientIo, mapped.Code);
        Assert.Equal(1, shared.PendingLateConnections);
        Assert.Equal(0, shared.ActiveConnections);

        // Do powrotu wywołania kolejna próba do tego serwera czeka na nie (limit) i kończy się 121 — bez drugiego wywołania systemowego.
        var again = await Assert.ThrowsAsync<SmbConnectException>(async () => await shared.ConnectAsync(UncPath.Parse(@"\\NAS\inny"), _cred, _ct));
        Assert.Equal(SharedSmbConnector.TimeoutWin32Error, again.Win32Error);
        Assert.Equal(1, inner.Connects);

        inner.Block = false;
        inner.Unblock();
        await WaitUntil(() => shared.PendingLateConnections == 0);
        Assert.Equal(1, inner.Disconnects); // spóźnione połączenie nie zostało w sesji logowania usługi
        Assert.Equal(0, shared.ActiveConnections);

        await using (var lease = await shared.ConnectAsync(UncPath.Parse(@"\\nas\kopie"), _cred, _ct))
            Assert.Equal(1, shared.ActiveConnections);
        Assert.Equal(2, inner.Disconnects);
    }

    [Fact, Trait("Decision", "D4.13")]
    public async Task Other_servers_are_not_blocked_by_a_hanging_one()
    {
        using var hanging = new BlockingConnector();
        using var router = new RoutingConnector(hanging);
        using var shared = new SharedSmbConnector(router, TimeSpan.FromSeconds(5));
        var slow = shared.ConnectAsync(UncPath.Parse(@"\\martwy\kopie"), _cred, _ct).AsTask();
        await WaitUntil(() => hanging.Connects == 1);
        await using (var ok = await shared.ConnectAsync(UncPath.Parse(@"\\nas\kopie"), _cred, _ct))
            Assert.Equal("/ok/nas/kopie", ok.LocalRoot);
        Assert.False(slow.IsCompleted);
        hanging.Unblock();
        await using (await slow) { }
        Assert.Equal(0, shared.ActiveConnections);
    }

    [Fact, Trait("Decision", "D4.13")]
    public async Task Cancelled_connect_releases_the_server_and_closes_the_late_connection()
    {
        using var inner = new BlockingConnector();
        using var shared = new SharedSmbConnector(inner, TimeSpan.FromSeconds(10));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_ct);
        var t = shared.ConnectAsync(UncPath.Parse(@"\\nas\kopie"), _cred, cts.Token).AsTask();
        await WaitUntil(() => inner.Connects == 1);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => t);
        Assert.Equal(1, shared.PendingLateConnections);
        inner.Block = false;
        inner.Unblock();
        await WaitUntil(() => shared.PendingLateConnections == 0);
        Assert.Equal(1, inner.Disconnects);
    }

    [Fact]
    public async Task Late_failure_after_timeout_is_absorbed_and_unblocks_the_server()
    {
        using var inner = new BlockingConnector { FailAfterUnblock = 53 };
        using var shared = new SharedSmbConnector(inner, TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAsync<SmbConnectException>(async () => await shared.ConnectAsync(UncPath.Parse(@"\\nas\kopie"), _cred, _ct));
        inner.Unblock();
        await WaitUntil(() => shared.PendingLateConnections == 0);
        Assert.Equal(0, inner.Disconnects); // nie było czego zamykać, wyjątek spóźnionej próby nie wypłynął
        inner.FailAfterUnblock = null;
        await using var lease = await shared.ConnectAsync(UncPath.Parse(@"\\nas\kopie"), _cred, _ct);
        Assert.Equal(1, shared.ActiveConnections);
    }

    [Fact]
    public async Task Server_gates_are_evicted_when_unused()
    {
        using var inner = new BlockingConnector { Block = false };
        using var shared = new SharedSmbConnector(inner);
        var leases = new List<ISmbConnection>();
        for (var i = 0; i < 40; i++) leases.Add(await shared.ConnectAsync(UncPath.Parse($@"\\srv{i}\kopie"), _cred, _ct));
        Assert.Equal(40, shared.ActiveConnections);
        foreach (var l in leases) await l.DisposeAsync();
        Assert.Equal(0, shared.ActiveConnections);
        Assert.Equal(0, shared.ServerGateCount);
        Assert.Equal(40, inner.Disconnects);
    }

    [Fact]
    public void Connect_timeout_must_be_positive()
    {
        using var inner = new BlockingConnector();
        Assert.Throws<ArgumentOutOfRangeException>(() => new SharedSmbConnector(inner, TimeSpan.Zero));
        using var d = new SharedSmbConnector(inner);
        Assert.Equal(SharedSmbConnector.DefaultConnectTimeout, d.ConnectTimeout);
    }

    /// <summary>Serwer „martwy” wisi w konektorze blokującym, pozostałe łączą się od razu.</summary>
    private sealed class RoutingConnector(BlockingConnector hanging) : ISmbConnector, IDisposable
    {
        public ValueTask<ISmbConnection> ConnectAsync(SmbShare share, SmbCredential credential, CancellationToken ct) =>
            share.Server == "martwy" ? hanging.ConnectAsync(share, credential, ct) : ValueTask.FromResult<ISmbConnection>(new Ok("/ok/" + share.Server + "/" + share.Share));

        public void Dispose()
        {
        }

        private sealed class Ok(string root) : ISmbConnection
        {
            public string LocalRoot { get; } = root;

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
