using System.Text.Json;

namespace Kobian.UI.Settings;

/// <summary>Ustawienia interfejsu tego użytkownika (SCR-11, AC-31.1 częściowo): język i motyw. Ustawienia usługi (powiadomienia, aktualizacje) — później, przez Agenta.</summary>
public sealed record UiSettings(string Language = UiSettings.System, string Theme = UiSettings.System)
{
    public const string System = "system";

    public static readonly string[] Languages = [System, "pl", "en"];

    public static readonly string[] Themes = [System, "light", "dark"];

    /// <summary>Wartości spoza list → domyślne (plik edytowany ręcznie nie psuje startu).</summary>
    public UiSettings Normalize() => new(Languages.Contains(Language) ? Language : System, Themes.Contains(Theme) ? Theme : System);
}

/// <summary>Port zapisu ustawień UI.</summary>
public interface IUiSettingsStore
{
    UiSettings Load();

    void Save(UiSettings settings);
}

/// <summary>
/// Plik JSON w profilu użytkownika (%APPDATA%\Kobian\ui-settings.json; poza Windows ~/.config/kobian). Plik &gt; 4 KiB, nieczytelny albo
/// z nieznanymi wartościami → ustawienia domyślne (bez wyjątku). Zapis: plik tymczasowy + zamiana (bez połowicznego pliku). Bez sekretów.
/// </summary>
public sealed class FileUiSettingsStore(string path) : IUiSettingsStore
{
    public const int MaxBytes = 4096;

    public static string DefaultPath() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Kobian", "ui-settings.json");

    public UiSettings Load()
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaxBytes) return new UiSettings();
            using var doc = JsonDocument.Parse(File.ReadAllBytes(path), new JsonDocumentOptions { MaxDepth = 4 });
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object) return new UiSettings();
            string Str(string name) => r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? UiSettings.System : UiSettings.System;
            return new UiSettings(Str("language"), Str("theme")).Normalize();
        }
        // InvalidOperationException: niepoprawne UTF-8 wewnątrz napisu JSON (GetString) — uszkodzony plik nie może zablokować startu UI (it. 90, fuzzing)
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return new UiSettings();
        }
    }

    public void Save(UiSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var s = settings.Normalize();
        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(dir);
        var tmp = Path.Combine(dir, "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write))
            using (var w = new Utf8JsonWriter(fs, new JsonWriterOptions { Indented = true }))
            {
                w.WriteStartObject();
                w.WriteString("language", s.Language);
                w.WriteString("theme", s.Theme);
                w.WriteEndObject();
            }
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }
}

/// <summary>Informacje o usłudze dla SCR-11: wersja, rola wywołującego, poziom poprawek .NET (KB-7013).</summary>
public sealed record ServiceInfo(string AgentVersion, Dashboard.CallerRole Role, string? RuntimeCode, string? RuntimeVersion);

/// <summary>Wynik weryfikacji łańcucha audytu (VerifyAudit, RD-42); it. 104: ostatnia kotwica „seq:sha256” (D9.9) do zapisania poza serwerem.</summary>
public sealed record AuditCheckResult(bool Intact, long Entries, long? BrokenSeq, string? Code, string? LastAnchor = null, string? LastAnchorAtUtc = null, int AnchorsChecked = 0);

/// <summary>Port SCR-11 do usługi.</summary>
public interface IServiceInfoSource
{
    Task<ServiceInfo> GetServiceInfoAsync(CancellationToken ct);

    Task<AuditCheckResult> VerifyAuditAsync(CancellationToken ct);
}
