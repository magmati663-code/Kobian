using Kobian.Application.Storage;

namespace Kobian.Providers.Storage.WebDav;

public enum WebDavEntryKind
{
    File = 1,
    Collection = 2,
}

/// <summary>Wpis z odpowiedzi PROPFIND: nazwa (ostatni segment, zdekodowany), rodzaj, rozmiar, data modyfikacji.</summary>
public sealed record WebDavEntry(string Name, WebDavEntryKind Kind, long Length, DateTimeOffset? Modified);

/// <summary>
/// Port transportu WebDAV (testowalny bez serwera). Ścieżki to bezwzględne ścieżki URL po stronie serwera, NIEzakodowane („/kopie/data/ab”) —
/// kodowanie segmentów należy do adaptera. Wszystkie błędy jako <see cref="StorageException"/> (mapowanie w <see cref="WebDavErrorMap"/>).
/// </summary>
public interface IWebDavTransport : IAsyncDisposable
{
    /// <summary>Tożsamość serwera z uzgadniania TLS (pin SPKI, D4.11); null dla jawnego HTTP.</summary>
    ServerIdentity? Identity { get; }

    /// <summary>PROPFIND Depth 0; null = 404.</summary>
    ValueTask<WebDavEntry?> StatAsync(string path, CancellationToken ct);

    /// <summary>PROPFIND Depth 1 na kolekcji — wyłącznie dzieci (bez samej kolekcji).</summary>
    ValueTask<IReadOnlyList<WebDavEntry>> ListAsync(string collectionPath, CancellationToken ct);

    /// <summary>GET (z nagłówkiem Range, gdy offset &gt; 0).</summary>
    ValueTask<Stream> GetAsync(string path, long offset, CancellationToken ct);

    /// <summary>PUT całego obiektu; <paramref name="ifNoneMatch"/> = „If-None-Match: *” (412 = obiekt istnieje, KB-2301).</summary>
    ValueTask PutAsync(string path, ReadOnlyMemory<byte> body, bool ifNoneMatch, CancellationToken ct);

    /// <summary>MOVE z „Overwrite: F” — rename bez nadpisania wykonany przez serwer (RFC 4918 §9.9.3; 412 = cel istnieje).</summary>
    ValueTask MoveNoReplaceAsync(string source, string destination, CancellationToken ct);

    /// <summary>MKCOL; false = 405 (zasób już istnieje — wywołujący sprawdza, czy to kolekcja).</summary>
    ValueTask<bool> MakeCollectionAsync(string path, CancellationToken ct);

    ValueTask DeleteAsync(string path, CancellationToken ct);

    /// <summary>RFC 4331 quota-available-bytes; null, gdy serwer nie podaje.</summary>
    ValueTask<long?> QuotaAvailableAsync(string path, CancellationToken ct);
}

public interface IWebDavConnector
{
    /// <summary>Tworzy transport (bez ruchu sieciowego — pierwsze żądanie zestawia TLS i weryfikuje certyfikat przed wysłaniem poświadczeń).</summary>
    ValueTask<IWebDavTransport> ConnectAsync(DestinationConfig config, CredentialRole role, CancellationToken ct);
}
