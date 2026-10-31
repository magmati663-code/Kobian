using Kobian.Application.Storage;
using Kobian.Crypto;
using Kobian.Domain;

namespace Kobian.Engine.Repo;

/// <summary>Wynik fazy 2: <see cref="Deleted"/> = obiekty faktycznie usunięte (albo już nieobecne); <see cref="StopCode"/> ≠ null = zatrzymano przed obiektem <see cref="StopObject"/>.</summary>
public sealed record PruneDeletion(string? StopCode, string? StopObject, IReadOnlyList<PruneObject> Deleted)
{
    public long DeletedBytes => Deleted.Sum(o => o.Size);
}

/// <summary>
/// Faza 2 prune dwufazowego (BL-25 cz. 4, D3.7, D3.13, PM-18). Warunki wstępne sprawdzane w kodzie: sesja <see cref="CredentialRole.Maintenance"/> i trzymana
/// WYŁĄCZNA dzierżawa (bez niej trwający backup mógłby właśnie zapisywać packi, które w spisie wyglądają na sieroty).
/// Kroki: ponowny spis tuż przed usunięciem → ponowny plan z tymi samymi oznaczeniami → usuwane są tylko obiekty z <paramref name="approved"/>,
/// które NADAL są w <see cref="PrunePlan.Deletable"/> z tym samym rozmiarem. Kolejność: snapshoty → indeksy → packi (przerwanie w połowie zostawia repo spójne:
/// nic żywego nie wskazuje na usunięty obiekt). Pierwsza niezgodność zatrzymuje całość (KB-1603) — reszta czeka na kolejny przebieg.
/// </summary>
public static class PruneExecutor
{
    public static async Task<PruneDeletion> DeleteAsync(IStorageSession session, RepoKeys keys, RepoLease lease, IReadOnlySet<string> forgotten,
        IReadOnlyDictionary<string, DateTimeOffset> marks, IReadOnlyList<PruneObject> approved, TimeProvider time, TimeSpan grace, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(approved);
        ArgumentNullException.ThrowIfNull(time);
        if (session.Role != CredentialRole.Maintenance) throw new InvalidOperationException("Prune deletion requires a maintenance session (D4.1).");
        if (!lease.Exclusive) throw new InvalidOperationException("Prune deletion requires an exclusive repository lock (D3.13).");

        await lease.RenewIfDueAsync(ct).ConfigureAwait(false);
        var inv = await PrunePlanner.CollectAsync(session, keys, forgotten, ct).ConfigureAwait(false);
        var plan = PrunePlanner.Plan(inv, marks, time.GetUtcNow(), grace);
        if (plan.BlockedCode is not null) return new PruneDeletion(plan.BlockedCode, plan.BlockedObject, []);
        var fresh = plan.Deletable.ToDictionary(o => o.Key, StringComparer.Ordinal);

        var deleted = new List<PruneObject>();
        foreach (var o in approved.OrderBy(Rank).ThenBy(o => o.Id, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            if (!fresh.TryGetValue(o.Key, out var now) || now.Size != o.Size) return Stop(o, deleted);
            await lease.RenewIfDueAsync(ct).ConfigureAwait(false);
            var st = await session.StatAsync(o.Path, ct).ConfigureAwait(false);
            if (st is null) { deleted.Add(o); continue; } // już nie ma (np. wcześniejszy przerwany przebieg) — oznaczenie do zdjęcia
            if (st.IsDirectory || (o.Kind != "snapshot" && st.Length != o.Size)) return Stop(o, deleted);
            await session.DeleteAsync(o.Path, ct).ConfigureAwait(false);
            deleted.Add(o);
        }
        return new PruneDeletion(null, null, deleted);
    }

    private static int Rank(PruneObject o) => o.Kind switch { "snapshot" => 0, "index" => 1, _ => 2 };

    private static PruneDeletion Stop(PruneObject o, List<PruneObject> deleted) => new(KbCode.PruneStoppedInconsistent, o.Key, deleted);
}
