using System.Net.Sockets;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Grpc.Core;
using Grpc.Net.Client;
using Kobian.Agent.Hosting;
using Kobian.Agent.Jobs;
using Kobian.Api.Contracts;
using Kobian.Api.V1;
using Kobian.Application.Storage;
using Kobian.Catalog.Sqlite;
using Kobian.Providers.Storage.Local;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Kobian.Tests.Unit.Security;

/// <summary>
/// RD-44 / RD-31 (it. 86, rola 11, SEC-3.3): fuzzing API Agenta na poziomie bajtów wiadomości protobuf — każda metoda z MethodPolicy,
/// losowe i zmutowane ładunki przez prawdziwy host (Kestrel + gRPC, gniazdo Unix). Rola Reader: metody modyfikujące są odrzucane PO deserializacji
/// (sprawdzamy parser bez skutków ubocznych na dysku), metody odczytu wykonują się na pustym katalogu. Kryteria: tylko oczekiwane statusy gRPC,
/// szczegóły statusu bez typów wyjątków i ścieżek, host żyje po całej serii, ładunek &gt; 64 KiB odrzucony.
/// </summary>
public sealed class GrpcFuzzTests : IAsyncLifetime
{
    private const int PerMethod = 120;
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kobian-gfuzz-" + Guid.NewGuid().ToString("N"));
    private readonly string _pipe = "kobian-fuzz-" + Guid.NewGuid().ToString("N")[..12];
    private WebApplication? _app;
    private CatalogWriter _writer = null!;

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        var catalog = Path.Combine(_dir, "catalog.db");
        CatalogBootstrap.Bootstrap(catalog, TimeProvider.System);
        _writer = new CatalogWriter(catalog);
        if (OperatingSystem.IsWindows()) return; // gniazdo Unix — na Windows ta sama logika przez pipe w kolejce W (W-32)
        var providers = new Dictionary<string, IStorageProviderFactory> { ["local"] = new LocalStorageProviderFactory() };
        var backend = new CatalogAgentBackend(catalog, "fuzz", restore: new CatalogRestoreService(catalog, _writer, providers, TimeProvider.System),
            schedules: new ScheduleService(catalog, _writer));
        _app = AgentHost.Create(new AgentHostOptions(_pipe), backend, new FixedRoleResolver(AgentRole.Reader));
        await _app.StartAsync(_ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null) await _app.DisposeAsync();
        await _writer.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { /* katalog tymczasowy testu */ }
    }

    private GrpcChannel Channel()
    {
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (_, ct) =>
            {
                var s = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                await s.ConnectAsync(new UnixDomainSocketEndPoint(ApiConventions.UnixSocketPath(_pipe)), ct);
                return new NetworkStream(s, ownsSocket: true);
            },
        };
        return GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions { HttpHandler = handler, DisposeHttpClient = true });
    }

    private static readonly string[] Strings =
    [
        "", "latest", "../../etc", "a/b/../c", "C:\\Windows\\System32", "\\\\srv\\share", "x\u0000y", "ąę ¶ 😀", "0000000000000000", "ffffffff",
        "KB-0001", "*", "?", "/", "srv01\\SQLEXPRESS", "np:srv", "localhost,1433", new string('a', 300), "0 21 * * *", "file:x",
    ];

    /// <summary>Poprawny strukturalnie komunikat typu wejścia metody z losowymi wartościami pól (dociera za deserializację do interceptora/backendu).</summary>
    private static byte[] ValidMessage(MessageDescriptor d, Random rnd, int depth = 0)
    {
        var msg = (IMessage)Activator.CreateInstance(d.ClrType)!;
        foreach (var f in d.Fields.InFieldNumberOrder())
        {
            if (rnd.Next(3) == 0) continue;
            object? Value() => f.FieldType switch
            {
                FieldType.String => rnd.Next(4) == 0 ? new string((char)rnd.Next(32, 0x2FFF), rnd.Next(0, 40)) : Strings[rnd.Next(Strings.Length)],
                FieldType.Bool => rnd.Next(2) == 0,
                FieldType.Int32 or FieldType.SInt32 or FieldType.SFixed32 => rnd.Next(4) == 0 ? int.MinValue + rnd.Next(3) : rnd.Next(-5, 100_000),
                FieldType.Int64 or FieldType.SInt64 or FieldType.SFixed64 => rnd.Next(4) == 0 ? long.MaxValue - rnd.Next(3) : (long)rnd.Next(-5, int.MaxValue),
                FieldType.UInt32 or FieldType.Fixed32 => (uint)rnd.Next(),
                FieldType.UInt64 or FieldType.Fixed64 => (ulong)rnd.Next(),
                FieldType.Double => rnd.NextDouble() * 1e6,
                FieldType.Bytes => ByteString.CopyFrom(RandomBytes(rnd, rnd.Next(0, 40))),
                FieldType.Enum => rnd.Next(-1, 6),
                FieldType.Message when depth < 2 => f.MessageType.Parser.ParseFrom(ValidMessage(f.MessageType, rnd, depth + 1)),
                _ => null,
            };
            if (f.IsMap) continue;
            if (f.IsRepeated)
            {
                var list = (System.Collections.IList)f.Accessor.GetValue(msg);
                for (var k = rnd.Next(0, 4); k > 0; k--) if (Value() is { } v) list.Add(v);
            }
            else if (Value() is { } v)
            {
                f.Accessor.SetValue(msg, v);
            }
        }
        return msg.ToByteArray();
    }

    private static byte[] RandomBytes(Random rnd, int n)
    {
        var b = new byte[n];
        rnd.NextBytes(b);
        return b;
    }

    private static readonly Marshaller<byte[]> Raw = Marshallers.Create(b => b, b => b);

    private static Method<byte[], byte[]> M(string full)
    {
        var name = full[(full.LastIndexOf('/') + 1)..];
        return new Method<byte[], byte[]>(MethodType.Unary, "kobian.v1.Agent", name, Raw, Raw);
    }

    private static byte[] Payload(Random rnd)
    {
        var len = rnd.Next(0, 300);
        var b = new byte[len];
        rnd.NextBytes(b);
        if (len > 2 && rnd.Next(3) == 0)
        {
            // kształt protobuf: znaczniki pól 1–8, typy długość/varint, długości przesadzone
            for (var i = 0; i + 1 < len; i += rnd.Next(2, 12))
            {
                b[i] = (byte)((rnd.Next(1, 9) << 3) | (rnd.Next(2) == 0 ? 2 : 0));
                b[i + 1] = (byte)rnd.Next(0, 255);
            }
        }
        return b;
    }

    private static readonly Dictionary<string, MessageDescriptor> Inputs =
        Kobian.Api.V1.Agent.Descriptor.Methods.ToDictionary(x => x.Name, x => x.InputType, StringComparer.Ordinal);

    private const string GenericHandlerError = "Exception was thrown by handler.";

    private static readonly HashSet<StatusCode> Allowed =
        [StatusCode.OK, StatusCode.PermissionDenied, StatusCode.FailedPrecondition, StatusCode.InvalidArgument, StatusCode.Internal, StatusCode.ResourceExhausted, StatusCode.Unimplemented];

    [Fact, Trait("SEC", "SEC-3.3"), Trait("RD", "RD-44"), Trait("Category", "Integration")]
    public async Task Every_method_survives_random_protobuf_payloads()
    {
        if (OperatingSystem.IsWindows()) return;
        using var channel = Channel();
        var invoker = channel.CreateCallInvoker();
        var headers = new Metadata { { ApiConventions.ClientRevisionHeader, ApiConventions.CurrentRevision.ToString(System.Globalization.CultureInfo.InvariantCulture) } };
        var rnd = new Random(int.TryParse(Environment.GetEnvironmentVariable("KOBIAN_FUZZ_SEED"), out var seed) ? seed : 8601); // inne ziarno: KOBIAN_FUZZ_SEED (przegląd rundy)
        var seen = new Dictionary<StatusCode, int>();
        foreach (var method in MethodPolicy.Methods.Order(StringComparer.Ordinal))
        {
            var m = M(method);
            for (var i = 0; i < PerMethod; i++)
            {
                var status = StatusCode.OK;
                string detail = "";
                var valid = false;
                byte[] payload = [];
                try
                {
                    valid = i % 2 == 0;
                    payload = valid ? ValidMessage(Inputs[m.Name], rnd) : Payload(rnd);
                    _ = await invoker.AsyncUnaryCall(m, null, new CallOptions(headers, DateTime.UtcNow.AddSeconds(10), _ct), payload).ResponseAsync;
                }
                catch (RpcException ex)
                {
                    status = ex.StatusCode;
                    detail = ex.Status.Detail;
                }
                // Niepoprawny protobuf: grpc-dotnet odrzuca go PRZED interceptorem statusem Unknown z ogólnym tekstem (EnableDetailedErrors = false) —
                // akceptowane (bez wycieku, bez awarii); mapowanie na InvalidArgument + KB wymaga własnego marshallera (RD-47).
                // Poprawny strukturalnie komunikat NIE może skończyć się nieobsłużonym wyjątkiem w backendzie.
                var generic = !valid && status == StatusCode.Unknown && detail == GenericHandlerError;
                Assert.True(Allowed.Contains(status) || generic, $"{method}: {status} {detail} payload={Convert.ToBase64String(payload)}");
                if (!generic) Assert.DoesNotContain("Exception", detail, StringComparison.Ordinal);
                Assert.DoesNotContain(_dir, detail, StringComparison.Ordinal);
                seen[status] = seen.GetValueOrDefault(status) + 1;
            }
        }
        Assert.True(seen.Count >= 2, "fuzzer nie dał różnych wyników");
        TestContext.Current.SendDiagnosticMessage("grpc fuzz: " + string.Join(", ", seen.OrderBy(kv => kv.Key).Select(kv => kv.Key + "=" + kv.Value)));
        // Host żyje: poprawne wywołanie po całej serii.
        var ok = await new Kobian.Api.V1.Agent.AgentClient(channel).GetStatusAsync(new GetStatusRequest(), headers, cancellationToken: _ct);
        Assert.Equal("fuzz", ok.AgentVersion);
    }

    [Fact, Trait("SEC", "SEC-3.3"), Trait("SEC", "D2.9")]
    public async Task Oversized_message_is_rejected_before_reaching_the_backend()
    {
        if (OperatingSystem.IsWindows()) return;
        using var channel = Channel();
        var big = new byte[70 * 1024];
        big[0] = (1 << 3) | 2; // pole 1, długość
        var e = await Assert.ThrowsAsync<RpcException>(() => channel.CreateCallInvoker()
            .AsyncUnaryCall(M("/kobian.v1.Agent/BrowseTree"), null, new CallOptions(cancellationToken: _ct), big).ResponseAsync);
        Assert.Equal(StatusCode.ResourceExhausted, e.StatusCode);
    }

    /// <summary>Regresja z fuzzingu (it. 86): ogromne liczby w RetentionSpec przepełniały TimeSpan w walidacji → nieobsłużony wyjątek (status Unknown).</summary>
    [Fact, Trait("SEC", "SEC-3.3"), Trait("RD", "RD-44")]
    public async Task Huge_retention_values_are_rejected_not_thrown()
    {
        var p = new Kobian.Application.Jobs.RetentionPolicy(0, int.MaxValue, int.MaxValue, int.MaxValue, int.MaxValue);
        Assert.Equal(TimeSpan.MaxValue, p.ShortestTimeRetention());
        Assert.Empty(Kobian.Application.Jobs.JobValidator.ValidateRetention(p));
        var args = PreviewRetentionArgs.Parser.ParseFrom(Convert.FromBase64String("CiII/YoBEIGAgID4/////wEYxNwEIIKAgID4/////wEo/MgDEgZucDpzcnY="));
        var reply = await new CatalogAgentBackend(Path.Combine(_dir, "catalog.db"), "t").PreviewRetentionAsync(args, _ct);
        Assert.Equal((OverallState.Failed, Kobian.Domain.KbCode.RetentionInvalid), (reply.Result, reply.Code));
    }
}
