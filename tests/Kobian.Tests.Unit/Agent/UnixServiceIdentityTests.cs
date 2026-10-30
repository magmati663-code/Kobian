using System.Net.Sockets;
using Kobian.Agent.Hosting;
using Kobian.Api.Contracts;
using Kobian.Domain;
using Xunit;

namespace Kobian.Tests.Unit.Agent;

/// <summary>Usługa Linux/macOS: rola z poświadczeń jądra klienta gniazda (SO_PEERCRED), grupy kobian-admins/kobian-operators, kontrola folderu odtwarzania bitami POSIX.</summary>
public sealed class UnixServiceIdentityTests
{
    private sealed class FakeAccounts : IUnixAccounts
    {
        public Dictionary<string, HashSet<uint>> Groups { get; } = new(StringComparer.Ordinal);

        public Dictionary<uint, HashSet<uint>> ByGid { get; } = [];

        public bool IsMember(uint uid, uint primaryGid, string group) => Groups.TryGetValue(group, out var m) && m.Contains(uid);

        public bool IsMember(uint uid, uint primaryGid, uint gid) => gid == primaryGid || (ByGid.TryGetValue(gid, out var m) && m.Contains(uid));
    }

    [Fact]
    public void Roles_follow_root_and_kobian_groups_and_everyone_else_is_reader()
    {
        var acc = new FakeAccounts();
        acc.Groups[UnixRoleMap.AdminsGroup] = [1001];
        acc.Groups[UnixRoleMap.OperatorsGroup] = [1002];
        var map = new UnixRoleMap(acc);
        Assert.Equal(AgentRole.Administrator, map.Map(new UnixPeer(0, 0)));
        Assert.Equal(AgentRole.Administrator, map.Map(new UnixPeer(1001, 1001)));
        Assert.Equal(AgentRole.Operator, map.Map(new UnixPeer(1002, 1002)));
        Assert.Equal(AgentRole.Reader, map.Map(new UnixPeer(1003, 100)));
        Assert.Equal(AgentRole.None, map.Map(null));
        Assert.Equal("unix-uid:1002", UnixPeerCallerResolver.ActorOf(new UnixPeer(1002, 1002)));
    }

