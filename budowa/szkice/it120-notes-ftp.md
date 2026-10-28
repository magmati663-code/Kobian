# Notatki — dostawca celu FTP/FTPS (BL-34 / KOSZT-22)

Kopia: `/tmp/kb/w-ftp`. ID planu: BL-34, KOSZT-22, D4.3, D4.6, D4.7, D4.8, D4.11, D4.12 (częściowo), D4.14, D4.16 pkt 1 (421), SEC-4.6, SEC-4.7, SP-04, AC-12.2, AC-18.1, AC-04.5, CE-22.

## Co zrobiono
1. **Pakiet FluentFTP 55.0.0** (MIT) w `Kobian.Providers.Storage` (55.1.0 istnieje, ale to wydanie minor, nie łatka — zostaje wersja z planu).
2. **Port** `IFtpTransport` / `IFtpUpload` / `IFtpConnector`, `FtpProtocolException` z `FtpFailure` + kodem odpowiedzi (`Ftp/FtpPorts.cs`):
   stat (MLST, bez podążania za dowiązaniami), list (MLSD), SIZE (zawsze po `TYPE I`), odczyt od offsetu (REST+RETR), upload STOR / wznowienie APPE,
   `RenameNoReplaceAsync` (sprawdzenie istnienia → RNFR/RNTO), MKD, DELE, AVBL, HASH SHA-256.
3. **Dostawca** `FtpStorageProviderFactory` (scheme **„ftps”** domyślnie i **„ftp”** — oba w rejestrze, zgodnie z `dest_targets.scheme` w planie; „ftp”
   odrzucany PRZED połączeniem bez opcji `insecure=1` → KB-2115) + `FtpStorageSession` + `FtpObjectWriter` — ściśle wg wzoru SFTP: walidacja korzenia
   (`SftpStorageProviderFactory.NormalizeRoot`), łańcuch katalogów bez dowiązań (KB-2303), StagedRename (STOR do `.tmp-<rnd>` → SIZE → MLST celu → RNTO;
   błąd RNTO + cel istnieje = KB-2301), DirectFinal (sprawdzenie → STOR; wznowienie APPE po zgodności SIZE, inaczej KB-2401), rola Backup nie usuwa
   (baza + brak `DeleteAllowed`), `ServerHashAsync` (HASH → V1-a), SIZE po zapisie ponawiany 3× co 1 s (D4.14). Capabilities: AtomicRename|RenameAllowed|
   ResumeUpload zawsze; RangeRead ⇐ REST STREAM; Mtime ⇐ MLST; FreeSpace ⇐ AVBL; ServerSideHash ⇐ HASH SHA-256; RecommendedParallelism 2; pack 16 MiB.
4. **Mapa błędów** `FtpErrorMap` (D4.8): 421/425/426/450/451 i inne 4xx → KB-2001; 421 „too many/connections/limit” → KB-2112; 530 → KB-2102;
   452/552 → KB-2201; 550 przy zapisie/MKD/RNTO → KB-2105, przy odczycie/stat → KB-2106; 553 → KB-2302; inne 5xx → KB-2105; TLS/pin → KB-2109 (Security,
   z `PresentedIdentity`); odmowa AUTH TLS → KB-2110; jawny FTP bez flagi → KB-2115 (Security).
5. **Adapter** `FluentFtpConnector` / `FluentFtpTransport` (`Ftp/FluentFtpConnector.cs`) + czyste, testowalne części (`Ftp/FluentFtpSupport.cs`):
   `FtpOptions` (host, port 21/990, `tls`=explicit|implicit, `spki` — do 4 pinów po przecinku, `passive`=pasv|epsv, `connect_timeout_s`, `insecure`),
   `FluentFtpConfig.Build` (EncryptionMode Explicit/Implicit/None, PROT P, bez CCC, TLS 1.2|1.3, `ValidateAnyCertificate=false`, revocation on,
   `LogPassword/LogUserName/LogHost/LogToConsole=false` i brak loggera — SEC-4.6, PASV z ignorowaniem IP (`PassiveExtended`) lub EPSV, TYPE I dla
   upload/download/listing, timeouty 30 s connect / 30 s sterowanie / 60 s dane, `SelfConnectMode=Never`, `SslSessionLength=0`, bez NOOP),
   `FtpsCertificatePolicy` (D4.11: łańcuch systemowy + nazwa → OK; inaczej wymagany przypięty SPKI SHA-256; brak certyfikatu nigdy), `TlsSpkiPin`
   (`SHA256:`+base64 SHA-256(SubjectPublicKeyInfo), porównanie stałoczasowe), parsery MLST/SIZE/AVBL/HASH, tłumacz wyjątków (komunikaty tylko z kodem).
   Werdykt certyfikatu zapada w zdarzeniu `ValidateCertificate` przed USER/PASS → odrzucony serwer nie dostaje hasła (zweryfikowane testem loopback).
