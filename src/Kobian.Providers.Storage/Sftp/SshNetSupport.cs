using System.Globalization;
using System.Security.Cryptography;
using Kobian.Application.Storage;
using Renci.SshNet.Common;

namespace Kobian.Providers.Storage.Sftp;

// Czyste (testowalne bez serwera) elementy adaptera SSH.NET: opcje połączenia, pinning klucza hosta (D4.11),
// tłumaczenie wyjątków biblioteki na SftpProtocolException (D4.8). Tylko SftpClient — ScpClient zakazany (D4.4, CVE-2026-48798).

/// <summary>Opcje celu SFTP z <see cref="DestinationConfig.Options"/>: host, port, hostkey ("SHA256:&lt;base64&gt;" jak w OpenSSH).</summary>
internal sealed record SshNetOptions(string Host, int Port, string PinnedSha256, TimeSpan ConnectTimeout, bool IdentityProbe = false)
{
    /// <summary>
    /// Opcja „identity_probe” = „1” (tylko ProbeDestination, BL-60): połączenie bez przypiętego klucza wyłącznie po to, by poznać odcisk klucza hosta.
    /// Klucz NIGDY nie jest wtedy zaufany (CanTrust = false), więc wymiana kluczy kończy się przed uwierzytelnieniem — żadne poświadczenie nie trafia do serwera.
    /// </summary>
    public const string IdentityProbeOption = "identity_probe";

    public const string PinPrefix = "SHA256:";

    /// <summary>Limit oczekiwania na odpowiedź na pojedyncze żądanie SFTP (zgodny z ReadTimeout sterowania FTP: 30 s; transfer dużego pliku to wiele żądań).</summary>
    public static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(60);

    public static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(30);
    public const int MaxPins = 4;

    /// <summary>Przypięte odciski (pusta lista tylko w trybie identity_probe).</summary>
    public IReadOnlyList<string> Pins => PinnedSha256.Length == 0 ? [] : PinnedSha256.Split(',');

    public static SshNetOptions Parse(DestinationConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var o = config.Options;
        string Get(string k) => o is not null && o.TryGetValue(k, out var v) ? v : "";
        var host = Get("host");
        if (host.Length is 0 or > 253 || !host.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or ':' or '_'))
            throw new SftpProtocolException(SftpStatus.NoConnection, "Invalid SFTP host option.");
        var port = 22;
        if (Get("port") is { Length: > 0 } ps
            && (!int.TryParse(ps, NumberStyles.None, CultureInfo.InvariantCulture, out port) || port is < 1 or > 65535))
            throw new SftpProtocolException(SftpStatus.NoConnection, "Invalid SFTP port option.");
        // PROWIZORYCZNIE Qb15 (ADR-0006): brak przypiętego klucza hosta = odmowa połączenia (bezpieczniejsze niż TOFU bez UI).
        // BL-35 (D4.11, AC-12.2): „hostkey” może być listą do MaxPins pinów rozdzieloną przecinkami (rotacja kluczy hosta, klaster) — z katalogu tożsamości Agenta.
        var pin = Get("hostkey");
        var probe = Get(IdentityProbeOption) == "1";
        if (probe && pin.Length == 0) pin = "";
        else if (pin.Split(',').Length > MaxPins
                 || pin.Split(',').Any(p => !p.StartsWith(PinPrefix, StringComparison.Ordinal) || p.Length <= PinPrefix.Length))
            throw new SftpProtocolException(SftpStatus.HostKeyMismatch, "Host key is not pinned for this destination.");
        var timeout = TimeSpan.FromSeconds(30);
        if (Get("connect_timeout_s") is { Length: > 0 } ts
            && int.TryParse(ts, NumberStyles.None, CultureInfo.InvariantCulture, out var sec) && sec is >= 1 and <= 300)
            timeout = TimeSpan.FromSeconds(sec);
        return new SshNetOptions(host, port, pin, timeout, probe && pin.Length == 0);
    }
}

internal static class HostKeyPin
{
    /// <summary>Odcisk w formacie OpenSSH: "SHA256:" + base64 bez dopełnienia.</summary>
    public static string Fingerprint(ReadOnlySpan<byte> hostKeyBlob)
        => SshNetOptions.PinPrefix + Convert.ToBase64String(SHA256.HashData(hostKeyBlob)).TrimEnd('=');

    /// <summary>Porównanie stałoczasowe; tylko dokładna zgodność (brak ostrzeżeń „zaufaj mimo to”).</summary>
    /// <summary>Zgodność z którymkolwiek pinem (porównanie stałoczasowe każdego); pusta lista nigdy nie jest zgodnością.</summary>
    public static bool MatchesAny(IReadOnlyList<string> pins, ReadOnlySpan<byte> hostKeyBlob)
    {
        var ok = false;
        foreach (var p in pins) ok |= Matches(p, hostKeyBlob);
        return ok;
    }

    public static bool Matches(string expectedPin, ReadOnlySpan<byte> hostKeyBlob)
    {
        if (string.IsNullOrEmpty(expectedPin)) return false; // brak przypięcia nigdy nie jest zgodnością (tryb identity_probe)
        var actual = System.Text.Encoding.ASCII.GetBytes(Fingerprint(hostKeyBlob));
        var expected = System.Text.Encoding.ASCII.GetBytes(expectedPin.TrimEnd('='));
        return actual.Length == expected.Length && CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}

internal static class SshNetErrorTranslator
{
    public static SftpProtocolException Translate(Exception ex) => ex switch
    {
        SftpProtocolException p => p,
        SshAuthenticationException => new(SftpStatus.AuthRejected, "SFTP authentication rejected.", ex),
        SftpPathNotFoundException => new(SftpStatus.NoSuchFile, "SFTP path not found.", ex),
        SftpPermissionDeniedException => new(SftpStatus.PermissionDenied, "SFTP permission denied.", ex),
        SshConnectionException => new(SftpStatus.ConnectionLost, "SSH connection lost.", ex),
        SshOperationTimeoutException => new(SftpStatus.ConnectionLost, "SSH operation timed out.", ex),
        System.Net.Sockets.SocketException => new(SftpStatus.NoConnection, "SSH connection failed.", ex),
        SshException when ex.Message.Contains("administratively prohibited", StringComparison.OrdinalIgnoreCase)
            => new(SftpStatus.SessionLimit, "SSH server refused a new channel.", ex),
        _ => new(SftpStatus.Failure, "SFTP error.", ex),
    };
}
