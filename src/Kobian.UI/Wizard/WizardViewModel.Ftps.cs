using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using Kobian.UI.Localization;

namespace Kobian.UI.Wizard;

/// <summary>
/// Reguły pól celu FTPS w kreatorze (BL-34, it. 120) — wstępne, by użytkownik dostał komunikat przy polu zamiast KB z usługi. Zgodne z regułami Agenta
/// (JobDestination: host ASCII bez schematu/ścieżki, port 1–65535, folder uniksowy od „/” bez „..” i „\”, poświadczenie = bezpieczna nazwa wpisu); Agent i tak
/// waliduje ponownie (jedno źródło prawdy — D6.7). Wynik = klucz zasobu UiStrings (null = poprawne).
/// </summary>
public static partial class FtpsFieldRules
{
    public const int MaxHostLength = 253;
    public const int MaxPathLength = 1024;
    public const int ExplicitPort = 21;
    public const int ImplicitPort = 990;
    public const int HttpsPort = 443;

    public static string? HostError(string? host)
    {
        var h = (host ?? "").Trim();
        if (h.Length == 0) return "Wizard.Ftps.Err.HostRequired";
        if (h.Contains("://", StringComparison.Ordinal) || h.StartsWith("ftp:", StringComparison.OrdinalIgnoreCase) || h.StartsWith("ftps:", StringComparison.OrdinalIgnoreCase)
            || h.StartsWith("http:", StringComparison.OrdinalIgnoreCase) || h.StartsWith("https:", StringComparison.OrdinalIgnoreCase))
            return "Wizard.Ftps.Err.HostScheme";
        if (h.IndexOfAny(['/', '\\']) >= 0) return "Wizard.Ftps.Err.HostPath";
        if (h.Contains('@', StringComparison.Ordinal)) return "Wizard.Ftps.Err.HostUser";
        if (h.Contains(':', StringComparison.Ordinal) && !IsIPv6(h)) return "Wizard.Ftps.Err.HostPort";
        if (h.Length > MaxHostLength || !h.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or ':' or '_') || h.StartsWith('.') || h.StartsWith('-'))
            return "Wizard.Ftps.Err.HostInvalid";
        return null;
    }

    public static string? PortError(int port) => port is < 1 or > 65535 ? "Wizard.Ftps.Err.Port" : null;

    public static string? PathError(string? path)
    {
        var p = (path ?? "").Trim();
        if (p.Length == 0) return "Wizard.Ftps.Err.PathRequired";
        if (p.Length > MaxPathLength || p.Any(char.IsControl)) return "Wizard.Ftps.Err.PathInvalid";
        if (p.Contains('\\', StringComparison.Ordinal) || !p.StartsWith('/')) return "Wizard.Ftps.Err.PathAbsolute";
        if (p.Split('/').Any(s => s == "..")) return "Wizard.Ftps.Err.PathDotDot";
        return null;
    }

    public static string? CredentialError(string? name)
    {
        var n = (name ?? "").Trim();
        if (n.Length == 0) return "Wizard.Ftps.Err.CredentialRequired";
        return SafeName().IsMatch(n) && !n.Contains("..", StringComparison.Ordinal) && !n.EndsWith('.') ? null : "Wizard.Ftps.Err.CredentialInvalid";
    }

    /// <summary>PROWIZORYCZNIE Qb68 (jak Agent — JobDestination.FtpsOptions): 990 = Implicit, każdy inny port = Explicit.</summary>
    public static FtpsTlsMode ModeForPort(int port) => port == ImplicitPort ? FtpsTlsMode.Implicit : FtpsTlsMode.Explicit;

    /// <summary>Odcisk do wyświetlenia w skrócie: „SHA256:AbCdEf…WxYz” (pełny w podpowiedzi i w kreatorze przy zatwierdzaniu).</summary>
    public static string ShortFingerprint(string fingerprint)
    {
        ArgumentNullException.ThrowIfNull(fingerprint);
        const string prefix = "SHA256:";
        var body = fingerprint.StartsWith(prefix, StringComparison.Ordinal) ? fingerprint[prefix.Length..] : fingerprint;
        var head = fingerprint.Length - body.Length;
        return body.Length <= 16 ? fingerprint : string.Concat(fingerprint.AsSpan(0, head + 8), "…", body.AsSpan(body.Length - 6));
    }

    private static bool IsIPv6(string h) => IPAddress.TryParse(h, out var ip) && ip.AddressFamily == AddressFamily.InterNetworkV6 && !h.Contains('%', StringComparison.Ordinal);

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeName();
}

