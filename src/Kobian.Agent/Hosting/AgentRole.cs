namespace Kobian.Agent.Hosting;

/// <summary>Rola wywołującego (D2.9). Kolejność = rosnące uprawnienia.</summary>
public enum AgentRole
{
    None = 0,
    Reader = 1,
    Operator = 2,
    Administrator = 3,
}

/// <summary>Ustala rolę klienta named pipe. Produkcja (usługa Windows): <see cref="TokenCallerResolver"/> — SID i grupy z tokenu klienta (RunAsClient, BL-17; SP-02/W-17).</summary>
public interface ICallerRoleResolver
{
    AgentRole Resolve(Grpc.Core.ServerCallContext context);

    /// <summary>Rola + SID do audytu (BL-17). Domyślnie: rola z <see cref="Resolve"/>, aktor „local-pipe” (tryb deweloperski, CurrentUserOnly).</summary>
    CallerInfo ResolveCaller(Grpc.Core.ServerCallContext context) => new(Resolve(context), AgentCallAudit.LocalActor);
}

/// <summary>Stała rola — dla testów i trybu deweloperskiego. Dostęp do pipe jest wtedy jedyną bramką (CurrentUserOnly).</summary>
public sealed class FixedRoleResolver(AgentRole role) : ICallerRoleResolver
{
    public AgentRole Resolve(Grpc.Core.ServerCallContext context) => role;
}
