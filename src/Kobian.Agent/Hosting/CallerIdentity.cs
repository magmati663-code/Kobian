namespace Kobian.Agent.Hosting;

/// <summary>Poziom tokenu klienta pipe (D2.9: Anonymous = odmowa KB-7001; Identification wystarcza).</summary>
public enum CallerTokenLevel
{
    /// <summary>Brak impersonacji — odczytano token procesu usługi zamiast klienta (fail-closed: traktowane jak brak tożsamości).</summary>
    None = 0,
    Anonymous = 1,
    Identification = 2,
    Impersonation = 3,
    Delegation = 4,
}

/// <summary>
/// Tożsamość klienta pipe odczytana z tokenu (BL-17, D2.9). <paramref name="GroupSids"/> = wyłącznie grupy WŁĄCZONE
/// (bez deny-only — administrator bez podniesienia UAC nie ma tu S-1-5-32-544). Tylko SID-y, bez nazw (nazwy lokalizowane).
/// </summary>
public sealed record CallerIdentity(string Sid, IReadOnlySet<string> GroupSids, CallerTokenLevel Level);

/// <summary>Wynik rozpoznania wywołującego: rola i SID do audytu (<see cref="Audit.AuditRecord.ActorSid"/>).</summary>
public sealed record CallerInfo(AgentRole Role, string ActorSid, bool Authenticated = true)
{
    public static readonly CallerInfo Unauthenticated = new(AgentRole.None, "unauthenticated", Authenticated: false);
}

/// <summary>Port: odczyt tożsamości klienta połączenia. null = połączenie nie jest named pipe z tokenem klienta.</summary>
public interface IPipeClientIdentityReader
{
    CallerIdentity? Read(Grpc.Core.ServerCallContext context);
}

/// <summary>
/// Mapa token → rola (D2.9): SYSTEM, Administratorzy z tokenem pełnym (grupa włączona = podniesiony) albo „Kobian Admins” → Administrator;
/// „Kobian Operators” → Operator; pozostali dopuszczeni ACL → Reader (Viewer). Fail-closed: brak tokenu, Anonymous,
/// token procesu zamiast klienta, niepoprawny SID albo SID NETWORK w grupach (logowanie sieciowe — D2.15) → None.
/// SID-y grup własnych podaje <see cref="ForLocalMachine"/> (Windows) — null = grupa nieutworzona przez instalator.
/// </summary>
public sealed class AgentRoleMap(string? adminsGroupSid, string? operatorsGroupSid)
{
    public string? AdminsGroupSid { get; } = Check(adminsGroupSid);
    public string? OperatorsGroupSid { get; } = Check(operatorsGroupSid);

    public AgentRole Map(CallerIdentity? caller)
    {
        if (caller is null || caller.Level < CallerTokenLevel.Identification || !AgentPipeAcl.IsSid(caller.Sid)) return AgentRole.None;
        var groups = caller.GroupSids;
        if (caller.Sid == AgentPipeAcl.NetworkSid || groups.Contains(AgentPipeAcl.NetworkSid)) return AgentRole.None;
        if (caller.Sid == AgentPipeAcl.SystemSid || groups.Contains(AgentPipeAcl.AdministratorsSid) || Has(caller, groups, AdminsGroupSid))
            return AgentRole.Administrator;
        if (Has(caller, groups, OperatorsGroupSid)) return AgentRole.Operator;
        return AgentRole.Reader;
    }

    private static bool Has(CallerIdentity caller, IReadOnlySet<string> groups, string? sid) => sid is not null && (groups.Contains(sid) || caller.Sid == sid);

    private static string? Check(string? sid) =>
        sid is null || AgentPipeAcl.IsSid(sid) ? sid : throw new ArgumentException("Group SID is not a valid SID.", nameof(sid));

#if KOBIAN_WINDOWS
    /// <summary>SID-y grup „Kobian Admins” i „Kobian Operators” z lokalnej bazy kont (nazwy własne — nielokalizowane).</summary>
    public static AgentRoleMap ForLocalMachine() => new(AgentPipeAcl.TryGroupSid(AgentPipeAcl.AdminsGroupName), AgentPipeAcl.TryGroupSid(AgentPipeAcl.OperatorsGroupName));
#endif
}

/// <summary>
/// BL-17: rola z tokenu klienta pipe (zastępuje FixedRoleResolver w usłudze). Każdy wyjątek odczytu → brak tożsamości (KB-7001), nigdy rola domyślna.
/// Cache per połączenie robi adapter (<c>NamedPipeIdentityReader</c>), bo tylko on zna obiekt połączenia.
/// </summary>
public sealed class TokenCallerResolver(IPipeClientIdentityReader reader, AgentRoleMap map) : ICallerRoleResolver
{
    public AgentRole Resolve(Grpc.Core.ServerCallContext context) => ResolveCaller(context).Role;

    public CallerInfo ResolveCaller(Grpc.Core.ServerCallContext context)
    {
        CallerIdentity? id;
        try
        {
            id = reader.Read(context);
        }
#pragma warning disable CA1031 // fail-closed: dowolny błąd odczytu tokenu = brak tożsamości (odmowa), szczegóły nie trafiają do klienta
        catch (Exception)
#pragma warning restore CA1031
        {
            return CallerInfo.Unauthenticated;
        }
        var role = map.Map(id);
        return role == AgentRole.None || id is null ? CallerInfo.Unauthenticated : new CallerInfo(role, id.Sid);
    }
}
