using Kobian.Domain;

namespace Kobian.Agent.Jobs;

/// <summary>Stan tożsamości serwera celu (D4.11, DDL <c>repo_dest_identities.state</c>).</summary>
public enum IdentityState
{
    /// <summary>Serwer przedstawił klucz, którego nikt nie zatwierdził — połączenie odmówione (KB-2104/KB-2109) do decyzji Administratora.</summary>
    Pending,

    /// <summary>Zatwierdzony (Administrator, pin z definicji zadania albo — tylko informacyjnie — ważny łańcuch CA).</summary>
    Pinned,

    /// <summary>Odrzucony przez Administratora — połączenie z tym kluczem zawsze odmówione.</summary>
    Rejected,

    /// <summary>Zastąpiony nowym kluczem („zastąp”) albo wyparty przez nowszy SPKI z CA — nie jest już pinem.</summary>
    Retired,
}

/// <summary>Jeden klucz serwera celu (wiersz <c>repo_dest_identities</c>). Odcisk „SHA256:&lt;base64 bez dopełnienia&gt;” (format OpenSSH, także dla SPKI).</summary>
public sealed record ServerIdentityRow(
    string Fingerprint,
    string Kind,
    IdentityState State,
    string Endpoint,
    string? Display,
    DateTimeOffset FirstSeen,
    DateTimeOffset LastSeen,
    string? DecidedBy,
    DateTimeOffset? DecidedAt)
{
    /// <summary>Pin przekazywany do połączenia: zatwierdzony i NIE pochodzący wyłącznie z łańcucha CA (taki SPKI jest informacją, nie pinem — SEC-14.5).</summary>
    public bool IsEffectivePin => State == IdentityState.Pinned && !string.Equals(DecidedBy, ServerIdentityMachine.CaChainActor, StringComparison.Ordinal);

    public bool IsExpired(DateTimeOffset now) => State == IdentityState.Pending && now - FirstSeen > ServerIdentityMachine.PendingTtl;
}

/// <summary>Zdarzenia maszyny stanów (D4.11).</summary>
public abstract record ServerIdentityEvent
{
    private ServerIdentityEvent() { }

    /// <summary>Połączenie odmówione: serwer przedstawił ten odcisk, który nie jest pinem celu (pierwszy kontakt albo zmiana klucza).</summary>
    public sealed record Observed(string Kind, string Fingerprint, string Endpoint, string? Display) : ServerIdentityEvent;

    /// <summary>Decyzja Administratora: zatwierdź oczekujący odcisk — dodaj do zbioru pinów (klaster, CE-25) albo zastąp dotychczasowe.</summary>
    public sealed record Approve(string Fingerprint, bool Replace, string Actor) : ServerIdentityEvent;

    /// <summary>Decyzja Administratora: odrzuć oczekujący odcisk.</summary>
    public sealed record Reject(string Fingerprint, string Actor) : ServerIdentityEvent;

    /// <summary>Kompatybilność wstecz: pin z pola <c>hostKey</c> definicji zadania — przyjmowany tylko, gdy cel nie ma jeszcze żadnego pinu.</summary>
    public sealed record SeedFromDefinition(string Kind, string Fingerprint, string Endpoint) : ServerIdentityEvent;

    /// <summary>TLS: połączenie przyjęte dzięki ważnemu łańcuchowi CA (cel nie jest „wysokiego ryzyka”) z SPKI spoza pinów — zdarzenie informacyjne (SEC-14.5).</summary>
    public sealed record TrustedByCa(string Fingerprint, string Endpoint, string? Display) : ServerIdentityEvent;
}

/// <summary>Co się stało (do audytu/logu i testów).</summary>
public enum ServerIdentityNotice
{
    None,
    NewPending,
    StillPending,
    Approved,
    Rejected,
    Seeded,
    AutoApprovedByCa,
    Ignored,
}

