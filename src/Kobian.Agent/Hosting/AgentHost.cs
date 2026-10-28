using Kobian.Api.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Kobian.Agent.Hosting;

public sealed record AgentHostOptions(string PipeName, int MinModifyingRevision = ApiConventions.CurrentRevision)
{
    /// <summary>BL-72: cykl życia usługi Windows (SCM: start/stop, ServiceName). Działa tylko w kompilacji net10.0-windows i tylko pod SCM; w konsoli/Linuksie bez efektu.</summary>
    public bool WindowsService { get; init; }

    /// <summary>SEC-6.3 (it. 101): kontrola folderu docelowego odtwarzania tokenem klienta; null = tryb konsoli (ten sam użytkownik).</summary>
    public IRestoreTargetAuthorizer? RestoreTargets { get; init; }
}

/// <summary>
/// Host gRPC Agenta na named pipe (D2.4, BL-14/BL-15). Tylko pipe — brak portu TCP. HTTP/2 bez TLS (transport lokalny).
/// CurrentUserOnly = true (fail-closed): rozszerzenie ACL pipe dla grup Kobian to BL-17 (kolejka Windows).
/// </summary>
public static class AgentHost
{
    public static WebApplication Create(AgentHostOptions options, IAgentBackend backend, ICallerRoleResolver roles, Kobian.Agent.Audit.IAuditSink? audit = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(roles);
        if (string.IsNullOrWhiteSpace(options.PipeName) || options.PipeName.AsSpan().IndexOfAny('/', '\\') >= 0)
            throw new ArgumentException("Pipe name must be a plain name.", nameof(options));

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
#if KOBIAN_WINDOWS
        if (options.WindowsService)
        {
            // Microsoft.Extensions.Hosting.WindowsServices: lifetime SCM + dostawca Event Log (tylko ostrzeżenia i błędy — bez szumu Kestrel; dane klienta nie trafiają do logów, NFR-11).
            Microsoft.Extensions.Hosting.WindowsServiceLifetimeHostBuilderExtensions.AddWindowsService(builder.Services, o => o.ServiceName = AgentServiceProfile.ServiceName);
            builder.Logging.SetMinimumLevel(LogLevel.Warning);
        }
#endif
        builder.WebHost.UseKestrel(k =>
        {
            if (OperatingSystem.IsWindows()) k.ListenNamedPipe(options.PipeName, l => l.Protocols = HttpProtocols.Http2);
            else k.ListenUnixSocket(ApiConventions.UnixSocketPath(options.PipeName), l => l.Protocols = HttpProtocols.Http2); // dev/testy, ADR-0004
            k.AddServerHeader = false;
        });
#if KOBIAN_WINDOWS
        if (options.WindowsService)
        {
            // BL-72: usługa (konto wirtualne) — ACL z AgentPipeAcl (Deny NETWORK, klienci bez CreateNewInstance; D2.9, D2.15). Test: W-34.
            var pipeSecurity = AgentPipeAcl.ForCurrentService();
            builder.WebHost.UseNamedPipes(p =>
            {
                p.CurrentUserOnly = false;
                p.PipeSecurity = pipeSecurity;
            });
        }
        else
#endif
        if (OperatingSystem.IsWindows()) builder.WebHost.UseNamedPipes(p => p.CurrentUserOnly = true);
        builder.Services.AddSingleton(backend);
        builder.Services.AddSingleton(roles);
        builder.Services.AddSingleton(options.RestoreTargets ?? SameUserRestoreTargets.Instance); // SEC-6.3 (it. 101): usługa — AccessCheck tokenem klienta
        builder.Services.AddSingleton(audit ?? Kobian.Agent.Audit.NullAuditSink.Instance); // RD-42: łańcuch audytu (Program.cs: AuditLogService)
        builder.Services.AddGrpc(g =>
        {
            g.Interceptors.Add<AgentCallInterceptor>(options.MinModifyingRevision);
            g.MaxReceiveMessageSize = 64 * 1024; // SEC-3.3: limity wejścia (D2.9)
            g.EnableDetailedErrors = false;
        });
        var app = builder.Build();
        if (!OperatingSystem.IsWindows())
        {
            var socket = ApiConventions.UnixSocketPath(options.PipeName);
            app.Lifetime.ApplicationStarted.Register(() =>
            {
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(socket, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            });
        }
        app.MapGrpcService<AgentGrpcService>();
        return app;
    }
}
