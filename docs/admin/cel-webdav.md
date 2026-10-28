# Cel WebDAV (HTTPS) — przewodnik administratora

Stan: dostawca „webdav” zaimplementowany na `HttpClient` (.NET, bez dodatkowych bibliotek); przetestowany na serwerze WebDAV w procesie na 127.0.0.1
(prawdziwy HTTP i TLS, pinning SPKI, pełny backup → restore). **Na prawdziwych serwerach niezweryfikowany** (Nextcloud, ownCloud, Synology, QNAP,
Apache `mod_dav`, nginx, IIS) — do tego czasu traktuj cel WebDAV jako „wczesny dostęp”.

```text
kobian init Biuro --source C:\Dane --dest /remote.php/dav/files/kopie/biuro --dest-scheme webdav --host cloud.firma.pl --secret-ref file:dav-kopie --password-file C:\sekret\pw.txt
kobian init Biuro --source C:\Dane --dest /kopie/biuro --dest-scheme webdav --host 192.168.1.20 --port 5006 --host-key SHA256:<odcisk SPKI> --secret-ref file:nas-dav --password-file C:\sekret\pw.txt
```

## Połączenie
- Zawsze **HTTPS** (TLS 1.2/1.3), domyślnie port 443. Jawny HTTP nie jest dostępny w zadaniu (KB-2115); serwer bez TLS = KB-2110.
- Uwierzytelnienie **Basic** (login i hasło), wysyłane dopiero po zweryfikowaniu certyfikatu — odrzucony certyfikat nie ujawnia hasła. Digest/NTLM poza zakresem tej wersji (IIS: włącz Basic na HTTPS).
- Przekierowania nie są wykonywane (poświadczenie nie trafi na inny adres); ciasteczka i kompresja wyłączone. Proxy systemowe jest respektowane.
- Limity czasu: 60 s na operację sterującą (PROPFIND, MKCOL, MOVE, DELETE) i na bezczynność pobierania; wysyłanie: 60 s + czas przy 32 KiB/s.

## Ścieżka (folder)
Ścieżka z adresu WebDAV, od „/”, folder musi istnieć. Przykłady:

| Serwer | Ścieżka |
|---|---|
| Nextcloud / ownCloud | `/remote.php/dav/files/<użytkownik>/<folder>` |
| Synology (WebDAV Server, HTTPS 5006) | `/<folder współdzielony>/<folder>` |
| Apache `mod_dav` / nginx | ścieżka lokalizacji z konfiguracji serwera |

Spacje i polskie znaki są dozwolone (kodowane automatycznie).

## Zapis bez nadpisywania
Każdy obiekt: `PUT` do pliku tymczasowego z `If-None-Match: *`, potem `MOVE` z `Overwrite: F` — **serwer sam odmawia** (412), jeśli plik docelowy już istnieje,
więc nie ma okna wyścigu znanego z FTP. Po zapisie rozmiar jest sprawdzany (`PROPFIND`), a silnik czyta obiekt kontrolnie (V1). Brak wznawiania przerwanego
wysyłania — obiekt (pack ≤ 16 MiB) jest wysyłany w całości ponownie. Pliki tymczasowe po nieudanym `MOVE` usuwa konserwacja (prune).

**Wymaganie serwera:** limit rozmiaru żądania ≥ 64 MB (nginx `client_max_body_size`, Apache `LimitRequestBody`, PHP `upload_max_filesize` dla Nextcloud) — inaczej KB-2116.

## Poświadczenia
`SecretRef` = `file:<nazwa>` → `<katalog sekretów>\<nazwa>.json`, format jak FTPS (osobne konto dla kopii i konserwacji, D4.1):

```json
{"backup":{"user":"kobian-kopie","password":"…"},"maintenance":{"user":"kobian-admin","password":"…"}}
```

Nextcloud: użyj **hasła aplikacji** (Ustawienia → Bezpieczeństwo), nie hasła głównego konta. Zalecane konto kopii bez prawa usuwania.

## Tożsamość serwera — certyfikat i pin SPKI
Tak samo jak FTPS (`cel-ftps.md`, D4.11): certyfikat z zaufanego urzędu i zgodna nazwa → bez pinu; certyfikat samopodpisany / połączenie po IP → wymagany
pin SPKI `SHA256:<base64>` (kreator pokaże odcisk do zatwierdzenia), niezgodność = KB-2109. Piny są w katalogu tożsamości Agenta (`kobian destinations identity …`).

## Kody błędów
| Kod | Znaczenie |
|---|---|
| KB-2102 | login/hasło odrzucone (401) |
| KB-2105 | brak uprawnień (403) |
| KB-2106 | folder nie istnieje (404/409) |
| KB-2109 | certyfikat niezaufany / inny klucz |
| KB-2110 | serwer nie mówi TLS |
| KB-2112 | serwer ogranicza żądania (429/503) — ponowienie później |
| KB-2116 | obiekt za duży dla limitu serwera (413) |
| KB-2117 | serwer nie obsługuje operacji WebDAV (np. adres to zwykła strona WWW) |
| KB-2201 | brak miejsca (507) |
| KB-2301 | obiekt już istnieje — nigdy nie nadpisujemy |

## Odtwarzanie awaryjne
`kobian-restore … --dest-scheme webdav --host <h> [--port <n>] [--spki SHA256:…] --secrets-dir <dir> --secret-ref file:<nazwa> --repo-header <kobian.repo> --repo <ścieżka>`.