6. **Agent**: `FileFtpCredentialProvider` (SecretRef `file:<nazwa>`, ten sam magazyn/kontrole co SFTP/SMB, rola backup/maintenance osobno, wpis bez hasła = KB-2102),
   `AgentProviders` rejestruje `ftps` i `ftp` przez `GovernedStorageProviderFactory` (dławienie otwarć D4.16 pkt 3), `JobDestination` (scheme `ftps`:
   ścieżka uniksowa, host, port, opcjonalny pin SPKI w `hostKey`, port 990 ⇒ `tls=implicit`; id celu z host:port; `ftp` ⇒ KB-2115 / KB-0103 w definicji),
   `DestinationProbeService` (kształt ftps/ftp, opcje FTPS, KB-2109 z odciskiem → `UnknownIdentity` bez pinu / `Failed` przy przypiętym innym — jak SFTP;
   tryb „identity_probe” zbędny, bo TLS kończy się przed USER/PASS). CLI: `init --dest-scheme ftps` działa przez istniejące `--host/--port/--host-key/--secret-ref`
   (zmieniony tylko tekst usage).
7. **Dokumentacja**: `docs/admin/cel-ftps.md` + `.en.md` (tryby, pin SPKI z poleceniem OpenSSL, okno wyścigu RNTO, wymagania serwera, kody), wiersz w
   `docs/agent-job-definition.md`, indeks `docs/README.md`, `docs/kb-codes.md`, `docs/ac-coverage.md` (odświeżone), `THIRD-PARTY-NOTICES.md`.

## Okno wyścigu „rename bez nadpisania” (wymagane w zadaniu)
FTP nie ma RNTO „no-replace”; vsftpd/ProFTPD/pure-ftpd (rename(2)) nadpisują, IIS i FileZilla Server odrzucają (550/553). Implementacja: MLST celu tuż przed
RNTO (i przed STOR w DirectFinal — STOR też nadpisuje). Okno = czas między odpowiedzią MLST a wykonaniem RNTO (1 RTT + RNFR). Ograniczenie: inny pisarz,
który w tym oknie utworzy ten sam plik, zostanie nadpisany na serwerach uniksowych. Mitygacje: nazwy obiektów to skróty treści + jeden pisarz repo (blokada),
serwery odrzucające RNTO → KB-2301 (test), pełna ochrona tylko po stronie serwera (P2, D4.12). Błąd MLST ≠ „nie istnieje” (wyjątek, nie null) — transient
nigdy nie prowadzi do zapisu „na ślepo”.

## Pliki
Nowe: `src/Kobian.Providers.Storage/Ftp/{FtpPorts,FtpErrorMap,FtpStorageProvider,FtpObjectWriter,FluentFtpSupport,FluentFtpConnector}.cs`,
`src/Kobian.Agent/Secrets/FileFtpCredentialProvider.cs`, `tests/Kobian.Tests.Unit/TestDoubles/{FakeFtpServer,LoopbackFtpsControlServer}.cs`,
`tests/Kobian.Tests.Unit/Storage/{FtpStorageTests,FluentFtpAdapterTests,FluentFtpLoopbackTests}.cs`, `tests/Kobian.Tests.Unit/Agent/FtpAgentWiringTests.cs`,
`docs/admin/cel-ftps.md`, `docs/admin/cel-ftps.en.md`.
Zmienione: `Directory.Packages.props`, `src/Kobian.Providers.Storage/Kobian.Providers.Storage.csproj`, `packages.lock.json` (Providers.Storage, Agent,
Restore.Standalone, Tests.Unit, tools/Kobian.Bench), `src/Kobian.Agent/Hosting/AgentProviders.cs`, `src/Kobian.Agent/Jobs/{JobDestination,DestinationProbeService}.cs`,
`src/Kobian.Cli/Commands/CliApp.cs` (usage), `docs/kb-codes.yaml`, `src/Kobian.Domain/KbCode.cs`, `src/Kobian.UI/Resources/KbMessages{,.en}.resx`, `docs/kb-codes.md`,
`docs/README.md`, `docs/agent-job-definition.md`, `docs/ac-coverage.md`, `THIRD-PARTY-NOTICES.md`,
testy: `Agent/JobDestinationTests.cs` (przypadek `ftp` → KB-2115 zamiast KB-0103 + 9 przypadków ftps), `Agent/SmbAgentWiringTests.cs` i
`Agent/FileSftpCredentialProviderTests.cs` (liczba dostawców w rejestrze 3→5, 2→4).

