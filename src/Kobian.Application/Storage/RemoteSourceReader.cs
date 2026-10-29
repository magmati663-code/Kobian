using Kobian.Domain;

namespace Kobian.Application.Storage;

public enum RemoteSourceKind
{
    File = 0,
    Directory = 1,

    /// <summary>Dowiązanie albo inny typ (urządzenie, gniazdo) — nie jest kopiowany (dowiązania nie są śledzone, D3.6).</summary>
    Other = 2,
}

/// <summary>Wpis katalogu na serwerze źródłowym: nazwa (jeden segment), rodzaj, rozmiar, data modyfikacji.</summary>
public sealed record RemoteSourceEntry(string Name, RemoteSourceKind Kind, long Length, DateTimeOffset? Modified);

/// <summary>
/// Odczyt DOWOLNYCH plików spod korzenia sesji (serwer SFTP/FTP/FTPS/WebDAV jako ŹRÓDŁO kopii) — w odróżnieniu od <see cref="IStorageSession"/>,
/// która zna tylko nazwy obiektów repozytorium. Tylko odczyt; ścieżki względne („Dokumenty/umowa 2026.pdf”) walidowane przez <see cref="RemoteSourcePath"/>
/// (bez „..”, „.”, pustych segmentów, „\\”, NUL) — odczyt nie wychodzi poza korzeń. Błędy jako <see cref="StorageException"/>.
/// </summary>
public interface IRemoteSourceReader
{
    ValueTask<IReadOnlyList<RemoteSourceEntry>> ListDirectoryAsync(string relativeDir, CancellationToken ct);

    /// <summary>Strumień całego pliku; wywołujący zamyka go przed następnym poleceniem (FTP: jeden kanał danych naraz).</summary>
    ValueTask<Stream> OpenFileAsync(string relativePath, CancellationToken ct);
}

public static class RemoteSourcePath
{
    /// <summary>Segmenty ścieżki względnej ("" = korzeń). Niepoprawna ścieżka → KB-2302 (klasa bezpieczeństwa — próba wyjścia poza korzeń).</summary>
    public static IReadOnlyList<string> Segments(string relative)
    {
        ArgumentNullException.ThrowIfNull(relative);
        if (relative.Length == 0) return [];
        var segs = relative.Split('/');
        foreach (var s in segs)
            if (s.Length == 0 || s is "." or ".." || s.IndexOfAny(['\\', '\0']) >= 0 || s.Any(char.IsControl))
                throw new StorageException(ErrorClass.Security, KbCode.InvalidObjectName, "Invalid source path.");
        return segs;
    }

    /// <summary>Pełna ścieżka POSIX na serwerze: korzeń („/” albo „/a/b”) + segmenty.</summary>
    public static string Join(string root, string relative)
    {
        var segs = Segments(relative);
        if (segs.Count == 0) return root;
        return (root == "/" ? "" : root) + "/" + string.Join('/', segs);
    }
}
