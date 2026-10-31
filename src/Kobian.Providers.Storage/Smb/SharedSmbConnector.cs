using Kobian.Application.Storage;

namespace Kobian.Providers.Storage.Smb;

/// <summary>
/// Współdzielenie połączeń SMB w procesie (BL-73): sesje równoległe (4.7 — do 4 na cel) do tego samego udziału z tym samym kontem korzystają z jednego
/// połączenia; rozłączenie następuje po zwolnieniu ostatniej sesji (inaczej pierwsza zamknięta sesja zerwałaby pozostałe). Drugie konto do tego samego
/// serwera, gdy pierwsze jest połączone, kończy się od razu błędem 1219 (KB-2101) bez wywołania systemu — Windows i tak by odmówił, a kolejka (D7.4)
/// powinna była takie zadania rozdzielić. Nazwy serwera porównywane bez rozróżniania wielkości liter; konto także (konwencja Windows).
/// <para>
/// Watchdog łączenia (D4.13, it. 94): <c>WNetAddConnection2</c> jest wywołaniem blokującym bez własnego limitu (DNS, timeout SMB — dziesiątki sekund
/// i więcej). Próba trwa najwyżej <see cref="ConnectTimeout"/> (domyślnie 30 s = bezczynność „sterowania” z D4.13); po przekroczeniu — błąd 121
/// (<c>ERROR_SEM_TIMEOUT</c> → przejściowy KB-2001 w <see cref="SmbErrorMap"/>). Spóźnione połączenie systemowe jest zamykane, gdy wywołanie wróci,
/// a kolejna próba do tego serwera najpierw czeka (najwyżej <see cref="ConnectTimeout"/>) na powrót porzuconego wywołania i dopiero potem łączy
/// — bez drugiego wiszącego wątku i bez wyścigu o 1219; gdy wywołanie nie wraca, kończy się tym samym błędem 121.
/// Bramki per serwer są usuwane, gdy nikt ich nie używa (Niskie z przeglądu it. 93 — brak wzrostu słownika przy wielu serwerach).
/// </para>
/// </summary>
public sealed class SharedSmbConnector : ISmbConnector, IDisposable
{
    /// <summary>Domyślny limit pojedynczej próby połączenia (D4.13: bezczynność „sterowania” 30 s).</summary>
    public static readonly TimeSpan DefaultConnectTimeout = TimeSpan.FromSeconds(30);

    /// <summary><c>ERROR_SEM_TIMEOUT</c> — kod zwracany po przekroczeniu limitu łączenia (klasa przejściowa).</summary>
    public const int TimeoutWin32Error = 121;

    // Przegląd it. 92 (Średnie, naprawione w it. 93): blokada per serwer zamiast globalnej — niedostępny serwer A nie wstrzymuje połączeń do serwera B
    // ani zwalniania sesji innych serwerów. Słownik chroniony krótkim lock (bez I/O pod nim).
    private readonly object _map = new();
    private readonly Dictionary<string, Gate> _serverGates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Entry> _byShare = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _pendingLate = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Task> _lateDone = new(StringComparer.OrdinalIgnoreCase);
    private readonly ISmbConnector _inner;
    private readonly TimeProvider _time;

    public SharedSmbConnector(ISmbConnector inner, TimeSpan? connectTimeout = null, TimeProvider? time = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        ConnectTimeout = connectTimeout ?? DefaultConnectTimeout;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(ConnectTimeout, TimeSpan.Zero, nameof(connectTimeout));
        _time = time ?? TimeProvider.System;
    }

    public TimeSpan ConnectTimeout { get; }

    /// <summary>Liczba aktywnych połączeń systemowych (diagnostyka i testy).</summary>
    public int ActiveConnections
    {
        get
        {
            lock (_map)
            {
                return _byShare.Count;
            }
        }
    }

