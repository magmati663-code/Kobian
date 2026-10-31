using Kobian.Application.Install;

namespace Kobian.Agent.Hosting;

/// <summary>
/// Plan custom action MSI (it. 100; D2.12, 2.6, D10.2; PROWIZORYCZNIE Qb50): kroki wykonywane przez <c>Kobian.Agent.exe --provision</c>
/// (deferred, Impersonate=no — LocalSystem) po InstallServices, a przy odinstalowaniu <c>--unprovision</c> (best effort).
/// Jedno źródło prawdy z <see cref="AgentServiceProfile"/> i <see cref="AgentPipeAcl"/> — test pilnuje zgodności z installer/Package.wxs.
/// Odinstalowanie NIE usuwa danych (%ProgramData%\Kobian: katalog, klucze, sekrety) ani grup (mogą mieć członków) — D10.2 „Ryzyka”.
/// </summary>
public static class AgentInstallPlan
{
    /// <summary>SID-y wbudowane (niezależne od języka systemu).</summary>
    public const string LocalSystemSid = "S-1-5-18";
    public const string AdministratorsSid = "S-1-5-32-544";

    public const string DataDirName = "Kobian";

    /// <summary>Podkatalogi tworzone z góry (dziedziczą ACL katalogu danych): sekrety (FileSmb/FileSftpCredentialProvider), klucze, certyfikaty SQL, staging.</summary>
    public static IReadOnlyList<string> DataSubdirectories { get; } = ["secrets", "keys", "certs", "staging"];

    /// <summary>ACL katalogu danych (2.6): SYSTEM, Administrators, konto usługi — pełny; „Kobian Operators”/„Kobian Admins” — brak (tylko przez API).</summary>
    public static IReadOnlyList<AclEntry> DataDirAcl { get; } =
    [
        new(LocalSystemSid, DirectoryRight.FullControl),
        new(AdministratorsSid, DirectoryRight.FullControl),
        new(AgentServiceProfile.Account, DirectoryRight.FullControl),
    ];

    public static IReadOnlyList<InstallStep> Install(string programData)
    {
        var data = DataDir(programData);
        var steps = new List<InstallStep>
        {
            new EnsureLocalGroup("group-operators", AgentPipeAcl.OperatorsGroupName, "Kobian Backup: uruchamianie kopii i odtwarzanie (rola Operator)."),
            new EnsureLocalGroup("group-admins", AgentPipeAcl.AdminsGroupName, "Kobian Backup: administracja bez pełnych uprawnień systemu (rola Administrator)."),
            new EnsureDirectoryAcl("data-dir", data, DataDirAcl, Protect: true),
        };
        steps.AddRange(DataSubdirectories.Select(d => new EnsureDirectoryAcl("data-" + d, data + "\\" + d, DataDirAcl, Protect: true)));
        steps.Add(new SetRegistryDword("vss-access", VssKey, AgentServiceProfile.Account, AgentServiceProfile.VssAccessControlValue));
        steps.Add(new GrantAccountRights("lsa-rights", AgentServiceProfile.Account, AgentServiceProfile.LsaRights));
        return steps;
    }

    public static IReadOnlyList<InstallStep> Uninstall() =>
    [
        new RemoveRegistryValue("vss-access", VssKey, AgentServiceProfile.Account),
        new RevokeAccountRights("lsa-rights", AgentServiceProfile.Account, AgentServiceProfile.LsaRights),
    ];

    /// <summary>Klucz rejestru VSS bez prefiksu „HKLM\” (adapter otwiera LocalMachine).</summary>
    public static string VssKey => AgentServiceProfile.VssAccessControlKey["HKLM\\".Length..];

    /// <summary>%ProgramData%\Kobian — wyłącznie lokalna ścieżka bezwzględna (bez UNC/urządzeń), jak <see cref="AgentPaths"/>.</summary>
    public static string DataDir(string programData)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(programData);
        if (!AgentServiceProfile.IsWindowsAbsolute(programData) || programData.AsSpan().IndexOfAny('\0', '\r', '\n') >= 0)
            throw new ArgumentException("ProgramData must be an absolute local Windows path.", nameof(programData));
        return programData.TrimEnd('\\', '/') + "\\" + DataDirName;
    }

    /// <summary>Tryb wiersza poleceń Agenta dla custom action: „--provision”/„--unprovision”, inaczej null (zwykły start).</summary>
    public static string? Mode(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        return args.Count == 1 && args[0] is ProvisionArg or UnprovisionArg ? args[0] : null;
    }

    public const string ProvisionArg = "--provision";
    public const string UnprovisionArg = "--unprovision";
}

/// <summary>Wykonanie trybu custom action: plan → adapter Windows; poza Windows kod 64 (konfiguracja) z komunikatem. Kod 0 = wszystkie kroki OK, 1 = błąd kroku.</summary>
public static class AgentInstallCommand
{
    public const int StepFailed = 1;

    public static int Run(string mode, TextWriter log, IInstallProvisioner? provisioner = null, string? programData = null)
    {
        ArgumentNullException.ThrowIfNull(log);
#if KOBIAN_WINDOWS
        if (provisioner is null && OperatingSystem.IsWindows()) provisioner = new Kobian.Platform.Windows.Install.WindowsInstallProvisioner();
#endif
        if (provisioner is null)
        {
            log.WriteLine("install mode is supported only on Windows");
            return AgentExitCodes.Configuration;
        }
        var install = mode == AgentInstallPlan.ProvisionArg;
        IReadOnlyList<InstallStep> steps;
        if (install)
        {
            var pd = programData ?? Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            try
            {
                steps = AgentInstallPlan.Install(pd);
            }
            catch (ArgumentException)
            {
                log.WriteLine("ProgramData is not a local absolute path");
                return AgentExitCodes.Configuration;
            }
        }
        else
        {
            steps = AgentInstallPlan.Uninstall();
        }
        // Instalacja: pierwszy błąd przerywa (MSI Return="check" wycofuje). Odinstalowanie: best effort, zawsze 0 (nie blokuje usunięcia produktu).
        var report = InstallProvisioning.Run(steps, provisioner, stopOnError: install, line => log.WriteLine(line));
        return report.Ok || !install ? AgentExitCodes.Ok : StepFailed;
    }
}