    [Fact]
    public void Restore_target_requires_search_on_path_and_write_on_target()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Ścieżki i uprawnienia POSIX — usługa Linux/macOS.");
        var acc = new FakeAccounts();
        acc.ByGid[50] = [1002];
        var dirs = new Dictionary<string, UnixStat>(StringComparer.Ordinal)
        {
            ["/"] = UnixFileStat.Make(0x41ED, 0, 0), // 0755 root
            ["/home"] = UnixFileStat.Make(0x41ED, 0, 0),
            ["/home/jan"] = UnixFileStat.Make(0x41C0, 1002, 1002), // 0700 jan
            ["/home/jan/odtw"] = UnixFileStat.Make(0x41ED, 1002, 1002),
            ["/srv"] = UnixFileStat.Make(0x41ED, 0, 0),
            ["/srv/wspolny"] = UnixFileStat.Make(0x41F8, 0, 50), // 0770 grupa 50
            ["/srv/obcy"] = UnixFileStat.Make(0x41ED, 0, 0), // 0755 root — bez zapisu dla innych
            ["/home/ewa"] = UnixFileStat.Make(0x41C0, 1003, 1003),
            ["/home/ewa/x"] = UnixFileStat.Make(0x41FF, 1003, 1003), // 0777, ale rodzic 0700 cudzy
            ["/tmp"] = UnixFileStat.Make(0x43FF, 0, 0), // 1777
            ["/home/jan/link"] = UnixFileStat.Make(0xA1FF, 1002, 1002),
        };
        var a = new UnixRestoreTargetAuthorizer(_ => null, acc, p => dirs.TryGetValue(p, out var s) ? s : null);
        var jan = new UnixPeer(1002, 1002);
        Assert.Equal(RestoreTargetAccess.Allowed, a.Check(jan, "/home/jan/odtw"));
        Assert.Equal(RestoreTargetAccess.Allowed, a.Check(jan, "/home/jan/odtw/"));
        Assert.Equal(RestoreTargetAccess.Allowed, a.Check(jan, "/srv/wspolny"));
        Assert.Equal(RestoreTargetAccess.Allowed, a.Check(jan, "/tmp"));
        Assert.Equal(RestoreTargetAccess.Denied, a.Check(jan, "/srv/obcy"));
        Assert.Equal(RestoreTargetAccess.Denied, a.Check(jan, "/home/ewa/x"));
        Assert.Equal(RestoreTargetAccess.Denied, a.Check(jan, "/home/jan/link"));
        Assert.Equal(RestoreTargetAccess.Denied, a.Check(jan, "/home/jan/brak"));
        Assert.Equal(RestoreTargetAccess.Denied, a.Check(jan, "wzgledna"));
        Assert.Equal(RestoreTargetAccess.Denied, a.Check((UnixPeer?)null, "/tmp"));
        Assert.Equal(RestoreTargetAccess.Allowed, a.Check(new UnixPeer(0, 0), "/srv/obcy"));
        Assert.Equal(RestoreTargetAccess.Denied, a.Check(new UnixPeer(1003, 1003), "/home/jan/odtw"));
    }

    [Fact]
    public void Destination_and_password_file_follow_the_callers_own_rights()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Ścieżki i uprawnienia POSIX — usługa Linux/macOS.");
        var acc = new FakeAccounts();
        var fs = new Dictionary<string, UnixStat>(StringComparer.Ordinal)
        {
            ["/"] = UnixFileStat.Make(0x41ED, 0, 0),
            ["/etc"] = UnixFileStat.Make(0x41ED, 0, 0),
            ["/etc/shadow"] = UnixFileStat.Make(0x81A0, 0, 42), // 0640
            ["/home"] = UnixFileStat.Make(0x41ED, 0, 0),
            ["/home/jan"] = UnixFileStat.Make(0x41C0, 1002, 1002),
            ["/home/jan/haslo.txt"] = UnixFileStat.Make(0x8180, 1002, 1002), // 0600
            ["/home/jan/link.txt"] = UnixFileStat.Make(0xA1FF, 1002, 1002),
        };
        var a = new UnixRestoreTargetAuthorizer(_ => null, acc, p => fs.TryGetValue(p, out var st) ? st : null);
        var jan = new UnixPeer(1002, 1002);
        Assert.Equal(RestoreTargetAccess.Allowed, a.CheckDestination(jan, "/home/jan/kopie/nowe")); // nie istnieje — powstanie w /home/jan
        Assert.Equal(RestoreTargetAccess.Denied, a.CheckDestination(jan, "/etc/cron.d"));
        Assert.Equal(RestoreTargetAccess.Allowed, a.CheckReadableFile(jan, "/home/jan/haslo.txt"));
        Assert.Equal(RestoreTargetAccess.Denied, a.CheckReadableFile(jan, "/etc/shadow"));
        Assert.Equal(RestoreTargetAccess.Denied, a.CheckReadableFile(jan, "/home/jan/link.txt"));
        Assert.Equal(RestoreTargetAccess.Allowed, a.CheckReadableFile(new UnixPeer(0, 0), "/etc/shadow"));
        Assert.Null(RestoreTargetGate.DenyPath(AgentRole.Administrator, "/etc", _ => RestoreTargetAccess.Denied, KbCode.AccessDenied));
        Assert.Equal(KbCode.AccessDenied, RestoreTargetGate.DenyPath(AgentRole.Operator, "/etc", _ => throw new IOException(), KbCode.AccessDenied));
    }

    [Fact, Trait("Category", "Unix")]
    public async Task Kernel_reports_the_peer_uid_of_a_unix_socket_client()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;
        var path = Path.Combine(Path.GetTempPath(), "kobian-peer-" + Guid.NewGuid().ToString("N")[..8] + ".sock");
        try
        {
            using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            listener.Bind(new UnixDomainSocketEndPoint(path));
            listener.Listen(1);
            using var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await client.ConnectAsync(new UnixDomainSocketEndPoint(path), TestContext.Current.CancellationToken);
            using var server = await listener.AcceptAsync(TestContext.Current.CancellationToken);
            var peer = UnixPeerCredentials.Read(server);
            Assert.NotNull(peer);
            Assert.Equal(UnixFileStat.TryLStat(path)!.Value.Uid, peer.Uid); // gniazdo założył ten sam proces
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Service_paths_and_client_socket_resolution()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Ścieżki i uprawnienia POSIX — usługa Linux/macOS.");
        var (root, app) = AgentPaths.ServiceDataRoot();
        if (OperatingSystem.IsLinux()) Assert.Equal(("/var/lib", "kobian"), (root, app));
        var (paths, err) = AgentPaths.Resolve(_ => null, runningAsService: true, "/var/lib", "kobian");
        Assert.Null(err);
        Assert.Equal("/var/lib/kobian/catalog.db", paths!.Catalog.Replace('\\', '/'));
        Assert.False(AgentServiceRuntime.IsUnixService(_ => null));
        Assert.Equal(!OperatingSystem.IsWindows(), AgentServiceRuntime.IsUnixService(_ => "1"));
        Assert.Equal(ApiConventions.UnixSocketPath("inny-pipe"), ApiConventions.ClientSocketPath("inny-pipe"));
    }

    [Fact]
    public void Too_long_temp_directory_falls_back_to_a_short_socket_path_instead_of_crashing()
    {
        Assert.Equal("/tmp/x/Kobian.Agent.v1.sock", ApiConventions.UnixSocketPath("Kobian.Agent.v1", "/tmp/x", "jan").Replace('\\', '/'));
        var deep = "/home/jan/" + new string('a', 120);
        Assert.Equal("/tmp/Kobian.Agent.v1-jan.sock", ApiConventions.UnixSocketPath("Kobian.Agent.v1", deep, "jan"));
        Assert.Equal("/tmp/Kobian.Agent.v1-j_n_k.sock", ApiConventions.UnixSocketPath("Kobian.Agent.v1", deep, "j n/k"));
        Assert.Equal("/tmp/Kobian.Agent.v1.sock", ApiConventions.UnixSocketPath("Kobian.Agent.v1", deep, new string('u', 200)));
    }

    [Fact]
    public void Stale_socket_file_is_not_listening_but_a_live_one_is()
    {
        if (OperatingSystem.IsWindows()) return;
        var path = "/tmp/kbt-" + Guid.NewGuid().ToString("N")[..8] + ".sock";
        try
        {
            using var socket = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.Unix, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Unspecified);
            socket.Bind(new System.Net.Sockets.UnixDomainSocketEndPoint(path));
            Assert.True(Path.Exists(path)); // plik gniazda bez nasłuchu — jak po awarii Agenta (kill -9)
            Assert.False(ApiConventions.IsListening(path));
            socket.Listen(1);
            Assert.True(ApiConventions.IsListening(path));
            Assert.False(ApiConventions.IsListening("/tmp/kbt-brak-" + Guid.NewGuid().ToString("N")[..8] + ".sock"));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