    /// <summary>Liczba bramek serwerów w słowniku (diagnostyka i testy: po zwolnieniu wszystkiego = 0).</summary>
    public int ServerGateCount
    {
        get
        {
            lock (_map)
            {
                return _serverGates.Count;
            }
        }
    }

    /// <summary>Liczba spóźnionych prób (po watchdogu), na których powrót czeka zamknięcie połączenia.</summary>
    public int PendingLateConnections
    {
        get
        {
            lock (_map)
            {
                return _pendingLate.Values.Sum();
            }
        }
    }

    public async ValueTask<ISmbConnection> ConnectAsync(SmbShare share, SmbCredential credential, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(share);
        ArgumentNullException.ThrowIfNull(credential);
        var gate = AcquireGate(share.Server);
        try
        {
            // Czekanie na bramkę też ma limit: inna próba do tego serwera może wisieć w systemie do granicy watchdoga.
            if (!await gate.Semaphore.WaitAsync(ConnectTimeout, ct).ConfigureAwait(false))
                throw new SmbConnectException(TimeoutWin32Error, "Timed out waiting for another SMB connection attempt to this server.");
            try
            {
                ct.ThrowIfCancellationRequested(); // anulowanie ma pierwszeństwo przed diagnozą stanu serwera
                Task? late;
                lock (_map)
                {
                    _lateDone.TryGetValue(share.Server, out late);
                }
                if (late is not null)
                {
                    // Pod bramką serwera nikt nie porzuci nowej próby, więc wystarczy poczekać na znane porzucone (ograniczone limitem).
                    try
                    {
                        await late.WaitAsync(ConnectTimeout, _time, ct).ConfigureAwait(false);
                    }
                    catch (TimeoutException ex)
                    {
                        throw new SmbConnectException(TimeoutWin32Error, "A previous SMB connection attempt to this server has not returned yet.", ex);
                    }
                }
                Entry? entry;
                lock (_map)
                {
                    foreach (var e in _byShare.Values)
                        if (string.Equals(e.Server, share.Server, StringComparison.OrdinalIgnoreCase) && !string.Equals(e.User, credential.User, StringComparison.OrdinalIgnoreCase))
                            throw new SmbConnectException(1219, "Another account is already connected to this SMB server.");
                    _byShare.TryGetValue(share.ShareUnc, out entry);
                }
                if (entry is null)
                {
                    // I/O poza lock(_map), ale pod blokadą serwera: dwa równoległe połączenia do tego samego udziału nie powstaną.
                    var conn = await ConnectWithWatchdogAsync(new SmbShare(share.Server, share.Share, ""), credential, ct).ConfigureAwait(false);
                    entry = new Entry(share.Server, credential.User, conn);
                    lock (_map)
                    {
                        _byShare[share.ShareUnc] = entry;
                    }
                }
                entry.Count++;
                var root = share.RelativePath.Length == 0
                    ? entry.Connection.LocalRoot
                    : Path.Join(entry.Connection.LocalRoot, share.RelativePath.Replace('\\', Path.DirectorySeparatorChar));
                return new Lease(this, share.ShareUnc, entry, root);
            }
            finally
            {
                gate.Semaphore.Release();
            }
        }
        finally
        {
            ReleaseGate(share.Server, gate);
        }
    }

    public void Dispose()
    {
        lock (_map)
        {
            // Bramki są usuwane po użyciu; tu zostają tylko te, na które ktoś jeszcze czeka (zamknięcie procesu) — nie zwalniamy ich pod czekającymi.
            _serverGates.Clear();
        }
    }