## Pakiet — wiersz audytu
| Pakiet | Wersja | Licencja | CVE/ostrzeżenia | Ryzyko | Decyzja |
|---|---|---|---|---|---|
| FluentFTP | 55.0.0 (nuget.org; najnowsza 55.1.0 to minor — nie przyjęta bez re-audytu) | MIT | `dotnet list … --vulnerable --include-transitive`: brak; zero zależności przechodnich (net9.0/netstandard) | Niskie; znalezisko: certyfikat kanału danych akceptowany bez walidacji przez bibliotekę (niżej, Średnie) | **Przyjęte** (D4.3), przypięte w `Directory.Packages.props` |

## Nowe kody KB (`tools/kb.py add` + gen/resx/docs)
- KB-2109 `TlsIdentityMismatch` (security, critical) — z planu D4.8/D4.11.
- KB-2110 `FtpsNotSupported` (permanent) — z planu D4.8.
- KB-2115 `InsecureTransportNotConfirmed` (security, error) — **nowy (budowa)**: jawny FTP bez flagi „insecure” (D4.3). Do akceptacji PO.

## Testy (uruchomione)
- `Kobian.Tests.Unit.Storage.FtpStorageTests` — 52 (StagedRename/DirectFinal zapis+odczyt, TYPE I przed SIZE, odmowa nadpisania mimo serwera nadpisującego,
  wyścig przed commit i przed RNTO, Backup nie usuwa / Maintenance usuwa, porzucony tmp bez DELE, korzenie/ścieżki, dowiązania, listing, odczyt zakresowy REST,
  brak REST ⇒ brak RangeRead, zerwanie + wznowienie APPE, zły offset, SIZE 3× co 1 s, mapa 4xx/5xx, mapa błędów połączenia, KB-2109 z odciskiem,
  „ftp” bez flagi = odmowa bez połączenia, capabilities, HASH) — 52/52 OK.
- `Kobian.Tests.Unit.Storage.FluentFtpAdapterTests` — 19 (opcje, brak downgrade, host/pin walidacja, konfiguracja klienta SEC-4.6, SPKI z certyfikatów
  generowanych `CertificateRequest`, pin przetrwa odnowienie, 2 piny AC-12.2, łańcuch zaufany bez pinu, parsery, tłumacz wyjątków bez tekstu komend,
  konektor odmawia „ftp” przed rozwiązaniem poświadczeń) — 19/19 OK.
- `Kobian.Tests.Unit.Storage.FluentFtpLoopbackTests` — 7: **prawdziwy AsyncFtpClient** wobec skryptowego serwera sterowania FTPS na 127.0.0.1 (SslStream,
  certyfikat testowy): self-signed bez pinu → KB-2109 z SPKI i **bez USER/PASS**; zły pin → KB-2109; pin → logowanie po TLS, PROT P, sesja; Implicit 990;
  odmowa AUTH TLS → KB-2110 bez spadku do jawnego; 530 → KB-2102 bez sekretu w wyjątku; jawny FTP z flagą (Identity null) — 7/7 OK.
- `Kobian.Tests.Unit.Agent.FtpAgentWiringTests` — 5 (poświadczenia per rola, rejestr, „ftp” bez flagi, definicja v1 ftps/990/pin, ProbeDestination:
  UnknownIdentity z SPKI bez wysłania hasła → z pinem Ok → inny pin Failed) — 5/5 OK.
