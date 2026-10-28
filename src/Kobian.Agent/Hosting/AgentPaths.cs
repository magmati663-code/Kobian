namespace Kobian.Agent.Hosting;

/// <summary>Kody wyjścia procesu Agenta (SCM i skrypty). 64 = EX_USAGE (konfiguracja), 75 = EX_TEMPFAIL (druga instancja, KB-7007).</summary>
public static class AgentExitCodes
{
    public const int Ok = 0;
    public const int Emergency = 2;
    public const int Configuration = 64;
    public const int AlreadyRunning = 75;
}

/// <summary>Ścieżki stanu Agenta po rozwiązaniu konfiguracji.</summary>
public sealed record AgentPaths(string Catalog, string StateDir, string SecretsDir)
{
    public const string CatalogVariable = "KOBIAN_CATALOG";
    public const string StateVariable = "KOBIAN_STATE_DIR";
    public const string SecretsVariable = "KOBIAN_SECRETS_DIR";

    /// <summary>
    /// Rozwiązuje ścieżki z zmiennych środowiskowych (BL-72, Qb41 PROWIZORYCZNIE).
    /// Usługa Windows (SCM nie przekazuje zmiennych użytkownika, katalog bieżący = System32): brak <c>KOBIAN_CATALOG</c> →
    /// <c>%ProgramData%\Kobian\catalog.db</c> (R2.2.1: dane w %ProgramData%\Kobian); każda ścieżka musi być bezwzględna.
    /// Konsola (dev/testy): <c>KOBIAN_CATALOG</c> wymagane, względne rozwijane względem katalogu bieżącego (zachowanie sprzed it. 91).
    /// Zawsze: odmowa ścieżek UNC / urządzeń (\\host\…, \\?\…) — SQLite na udziale sieciowym grozi uszkodzeniem katalogu (D2.3), a stan Agenta jest lokalny (D8.9).
    /// </summary>
    /// <returns>Ścieżki albo komunikat błędu (bez danych z wartości zmiennych poza nazwą zmiennej).</returns>
    public static (AgentPaths? Paths, string? Error) Resolve(Func<string, string?> env, bool runningAsService, string? programData)
    {
        ArgumentNullException.ThrowIfNull(env);
        var catalog = Clean(env(CatalogVariable));
        if (catalog is null)
        {
            if (!runningAsService) return (null, $"{CatalogVariable} is not set (path to catalog.db).");
            var root = Clean(programData);
            if (root is null) return (null, $"{CatalogVariable} is not set and ProgramData is unavailable.");
            catalog = Path.Combine(root, "Kobian", "catalog.db");
        }

        var (c, e1) = Normalize(catalog, CatalogVariable, runningAsService);
        if (c is null) return (null, e1);
        // it. 105 (fuzzing rundy 7, Niskie): katalog musi być PLIKIEM w katalogu nadrzędnym — korzeń („/”, „C:\”) dawał katalog stanu „.”
        // (względny: w usłudze = System32), więc sekrety trafiałyby do katalogu bieżącego.
        var parent = Path.GetDirectoryName(c);
        if (parent is null || Path.GetFileName(c).Length == 0) return (null, $"{CatalogVariable} must be a file path (e.g. ...\\catalog.db).");
        var stateRaw = Clean(env(StateVariable));
        var (s, e2) = stateRaw is null ? (parent, null) : Normalize(stateRaw, StateVariable, runningAsService);
        if (s is null) return (null, e2);
        var secretsRaw = Clean(env(SecretsVariable));
        var (k, e3) = secretsRaw is null ? (Path.Combine(s, "secrets"), null) : Normalize(secretsRaw, SecretsVariable, runningAsService);
        if (k is null) return (null, e3);
        return (new AgentPaths(c, s, k), null);
    }

    private static string? Clean(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();

    private static (string? Path, string? Error) Normalize(string value, string name, bool service)
    {
        if (value.AsSpan().IndexOfAny('\0', '\r', '\n') >= 0) return (null, $"{name} contains control characters.");
        if (IsNetworkOrDevice(value)) return (null, $"{name} must be a local path (UNC and device paths are not allowed).");
        if (service && !Path.IsPathFullyQualified(value)) return (null, $"{name} must be an absolute path when running as a service.");
        string full;
        try
        {
            full = Path.GetFullPath(value);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return (null, $"{name} is not a valid path.");
        }
        return IsNetworkOrDevice(full) ? (null, $"{name} must be a local path (UNC and device paths are not allowed).") : (full, null);
    }

    /// <summary>\\host\share, //host/share, \\?\…, \\.\… — także w Linuksie (wartość może pochodzić z konfiguracji Windows).</summary>
    internal static bool IsNetworkOrDevice(string p) =>
        p.Length >= 2 && (p[0] == '\\' || p[0] == '/') && (p[1] == '\\' || p[1] == '/');
}