    /// <summary>
    /// Próba połączenia z limitem czasu. Wywołanie systemowe idzie w wątku puli (jest blokujące); po przekroczeniu limitu lub anulowaniu
    /// czekamy na nie w tle i zamykamy spóźnione połączenie — inaczej zostałoby otwarte w sesji logowania usługi bez właściciela.
    /// </summary>
    private async Task<ISmbConnection> ConnectWithWatchdogAsync(SmbShare share, SmbCredential credential, CancellationToken ct)
    {
        var attempt = Task.Run(async () => await _inner.ConnectAsync(share, credential, CancellationToken.None).ConfigureAwait(false), CancellationToken.None);
        try
        {
            return await attempt.WaitAsync(ConnectTimeout, _time, ct).ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            AbandonLate(share.Server, attempt);
            throw new SmbConnectException(TimeoutWin32Error, "SMB connection attempt timed out.", ex);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested && !attempt.IsCompleted)
        {
            AbandonLate(share.Server, attempt);
            throw;
        }
    }

    private void AbandonLate(string server, Task<ISmbConnection> attempt)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanup = started.Task.ContinueWith(_ => attempt, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap().ContinueWith(
            async t =>
            {
                try
                {
                    if (t.IsCompletedSuccessfully) await t.Result.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Zamykanie porzuconego połączenia jest najlepszym wysiłkiem (2250 = już rozłączone); błąd nie może wywrócić procesu.
                }
                finally
                {
                    lock (_map)
                    {
                        if (--_pendingLate[server] == 0)
                        {
                            _pendingLate.Remove(server);
                            _lateDone.Remove(server);
                        }
                    }
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default).Unwrap();
        lock (_map)
        {
            _pendingLate[server] = _pendingLate.GetValueOrDefault(server) + 1;
            _lateDone[server] = _lateDone.TryGetValue(server, out var prev) ? Task.WhenAll(prev, cleanup) : cleanup;
        }
        started.SetResult(); // licznik i zadanie zapisane, zanim sprzątanie może je zmniejszyć
    }

    private Gate AcquireGate(string server)
    {
        lock (_map)
        {
            if (!_serverGates.TryGetValue(server, out var g)) _serverGates[server] = g = new Gate();
            g.Users++;
            return g;
        }
    }

    private void ReleaseGate(string server, Gate gate)
    {
        lock (_map)
        {
            // Bramka znika, gdy nikt jej nie trzyma ani na nią nie czeka; aktywne połączenia biorą bramkę ponownie przy zwolnieniu (ReleaseAsync).
            if (--gate.Users == 0 && _serverGates.TryGetValue(server, out var cur) && ReferenceEquals(cur, gate))
            {
                _serverGates.Remove(server);
                gate.Dispose(); // nikt jej nie trzyma ani nie czeka (Users == 0 pod tą samą blokadą)
            }
        }
    }

    private async ValueTask ReleaseAsync(string key, Entry entry)
    {
        var gate = AcquireGate(entry.Server);
        try
        {
            await gate.Semaphore.WaitAsync().ConfigureAwait(false);
            try
            {
                if (--entry.Count != 0) return;
                lock (_map)
                {
                    if (!_byShare.TryGetValue(key, out var cur) || !ReferenceEquals(cur, entry)) return;
                    _byShare.Remove(key);
                }
                // Rozłączenie pod blokadą serwera: nowe połączenie (np. innym kontem) do tego serwera poczeka, aż system zwolni poprzednie (bez wyścigu o 1219).
                await entry.Connection.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                gate.Semaphore.Release();
            }
        }
        finally
        {
            ReleaseGate(entry.Server, gate);
        }
    }

    private sealed class Gate : IDisposable
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);

        public int Users { get; set; }

        public void Dispose() => Semaphore.Dispose();
    }

    private sealed class Entry(string server, string user, ISmbConnection connection)
    {
        public string Server { get; } = server;

        public string User { get; } = user;

        public ISmbConnection Connection { get; } = connection;

        public int Count { get; set; }
    }

    private sealed class Lease(SharedSmbConnector owner, string key, Entry entry, string root) : ISmbConnection
    {
        private int _disposed;

        public string LocalRoot { get; } = root;

        public ValueTask DisposeAsync() =>
            Interlocked.Exchange(ref _disposed, 1) == 0 ? owner.ReleaseAsync(key, entry) : ValueTask.CompletedTask;
    }
}
