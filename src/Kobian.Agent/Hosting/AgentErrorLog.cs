using System.Globalization;
using System.Text;

namespace Kobian.Agent.Hosting;

/// <summary>
/// Dziennik NIEOCZEKIWANYCH błędów usługi (wyjątki, które nie są błędem domenowym z kodem KB): <c>&lt;stan&gt;/logs/agent-errors.log</c>
/// (Windows: C:\ProgramData\Kobian\logs, Linux: /var/lib/kobian/logs — tylko konto usługi i administratorzy). Każdy wpis ma krótki numer zgłoszenia,
/// który klient dostaje w trailerze <see cref="ReferenceTrailer"/> i aplikacja pokazuje przy KB-0001 — bez tego „Błąd wewnętrzny” był nie do zdiagnozowania.
/// Treść: czas, metoda, typ wyjątku, komunikat i stos (bez parametrów wywołania — hasła i dane klienta nie trafiają do dziennika). Rozmiar ≤ 1 MiB (rotacja do .1).
/// </summary>
public static class AgentErrorLog
{
    public const string ReferenceTrailer = "kb-error-ref";
    public const string FileName = "agent-errors.log";
    private const long MaxBytes = 1024 * 1024;
    private static readonly object Gate = new();

    /// <summary>Katalog dziennika (ustawiany przy starcie Agenta); null = tylko stderr.</summary>
    public static string? Directory { get; set; }

    /// <summary>Zapisuje wpis i zwraca numer zgłoszenia (8 znaków hex). Błąd zapisu dziennika nigdy nie zmienia wyniku wywołania.</summary>
    public static string Record(string operation, Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        var id = Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(4));
        var entry = new StringBuilder()
            .Append(DateTimeOffset.UtcNow.ToString("u", CultureInfo.InvariantCulture)).Append(" [").Append(id).Append("] ").Append(operation).AppendLine()
            .AppendLine(ex.ToString()).AppendLine().ToString();
        try
        {
            Console.Error.Write(entry);
        }
        catch (IOException)
        {
        }
        if (Directory is not { } dir) return id;
        try
        {
            lock (Gate)
            {
                System.IO.Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, FileName);
                if (File.Exists(path) && new FileInfo(path).Length > MaxBytes) File.Move(path, path + ".1", overwrite: true);
                File.AppendAllText(path, entry, new UTF8Encoding(false));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // dziennik niedostępny — wpis został na stderr (Event Log/journald)
        }
        return id;
    }
}
