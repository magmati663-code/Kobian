# Cel FTPS (serwer FTP z TLS) — przewodnik administratora (BL-34)

Stan: dostawca „ftps” (i „ftp” z flagą) zaimplementowany na bibliotece FluentFTP 55.0.0; przetestowany na serwerze w pamięci i na skryptowym
serwerze sterowania FTPS na 127.0.0.1 (prawdziwy TLS, pinning SPKI, logowanie). **Transfery na prawdziwych serwerach niezweryfikowane** —
vsftpd z `require_ssl_reuse=YES`, FileZilla Server, IIS FTP, ProFTPD: kolejka SP-04/SP-21. Do tego czasu traktuj cel FTPS jako „wczesny dostęp”.

```text
kobian init Biuro --source C:\Dane --dest /kopie/biuro --dest-scheme ftps --host nas.firma.local --secret-ref file:nas-ftp --password-file C:\sekret\pw.txt
kobian init Biuro --source C:\Dane --dest /kopie/biuro --dest-scheme ftps --host 192.168.1.20 --port 990 --host-key SHA256:<odcisk SPKI> --secret-ref file:nas-ftp --password-file C:\sekret\pw.txt
```

## Tryby połączenia (D4.3)
| Tryb | Jak wybrać | Uwagi |
|---|---|---|
| Explicit FTPS (`AUTH TLS`, port 21) | domyślny | zalecany; TLS 1.2/1.3, szyfrowany także kanał danych (`PROT P`) |
| Implicit FTPS (port 990) | `--port 990` (PROWIZORYCZNIE: port 990 = tryb Implicit) | starsze NAS |
| FTP bez szyfrowania | **niedostępny w zadaniu** (KB-2115) | hasło szłoby otwartym tekstem; dostawca „ftp” wymaga opcji `insecure=1`, definicja zadania v1 jej nie ma |

Kobian **nigdy** nie przechodzi po cichu z FTPS na jawny FTP: serwer odrzucający `AUTH TLS` = KB-2110. Kanał danych zawsze pasywny; adres
z odpowiedzi `PASV` jest ignorowany (połączenie danych na adres serwera sterowania — ochrona przed „FTP bounce” i błędnym NAT). Serwer tylko z IPv6
wymaga EPSV (opcja dostawcy `passive=epsv`, w definicji zadania jeszcze niedostępna).

## Kreator w aplikacji (SCR-03, krok 2)
Rodzaj celu **„Serwer FTPS”**: serwer, port i „Szyfrowanie połączenia” — *Standardowe (zalecane) · port 21* = Explicit, *Starsze serwery NAS · port 990* = Implicit
(tryb wynika z portu, PROWIZORYCZNIE Qb68), nazwa wpisu poświadczenia (jak wyżej — hasła kreator nie przyjmuje) i folder od „/”. Po „Dalej” kreator sprawdza
połączenie; jeśli system nie ufa certyfikatowi, pokazuje odcisk SPKI do porównania i po zatwierdzeniu zapisuje go w zadaniu (pole `hostKey`). Jawnego FTP
kreator nie oferuje.

## Tożsamość serwera — certyfikat i pin SPKI (D4.11)
1. Certyfikat z zaufanego urzędu (magazyn systemowy Windows) i nazwa zgodna z `--host` → połączenie przyjęte, pin niepotrzebny. Zmiana klucza przy ważnym łańcuchu nie jest błędem. Sprawdzane jest też unieważnienie (CRL/OCSP) — bez dostępu do listy CRL łańcuch jest uznany za nieważny i potrzebny jest pin (pkt 2).
2. Certyfikat samopodpisany, nieważny łańcuch lub nazwa niezgodna (np. łączysz się po IP) → wymagany **pin SPKI**: `SHA256:<base64>` — skrót SHA-256 klucza publicznego (SubjectPublicKeyInfo), nie odcisk całego certyfikatu, więc **przetrwa odnowienie certyfikatu z tym samym kluczem**.
3. Brak pinu albo inny klucz → **KB-2109**, kopia wstrzymana. Kreator / ProbeDestination pokazuje odcisk przedstawiony przez serwer — porównaj go poza pasmem (na serwerze), zanim zatwierdzisz.
4. Od BL-35 piny są w katalogu Agenta i zatwierdza je Administrator (`kobian destinations identity approve`), a cel „wysokiego ryzyka” wymaga pinu mimo ważnego CA — zob. `tozsamosc-serwera.md`.

Odcisk na serwerze (OpenSSL):

```sh
openssl x509 -in /etc/ssl/certs/vsftpd.pem -pubkey -noout | openssl pkey -pubin -outform der | openssl dgst -sha256 -binary | openssl base64
```

Wynik poprzedź `SHA256:` i usuń końcowe `=`. Certyfikat jest sprawdzany przed wysłaniem `USER`/`PASS` — odrzucony serwer nie dostaje hasła.
Nie ma opcji „akceptuj każdy certyfikat”.

