using System.Net.Sockets;
using Kobian.Agent.Hosting;
using Xunit;

namespace Kobian.Tests.Unit.Agent;

/// <summary>Dev/testy poza Windows: martwe gniazdo po awarii Agenta jest sprzątane, żywe i obce pliki — nie.</summary>
public sealed class UnixSocketCleanupTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kobian-sock-" + Guid.NewGuid().ToString("N")[..8]);

    public UnixSocketCleanupTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string P(string n) => Path.Combine(_dir, n);

    private static Socket Listen(string path)
    {
        var s = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        s.Bind(new UnixDomainSocketEndPoint(path));
        s.Listen(1);
        return s;
    }

    /// <summary>
    /// Martwe gniazdo jak po kill -9: .NET usuwa plik gniazda przy Dispose, więc tworzymy je w osobnym procesie, który kończy się bez sprzątania (os._exit).
    /// Brak python3 → false (test pomijany; ci.sh i tak go wymaga).
    /// </summary>
    private static bool TryCreateOrphanSocket(string path)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("python3") { UseShellExecute = false };
            foreach (var a in new[] { "-I", "-c", "import socket,os,sys\ns=socket.socket(socket.AF_UNIX)\ns.bind(sys.argv[1])\nos._exit(0)", path }) psi.ArgumentList.Add(a);
            using var p = System.Diagnostics.Process.Start(psi);
            if (p is null) return false;
            p.WaitForExit();
            return p.ExitCode == 0 && Path.Exists(path);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    [Fact, Trait("Category", "Posix")]
    public void Dead_socket_file_is_removed()
    {
        if (OperatingSystem.IsWindows()) return;
        var path = P("dead.sock");
        if (!TryCreateOrphanSocket(path)) return;
        Assert.True(UnixSocketCleanup.RemoveIfStale(path));
        Assert.False(Path.Exists(path));
    }

    [Fact, Trait("Category", "Posix")]
    public void Live_socket_is_left_alone()
    {
        if (OperatingSystem.IsWindows()) return;
        var path = P("live.sock");
        using var listener = Listen(path);
        Assert.False(UnixSocketCleanup.RemoveIfStale(path));
        Assert.True(Path.Exists(path));
    }

    [Fact]
    public void Regular_files_and_missing_paths_are_left_alone()
    {
        var file = P("zwykly.txt");
        File.WriteAllText(file, "dane");
        Assert.False(UnixSocketCleanup.RemoveIfStale(file));
        Assert.True(File.Exists(file));
        var lookalike = P("udaje.sock"); // zwykły plik o nazwie gniazda: połączenie jest odrzucane jak przy martwym gnieździe, ale nie jest to gniazdo
        File.WriteAllText(lookalike, "dane");
        Assert.False(UnixSocketCleanup.RemoveIfStale(lookalike));
        Assert.True(File.Exists(lookalike));
        Assert.False(UnixSocketCleanup.RemoveIfStale(P("brak.sock")));
    }
}
