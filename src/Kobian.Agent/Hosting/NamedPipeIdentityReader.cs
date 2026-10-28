#if KOBIAN_WINDOWS
using System.IO.Pipes;
using System.Runtime.CompilerServices;
using System.Security.Principal;
using Grpc.Core;
using Microsoft.AspNetCore.Connections.Features;

namespace Kobian.Agent.Hosting;

/// <summary>
/// Adapter Windows portu <see cref="IPipeClientIdentityReader"/> (BL-17, D2.9, SP-02): <c>NamedPipeServerStream</c> z funkcji połączenia Kestrel,
/// <c>RunAsClient</c> → token klienta (klient łączy się z poziomem Identification — Kobian.Cli/UI), SID + grupy włączone.
/// Cache per połączenie: <see cref="ConditionalWeakTable{TKey,TValue}"/> po obiekcie strumienia — nowe połączenie = nowy obiekt = nowy odczyt;
/// wpis znika razem z połączeniem (brak ręcznego unieważniania). Token procesu zamiast klienta (ImpersonationLevel = None) → fail-closed.
/// Test: W-17 (4 typy kont), W-37 (Anonymous, administrator bez podniesienia).
/// </summary>
public sealed class NamedPipeIdentityReader : IPipeClientIdentityReader
{
    private readonly ConditionalWeakTable<NamedPipeServerStream, CallerIdentity> _cache = new();

    public CallerIdentity? Read(Grpc.Core.ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var pipe = context.GetHttpContext().Features.Get<IConnectionNamedPipeFeature>()?.NamedPipe;
        return pipe is null ? null : _cache.GetValue(pipe, Capture);
    }

    private static CallerIdentity Capture(NamedPipeServerStream pipe)
    {
        CallerIdentity? result = null;
        pipe.RunAsClient(() =>
        {
            // Wątek impersonuje klienta; TokenAccessLevels.Query wystarcza do SID i grup (bez SeImpersonatePrivilege — D2.12, Q44).
            using var id = WindowsIdentity.GetCurrent(System.Security.Principal.TokenAccessLevels.Query);
            var groups = new HashSet<string>(StringComparer.Ordinal);
            foreach (var g in id.Groups ?? [])
                if (g is SecurityIdentifier sid) groups.Add(sid.Value); // .NET pomija grupy deny-only i SID logowania
            result = new CallerIdentity(id.User?.Value ?? string.Empty, groups, id.ImpersonationLevel switch
            {
                TokenImpersonationLevel.Anonymous => CallerTokenLevel.Anonymous,
                TokenImpersonationLevel.Identification => CallerTokenLevel.Identification,
                TokenImpersonationLevel.Impersonation => CallerTokenLevel.Impersonation,
                TokenImpersonationLevel.Delegation => CallerTokenLevel.Delegation,
                _ => CallerTokenLevel.None,
            });
        });
        return result ?? new CallerIdentity(string.Empty, new HashSet<string>(), CallerTokenLevel.None);
    }
}
#endif
