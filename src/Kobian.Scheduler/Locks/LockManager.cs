namespace Kobian.Scheduler.Locks;

public enum LockMode
{
    Shared,
    Exclusive,
}

/// <summary>Żądanie blokady; dla <c>smb:</c> klucz to serwer, a <see cref="Account"/> rozstrzyga zgodność (Q20, D7.10).</summary>
public sealed record LockRequest(string Key, LockMode Mode, string? Account = null);

/// <summary>
/// Menedżer blokad D7.10: nabywanie „wszystko albo nic” w jednej sekcji krytycznej (brak hold-and-wait → brak zakleszczeń),
/// semafor globalny, postarzanie priorytetu w dyspozytorze.
/// </summary>
public sealed class LockManager
{
    private readonly object _gate = new();
    private readonly Dictionary<string, List<(string Run, LockRequest Req)>> _held = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<LockRequest>> _byRun = new(StringComparer.Ordinal);
    private readonly HashSet<string> _globalHolders = new(StringComparer.Ordinal);
    private int _globalFree;

    public LockManager(int globalSlots)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(globalSlots, 1);
        _globalFree = globalSlots;
    }

    public int GlobalFree
    {
        get
        {
            lock (_gate) return _globalFree;
        }
    }

    public bool TryAcquireAll(string runId, IReadOnlyList<LockRequest> requests)
    {
        ArgumentException.ThrowIfNullOrEmpty(runId);
        ArgumentNullException.ThrowIfNull(requests);
        lock (_gate)
        {
            // Wznowienie z pauzy: uruchomienie może już trzymać blokadę job: — takie klucze są pomijane.
            var already = _byRun.TryGetValue(runId, out var existing) ? existing : null;
            if (already is not null && already.Any(r => !r.Key.StartsWith("job:", StringComparison.Ordinal)))
                throw new InvalidOperationException("Run already holds locks.");
            requests = requests.Where(r => already is null || !already.Any(a => a.Key == r.Key)).ToList();
            if (_globalFree <= 0) return false;
            foreach (var r in requests)
            {
                if (_held.TryGetValue(r.Key, out var holders) && holders.Any(h => !Compatible(h.Req, r))) return false;
                // Wewnątrz jednego żądania: ten sam klucz dwukrotnie musi być zgodny sam ze sobą.
                if (requests.Any(o => !ReferenceEquals(o, r) && o.Key == r.Key && !Compatible(o, r))) return false;
            }
            foreach (var r in requests)
            {
                if (!_held.TryGetValue(r.Key, out var list)) _held[r.Key] = list = [];
                list.Add((runId, r));
            }
            _byRun[runId] = [.. (already ?? []), .. requests];
            _globalHolders.Add(runId);
            _globalFree--;
            return true;
        }
    }

    /// <summary>Zwalnia wszystkie blokady uruchomienia; przy pauzie można zachować <c>job:</c> (D7.8 „zwolnij blokady oprócz job:”).</summary>
    public void Release(string runId, bool keepJobLock = false)
    {
        lock (_gate)
        {
            if (!_byRun.TryGetValue(runId, out var reqs)) return;
            var kept = new List<LockRequest>();
            foreach (var r in reqs)
            {
                if (keepJobLock && r.Key.StartsWith("job:", StringComparison.Ordinal))
                {
                    kept.Add(r);
                    continue;
                }
                var list = _held[r.Key];
                list.RemoveAll(h => h.Run == runId && ReferenceEquals(h.Req, r));
                if (list.Count == 0) _held.Remove(r.Key);
            }
            if (kept.Count > 0)
            {
                _byRun[runId] = kept;
            }
            else
            {
                _byRun.Remove(runId);
            }
            if (_globalHolders.Remove(runId)) _globalFree++;
        }
    }

    public static bool Compatible(LockRequest held, LockRequest wanted)
    {
        ArgumentNullException.ThrowIfNull(held);
        ArgumentNullException.ThrowIfNull(wanted);
        if (held.Key.StartsWith("smb:", StringComparison.Ordinal))
        {
            // Dwa różne konta do jednego serwera — niezgodne (błąd 1219); to samo konto — zgodne.
            return string.Equals(held.Account, wanted.Account, StringComparison.OrdinalIgnoreCase);
        }
        return held.Mode == LockMode.Shared && wanted.Mode == LockMode.Shared;
    }

    /// <summary>Priorytet efektywny: priority + floor(czekanie / 30 min) (D7.10).</summary>
    public static int EffectivePriority(int priority, TimeSpan waited) => priority + (int)Math.Floor(waited.TotalMinutes / 30);
}