/// <summary>S2 cel FTPS (BL-34): pola, tryb TLS czytelny dla nietechnicznego użytkownika (mapowany na port — Qb68), walidacja przy polach.</summary>
public sealed partial class WizardViewModel
{
    public IReadOnlyList<WizardOption<FtpsTlsMode>> FtpsModes { get; } =
        [new(FtpsTlsMode.Explicit, "Wizard.Ftps.Mode.Explicit"), new(FtpsTlsMode.Implicit, "Wizard.Ftps.Mode.Implicit")];

    public bool IsFtps => SelectedDestination?.Value == DestinationKind.Ftps;

    public bool IsWebDav => SelectedDestination?.Value == DestinationKind.WebDav;

    /// <summary>Cel z serwerem TLS (FTPS albo WebDAV/HTTPS): wspólne pola host/port/poświadczenie/folder i zatwierdzanie odcisku SPKI.</summary>
    public bool IsTlsServer => IsFtps || IsWebDav;

    [ObservableProperty]
    public partial string FtpsHost { get; set; } = "";

    /// <summary>Port; 990 przełącza tryb na Implicit, każdy inny — Explicit (Qb68: jedna prawda o trybie, bez sprzecznej kombinacji).</summary>
    [ObservableProperty]
    public partial int FtpsPort { get; set; } = FtpsFieldRules.ExplicitPort;

    [ObservableProperty]
    public partial WizardOption<FtpsTlsMode>? SelectedFtpsMode { get; set; }

    /// <summary>Nazwa wpisu poświadczenia w magazynie sekretów usługi (jak SMB/SFTP: odwołanie <c>file:&lt;nazwa&gt;</c>; hasła kreator nie przyjmuje — AC-01.3).</summary>
    [ObservableProperty]
    public partial string FtpsCredential { get; set; } = "";

    [ObservableProperty]
    public partial string FtpsHostError { get; private set; } = "";

    [ObservableProperty]
    public partial string FtpsPortError { get; private set; } = "";

    [ObservableProperty]
    public partial string FtpsPathError { get; private set; } = "";

    [ObservableProperty]
    public partial string FtpsCredentialError { get; private set; } = "";

    public bool HasFtpsHostError => FtpsHostError.Length > 0;

    public bool HasFtpsPortError => FtpsPortError.Length > 0;

    public bool HasFtpsPathError => FtpsPathError.Length > 0;

    public bool HasFtpsCredentialError => FtpsCredentialError.Length > 0;

    /// <summary>Ekran zatwierdzenia tożsamości dotyczy certyfikatu FTPS (inne teksty niż klucz hosta SFTP).</summary>
    public bool IsFtpsIdentity => IsTlsServer;

    public bool IsSftpIdentity => !IsTlsServer;

    /// <summary>„Serwer: nas.firma.local:990 · Implicit TLS” — serwer, którego dotyczy odcisk (S2Identity).</summary>
    public string FtpsServerText => Strings.Format("Wizard.Identity.Server", string.Create(CultureInfo.InvariantCulture, $"{HostForUrl(FtpsHost.Trim())}:{FtpsPort}") + " · " + (IsWebDav ? "HTTPS" : ModeText(FtpsPort)));

    internal static string ModeText(int port) => Strings.Get(FtpsFieldRules.ModeForPort(port) == FtpsTlsMode.Implicit ? "Wizard.Ftps.ModeShort.Implicit" : "Wizard.Ftps.ModeShort.Explicit");

    /// <summary>Podpowiedź pola folderu: dla FTPS ścieżka uniksowa na serwerze, inaczej dysk/UNC.</summary>
    public string PathWatermark => Strings.Get(IsWebDav ? "Wizard.WebDav.PathWatermark" : IsFtps ? "Wizard.Ftps.PathWatermark" : "Wizard.S2.PathWatermark");

    public string PathHint => Strings.Get(IsWebDav ? "Wizard.WebDav.PathHint" : IsFtps ? "Wizard.Ftps.PathHint" : "Wizard.S2.Hint");

    private bool _syncingFtps;

    partial void OnSelectedDestinationChanged(WizardOption<DestinationKind> value)
    {
        ForgetApprovedIdentity();
        ClearFtpsErrors();
        // Pola serwera TLS są wspólne dla FTPS i WebDAV — domyślny port podąża za rodzajem celu, chyba że użytkownik wpisał własny.
        if (value?.Value == DestinationKind.WebDav && FtpsPort is FtpsFieldRules.ExplicitPort or FtpsFieldRules.ImplicitPort) FtpsPort = FtpsFieldRules.HttpsPort;
        else if (value?.Value == DestinationKind.Ftps && FtpsPort == FtpsFieldRules.HttpsPort) FtpsPort = FtpsFieldRules.ExplicitPort;
    }