/// <summary>Wynik przejścia: nowy zbiór wierszy celu, kod KB odmowy (null = przejście dozwolone) i rodzaj zdarzenia.</summary>
public sealed record ServerIdentityTransition(IReadOnlyList<ServerIdentityRow> Rows, string? Code, ServerIdentityNotice Notice);

/// <summary>
/// Czysta maszyna stanów zatwierdzania tożsamości serwera (D4.11, SEC-14.5, AC-12.2; zamyka L6) dla JEDNEGO celu — bez I/O, zegar w argumencie.
/// <list type="bullet">
/// <item>nieznany odcisk przy odmowie połączenia → <c>pending</c> (bez TOFU: nic nie staje się pinem samo z siebie, poza informacyjnym SPKI z ważnym CA);</item>
/// <item><c>pending</c> → <c>pinned</c> tylko decyzją Administratora z DOKŁADNIE tym odciskiem i w ciągu 24 h (inaczej KB-2141/KB-2142);
/// „zastąp” wycofuje (<c>retired</c>) pozostałe piny, „dodaj” zachowuje je (klaster za load balancerem, CE-25) — maks. 4 piny (KB-2143);</item>
/// <item>zmiana klucza przy celu z pinem → nowy odcisk <c>pending</c>, stary pin zostaje (kopia staje z KB-2104/KB-2109 do decyzji);</item>
/// <item><c>pending</c> → <c>rejected</c> decyzją Administratora; odrzucony odcisk przy kolejnych połączeniach pozostaje odrzucony;</item>
/// <item>TLS z ważnym łańcuchem CA (cel nie „wysokiego ryzyka”) → SPKI zapisany jako <c>pinned</c> przez <see cref="CaChainActor"/>, ale NIE jest pinem
/// połączenia (gdyby był, certyfikat z tym kluczem byłby przyjmowany także po unieważnieniu/wygaśnięciu); po włączeniu „wysokiego ryzyka” taki SPKI
/// przy odmowie wraca do <c>pending</c> i wymaga zatwierdzenia.</item>
/// </list>
/// </summary>
public static class ServerIdentityMachine
{
    public const string SshHostKey = "ssh-hostkey";
    public const string TlsSpki = "tls-spki";
    public const string CaChainActor = "system:ca-chain";
    public const string DefinitionActor = "system:job-definition";
    public const int MaxPins = 4;
    public const int MaxCaRecords = 4;
    public const int MaxPending = 8;
    public const int MaxRows = 32;
    public const int MaxDisplayLength = 200;
    public const int MaxEndpointLength = 300;
    public const int MaxActorLength = 200;
    public static readonly TimeSpan PendingTtl = TimeSpan.FromHours(24);

    /// <summary>Kształt odcisku: „SHA256:” + base64 bez dopełnienia, ≤ 64 znaki, bez przecinka (lista pinów w opcjach dostawcy).</summary>
    public static bool IsFingerprint(string? fp) =>
        fp is { Length: > 7 and <= 64 } && fp.StartsWith("SHA256:", StringComparison.Ordinal) && fp.AsSpan(7).IndexOfAnyExcept(Base64NoPad) < 0;

    /// <summary>Normalizacja odcisku z definicji/żądania (końcowe „=” usuwane — jak w porównaniach dostawców); null = niepoprawny kształt.</summary>
    public static string? Normalize(string? fp)
    {
        if (fp is null) return null;
        var t = fp.Trim().TrimEnd('=');
        return IsFingerprint(t) ? t : null;
    }

