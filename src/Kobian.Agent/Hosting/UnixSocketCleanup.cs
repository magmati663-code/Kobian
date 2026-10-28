using System.Net.Sockets;

namespace Kobian.Agent.Hosting;

/// <summary>
/// Tryb dev/testy poza Windows (ADR-0004): po zabiciu Agenta (kill -9, awaria) w katalogu tymczasowym zostaje plik gniazda, a Kestrel nie potrafi się związać z istniejącym
/// plikiem („address already in use”) — Agent nie wstaje, dopóki ktoś nie usunie pliku ręcznie. Usuwamy WYŁĄCZNIE martwe gniazdo: plik gniazda o nazwie „*.sock” (nie dowiązanie, nie katalog),
/// do którego nikt nie nasłuchuje (próba połączenia kończy się odmową). Działające gniazdo innego Agenta zostaje nietknięte (wtedy bind zgłosi błąd, jak dotąd).
/// </summary>
public static class UnixSocketCleanup
{
    /// <returns>true, jeśli usunięto martwe gniazdo.</returns>
    public static bool RemoveIfStale(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (OperatingSystem.IsWindows()) return false;
        // Wywołujący podaje WŁASNĄ ścieżkę gniazda Agenta (ApiConventions.UnixSocketPath); dodatkowo nie ruszamy niczego, co nie wygląda na gniazdo.
        if (!path.EndsWith(".sock", StringComparison.Ordinal)) return false;
        try
        {
            var info = new FileInfo(path);
            if (info.LinkTarget is not null || Directory.Exists(path)) return false;
            if (info.Length != 0) return false; // gniazdo ma rozmiar 0; plik z treścią o takiej nazwie to nie nasze gniazdo
            // File.Exists/FileInfo.Exists zwracają false dla gniazd — istnienie sprawdzamy trybem uniksowym (FileNotFoundException = brak).
            _ = File.GetUnixFileMode(path);
            using var probe = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                probe.Connect(new UnixDomainSocketEndPoint(path));
                return false; // ktoś nasłuchuje — żywy Agent
            }
            catch (SocketException ex) when (ex.SocketErrorCode is SocketError.ConnectionRefused or SocketError.AddressNotAvailable or SocketError.NotConnected)
            {
                File.Delete(path);
                return true;
            }
            catch (SocketException)
            {
                return false; // nie wiemy — nie ruszamy (np. brak uprawnień do cudzego gniazda)
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false; // m.in. FileNotFoundException: nic do sprzątania
        }
    }
}