- `Kobian.Tests.Unit.Agent.JobDestinationTests` — 42/42 OK.
- Pełny `Kobian.Tests.Unit`: **1248/1248 OK** (65 s). `Kobian.Tests.Architecture`: 15/15 OK.
- Build Release `-warnaserror -m:1`: Providers.Storage, Agent, Tests.Unit, Tests.Architecture, Restore.Standalone, tools/Kobian.Bench, Kobian.UI — 0 błędów/ostrzeżeń.

## Przegląd bezpieczeństwa
Lista kontrolna: brak wyłączania walidacji certyfikatu (`ValidateAnyCertificate=false`, decyzja wyłącznie w polityce D4.11) ✔; brak downgrade FTPS→FTP ✔;
jawny FTP tylko z flagą, sprawdzany przed połączeniem i ponownie w adapterze ✔; poświadczenia przez SecretRef, rola Backup bez usuwania ✔; hasło nie trafia
do logów (logger biblioteki niepodłączony, Log* = false), komunikaty wyjątków zawierają tylko kod odpowiedzi ✔; wejścia walidowane (host — znaki, port,
pin — kształt/długość/liczba, korzeń — POSIX bez `..`/sterujących, ścieżki repo — `StoragePath`) — brak wstrzyknięcia CR/LF do komend ✔; dowiązania odrzucane ✔;
timeouty (connect 30 s, sterowanie 30 s, dane 60 s) i brak ukrytego auto-reconnectu ✔; brak MD5/SHA-1 (HASH tylko SHA-256), RNG kryptograficzny dla nazw tmp ✔;
sekrety testowe losowe ✔.
STRIDE nowych elementów:
- S (podszycie się pod serwer): pin SPKI / łańcuch CA; certyfikat sprawdzany przed USER/PASS. **Średnie — kanał danych**: FluentFTP 55.0.0 w `FtpDataStream`
  rejestruje własny handler `e.Accept = true` — certyfikat kanału danych nie jest walidowany (zweryfikowane w źródle biblioteki, commit 05c3066). Kanał danych
  idzie na IP sterowania (PASVEX/EPSV), dane repo są AEAD, zapis weryfikowany SIZE + odczyt kontrolny (V1-b) → podmiana wykrywana, ale aktywny MITM na ścieżce
  może czytać szyfrogram i psuć dostępność. Do SP-04: (a) zgłoszenie upstream / CustomStream, (b) sprawdzenie, czy wymuszone wznowienie sesji TLS
  (vsftpd `require_ssl_reuse`) blokuje obcy certyfikat.
- T (manipulacja): nadpisanie obiektu w oknie wyścigu RNTO/STOR (**Niskie**, opisane wyżej); 226 nie jest dowodem — SIZE w TYPE I.
- R: kody odpowiedzi w wyjątku (`ReplyCode`) do logu „ftp=NNN”.
- I (ujawnienie): jawny FTP ujawnia hasło — tylko z flagą, w zadaniu niedostępny (**Niskie**). Treść odpowiedzi serwera może trafić do `InnerException`
  (FtpCommandException.Message) — serwery nie odsyłają hasła, ale warto filtrować przy logowaniu łańcucha wyjątków (**Niskie**).
- D: 421 limit → KB-2112 (AIMD), dławienie otwarć przez GovernedStorageProviderFactory; parser MLST/SIZE ścisły; liczba pinów ≤ 4.
- E: rola Backup nie wywołuje DELE (test), Maintenance osobnym kontem.
Znaleziska: Wysokie — brak; Średnie — certyfikat kanału danych (wyżej); Niskie — okno wyścigu RNTO/STOR, InnerException z tekstem serwera, revocation online
może wymusić pin w sieciach bez dostępu do CRL.

## Decyzje PROWIZORYCZNE
- **Qb-NEW-ftps-implicit-port** (`JobDestination.FtpsOptions`): definicja v1 nie ma pola trybu TLS → port 990 = Implicit, inny = Explicit. Alternatywa: pole
  `tls` w definicji (ADR-0026). Koszt: mały (parser/zapis/UI).
