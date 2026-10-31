using Kobian.Agent.Hosting;
using Kobian.Application.Worker;
using Kobian.Domain;
using Xunit;

namespace Kobian.Tests.Unit.Worker;

/// <summary>
/// it. 106 — launcher procesu Kobian.Worker (D2.8, SEC-2.5/2.7, NFR-19; RD-65): kontrole przed startem (rola, ścieżka, dowiązanie, podpis — KB-7015/7016),
/// prawdziwy proces potomny w Linuksie (`dotnet Kobian.Worker.dll --role vss`) przez protokół (dziś KB-1406 — SP-01), Worker umierający bez odpowiedzi.
/// Job object i Authenticode — adaptery Windows (W-45).
/// </summary>
public sealed class WorkerLauncherTests : IDisposable
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kobian-wl-" + Guid.NewGuid().ToString("N"));

    public WorkerLauncherTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { /* katalog tymczasowy testu */ }
    }

    private sealed class Verifier(bool trusted) : ISignatureVerifier
    {
        public List<string> Checked { get; } = [];

        public bool IsTrusted(string path)
        {
            Checked.Add(path);
            return trusted;
        }
    }

    private static string? WorkerDll()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Kobian.sln"))) dir = dir.Parent;
        if (dir is null) return null;
        var bin = Path.Combine(dir.FullName, "src", "Kobian.Worker", "bin", "Release");
        return Directory.Exists(bin) ? Directory.GetFiles(bin, "Kobian.Worker.dll", SearchOption.AllDirectories).FirstOrDefault() : null;
    }

    [Fact, Trait("Plan", "D2.8"), Trait("SEC", "SEC-2.7")]
    public void Start_is_refused_before_any_process_for_bad_role_path_link_or_signature()
    {
        var file = Path.Combine(_dir, "Kobian.Worker.exe");
        File.WriteAllText(file, "nie-program");
        WorkerStartException Fail(WorkerCommand c, ISignatureVerifier v, string role = "vss") =>
            Assert.Throws<WorkerStartException>(() => new WorkerLauncher(c, v).Start(role));

        Assert.Equal(KbCode.WorkerStartFailed, Fail(new(file, [], file), new Verifier(true), "powershell").Code);
        Assert.Equal(KbCode.WorkerStartFailed, Fail(new("Kobian.Worker.exe", [], "Kobian.Worker.exe"), new Verifier(true)).Code);
        Assert.Equal(KbCode.WorkerStartFailed, Fail(new(Path.Combine(_dir, "brak.exe"), [], Path.Combine(_dir, "brak.exe")), new Verifier(true)).Code);
        var untrusted = new Verifier(false);
        Assert.Equal(KbCode.WorkerSignatureInvalid, Fail(new(file, [], file), untrusted).Code);
        Assert.Equal([file], untrusted.Checked);
        if (!OperatingSystem.IsWindows())
        {
            var link = Path.Combine(_dir, "link.exe");
            File.CreateSymbolicLink(link, file);
            var v = new Verifier(true);
            Assert.Equal(KbCode.WorkerSignatureInvalid, Fail(new(link, [], link), v).Code);
            Assert.Empty(v.Checked); // dowiązanie odrzucone przed weryfikacją podpisu
        }
        Assert.DoesNotContain(WorkerLauncher.AllowedEnvironment, n => n.StartsWith("KOBIAN", StringComparison.OrdinalIgnoreCase) || n is "PATH" or "USERPROFILE" or "APPDATA");
        Assert.True(NoSignatureCheck.Instance.IsTrusted(file));
    }

    [Fact, Trait("Plan", "D2.8"), Trait("Category", "Integration")]
    public async Task Real_worker_process_answers_over_the_protocol_and_is_gone_afterwards()
    {
        if (OperatingSystem.IsWindows()) return; // na Windows: W-45 z exe i obiektem zadania
        var dll = WorkerDll();
        if (dll is null || Environment.ProcessPath is not { } dotnet || !Path.GetFileNameWithoutExtension(dotnet).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            TestContext.Current.SendDiagnosticMessage($"pominięte: dll={dll} proces={Environment.ProcessPath}");
            return; // brak zbudowanego Workera (ci.sh buduje całą solucję przed testami)
        }
        var verifier = new Verifier(true);
        var launcher = new WorkerLauncher(new WorkerCommand(dotnet, [dll], dll), verifier);
        var svc = new WorkerVssSnapshotService(launcher, TimeSpan.FromSeconds(30));
        var ex = await Assert.ThrowsAsync<VssException>(() => svc.CreateAsync(["C:\\"], _ct));
        Assert.Equal(KbCode.VssSnapshotFailed, ex.Code); // zaślepka do SP-01 — ale przeszła przez prawdziwy proces i protokół
        Assert.Equal([dll], verifier.Checked);

        await using var w = launcher.Start("vss");
        var client = new WorkerClient(w.Input, w.Output, TimeSpan.FromSeconds(30));
        Assert.True((await client.CallAsync(WorkerOps.Ping, null, _ct)).Ok);
        Assert.True((await client.CallAsync(WorkerOps.VssRelease, null, _ct)).Ok);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await w.WaitForExitAsync(cts.Token);
        Assert.Equal(0, w.ExitCode);
    }

    [Fact, Trait("Plan", "D2.8"), Trait("SEC", "SEC-2.5"), Trait("Category", "Integration")]
    public async Task Worker_that_dies_or_talks_nonsense_gives_a_protocol_error_not_a_hang()
    {
        if (OperatingSystem.IsWindows() || !File.Exists("/bin/cat")) return;
        // „Worker” = cat: nieznana opcja --role → kończy się bez odpowiedzi
        var launcher = new WorkerLauncher(new WorkerCommand("/bin/cat", [], "/bin/cat"), NoSignatureCheck.Instance);
        var svc = new WorkerVssSnapshotService(launcher, TimeSpan.FromSeconds(20));
        await Assert.ThrowsAsync<WorkerProtocolException>(() => svc.CreateAsync(["C:\\"], _ct));
    }
}
