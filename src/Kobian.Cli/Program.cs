// kobian.exe — klient API (6.8): gRPC po named pipe do Agenta (BL-14). Brak usługi → kod 3.
using Kobian.Api.Contracts;
using Kobian.Cli.Commands;

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
using var client = new GrpcAgentClient(ApiConventions.DefaultPipeName); // brak nadpisania przez zmienną środowiskową: ochrona przed podstawieniem pipe (SP-02)
return await CliApp.RunAsync(args, client, Console.Out, Console.Error, cts.Token).ConfigureAwait(false);