- **Qb-NEW-ftp-parallelism** (`FtpStorageSession.Parallelism`): 2 sesje na cel (limity `max_per_ip`, NAS). Alternatywa: 3 jak SFTP. Koszt: stała.
- **Qb-NEW-ftp-passive-mode** (`FtpOptions.Parse`): domyślnie PASV z ignorowaniem adresu (`PassiveExtended`),
  EPSV opcją `passive=epsv` (plan: „EPSV → PASV”; FluentFTP nie ma trybu „EPSV, potem PASV z ignorowaniem IP”). Alternatywa: EPSV domyślnie (IPv6 OK,
  ryzyko starych NAS). Koszt: jedna wartość domyślna + test.
- **Qb-NEW-ftp-staged-rename** (komentarz w `FtpStorageSession`): FTP deklaruje AtomicRename|RenameAllowed (silnik wybiera StagedRename), choć nie ma
  RenameNoReplace (D4.12, bit 512 jeszcze nie w kontrakcie v1). Uzasadnienie: DirectFinal ma to samo okno (STOR nadpisuje), a dodatkowo zostawia obcięty
  obiekt pod nazwą docelową po awarii. Po dodaniu bitu RenameNoReplace FTP go **nie** ustawia; probe (SP-21) decyduje o P0–P2.
- **Qb-NEW-ftp-revocation**: `ValidateCertificateRevocation=true` (bezpieczniej; offline CRL ⇒ potrzebny pin). Alternatywa: NoCheck. Koszt: flaga.
- **Qb-NEW-ftp-insecure-code**: nowy KB-2115 zamiast ogólnego KB-2110 dla jawnego FTP bez flagi.

## Kolejka Windows / prawdziwe serwery (SP-04, SP-21, Q19, Q57)
Brak Dockera i serwerów — do uruchomienia na maszynie z serwerami (Windows-klient Kobian, polecenia przykładowe):
1. vsftpd 3.x z `ssl_enable=YES`, `require_ssl_reuse=YES`, `force_local_data_ssl=YES`: `kobian init T --dest /kopie --dest-scheme ftps --host <vsftpd> --host-key <SPKI> --secret-ref file:vs --password-file pw.txt`, potem 1 000 uploadów 8 MiB (bench lub `job run` z danymi 8 GiB). Oczekiwane: 0 błędów TLS (SP-04 „go”); jeśli 425/„TLS session reuse required” → udokumentować `require_ssl_reuse=NO` i rozważyć CustomStream.
2. To samo dla FileZilla Server 1.x (Explicit), IIS FTP 10 (Explicit, „Require SSL”), ProFTPD mod_tls (Implicit 990): zapis/odczyt/wznowienie (przerwanie sieci w trakcie packa → APPE), RNTO na istniejący (IIS/FileZilla: odmowa → KB-2301; vsftpd: nadpisuje — potwierdzić), MLST typu dowiązania (`OS.unix=slink`), SIZE w ASCII (CE-22), HASH SHA-256 (FileZilla), AVBL.
3. Konto bez DELE (vsftpd `cmds_denied=DELE,RMD`): backup przechodzi, prune konta maintenance usuwa.
4. Weryfikacja znaleziska kanału danych: MITM (mitmproxy z obcym certyfikatem tylko na porcie danych) — oczekiwane po poprawce: odrzucenie; dziś: akceptacja (dokumentować).
5. `max_per_ip=2` + 6 zadań (SP-24/NFR-34): 421 → KB-2112, równoległość spada, brak blokady IP.

## Niedokończone / następne kroki
- Jawny FTP w definicji zadania/CLI/kreatorze: brak pola „insecure” w `DestinationRequest`/definicji v1 (wymaga ADR-0026 + potwierdzenia w UI i ostrzeżenia
  w raporcie D4.3); dziś „ftp” = KB-2115 (dostawca obsługuje `insecure=1`).
- Kreator (SCR-03) — pole FTPS i zatwierdzanie SPKI (backend ProbeDestination gotowy; UI ma już etykiety „ftp/ftps” w kartach celów).
- Opcje `passive=epsv` i lista wielu pinów w definicji zadania (dziś jeden pin w `hostKey`).
- Ostrzeżenie w raporcie dla celu „ftp” i maszyna stanów pinów D4.11 (dest_identity) — wspólne z SFTP.
- Znalezisko „certyfikat kanału danych” — SP-04.
- `budowa/zaleznosci.md` nie edytowany (zakaz) — wiersz audytu wyżej do przeniesienia przez koordynatora.