    public static ServerIdentityTransition Apply(IReadOnlyList<ServerIdentityRow> rows, ServerIdentityEvent ev, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(ev);
        var list = rows.ToList();
        int Index(string fp) => list.FindIndex(r => string.Equals(r.Fingerprint, fp, StringComparison.Ordinal));
        ServerIdentityTransition Same(ServerIdentityNotice n = ServerIdentityNotice.None, string? code = null) => new(rows, code, n);
        ServerIdentityTransition Done(ServerIdentityNotice n) => new(list, null, n);

        switch (ev)
        {
            case ServerIdentityEvent.Observed o:
            {
                if (!IsFingerprint(o.Fingerprint) || !IsKind(o.Kind)) return Same(ServerIdentityNotice.Ignored);
                var i = Index(o.Fingerprint);
                if (i < 0)
                {
                    if (list.Count(r => r.State == IdentityState.Pending) >= MaxPending || list.Count >= MaxRows) return Same(ServerIdentityNotice.Ignored);
                    list.Add(new ServerIdentityRow(o.Fingerprint, o.Kind, IdentityState.Pending, Clip(o.Endpoint, MaxEndpointLength), CleanDisplay(o.Display), now, now, null, null));
                    return Done(ServerIdentityNotice.NewPending);
                }
                var r = list[i];
                switch (r.State)
                {
                    case IdentityState.Rejected:
                        return Same();
                    case IdentityState.Pinned when r.IsEffectivePin:
                        return Same(); // pin odrzucony przez dostawcę z innego powodu — bez zmiany stanu
                    case IdentityState.Pending when !r.IsExpired(now):
                        list[i] = r with { LastSeen = now };
                        return Done(ServerIdentityNotice.StillPending);
                    default:
                        // wygasłe oczekiwanie, wycofany pin albo SPKI znany tylko z CA (cel przeszedł na „wysokie ryzyko”) → nowe oczekiwanie (24 h od teraz)
                        if (list.Count(x => x.State == IdentityState.Pending && !ReferenceEquals(x, r)) >= MaxPending) return Same(ServerIdentityNotice.Ignored);
                        list[i] = r with { State = IdentityState.Pending, FirstSeen = now, LastSeen = now, DecidedBy = null, DecidedAt = null, Display = CleanDisplay(o.Display) ?? r.Display };
                        return Done(ServerIdentityNotice.NewPending);
                }
            }

            case ServerIdentityEvent.Approve a:
            {
                if (!IsActor(a.Actor)) return Same(code: KbCode.ApiAccessDenied);
                var i = Index(a.Fingerprint);
                if (i < 0 || list[i].State != IdentityState.Pending) return Same(code: KbCode.ServerIdentityNotPending);
                if (list[i].IsExpired(now)) return Same(code: KbCode.ServerIdentityApprovalExpired);
                if (a.Replace)
                {
                    for (var k = 0; k < list.Count; k++)
                        if (k != i && list[k].State == IdentityState.Pinned)
                            list[k] = list[k] with { State = IdentityState.Retired, DecidedBy = a.Actor, DecidedAt = now };
                }
                else if (list.Count(x => x.IsEffectivePin) >= MaxPins)
                {
                    return Same(code: KbCode.ServerIdentityPinLimit);
                }
                list[i] = list[i] with { State = IdentityState.Pinned, DecidedBy = a.Actor, DecidedAt = now };
                return Done(ServerIdentityNotice.Approved);
            }

            case ServerIdentityEvent.Reject j:
            {
                if (!IsActor(j.Actor)) return Same(code: KbCode.ApiAccessDenied);
                var i = Index(j.Fingerprint);
                if (i < 0 || list[i].State != IdentityState.Pending) return Same(code: KbCode.ServerIdentityNotPending);
                list[i] = list[i] with { State = IdentityState.Rejected, DecidedBy = j.Actor, DecidedAt = now }; // także wygasłe — odrzucenie zawsze dozwolone
                return Done(ServerIdentityNotice.Rejected);
            }

            case ServerIdentityEvent.SeedFromDefinition s:
            {
                if (!IsFingerprint(s.Fingerprint) || !IsKind(s.Kind)) return Same(ServerIdentityNotice.Ignored);
                if (list.Any(x => x.IsEffectivePin)) return Same(); // pin z katalogu ma pierwszeństwo — zmiana klucza tylko przez Administratora
                var i = Index(s.Fingerprint);
                if (i >= 0 && list[i].State == IdentityState.Rejected) return Same(); // odrzucony przez Administratora — definicja go nie przywraca
                if (i < 0)
                {
                    if (list.Count >= MaxRows) return Same(ServerIdentityNotice.Ignored);
                    list.Add(new ServerIdentityRow(s.Fingerprint, s.Kind, IdentityState.Pinned, Clip(s.Endpoint, MaxEndpointLength), null, now, now, DefinitionActor, now));
                }
                else
                {
                    list[i] = list[i] with { State = IdentityState.Pinned, DecidedBy = DefinitionActor, DecidedAt = now };
                }
                return Done(ServerIdentityNotice.Seeded);
            }

            case ServerIdentityEvent.TrustedByCa t:
            {
                if (!IsFingerprint(t.Fingerprint)) return Same(ServerIdentityNotice.Ignored);
                var i = Index(t.Fingerprint);
                if (i >= 0)
                {
                    var r = list[i];
                    if (r.State == IdentityState.Rejected || r.IsEffectivePin) return Same(); // odrzucenie nie blokuje CA (od tego jest „wysokie ryzyko”), ale zostaje zapisane
                    if (r.State == IdentityState.Pinned)
                    {
                        list[i] = r with { LastSeen = now };
                        return Done(ServerIdentityNotice.None);
                    }
                    list[i] = r with { State = IdentityState.Pinned, LastSeen = now, DecidedBy = CaChainActor, DecidedAt = now, Display = CleanDisplay(t.Display) ?? r.Display };
                }
                else
                {
                    if (list.Count >= MaxRows) return Same(ServerIdentityNotice.Ignored);
                    list.Add(new ServerIdentityRow(t.Fingerprint, TlsSpki, IdentityState.Pinned, Clip(t.Endpoint, MaxEndpointLength), CleanDisplay(t.Display), now, now, CaChainActor, now));
                }
                // Zbiór informacyjny ograniczony: najstarsze zapisy „z CA” wycofywane (rotacja certyfikatów co 90 dni nie rośnie bez końca).
                var ca = list.Select((x, k) => (x, k)).Where(p => p.x.State == IdentityState.Pinned && !p.x.IsEffectivePin)
                    .OrderBy(p => p.x.DecidedAt).ThenBy(p => p.x.Fingerprint, StringComparer.Ordinal).ToList();
                foreach (var (x, k) in ca.Take(Math.Max(0, ca.Count - MaxCaRecords)))
                    list[k] = x with { State = IdentityState.Retired, DecidedAt = now };
                return Done(ServerIdentityNotice.AutoApprovedByCa);
            }

            default:
                return Same(ServerIdentityNotice.Ignored);
        }
    }

    /// <summary>Piny przekazywane do dostawcy (najnowsze zatwierdzenia najpierw, maks. <see cref="MaxPins"/>).</summary>
    public static IReadOnlyList<string> EffectivePins(IEnumerable<ServerIdentityRow> rows) =>
        [.. rows.Where(r => r.IsEffectivePin).OrderByDescending(r => r.DecidedAt).ThenBy(r => r.Fingerprint, StringComparer.Ordinal).Take(MaxPins).Select(r => r.Fingerprint)];

    internal static bool IsKind(string? kind) => kind is SshHostKey or TlsSpki;

    internal static bool IsActor(string? actor) => actor is { Length: > 0 and <= MaxActorLength } && !actor.Any(char.IsControl);

    /// <summary>Opis z serwera (niezaufany): bez znaków sterujących, ≤ 200 znaków.</summary>
    internal static string? CleanDisplay(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var clean = new string([.. s.Where(c => !char.IsControl(c))]);
        return Clip(clean, MaxDisplayLength);
    }

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..max];

    private static readonly System.Buffers.SearchValues<char> Base64NoPad =
        System.Buffers.SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/");
}