Znane ograniczenie (FluentFTP 55.0.0): certyfikat **kanału danych** nie jest weryfikowany przez bibliotekę (sprawdzany jest kanał sterowania,
a połączenie danych idzie zawsze na ten sam adres). Dane w repozytorium są szyfrowane i uwierzytelniane (AEAD), a zapis jest weryfikowany
rozmiarem i odczytem kontrolnym — podmiana zostanie wykryta, ale nie zapobiegnie jej. Do rozwiązania w SP-04.

## Poświadczenia (PROWIZORYCZNIE Qb16)
`SecretRef` = `file:<nazwa>` → `<katalog sekretów>\<nazwa>.json` (tylko konto usługi; docelowo DPAPI — W-21), ten sam format co SMB/SFTP:

```json
{"backup":{"user":"kobian-backup","password":"<hasło>"},"maintenance":{"user":"kobian-maint","password":"<hasło>"}}
```

Zalecenie (D4.1, SEC-1.3): konto „backup” z prawem zapisu i tworzenia katalogów, **bez** `DELE`/`RMD` i bez nadpisywania (np. vsftpd
`cmds_denied=DELE,RMD`; FileZilla Server: bez „Delete”); konto „maintenance” tylko dla retencji. Sesja „backup” w Kobian i tak nigdy nie usuwa.

## Zapis bez nadpisania — jak i z jakim ograniczeniem (D4.7)
- Nowy obiekt: `STOR` do pliku tymczasowego `<nazwa>.tmp-<losowe>`, kontrola rozmiaru `SIZE` w trybie binarnym (`TYPE I`, do 3 prób co 1 s), potem `RNFR`/`RNTO`.
- Protokół FTP nie ma „zmień nazwę, ale nie nadpisuj”; na serwerach uniksowych `RNTO` i `STOR` nadpisują istniejący plik. Kobian sprawdza
  istnienie celu (`MLST`) **tuż przed** `RNTO`/`STOR` — pozostaje krótkie **okno wyścigu**: jeśli inny program utworzy ten sam plik między
  sprawdzeniem a `RNTO`, zostanie on nadpisany. W praktyce nazwy obiektów są unikalne (skróty treści) i repozytorium ma jednego pisarza; IIS
  i FileZilla Server odrzucają `RNTO` na istniejący plik — wtedy KB-2301. Pełną ochronę daje konto bez prawa nadpisywania (poziom P2, D4.12).
- Przerwany transfer: wznowienie przez `APPE` od rozmiaru potwierdzonego przez `SIZE`; niezgodny rozmiar = obiekt porzucony (KB-2401).
- Pliki `.tmp-*` po przerwanym zapisie sprząta retencja (konto maintenance).

## Wymagania wobec serwera
`FEAT` z `MLST`/`MLSD` (zalecane; bez nich listing `LIST`), `SIZE`, `REST STREAM` (odczyt zakresowy), opcjonalnie `HASH` SHA-256 (weryfikacja
po stronie serwera) i `AVBL` (wolne miejsce). vsftpd: `ssl_enable=YES`, `force_local_logins_ssl=YES`, `force_local_data_ssl=YES`, `ssl_tlsv1_2=YES`;
`require_ssl_reuse=YES` może powodować błędy kanału danych w kliencie .NET — do potwierdzenia w SP-04 (obejście: `require_ssl_reuse=NO`).
Domyślnie Kobian otwiera do 2 równoległych sesji na cel (limit np. `max_per_ip` ≥ 3 zostawia zapas); przy odmowie 421 równoległość spada (KB-2112).

## Komunikaty
| Kod | Kiedy | Co zrobić |
|---|---|---|
| KB-2109 | certyfikat niezaufany i brak pinu / inny klucz SPKI | porównaj odcisk poza pasmem, zatwierdź pin |
| KB-2110 | serwer odrzuca `AUTH TLS` | włącz FTPS na serwerze albo użyj SFTP |
| KB-2115 | jawny FTP bez potwierdzenia | użyj FTPS albo SFTP |
| KB-2102 | 530 — login/hasło odrzucone (bez ponowień) | popraw poświadczenie |
| KB-2105 | 550 przy zapisie / `MKD` | nadaj prawa do katalogu |
| KB-2106 | katalog repozytorium nie istnieje | utwórz katalog |
| KB-2201 | 452 / 552 — brak miejsca lub limit | zwolnij miejsce |
| KB-2301 | obiekt już istnieje (nie nadpisujemy) | zgłoś błąd |
| KB-2302 | 553 — nazwa niedozwolona | zgłoś błąd |
| KB-2303 | ścieżka zawiera dowiązanie (symlink) | usuń dowiązanie z repozytorium |
| KB-2401 | rozmiar po zapisie inny niż oczekiwany | brak — ponowienie; powtarza się → sprawdź serwer (tryb ASCII?) |
| KB-2112 | 421 „too many connections” | brak — mniejsza równoległość |
| KB-2001 | 421/425/426/450/451, zerwane połączenie, timeout | brak — ponowienie |