    partial void OnSftpHostChanged(string value) => ForgetApprovedIdentity();

    partial void OnSftpPortChanged(int value) => ForgetApprovedIdentity();

    partial void OnDestinationPathChanged(string value)
    {
        if (FtpsPathError.Length > 0) FtpsPathError = ErrorText(FtpsFieldRules.PathError(value));
    }

    partial void OnSelectedFtpsModeChanged(WizardOption<FtpsTlsMode>? value)
    {
        if (_syncingFtps || value is null) return;
        _syncingFtps = true;
        try
        {
            if (value.Value == FtpsTlsMode.Implicit) FtpsPort = FtpsFieldRules.ImplicitPort;
            else if (FtpsPort == FtpsFieldRules.ImplicitPort) FtpsPort = FtpsFieldRules.ExplicitPort;
        }
        finally
        {
            _syncingFtps = false;
        }
    }

    partial void OnFtpsPortChanged(int value)
    {
        ForgetApprovedIdentity();
        if (FtpsPortError.Length > 0) FtpsPortError = FtpsFieldRules.PortError(value) is { } k ? Strings.Get(k) : "";
        if (_syncingFtps) return;
        _syncingFtps = true;
        try
        {
            SelectedFtpsMode = FtpsModes.Single(m => m.Value == FtpsFieldRules.ModeForPort(value));
        }
        finally
        {
            _syncingFtps = false;
        }
    }

    partial void OnFtpsHostChanged(string value)
    {
        ForgetApprovedIdentity();
        if (FtpsHostError.Length > 0) FtpsHostError = ErrorText(FtpsFieldRules.HostError(value)); // poprawka zdejmuje komunikat od razu, nowy pojawia się dopiero po „Dalej”
    }

    partial void OnFtpsCredentialChanged(string value)
    {
        if (FtpsCredentialError.Length > 0) FtpsCredentialError = ErrorText(FtpsFieldRules.CredentialError(value));
    }

    partial void OnFtpsHostErrorChanged(string value) => OnPropertyChanged(nameof(HasFtpsHostError));

    partial void OnFtpsPortErrorChanged(string value) => OnPropertyChanged(nameof(HasFtpsPortError));

    partial void OnFtpsPathErrorChanged(string value) => OnPropertyChanged(nameof(HasFtpsPathError));

    partial void OnFtpsCredentialErrorChanged(string value) => OnPropertyChanged(nameof(HasFtpsCredentialError));

    /// <summary>Walidacja pól FTPS przed wywołaniem usługi; false = błędy pokazane przy polach (bez wywołań — brak ruchu sieciowego dla oczywistych pomyłek).</summary>
    internal bool ValidateFtpsFields()
    {
        FtpsHostError = ErrorText(FtpsFieldRules.HostError(FtpsHost));
        FtpsPortError = ErrorText(FtpsFieldRules.PortError(FtpsPort));
        FtpsPathError = ErrorText(FtpsFieldRules.PathError(DestinationPath));
        FtpsCredentialError = ErrorText(FtpsFieldRules.CredentialError(FtpsCredential));
        return !(HasFtpsHostError || HasFtpsPortError || HasFtpsPathError || HasFtpsCredentialError);
    }

    private void ClearFtpsErrors() => FtpsHostError = FtpsPortError = FtpsPathError = FtpsCredentialError = "";

    /// <summary>Zatwierdzony odcisk dotyczy konkretnego serwera — zmiana celu (rodzaj, host, port) go unieważnia (bez przenoszenia pinu na inny serwer).</summary>
    private void ForgetApprovedIdentity() => _approvedHostKey = null;

    private static string ErrorText(string? key) => key is null ? "" : Strings.Get(key);

    private static string HostForUrl(string host) => host.Contains(':', StringComparison.Ordinal) ? "[" + host + "]" : host;

    private string WebDavDestinationText() =>
        Strings.Format("Wizard.WebDav.Summary", string.Create(CultureInfo.InvariantCulture, $"https://{HostForUrl(FtpsHost.Trim())}:{FtpsPort}/{DestinationPath.Trim().TrimStart('/')}"),
            FtpsCredential.Trim());

    private string FtpsDestinationText() =>
        Strings.Format("Wizard.Ftps.Summary", string.Create(CultureInfo.InvariantCulture, $"ftps://{HostForUrl(FtpsHost.Trim())}:{FtpsPort}/{DestinationPath.Trim().TrimStart('/')}"),
            ModeText(FtpsPort), FtpsCredential.Trim());
}
