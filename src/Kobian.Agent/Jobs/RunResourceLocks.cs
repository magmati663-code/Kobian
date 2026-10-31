using Kobian.Providers.Storage.Smb;
using Kobian.Scheduler.Locks;

namespace Kobian.Agent.Jobs;

/// <summary>
/// Zasoby kolejki uruchomień (D7.4/D7.10, BL-73 cz. 3, it. 94) wyliczane z definicji zadania: cel „smb” → blokada <c>smb:&lt;serwer&gt;</c> z kontem,
/// dzięki czemu dwa zadania do jednego serwera z różnymi kontami czekają na siebie w kolejce zamiast kończyć się błędem 1219 (KB-2101, scenariusz E6).
/// <para>
/// PROWIZORYCZNIE Qb44 (ADR-0027): „kontem” jest odwołanie <c>secretRef</c>, a nie nazwa użytkownika — scheduler nie czyta sekretów. Skutki:
/// dwa różne wpisy z tym samym kontem są niepotrzebnie serializowane (bezpieczne); jeden wpis z różnymi kontami backup/maintenance (D4.1)
/// w dwóch równoległych uruchomieniach może nadal trafić na 1219 w fazie retencji — wtedy błąd KB-2101 jak dotąd (RD-55: konto z magazynu sekretów).
/// Nazwa serwera porównywana bez wielkości liter (klucz w małych literach), jak w <see cref="SharedSmbConnector"/> i w Windows.
/// </para>
/// Pozostałe zasoby D7.4 (repo, źródło, global) — global realizuje limit równoległości <see cref="LockManager"/>; reszta w kolejnych iteracjach.
/// </summary>
public static class RunResourceLocks
{
    public const string SmbPrefix = "smb:";

    /// <summary>D7.4: zasób repozytorium (cel zadania) — współdzielony dla kopii, wyłączny dla weryfikacji V3 z harmonogramu (BL-26).</summary>
    public const string RepoPrefix = "repo:";

    /// <summary>Klucz zasobu repozytorium = stabilny identyfikator celu (<see cref="JobDefinition.DestinationId"/>, bez ścieżek i hostów w jawnej postaci).</summary>
    public static string RepoKey(JobDefinition def)
    {
        ArgumentNullException.ThrowIfNull(def);
        return RepoPrefix + def.DestinationId;
    }

    /// <summary>
    /// Zasoby kopii (harmonogram i RunJob): <see cref="For(string?, byte[]?)"/> + <c>repo:&lt;cel&gt;</c> współdzielona — kopie tego samego repozytorium idą równolegle jak dotąd,
    /// ale czekają na trwającą weryfikację V3 (wyłączna) i odwrotnie (D7.4: „współdzielona dla backupu, wyłączna dla check”).
    /// </summary>
    public static IReadOnlyList<LockRequest> ForBackup(string? jobType, byte[]? definition)
    {
        var def = TryParse(jobType, definition);
        return def is null ? [] : [.. For(def), new LockRequest(RepoKey(def), LockMode.Shared)];
    }

    /// <summary>Zasoby weryfikacji V3 z harmonogramu: jak kopia (smb z kontem), ale <c>repo:&lt;cel&gt;</c> wyłączna — nigdy równolegle z kopią na tym repozytorium.</summary>
    public static IReadOnlyList<LockRequest> ForVerify(JobDefinition def) => [.. For(def), new LockRequest(RepoKey(def), LockMode.Exclusive)];

    private static JobDefinition? TryParse(string? jobType, byte[]? definition)
    {
        if (jobType is null || definition is null) return null;
        try
        {
            return JobDefinition.Parse(jobType, definition);
        }
        catch (Exception ex) when (ex is Kobian.Domain.KobianException or Kobian.Application.Storage.StorageException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>Blokady dla zadania; niepoprawna/nieznana definicja → brak blokad (uruchomienie i tak zakończy się kodem KB z walidacji definicji).</summary>
    public static IReadOnlyList<LockRequest> For(string? jobType, byte[]? definition) => TryParse(jobType, definition) is { } def ? For(def) : [];

    public static IReadOnlyList<LockRequest> For(JobDefinition def)
    {
        ArgumentNullException.ThrowIfNull(def);
        if (!string.Equals(def.DestinationScheme, "smb", StringComparison.Ordinal)) return [];
        Kobian.Application.Storage.SmbShare share;
        try
        {
            share = UncPath.Parse(def.DestinationRoot);
        }
        catch (Kobian.Application.Storage.StorageException)
        {
            return [];
        }
        // Brak secretRef jest odrzucany przy zapisie definicji (KB-2102); gdyby jednak wystąpił — pusty „klucz konta” jest niezgodny z każdym innym.
        return [new LockRequest(SmbPrefix + share.Server.ToLowerInvariant(), LockMode.Shared, def.Remote?.SecretRef ?? "")];
    }
}
