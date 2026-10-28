namespace Kobian.Application.Install;

/// <summary>Uprawnienie do katalogu w planie instalacji (lista zamknięta — bez „Modify” dla grup użytkowników, D2.6).</summary>
public enum DirectoryRight
{
    FullControl,
    ReadAndExecute,
}

/// <summary>
/// Wpis ACL: <see cref="Principal"/> = SID w postaci „S-1-…” (konta wbudowane — niezależne od języka systemu) albo nazwa konta
/// (np. „NT SERVICE\KobianAgent”, rozwiązywana przez adapter po utworzeniu usługi).
/// </summary>
public sealed record AclEntry(string Principal, DirectoryRight Right);

/// <summary>Krok instalacji/odinstalowania wykonywany przez custom action MSI (D2.12, SEC-10.7). <see cref="Id"/> — stabilny identyfikator do dziennika instalacji.</summary>
public abstract record InstallStep(string Id);

/// <summary>Grupa lokalna (istniejąca = sukces, bez zmiany członków).</summary>
public sealed record EnsureLocalGroup(string Id, string Name, string Description) : InstallStep(Id);

/// <summary>Katalog z jawnym ACL: <see cref="Protect"/> = bez dziedziczenia od rodzica; wpisy dziedziczone przez podkatalogi i pliki; właściciel = Administrators.</summary>
public sealed record EnsureDirectoryAcl(string Id, string Path, IReadOnlyList<AclEntry> Allow, bool Protect) : InstallStep(Id);

/// <summary>Wartość DWORD w HKLM (np. VssAccessControl — D2.12 pkt 3).</summary>
public sealed record SetRegistryDword(string Id, string Key, string ValueName, int Value) : InstallStep(Id);

/// <summary>Usunięcie wartości z HKLM (brak = sukces).</summary>
public sealed record RemoveRegistryValue(string Id, string Key, string ValueName) : InstallStep(Id);

/// <summary>Uprawnienia LSA konta (D2.12 pkt 2); posiadane = sukces.</summary>
public sealed record GrantAccountRights(string Id, string Account, IReadOnlyList<string> Rights) : InstallStep(Id);

/// <summary>Odebranie uprawnień LSA (brak konta/uprawnienia = sukces).</summary>
public sealed record RevokeAccountRights(string Id, string Account, IReadOnlyList<string> Rights) : InstallStep(Id);

/// <summary>Błąd kroku: kod Win32 (albo 0) i komunikat bez danych klienta.</summary>
public sealed class InstallStepException : Exception
{
    public InstallStepException(string message, int win32Error, Exception? inner = null)
        : base(message, inner) => Win32Error = win32Error;

    public InstallStepException()
        : this("Install step failed", 0)
    {
    }

    public InstallStepException(string message)
        : this(message, 0)
    {
    }

    public InstallStepException(string message, Exception inner)
        : this(message, 0, inner)
    {
    }

    public int Win32Error { get; }
}

/// <summary>Port wykonania kroków (adapter Windows: Kobian.Platform.Windows/Install). Każdy krok musi być idempotentny.</summary>
public interface IInstallProvisioner
{
    void Apply(InstallStep action);
}

public sealed record InstallStepResult(string Id, bool Ok, string? Error, int Win32Error);

/// <summary>Raport wykonania: kroki w kolejności; przerwanie na pierwszym błędzie (instalacja) albo kontynuacja (odinstalowanie — best effort).</summary>
public sealed record InstallReport(IReadOnlyList<InstallStepResult> Steps)
{
    public bool Ok => Steps.All(s => s.Ok);
}

/// <summary>Wykonawca planu (it. 100): kolejność z planu, zatrzymanie na błędzie gdy <paramref name="stopOnError"/> (MSI wycofa instalację — Return="check").</summary>
public static class InstallProvisioning
{
    public static InstallReport Run(IReadOnlyList<InstallStep> steps, IInstallProvisioner provisioner, bool stopOnError, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(provisioner);
        var results = new List<InstallStepResult>();
        foreach (var step in steps)
        {
            try
            {
                provisioner.Apply(step);
                results.Add(new InstallStepResult(step.Id, true, null, 0));
                log?.Invoke($"ok {step.Id}");
            }
            catch (InstallStepException ex)
            {
                results.Add(new InstallStepResult(step.Id, false, ex.Message, ex.Win32Error));
                log?.Invoke($"failed {step.Id} (win32 {ex.Win32Error}): {ex.Message}");
                if (stopOnError) break;
            }
        }
        return new InstallReport(results);
    }
}
