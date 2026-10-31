using System.Text.RegularExpressions;

namespace Kobian.Agent.Hosting;

public enum PipeAceKind
{
    Deny,
    Allow,
}

[Flags]
public enum PipeAceRights
{
    None = 0,
    ReadWrite = 1,
    CreateNewInstance = 2,
    FullControl = 4,
}

/// <summary>Wpis ACL pipe Agenta (SID jako tekst — budowany z SecurityIdentifier, nie z nazw lokalizowanych).</summary>
public sealed record PipeAce(PipeAceKind Kind, string Sid, PipeAceRights Rights, string Label);

/// <summary>
/// ACL named pipe Agenta w trybie usługi (D2.9 + D2.15 pkt 1, BL-72; pełne role z tokenu klienta = BL-17/SP-02).
/// Kolejność: Deny NETWORK (S-1-5-2) przed Allow; konto usługi: ReadWrite + CreateNewInstance; SYSTEM, Administrators, „Kobian Operators”:
/// wyłącznie ReadWrite (nigdy CreateNewInstance dla klientów — wektor podszycia). Users/Everyone — brak wpisu (domyślna odmowa).
/// Rola wywołującego w usłudze: TokenCallerResolver (BL-17) — SID i grupy z tokenu klienta, mapa AgentRoleMap (D2.9).
/// Tryb konsoli (dev) zostaje przy CurrentUserOnly (fail-closed). Adapter PipeSecurity: tylko net10.0-windows; test: W-34.
/// </summary>
public static partial class AgentPipeAcl
{
    public const string NetworkSid = "S-1-5-2";
    public const string SystemSid = "S-1-5-18";
    public const string AdministratorsSid = "S-1-5-32-544";
    public const string OperatorsGroupName = "Kobian Operators";

    /// <summary>Grupa ról Administrator Kobian (D2.9). PROWIZORYCZNIE Qb45: dopuszczona w ACL (tylko ReadWrite), inaczej jej członek spoza Administratorów nie połączy się.</summary>
    public const string AdminsGroupName = "Kobian Admins";

    [GeneratedRegex(@"^S-1-\d{1,13}(-\d{1,10}){1,15}$", RegexOptions.CultureInvariant)]
    private static partial Regex SidPattern();

    public static bool IsSid(string? s) => s is not null && SidPattern().IsMatch(s);

    /// <summary>Plan ACL. <paramref name="operatorsSid"/> null = grupa nie istnieje (instalator jej nie utworzył) — brak wpisu.</summary>
    public static IReadOnlyList<PipeAce> Plan(string serviceSid, string? operatorsSid, string? adminsSid = null)
    {
        if (!IsSid(serviceSid)) throw new ArgumentException("Service SID is not a valid SID.", nameof(serviceSid));
        if (operatorsSid is not null && !IsSid(operatorsSid)) throw new ArgumentException("Operators SID is not a valid SID.", nameof(operatorsSid));
        if (adminsSid is not null && !IsSid(adminsSid)) throw new ArgumentException("Admins SID is not a valid SID.", nameof(adminsSid));
        if (serviceSid is NetworkSid || operatorsSid is NetworkSid || adminsSid is NetworkSid) throw new ArgumentException("NETWORK cannot be granted access.");
        var list = new List<PipeAce>
        {
            new(PipeAceKind.Deny, NetworkSid, PipeAceRights.FullControl, "NT AUTHORITY\\NETWORK"),
            new(PipeAceKind.Allow, serviceSid, PipeAceRights.ReadWrite | PipeAceRights.CreateNewInstance, AgentServiceProfile.Account),
            new(PipeAceKind.Allow, SystemSid, PipeAceRights.ReadWrite, "NT AUTHORITY\\SYSTEM"),
            new(PipeAceKind.Allow, AdministratorsSid, PipeAceRights.ReadWrite, "BUILTIN\\Administrators"),
        };
        if (operatorsSid is not null && operatorsSid != serviceSid) list.Add(new(PipeAceKind.Allow, operatorsSid, PipeAceRights.ReadWrite, OperatorsGroupName));
        if (adminsSid is not null && adminsSid != serviceSid && adminsSid != operatorsSid) list.Add(new(PipeAceKind.Allow, adminsSid, PipeAceRights.ReadWrite, AdminsGroupName));
        return list;
    }

#if KOBIAN_WINDOWS
    /// <summary>ACL dla bieżącego procesu usługi: SID konta usługi z tokenu procesu, grupa operatorów po nazwie (grupa własna — nazwa nielokalizowana).</summary>
    public static System.IO.Pipes.PipeSecurity ForCurrentService()
    {
        using var me = System.Security.Principal.WindowsIdentity.GetCurrent();
        var service = me.User ?? throw new InvalidOperationException("Service token has no user SID.");
        return ToPipeSecurity(Plan(service.Value, TryGroupSid(OperatorsGroupName), TryGroupSid(AdminsGroupName)));
    }

    /// <summary>SID grupy lokalnej po nazwie własnej (nielokalizowanej); null = grupa nieutworzona przez instalator.</summary>
    public static string? TryGroupSid(string groupName)
    {
        try
        {
            return new System.Security.Principal.NTAccount(groupName).Translate(typeof(System.Security.Principal.SecurityIdentifier)).Value;
        }
        catch (System.Security.Principal.IdentityNotMappedException)
        {
            return null;
        }
    }

    public static System.IO.Pipes.PipeSecurity ToPipeSecurity(IReadOnlyList<PipeAce> plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var ps = new System.IO.Pipes.PipeSecurity();
        ps.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var ace in plan)
        {
            var rights = (System.IO.Pipes.PipeAccessRights)0;
            if (ace.Rights.HasFlag(PipeAceRights.FullControl)) rights |= System.IO.Pipes.PipeAccessRights.FullControl;
            if (ace.Rights.HasFlag(PipeAceRights.ReadWrite)) rights |= System.IO.Pipes.PipeAccessRights.ReadWrite;
            if (ace.Rights.HasFlag(PipeAceRights.CreateNewInstance)) rights |= System.IO.Pipes.PipeAccessRights.CreateNewInstance;
            var type = ace.Kind == PipeAceKind.Deny ? System.Security.AccessControl.AccessControlType.Deny : System.Security.AccessControl.AccessControlType.Allow;
            ps.AddAccessRule(new System.IO.Pipes.PipeAccessRule(new System.Security.Principal.SecurityIdentifier(ace.Sid), rights, type));
        }
        return ps;
    }
#endif
}
