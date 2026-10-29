using System.Text;
using System.Text.Json;
using Kobian.Domain;
using Kobian.Providers.Storage.Secrets;

namespace Kobian.Agent.Jobs;

/// <summary>
/// Zapis poświadczenia z kreatora do magazynu sekretów Agenta: <c>&lt;sekrety&gt;/&lt;nazwa&gt;.json</c> w formacie czytanym przez dostawców
/// (<see cref="FileSftpCredentialProvider"/>, FTP, WebDAV, SMB): <c>{"backup":{"user","password"},"maintenance":{…}}</c> — to samo konto dla obu ról
/// (osobne konto bez prawa usuwania ustawia administrator ręcznie). Plik tworzony od razu z prawami tylko dla właściciela (Unix 0600; Windows: ACL katalogu
/// sekretów), zapis atomowy (plik tymczasowy + zamiana), więc dostawca nigdy nie czyta połowy pliku. Hasło nie trafia do logów ani wyjątków.
/// </summary>
public sealed class CredentialStore(string secretsDir)
{
    public const int MaxFieldLength = 256;

    /// <summary>Zapisuje wpis i zwraca „file:&lt;nazwa&gt;”; niepoprawne dane → KB-2102 (bez treści pól w komunikacie).</summary>
    public string Save(string name, string user, string password)
    {
        if (string.IsNullOrEmpty(name) || !FileSftpCredentialProvider.IsSafeName(name)
            || string.IsNullOrEmpty(user) || user.Length > MaxFieldLength || user.Any(char.IsControl)
            || string.IsNullOrEmpty(password) || password.Length > MaxFieldLength || password.Contains('\0', StringComparison.Ordinal))
            throw new KobianException(KbCode.AuthRejected, ErrorClass.Permanent, "Credential name, user or password is not valid.");
        var dir = Path.GetFullPath(secretsDir);
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(dir);
        else Directory.CreateDirectory(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var target = Path.Combine(dir, name + ".json");
        var tmp = Path.Combine(dir, "." + name + "." + Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(6)) + ".tmp");
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            foreach (var role in new[] { "backup", "maintenance" })
            {
                w.WriteStartObject(role);
                w.WriteString("user", user);
                w.WriteString("password", password);
                w.WriteEndObject();
            }
            w.WriteEndObject();
        }
        var bytes = ms.ToArray();
        try
        {
            var opts = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) opts.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var fs = new FileStream(tmp, opts))
            {
                fs.Write(bytes);
                fs.Flush(flushToDisk: true);
            }
            if (new FileInfo(target) is { Exists: true, LinkTarget: not null })
                throw new KobianException(KbCode.AuthRejected, ErrorClass.Security, "Credential entry is a link; refusing to replace it.");
            File.Move(tmp, target, overwrite: true);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes);
            if (File.Exists(tmp)) File.Delete(tmp);
        }
        return "file:" + name;
    }

    /// <summary>Nazwa wpisu z adresu serwera i loginu („nas.firma.pl” + „kopie” → „nas.firma.pl-kopie”), przycięta do znaków dozwolonych.</summary>
    public static string SuggestName(string host, string user)
    {
        var raw = (host + "-" + user).ToLowerInvariant();
        var sb = new StringBuilder();
        foreach (var c in raw) sb.Append(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' ? c : '-');
        var s = sb.ToString().Trim('-', '.');
        if (s.Length == 0 || !char.IsAsciiLetterOrDigit(s[0])) s = "serwer" + s;
        s = s.Length > 64 ? s[..64] : s;
        while (s.Contains("..", StringComparison.Ordinal)) s = s.Replace("..", ".", StringComparison.Ordinal);
        return s.TrimEnd('.');
    }
}
