using Grpc.Core;
using Kobian.Api.Contracts;
using Kobian.Api.V1;
using Kobian.Domain;

namespace Kobian.UI.Services;

/// <summary>
/// Wspólne tłumaczenie wyjątków adaptera gRPC (<see cref="GrpcDashboardSource"/>: IOException = brak połączenia, InvalidOperationException z RpcException = odmowa/błąd usługi)
/// na kod KB dla ekranów. Bez tego komenda <c>[RelayCommand]</c> z nieobsłużonym wyjątkiem kończy cały proces UI (AsyncRelayCommand przekazuje go do kontekstu synchronizacji),
/// a każdy InvalidOperationException był pokazywany jako „KB-7011 za niska rola” — także Internal, klient zbyt stary czy błąd serwera.
/// </summary>
public static class UiFailure
{
    public const string Offline = "KB-9001";

    /// <summary>Wyjątki, które adapter zgłasza dla awarii usługi/transportu (reszta to błędy programistyczne i mają wychodzić).</summary>
    public static bool IsServiceFailure(Exception ex) => ex is IOException or TimeoutException or InvalidOperationException { InnerException: RpcException };

    public static string CodeOf(Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        if (ex is not InvalidOperationException { InnerException: RpcException rpc }) return ex is InvalidOperationException ? KbCode.Internal : Offline;
        var served = rpc.Trailers.GetValue(ApiConventions.KbCodeTrailer);
        return rpc.StatusCode switch
        {
            StatusCode.PermissionDenied => KbCode.ApiAccessDenied,
            StatusCode.Unauthenticated => KbCode.CallerUnauthenticated,
            StatusCode.FailedPrecondition when served == KbCode.ClientRevisionTooOld => KbCode.ClientRevisionTooOld,
            StatusCode.Unimplemented => KbCode.OperationNotAvailable,
            StatusCode.Unavailable or StatusCode.DeadlineExceeded or StatusCode.Cancelled => Offline,
            _ => KbCode.Internal,
        };
    }
}
