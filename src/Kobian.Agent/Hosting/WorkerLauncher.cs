using System.ComponentModel;
using System.Diagnostics;
using Kobian.Application.Worker;
using Kobian.Domain;

namespace Kobian.Agent.Hosting;

/// <summary>
/// Launcher procesu Kobian.Worker (it. 106; D2.8, SEC-2.5, SEC-2.7, SEC-7.1, NFR-19; RD-65; PROWIZORYCZNIE Qb55):
/// ścieżka bezwzględna, nie dowiązanie; podpis zweryfikowany PRZED startem (KB-7015); bez powłoki (<see cref="ProcessStartInfo.ArgumentList"/>);
/// środowisko wyczyszczone do listy dozwolonej (bez KOBIAN_* — sekrety i ścieżki usługi nie przechodzą do Workera), PATH tylko katalogi systemowe;
/// przekierowane wyłącznie stdin/stdout; obiekt zadania (Windows) przypisany zaraz po starcie; odmowa przez politykę systemu (WDAC/AppLocker/ASR) → KB-7008.
/// Role: lista zamknięta.
/// </summary>
public sealed class WorkerLauncher(WorkerCommand command, ISignatureVerifier verifier, Func<IProcessJob?>? jobs = null)
{
    /// <summary>Role Workera dostępne w tej wersji.</summary>
    public static IReadOnlySet<string> Roles { get; } = new HashSet<string>(StringComparer.Ordinal) { "vss" };

    /// <summary>Zmienne środowiskowe przekazywane Workerowi (reszta — w tym KOBIAN_*, poświadczenia proxy — nie).</summary>
    public static IReadOnlyList<string> AllowedEnvironment { get; } = ["SystemRoot", "windir", "TEMP", "TMP", "DOTNET_ROOT", "HOME", "LANG"];

    /// <summary>Kody Win32 odmowy uruchomienia przez politykę: dostęp, wirus/ASR, zasady grupy (AppLocker/SRP), integralność (WDAC).</summary>
    public static IReadOnlySet<int> PolicyErrors { get; } = new HashSet<int> { 5, 225, 1260, 4551 };

    public IWorkerProcess Start(string role)
    {
        if (!Roles.Contains(role)) throw new WorkerStartException(KbCode.WorkerStartFailed, "Unknown worker role.");
        CheckFile(command.FileName);
        if (command.VerifyPath != command.FileName) CheckFile(command.VerifyPath);
        if (!verifier.IsTrusted(command.VerifyPath)) throw new WorkerStartException(KbCode.WorkerSignatureInvalid, "Worker file signature is not trusted.");
        var psi = new ProcessStartInfo(command.FileName)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(command.VerifyPath)!,
        };
        foreach (var a in command.PrefixArguments) psi.ArgumentList.Add(a);
        psi.ArgumentList.Add("--role");
        psi.ArgumentList.Add(role);
        psi.Environment.Clear();
        foreach (var name in AllowedEnvironment)
            if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } v) psi.Environment[name] = v;
        psi.Environment["PATH"] = SystemPath();
        var job = jobs?.Invoke();
        Process p;
        try
        {
            p = Process.Start(psi) ?? throw new WorkerStartException(KbCode.WorkerStartFailed, "Worker process did not start.");
        }
        catch (Win32Exception ex)
        {
            job?.Dispose();
            throw new WorkerStartException(PolicyErrors.Contains(ex.NativeErrorCode) ? KbCode.WorkerStartBlocked : KbCode.WorkerStartFailed, "Worker process could not be started.", ex);
        }
        try
        {
            job?.Assign(p); // okno między startem a przypisaniem: proces dopiero ładuje runtime (bez wejścia od nas) — PROWIZORYCZNIE Qb55
        }
#pragma warning disable CA1031 // brak obiektu zadania = brak gwarancji sprzątania → Worker nie może działać
        catch (Exception ex)
#pragma warning restore CA1031
        {
            try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            p.Dispose();
            job?.Dispose();
            throw new WorkerStartException(KbCode.WorkerStartFailed, "Worker could not be placed in a job object.", ex);
        }
        return new ProcessWorker(p, job);
    }

    private static void CheckFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) throw new WorkerStartException(KbCode.WorkerStartFailed, "Worker path must be absolute.");
        var fi = new FileInfo(path);
        if (!fi.Exists) throw new WorkerStartException(KbCode.WorkerStartFailed, "Worker file not found.");
        if (fi.LinkTarget is not null) throw new WorkerStartException(KbCode.WorkerSignatureInvalid, "Worker file is a link.");
    }

    private static string SystemPath()
    {
        if (OperatingSystem.IsWindows())
        {
            var root = Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows";
            return Path.Combine(root, "System32") + ";" + root;
        }
        return "/usr/bin:/bin";
    }

    private sealed class ProcessWorker(Process process, IProcessJob? job) : IWorkerProcess
    {
        public Stream Input => process.StandardInput.BaseStream;

        public Stream Output => process.StandardOutput.BaseStream;

        public bool HasExited => process.HasExited;

        public int? ExitCode => process.HasExited ? process.ExitCode : null;

        public Task WaitForExitAsync(CancellationToken ct) => process.WaitForExitAsync(ct);

        public void Kill()
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // już zakończony
            }
        }

        public async ValueTask DisposeAsync()
        {
            Kill();
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            process.Dispose();
            job?.Dispose(); // KILL_ON_JOB_CLOSE — ostatnia gwarancja
        }
    }
}

/// <summary>
/// Migawka VSS przez Workera (D2.8): start procesu → vss.create → urządzenia; zwolnienie = vss.release + koniec procesu (zawsze zabijany przy Dispose).
/// Błąd VSS z Workera → <see cref="VssException"/> z kodem KB (dziś KB-1406 — SP-01); naruszenie protokołu → <see cref="WorkerProtocolException"/> i proces zabity.
/// </summary>
public sealed class WorkerVssSnapshotService(WorkerLauncher launcher, TimeSpan? timeout = null) : IVssSnapshotService
{
    public async Task<IVssSnapshot> CreateAsync(IReadOnlyList<string> volumes, CancellationToken ct)
    {
        var worker = launcher.Start("vss");
        try
        {
            var client = new WorkerClient(worker.Input, worker.Output, timeout);
            var r = await client.CallAsync(WorkerOps.VssCreate, volumes, ct).ConfigureAwait(false);
            if (!r.Ok) throw new VssException(r.Code!, "Worker could not create the snapshot.");
            return new Snapshot(worker, client, Guid.Parse(r.SnapshotSetId ?? Guid.Empty.ToString()), r.Devices ?? new Dictionary<string, string>());
        }
        catch
        {
            await worker.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private sealed class Snapshot(IWorkerProcess worker, WorkerClient client, Guid id, IReadOnlyDictionary<string, string> devices) : IVssSnapshot
    {
        public Guid SnapshotSetId { get; } = id;

        public IReadOnlyDictionary<string, string> Devices { get; } = devices;

        public async ValueTask DisposeAsync()
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await client.CallAsync(WorkerOps.VssRelease, null, cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is WorkerProtocolException or OperationCanceledException or IOException)
            {
                // i tak zabijamy proces — VSS zwalnia migawkę razem z procesem (D2.8)
            }
            await worker.DisposeAsync().ConfigureAwait(false);
        }
    }
}
