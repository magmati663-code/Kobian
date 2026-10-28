using Grpc.Core;
using Grpc.Core.Interceptors;
using Kobian.Agent.Audit;
using Kobian.Api.Contracts;
using Kobian.Domain;

namespace Kobian.Agent.Hosting;

/// <summary>
/// Autoryzacja każdej metody (D2.9: metoda spoza mapy = odmowa, brak roli = KB-7011) i kontrola rewizji klienta
/// dla metod modyfikujących (D2.16, AC-37.2: KB-7006). Kody KB w trailerze „kb-code”; komunikaty bez danych klienta.
/// Audyt (RD-42, D9.9): każda odmowa i każda metoda modyfikująca → wpis łańcucha (<see cref="IAuditSink"/>); wynik „failed” z kodem KB z odpowiedzi.
/// </summary>
public sealed class AgentCallInterceptor(ICallerRoleResolver roles, int minModifyingRevision, IAuditSink audit) : Interceptor
{
    public override async Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
        TRequest request, ServerCallContext context, UnaryServerMethod<TRequest, TResponse> continuation)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(continuation);
        var (caller, policy) = await AuthorizeAsync(request, context).ConfigureAwait(false);
        if (!policy.Modifying && !policy.Audited) return await continuation(request, context).ConfigureAwait(false);
        TResponse response;
        try
        {
            response = await continuation(request, context).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await AuditAsync(context, caller, request, "failed", KbCode.Internal).ConfigureAwait(false);
            throw;
        }
        var (failed, code) = Outcome(response);
        await AuditAsync(context, caller, request, failed ? "failed" : "allowed", code).ConfigureAwait(false);
        return response;
    }

    /// <summary>
    /// it. 134: metody strumieniowe serwera przechodzą TĘ SAMĄ bramkę co unarne (rola, uwierzytelnienie, odmowy w audycie). Bez tego handlera
    /// domyślny <see cref="Interceptor"/> przepuściłby strumień bez kontroli ról. Dozwolone tylko metody niemodyfikujące (strumień nie ma pojedynczej odpowiedzi do audytu).
    /// </summary>
    public override async Task ServerStreamingServerHandler<TRequest, TResponse>(
        TRequest request, IServerStreamWriter<TResponse> responseStream, ServerCallContext context, ServerStreamingServerMethod<TRequest, TResponse> continuation)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(continuation);
        var (caller, policy) = await AuthorizeAsync(request, context).ConfigureAwait(false);
        if (policy.Modifying)
        {
            // Strumień modyfikujący nie ma w tej wersji wsparcia audytu wyniku → odmowa (D2.9: co nieobsłużone, jest zabronione).
            await AuditAsync(context, caller, request, "denied", KbCode.ApiAccessDenied).ConfigureAwait(false);
            throw Reject(StatusCode.PermissionDenied, KbCode.ApiAccessDenied);
        }
        if (policy.Audited) await AuditAsync(context, caller, request, "allowed", null).ConfigureAwait(false);
        await continuation(request, responseStream, context).ConfigureAwait(false);
    }

    /// <summary>Kontrakt nie zawiera metod ze strumieniem klienta ani dwukierunkowych; gdyby je dodano, bez własnej bramki zostałyby przepuszczone → odmowa (D2.9).</summary>
    public override Task<TResponse> ClientStreamingServerHandler<TRequest, TResponse>(
        IAsyncStreamReader<TRequest> requestStream, ServerCallContext context, ClientStreamingServerMethod<TRequest, TResponse> continuation) =>
        throw Reject(StatusCode.PermissionDenied, KbCode.ApiAccessDenied);

    public override Task DuplexStreamingServerHandler<TRequest, TResponse>(
        IAsyncStreamReader<TRequest> requestStream, IServerStreamWriter<TResponse> responseStream, ServerCallContext context, DuplexStreamingServerMethod<TRequest, TResponse> continuation) =>
        throw Reject(StatusCode.PermissionDenied, KbCode.ApiAccessDenied);

    /// <summary>Wspólna bramka: uwierzytelnienie → polityka metody i rola → rewizja klienta (metody modyfikujące). Odmowa = wyjątek z kodem KB i wpis audytu.</summary>
    private async Task<(CallerInfo Caller, MethodPolicy.Entry Policy)> AuthorizeAsync(object? request, ServerCallContext context)
    {
        var policy = MethodPolicy.Find(context.Method);
        var caller = roles.ResolveCaller(context);
        if (!caller.Authenticated)
        {
            // BL-17 (D2.9): brak tokenu klienta / Anonymous / błąd odczytu → Unauthenticated + KB-7001 (bez szczegółów dla klienta).
            await AuditAsync(context, caller, request, "denied", KbCode.CallerUnauthenticated).ConfigureAwait(false);
            throw Reject(StatusCode.Unauthenticated, KbCode.CallerUnauthenticated);
        }
        if (policy is null || caller.Role < policy.Required)
        {
            await AuditAsync(context, caller, request, "denied", KbCode.ApiAccessDenied).ConfigureAwait(false);
            throw Reject(StatusCode.PermissionDenied, KbCode.ApiAccessDenied);
        }
        if (policy.Modifying && ClientRevision(context) < minModifyingRevision)
        {
            await AuditAsync(context, caller, request, "denied", KbCode.ClientRevisionTooOld).ConfigureAwait(false);
            throw Reject(StatusCode.FailedPrecondition, KbCode.ClientRevisionTooOld);
        }
        return (caller, policy);
    }

    /// <summary>Wynik z odpowiedzi kontraktu: pole <c>result</c>/<c>status</c> = FAILED albo lista problemów pól → „failed”; kod KB z pola <c>code</c>.</summary>
    internal static (bool Failed, string? Code) Outcome(object? response)
    {
        if (response is not Google.Protobuf.IMessage m) return (false, null);
        var d = m.Descriptor;
        string? code = d.FindFieldByName("code") is { } cf && cf.Accessor.GetValue(m) is string s && s.Length > 0 ? s : null;
        var failed = d.FindFieldByName("result") is { } rf && rf.Accessor.GetValue(m) is Kobian.Api.V1.OverallState st && st == Kobian.Api.V1.OverallState.Failed;
        if (d.FindFieldByName("status") is { } sf && sf.Accessor.GetValue(m) is Kobian.Api.V1.ProbeStatus ps && ps == Kobian.Api.V1.ProbeStatus.Failed) failed = true;
        if (d.FindFieldByName("issues") is { } issues && issues.Accessor.GetValue(m) is System.Collections.IList { Count: > 0 }) failed = true;
        return (failed, code);
    }

    private Task AuditAsync(ServerCallContext context, CallerInfo caller, object? request, string outcome, string? code)
    {
        var (target, args) = AgentCallAudit.Describe(request);
        return audit.AppendAsync(new AuditRecord(caller.ActorSid, caller.Role.ToString(), "api", AgentCallAudit.Operation(context.Method), target, args, outcome, code),
            CancellationToken.None);
    }

    internal static int ClientRevision(ServerCallContext context)
    {
        var value = context.RequestHeaders.GetValue(ApiConventions.ClientRevisionHeader);
        return int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var r) ? r : 0;
    }

    internal static RpcException Reject(StatusCode status, string kbCode) =>
        new(new Status(status, kbCode), new Metadata { { ApiConventions.KbCodeTrailer, kbCode } });
}
