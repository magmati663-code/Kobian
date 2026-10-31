using Grpc.Core;
using Kobian.Domain;

namespace Kobian.Agent.Hosting;

/// <summary>Wynik kontroli folderu docelowego odtwarzania tokenem klienta (SEC-6.3).</summary>
public enum RestoreTargetAccess
{
    Allowed,
    Denied,
}

/// <summary>
/// Port SEC-6.3 / AC-07.2 / R2.8.1 „AuthorizeTargets” (it. 101): czy KLIENT (nie konto usługi) ma prawo dodawać pliki i podkatalogi w folderze docelowym.
/// Adapter Windows: <c>NamedPipeRestoreTargetAuthorizer</c> (AccessCheck z tokenem klienta pipe, poziom Identification). Konsola/Linux:
/// <see cref="SameUserRestoreTargets"/> — pipe CurrentUserOnly, klient = użytkownik procesu, więc system plików sam egzekwuje jego prawa.
/// Kontrakt: każdy błąd adaptera = <see cref="RestoreTargetAccess.Denied"/> (fail-closed).
/// </summary>
public interface IRestoreTargetAuthorizer
{
    RestoreTargetAccess Check(ServerCallContext context, string target);

    /// <summary>
    /// Lokalny folder docelowy repozytorium (może jeszcze nie istnieć): czy KLIENT mógłby go utworzyć/zapisać. Domyślnie bez kontroli (Windows: konto usługi ma
    /// własne, wąskie prawa; konsola: ten sam użytkownik). Usługa Linux/macOS działa jako root — tam Operator nie może kazać zapisać repozytorium np. w /etc.
    /// </summary>
    RestoreTargetAccess CheckDestination(ServerCallContext context, string root) => RestoreTargetAccess.Allowed;

    /// <summary>Plik czytany przez Agenta w imieniu klienta (plik hasła w InitJob): czy klient sam mógłby go przeczytać. Domyślnie bez kontroli (jak wyżej).</summary>
    RestoreTargetAccess CheckReadableFile(ServerCallContext context, string path) => RestoreTargetAccess.Allowed;
}

/// <summary>Tryb konsoli (dev): proces działa na koncie klienta — brak dodatkowej kontroli.</summary>
public sealed class SameUserRestoreTargets : IRestoreTargetAuthorizer
{
    public static SameUserRestoreTargets Instance { get; } = new();

    public RestoreTargetAccess Check(ServerCallContext context, string target) => RestoreTargetAccess.Allowed;
}

/// <summary>
/// Bramka w adapterze gRPC (Restore, PlanRestore): Administrator odtwarza dokąd chce (R2.6.1 SCR-07 „Operator/Admin”, plan: Admin bez AccessCheck);
/// Operator — tylko do folderu, do którego ma prawo zapisu (AccessCheck tokenem klienta) → inaczej KB-5001 PRZED odczytem danych.
/// Ścieżka niebezwzględna/pusta → KB-5001 bez wywołania adaptera.
/// </summary>
public static class RestoreTargetGate
{
    /// <summary>Ta sama zasada dla ścieżek innych niż folder odtwarzania (cel repozytorium, plik hasła): Administrator bez kontroli, Operator — wynik adaptera, błąd = odmowa.</summary>
    public static string? DenyPath(AgentRole role, string? path, Func<string, RestoreTargetAccess> check, string deniedCode)
    {
        ArgumentNullException.ThrowIfNull(check);
        if (role >= AgentRole.Administrator) return null;
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) return null; // kształt ścieżki waliduje backend (KB-0103/KB-2106)
        RestoreTargetAccess access;
        try
        {
            access = check(path);
        }
#pragma warning disable CA1031 // fail-closed
        catch (Exception)
#pragma warning restore CA1031
        {
            access = RestoreTargetAccess.Denied;
        }
        return access == RestoreTargetAccess.Allowed ? null : deniedCode;
    }

    public static string? Deny(AgentRole role, string? target, Func<string, RestoreTargetAccess> check)
    {
        ArgumentNullException.ThrowIfNull(check);
        if (role >= AgentRole.Administrator) return null;
        if (string.IsNullOrWhiteSpace(target) || !Path.IsPathFullyQualified(target)) return KbCode.RestoreTargetAccessDenied;
        RestoreTargetAccess access;
        try
        {
            access = check(target);
        }
#pragma warning disable CA1031 // fail-closed: dowolny błąd kontroli = odmowa (bez szczegółów dla klienta)
        catch (Exception)
#pragma warning restore CA1031
        {
            access = RestoreTargetAccess.Denied;
        }
        return access == RestoreTargetAccess.Allowed ? null : KbCode.RestoreTargetAccessDenied;
    }
}
