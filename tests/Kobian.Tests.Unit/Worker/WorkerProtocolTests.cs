using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;
using Kobian.Application.Worker;
using Kobian.Domain;
using Xunit;

namespace Kobian.Tests.Unit.Worker;

/// <summary>
/// it. 103 — D2.8/SEC-2.5: protokół usługa ↔ Kobian.Worker (rola vss): ramki u32le + JSON ≤ 1 MiB, ścisła walidacja w obie strony,
/// migawka zwalniana przy vss.release, EOF i naruszeniu protokołu; Agent nie ufa odpowiedziom procesu potomnego. VSS — atrapa (SP-01: W-44).
/// </summary>
public sealed class WorkerProtocolTests
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    private sealed class FakeSnapshot(IReadOnlyDictionary<string, string> devices) : IVssSnapshot
    {
        public bool Disposed { get; private set; }

        public Guid SnapshotSetId { get; } = Guid.NewGuid();

        public IReadOnlyDictionary<string, string> Devices { get; } = devices;

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeVss(bool fail = false) : IVssSnapshotService
    {
        public List<FakeSnapshot> Created { get; } = [];

        public Task<IVssSnapshot> CreateAsync(IReadOnlyList<string> volumes, CancellationToken ct)
        {
            if (fail) throw new VssException(KbCode.VssSnapshotFailed, "writer failed");
            var s = new FakeSnapshot(volumes.Select((v, i) => (v, $@"\\?\GLOBALROOT\Device\HarddiskVolumeShadowCopy{i + 1}\")).ToDictionary(x => x.v, x => x.Item2));
            Created.Add(s);
            return Task.FromResult<IVssSnapshot>(s);
        }
    }

    /// <summary>Dwa anonimowe pipe'y jak stdin/stdout procesu potomnego (w jednym procesie testu).</summary>
    private static (Stream AgentOut, Stream WorkerIn, Stream WorkerOut, Stream AgentIn, IDisposable All) Pipes()
    {
        var toWorker = new AnonymousPipeServerStream(PipeDirection.Out);
        var workerIn = new AnonymousPipeClientStream(PipeDirection.In, toWorker.ClientSafePipeHandle);
        var fromWorker = new AnonymousPipeServerStream(PipeDirection.In);
        var workerOut = new AnonymousPipeClientStream(PipeDirection.Out, fromWorker.ClientSafePipeHandle);
        return (toWorker, workerIn, workerOut, fromWorker, new Disposer(toWorker, workerIn, fromWorker, workerOut));
    }

    private sealed class Disposer(params IDisposable[] items) : IDisposable
    {
        public void Dispose()
        {
            foreach (var i in items) i.Dispose();
        }
    }

    private static byte[] Frame(byte[] body)
    {
        var f = new byte[4 + body.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(f, (uint)body.Length);
        body.CopyTo(f, 4);
        return f;
    }

    [Fact, Trait("Plan", "D2.8"), Trait("SEC", "SEC-3.3")]
    public async Task Frames_roundtrip_and_reject_bad_lengths_and_truncation()
    {
        var ms = new MemoryStream();
        await WorkerFrames.WriteAsync(ms, "{\"a\":1}"u8.ToArray(), _ct);
        ms.Position = 0;
        Assert.Equal("{\"a\":1}"u8.ToArray(), await WorkerFrames.ReadAsync(ms, _ct));
        Assert.Null(await WorkerFrames.ReadAsync(ms, _ct)); // czysty EOF
        await Assert.ThrowsAsync<WorkerProtocolException>(() => WorkerFrames.ReadAsync(new MemoryStream(new byte[] { 0, 0, 0, 0 }), _ct));
        var big = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(big, WorkerFrames.MaxFrame + 1);
        await Assert.ThrowsAsync<WorkerProtocolException>(() => WorkerFrames.ReadAsync(new MemoryStream(big), _ct));
        await Assert.ThrowsAsync<WorkerProtocolException>(() => WorkerFrames.ReadAsync(new MemoryStream(new byte[] { 5, 0 }), _ct));
        await Assert.ThrowsAsync<WorkerProtocolException>(() => WorkerFrames.ReadAsync(new MemoryStream(Frame("abcde"u8.ToArray())[..6]), _ct));
        await Assert.ThrowsAsync<WorkerProtocolException>(() => WorkerFrames.WriteAsync(Stream.Null, new byte[WorkerFrames.MaxFrame + 1], _ct));
        await Assert.ThrowsAsync<WorkerProtocolException>(() => WorkerFrames.WriteAsync(Stream.Null, Array.Empty<byte>(), _ct));
    }

    [Theory, Trait("Plan", "D2.8"), Trait("SEC", "SEC-2.5")]
    [InlineData("""{"id":1,"op":"rm -rf"}""")]
    [InlineData("""{"id":0,"op":"ping"}""")]
    [InlineData("""{"id":1,"op":"ping","extra":true}""")]
    [InlineData("""{"id":1,"op":"ping","volumes":["C:\\"]}""")]
    [InlineData("""{"id":1,"op":"vss.create"}""")]
    [InlineData("""{"id":1,"op":"vss.create","volumes":["\\\\srv\\share\\"]}""")]
    [InlineData("""{"id":1,"op":"vss.create","volumes":["C:"]}""")]
    [InlineData("""{"id":1,"op":"vss.create","volumes":["C:\\","c:\\"]}""")]
    [InlineData("""{"id":1,"op":"vss.create","volumes":[["C:\\"]]}""")]
    [InlineData("""[1,2,3]""")]
    [InlineData("""{"id":1,""")]
    public void Requests_are_validated_strictly(string json) =>
        Assert.Throws<WorkerProtocolException>(() => WorkerJson.Validate(WorkerJson.Read<WorkerRequest>(Encoding.UTF8.GetBytes(json))));

    [Fact, Trait("Plan", "D2.8"), Trait("SEC", "SEC-2.5")]
    public void Agent_does_not_trust_worker_responses()
    {
        Assert.Throws<WorkerProtocolException>(() => WorkerJson.Validate(new WorkerResponse(2, true), 1)); // inne Id
        Assert.Throws<WorkerProtocolException>(() => WorkerJson.Validate(new WorkerResponse(1, false, "KB-9999"), 1)); // kod spoza rejestru
        Assert.Throws<WorkerProtocolException>(() => WorkerJson.Validate(new WorkerResponse(1, false), 1));
        Assert.Throws<WorkerProtocolException>(() => WorkerJson.Validate(new WorkerResponse(1, true, Devices: new Dictionary<string, string> { ["C:\\"] = @"C:\Windows" }), 1));
        Assert.Throws<WorkerProtocolException>(() => WorkerJson.Validate(new WorkerResponse(1, true, Devices: new Dictionary<string, string> { ["C:\\"] = @"\\?\GLOBALROOT\Device\..\x" }), 1));
        Assert.Throws<WorkerProtocolException>(() => WorkerJson.Validate(new WorkerResponse(1, true, SnapshotSetId: "nie-guid"), 1));
        Assert.Throws<WorkerProtocolException>(() => WorkerJson.Read<WorkerResponse>("""{"id":1,"ok":true,"devices":{"C:\\":{"x":1}}}"""u8.ToArray()));
        Assert.Equal(KbCode.VssSnapshotFailed, WorkerJson.Validate(new WorkerResponse(1, false, KbCode.VssSnapshotFailed), 1).Code);
        Assert.True(KbCode.IsKnown(KbCode.WorkerProtocolViolation));
        Assert.False(KbCode.IsKnown("KB-0000"));
        Assert.False(KbCode.IsKnown(null));
    }

    [Fact, Trait("Plan", "D2.8"), Trait("Category", "Integration")]
    public async Task Snapshot_lives_until_release_and_only_one_per_process()
    {
        var (agentOut, workerIn, workerOut, agentIn, all) = Pipes();
        using var _ = all;
        var vss = new FakeVss();
        var host = Task.Run(() => new VssWorkerHost(vss).RunAsync(workerIn, workerOut, _ct), _ct);
        var client = new WorkerClient(agentOut, agentIn, TimeSpan.FromSeconds(10));
        Assert.True((await client.CallAsync(WorkerOps.Ping, null, _ct)).Ok);
        var created = await client.CallAsync(WorkerOps.VssCreate, ["C:\\", "D:\\"], _ct);
        Assert.True(created.Ok);
        Assert.Equal(@"\\?\GLOBALROOT\Device\HarddiskVolumeShadowCopy2\", created.Devices!["D:\\"]);
        Assert.Equal(vss.Created[0].SnapshotSetId.ToString("D"), created.SnapshotSetId);
        var second = await client.CallAsync(WorkerOps.VssCreate, ["E:\\"], _ct);
        Assert.Equal((false, KbCode.VssSnapshotFailed), (second.Ok, second.Code));
        Assert.False(vss.Created[0].Disposed);
        Assert.True((await client.CallAsync(WorkerOps.VssRelease, null, _ct)).Ok);
        Assert.Equal(VssWorkerHost.ExitOk, await host);
        Assert.True(vss.Created[0].Disposed);
    }

    [Fact, Trait("Plan", "D2.8"), Trait("Category", "Integration")]
    public async Task Parent_closing_the_pipe_releases_the_snapshot_and_vss_errors_come_back_as_kb()
    {
        var (agentOut, workerIn, workerOut, agentIn, all) = Pipes();
        using var _ = all;
        var vss = new FakeVss();
        var host = Task.Run(() => new VssWorkerHost(vss).RunAsync(workerIn, workerOut, _ct), _ct);
        var client = new WorkerClient(agentOut, agentIn, TimeSpan.FromSeconds(10));
        Assert.True((await client.CallAsync(WorkerOps.VssCreate, ["C:\\"], _ct)).Ok);
        agentOut.Dispose(); // Agent zginął / zamknął stdin Workera
        Assert.Equal(VssWorkerHost.ExitOk, await host);
        Assert.True(vss.Created[0].Disposed);

        var (o2, i2, w2, a2, all2) = Pipes();
        using var __ = all2;
        var failing = Task.Run(() => new VssWorkerHost(new FakeVss(fail: true)).RunAsync(i2, w2, _ct), _ct);
        var c2 = new WorkerClient(o2, a2, TimeSpan.FromSeconds(10));
        var r = await c2.CallAsync(WorkerOps.VssCreate, ["C:\\"], _ct);
        Assert.Equal((false, KbCode.VssSnapshotFailed), (r.Ok, r.Code));
        await c2.CallAsync(WorkerOps.VssRelease, null, _ct);
        Assert.Equal(VssWorkerHost.ExitOk, await failing);
    }

    [Fact, Trait("Plan", "D2.8"), Trait("SEC", "SEC-2.5"), Trait("Category", "Integration")]
    public async Task Garbage_from_either_side_ends_the_conversation()
    {
        // Worker dostaje śmieci → kod 3, migawka zwolniona
        var (agentOut, workerIn, workerOut, agentIn, all) = Pipes();
        using var _ = all;
        var vss = new FakeVss();
        var host = Task.Run(() => new VssWorkerHost(vss).RunAsync(workerIn, workerOut, _ct), _ct);
        var client = new WorkerClient(agentOut, agentIn, TimeSpan.FromSeconds(10));
        Assert.True((await client.CallAsync(WorkerOps.VssCreate, ["C:\\"], _ct)).Ok);
        await agentOut.WriteAsync(Frame("""{"id":9,"op":"format c:"}"""u8.ToArray()), _ct);
        await agentOut.FlushAsync(_ct);
        Assert.Equal(VssWorkerHost.ExitProtocol, await host);
        Assert.True(vss.Created[0].Disposed);

        // Agent dostaje śmieci (ramka ~2 GiB) od „Workera” → wyjątek i klient nieużywalny
        var (o2, wIn2, wOut2, a2, all2) = Pipes();
        using var __ = all2;
        var c2 = new WorkerClient(o2, a2, TimeSpan.FromSeconds(10));
        var call = c2.CallAsync(WorkerOps.Ping, null, _ct);
        Assert.NotNull(await WorkerFrames.ReadAsync(wIn2, _ct)); // fałszywy Worker odebrał żądanie
        await wOut2.WriteAsync(new byte[] { 0xFF, 0xFF, 0xFF, 0x7F }, _ct);
        await wOut2.FlushAsync(_ct);
        await Assert.ThrowsAsync<WorkerProtocolException>(() => call);
        await Assert.ThrowsAsync<WorkerProtocolException>(() => c2.CallAsync(WorkerOps.Ping, null, _ct)); // klient nieużywalny
    }

    [Fact, Trait("Plan", "D2.8"), Trait("SEC", "SEC-3.3")]
    public async Task Random_input_never_crashes_the_worker_loop()
    {
        var rnd = new Random(1031);
        for (var i = 0; i < 2000; i++)
        {
            var body = new byte[rnd.Next(1, 200)];
            rnd.NextBytes(body);
            if (i % 3 == 0) body = Encoding.UTF8.GetBytes($"{{\"id\":{rnd.Next(-2, 5)},\"op\":\"{(i % 2 == 0 ? "vss.create" : "ping")}\",\"volumes\":[\"{(char)rnd.Next(32, 127)}:\\\\\"]}}");
            var input = new MemoryStream(i % 7 == 0 ? body : Frame(body));
            var vss = new FakeVss();
            var code = await new VssWorkerHost(vss).RunAsync(input, new MemoryStream(), _ct);
            Assert.True(code is VssWorkerHost.ExitOk or VssWorkerHost.ExitProtocol);
            Assert.All(vss.Created, s => Assert.True(s.Disposed));
        }
    }
}
