// Proces izolowany (D2.8, SEC-2.5): `Kobian.Worker --role vss` — requester VSS w osobnym procesie potomnym usługi.
// Protokół: stdin/stdout (anonimowy pipe), ramki u32le + JSON ≤ 1 MiB (Kobian.Application.Worker). Inne role (skrypty, dumpy, listingi FTP) — później.
// Kody wyjścia: 0 = koniec rozmowy (EOF/vss.release), 3 = naruszenie protokołu, 64 = złe argumenty.
using Kobian.Application.Worker;

if (args.Length != 2 || args[0] != "--role" || args[1] != "vss")
{
    await Console.Error.WriteLineAsync("usage: Kobian.Worker --role vss").ConfigureAwait(false);
    return 64;
}
await using var stdin = Console.OpenStandardInput();
await using var stdout = Console.OpenStandardOutput();
return await new VssWorkerHost(new Kobian.Platform.Windows.Vss.PendingVssSnapshotService()).RunAsync(stdin, stdout, CancellationToken.None).ConfigureAwait(false);
