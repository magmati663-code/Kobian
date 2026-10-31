using System.Reflection;
using Kobian.Cli.Commands;
using Kobian.Domain;
using Xunit;

namespace Kobian.Tests.Unit.Cli;

/// <summary>Błędy parsowania argumentów i kodów wyjścia znalezione w przeglądzie: opcje bez wartości, ścieżka względna, Ctrl+C, zadanie bez przebiegu.</summary>
public sealed class CliRobustnessTests
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    /// <summary>Rejestruje wywołania i odpowiada minimalnymi wynikami (bez ręcznej implementacji całego interfejsu).</summary>
    public class RecordingProxy : DispatchProxy
    {
        public static readonly List<(string Method, object?[] Args)> Calls = [];

        public static Func<CancellationToken, Task>? Hook { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            Calls.Add((targetMethod!.Name, args ?? []));
            var ct = args?.OfType<CancellationToken>().FirstOrDefault() ?? default;
            object? value = targetMethod.Name switch
            {
                nameof(IAgentClient.RestoreAsync) => new RunOutcome(OverallState.Ok, null, "snap"),
                nameof(IAgentClient.CheckAsync) => new CheckResult(OverallState.Ok, null, 2, 1, 1, 0, 0, [], 0, []),
                nameof(IAgentClient.GetStatusAsync) => new AgentStatus(OverallState.Ok, "1", [new JobStatus("Nowe", OverallState.Ok, null, null)]),
                _ => null,
            };
            var rt = targetMethod.ReturnType;
            if (Hook is { } h && targetMethod.Name == nameof(IAgentClient.RestoreAsync)) return Wrap(rt, h(ct), value);
            if (!rt.IsGenericType) return Task.CompletedTask;
            return Wrap(rt, Task.CompletedTask, value);
        }

        private static object Wrap(Type rt, Task before, object? value)
        {
            var inner = rt.GetGenericArguments()[0];
            var m = typeof(RecordingProxy).GetMethod(nameof(Continue), BindingFlags.NonPublic | BindingFlags.Static)!.MakeGenericMethod(inner);
            return m.Invoke(null, [before, value])!;
        }

        private static async Task<T> Continue<T>(Task before, object? value)
        {
            await before;
            return (T)value!;
        }
    }

    private static async Task<(int Code, string Out, string Err)> Run(CancellationToken ct, params string[] args)
    {
        RecordingProxy.Calls.Clear();
        var client = DispatchProxy.Create<IAgentClient, RecordingProxy>();
        var o = new StringWriter();
        var e = new StringWriter();
        var code = await CliApp.RunAsync(args, client, o, e, ct);
        return (code, o.ToString(), e.ToString());
    }

    [Theory]
    [InlineData("restore", "--snapshot", "latest", "--job", "--to", "/tmp/x")] // pusta zmienna powłoki: --job bez wartości
    [InlineData("restore", "--snapshot", "latest", "--to", "/tmp/x", "--path")]
    [InlineData("export", "--job")]
    [InlineData("check", "ERP", "--sample")]
    [InlineData("runs", "--limit")]
    public async Task Option_without_a_value_is_a_usage_error_not_silently_ignored(params string[] args)
    {
        var (code, _, err) = await Run(_ct, args);
        Assert.Equal(CliApp.ExitUsage, code);
        Assert.Contains("--", err, StringComparison.Ordinal);
        Assert.Empty(RecordingProxy.Calls); // żadna operacja nie została wysłana do Agenta (wcześniej: odtworzenie najnowszej kopii DOWOLNEGO zadania)
    }

    [Fact]
    public async Task Sample_value_is_not_mistaken_for_the_job_name()
    {
        var (code, _, _) = await Run(_ct, "check", "--sample", "50", "ERP");
        Assert.NotEqual(CliApp.ExitUsage, code);
        var call = RecordingProxy.Calls.Single(c => c.Method == nameof(IAgentClient.CheckAsync));
        Assert.Equal("ERP", call.Args[0]);
    }

    [Fact]
    public async Task Relative_restore_target_is_resolved_against_the_cli_working_directory()
    {
        var (code, _, _) = await Run(_ct, "restore", "--snapshot", "latest", "--to", "odtworzone");
        Assert.Equal(CliApp.ExitOk, code);
        var target = (string)RecordingProxy.Calls.Single(c => c.Method == nameof(IAgentClient.RestoreAsync)).Args[2]!;
        Assert.Equal(Path.GetFullPath("odtworzone"), target);
        Assert.True(Path.IsPathFullyQualified(target)); // Agent odrzuca względne jako KB-5001 „odmowa dostępu”
    }

    [Fact]
    public async Task Ctrl_c_exits_cleanly_with_130_instead_of_an_unhandled_exception()
    {
        using var cts = new CancellationTokenSource();
        RecordingProxy.Hook = async ct =>
        {
            await cts.CancelAsync();
            ct.ThrowIfCancellationRequested();
            await Task.Delay(Timeout.Infinite, ct);
        };
        try
        {
            var (code, _, err) = await Run(cts.Token, "restore", "--snapshot", "latest", "--to", "/tmp/x");
            Assert.Equal(CliApp.ExitCancelled, code);
            Assert.Contains("cancel", err, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            RecordingProxy.Hook = null;
        }
    }

    [Fact]
    public async Task Job_that_never_ran_is_reported_as_none_not_ok()
    {
        var (code, output, _) = await Run(_ct, "status", "--json");
        Assert.Equal(CliApp.ExitOk, code);
        Assert.Contains("\"lastResult\":\"none\"", output.Replace(" ", "", StringComparison.Ordinal), StringComparison.Ordinal);
        var (_, text, _) = await Run(_ct, "status");
        Assert.Contains("Nowe\tnone", text, StringComparison.Ordinal);
    }
}
