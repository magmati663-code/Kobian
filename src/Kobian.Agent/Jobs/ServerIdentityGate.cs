using Kobian.Application.Storage;
using Kobian.Domain;

namespace Kobian.Agent.Jobs;

/// <summary>
/// Dekorator dostawcy SFTP/FTPS w Agencie (D4.11, SEC-14.5; BL-35): pin dla sesji pochodzi z katalogu tożsamości (<see cref="ServerIdentityStore"/>), nie z definicji zadania.
/// <list type="number">
/// <item>Identyfikator celu = <see cref="JobDestination.Id"/> z konfiguracji (schemat, host, port, korzeń) — ten sam dla zadania, init, check, restore, prune i probe.</item>
/// <item>Kompatybilność wstecz: pin z pola <c>hostKey</c> definicji jest przenoszony do katalogu jako zatwierdzony, ale tylko gdy cel nie ma jeszcze pinu
/// (późniejsza zmiana klucza wymaga Administratora — Operator nie podmieni pinu edycją zadania). ProbeDestination (<c>Id = "probe"</c>) niczego nie przenosi:
/// podany w żądaniu odcisk jest sprawdzany tylko w tym połączeniu.</item>
/// <item>Odmowa z odciskiem (KB-2104/KB-2109) → zdarzenie <see cref="ServerIdentityEvent.Observed"/> (nowy klucz czeka na zatwierdzenie; stary pin zostaje).
/// SFTP bez żadnego pinu → tryb „identity_probe” (poświadczenie nie trafia do serwera, poznajemy tylko odcisk).</item>
/// <item>FTPS przyjęty z SPKI spoza pinów (czyli dzięki ważnemu łańcuchowi CA) → zdarzenie informacyjne <see cref="ServerIdentityEvent.TrustedByCa"/>; cel
/// „wysokiego ryzyka” dostaje opcję <c>require_pin</c> — wtedy CA nie wystarcza.</item>
/// </list>
/// Błąd zapisu katalogu przy obserwacji nie zmienia wyniku połączenia (odmowa pozostaje odmową — fail-closed).
/// </summary>
public sealed class ServerIdentityGate(IStorageProviderFactory inner, ServerIdentityStore store) : IStorageProviderFactory
{
    public const string ProbeConfigId = "probe";

    private readonly IStorageProviderFactory _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    private readonly ServerIdentityStore _store = store ?? throw new ArgumentNullException(nameof(store));

    public string Scheme => _inner.Scheme;

    /// <summary>Opakowuje dostawców sftp/ftps rejestru (pozostałe bez zmian).</summary>
    public static Dictionary<string, IStorageProviderFactory> Wrap(IReadOnlyDictionary<string, IStorageProviderFactory> providers, ServerIdentityStore store)
    {
        ArgumentNullException.ThrowIfNull(providers);
        var map = new Dictionary<string, IStorageProviderFactory>(StringComparer.Ordinal);
        foreach (var (k, v) in providers) map[k] = k is "sftp" or "ftps" ? new ServerIdentityGate(v, store) : v;
        return map;
    }

    public async ValueTask<IStorageSession> OpenAsync(DestinationConfig config, CredentialRole role, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(config);
        var kind = ServerIdentityStore.KindOf(config.Scheme);
        if (kind is null) return await _inner.OpenAsync(config, role, ct).ConfigureAwait(false);
        var sftp = config.Scheme == "sftp";
        var pinKey = sftp ? "hostkey" : "spki";
        var src = config.Options ?? new Dictionary<string, string>(StringComparer.Ordinal);
        var destId = JobDestination.Id(config.Scheme, config.Root, src);
        var endpoint = ServerIdentityStore.Endpoint(config.Scheme, Opt(src, "host"), Opt(src, "port"));
        var requested = ServerIdentityMachine.Normalize(Opt(src, pinKey));
        var probe = string.Equals(config.Id, ProbeConfigId, StringComparison.Ordinal);

        var rows = _store.Load(destId);
        if (!probe && requested is not null && !rows.Any(r => r.IsEffectivePin))
            rows = (await _store.ApplyAsync(destId, new ServerIdentityEvent.SeedFromDefinition(kind, requested, endpoint), ct).ConfigureAwait(false)).Rows;

        // Probe z odciskiem w żądaniu sprawdza dokładnie ten odcisk (bez zapisu); w pozostałych przypadkach — wyłącznie piny z katalogu.
        IReadOnlyList<string> pins = probe && requested is not null ? [requested] : ServerIdentityMachine.EffectivePins(rows);
        var requirePin = !sftp && _store.RequiresPin(destId);
        var options = new Dictionary<string, string>(src, StringComparer.Ordinal);
        options.Remove(pinKey);
        options.Remove("require_pin");
        if (pins.Count > 0)
        {
            options[pinKey] = string.Join(',', pins);
            options.Remove("identity_probe");
        }
        else if (sftp)
        {
            options["identity_probe"] = "1"; // brak pinu: tylko poznanie odcisku, poświadczenie nie jest wysyłane
        }
        if (requirePin) options["require_pin"] = "1";

        IStorageSession session;
        try
        {
            session = await _inner.OpenAsync(config with { Options = options }, role, ct).ConfigureAwait(false);
        }
        catch (StorageException ex) when (ex.Code is KbCode.HostKeyMismatch or KbCode.TlsIdentityMismatch
                                          && ex.PresentedIdentity is { Fingerprint.Length: > 0 } id)
        {
            await TryApplyAsync(destId, new ServerIdentityEvent.Observed(kind, id.Fingerprint, endpoint, id.Display)).ConfigureAwait(false);
            throw;
        }
        if (!sftp && !requirePin && session.PresentedIdentity is { Fingerprint.Length: > 0 } presented && !pins.Contains(presented.Fingerprint, StringComparer.Ordinal))
            await TryApplyAsync(destId, new ServerIdentityEvent.TrustedByCa(presented.Fingerprint, endpoint, presented.Display)).ConfigureAwait(false);
        return session;
    }

    private async Task TryApplyAsync(string destId, ServerIdentityEvent ev)
    {
        try
        {
            // Bez tokenu wywołującego: przerwane zadanie nie może zgubić zapisu obserwacji klucza (krótka operacja sterowania).
            await _store.ApplyAsync(destId, ev, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception e) when (e is Microsoft.Data.Sqlite.SqliteException or InvalidOperationException or ObjectDisposedException)
        {
            // Katalog niedostępny: wynik połączenia bez zmian (odmowa zostaje odmową); obserwacja powtórzy się przy następnym połączeniu.
        }
    }

    private static string? Opt(IReadOnlyDictionary<string, string> o, string k) => o.TryGetValue(k, out var v) && v.Length > 0 ? v : null;
}
