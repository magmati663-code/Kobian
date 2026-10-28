# Rejestr przeglądów bezpieczeństwa (budowa)


## 2026-10-12 — iteracja 1 (rola 11 recenzuje; prowadzi 1 Product Owner)
Zakres: wygenerowanie `budowa/backlog.md` (tylko dokumentacja, brak kodu, brak nowych pakietów, brak sieci).
Lista kontrolna: brak sekretów/danych klientów w artefaktach — OK; brak wykonania poleceń z danych — OK (skrypt generujący czytał plan jako dane, `python3 -I`); brak zmian w `plan.md` — OK.
STRIDE nowych elementów: brak (tylko backlog).
Znaleziska: Niskie — S5(c)/(d) = nie: brak Dockera i git ogranicza testy integracyjne i historię zmian (ryzyko jakości, nie podatność); status: zgłoszone w backlogu (F), pytanie do właściciela w decyzje-budowy.md.
Wysokie: brak.

## 2026-10-12 — iteracja 2 (prowadzi 2 Architekt; współpraca 10; recenzja 11)
Zakres: BL-1 (.NET SDK 10.0.401), BL-2 (szkielet 17 projektów, Directory.*.props, NuGet.config), BL-3 (test architektury), `tools/ci.sh`, `docs/konwencje.md`.
Lista kontrolna: supply-chain SDK — oficjalne źródło, SHA-512 zgodny z metadanymi wydania (OK); NuGet — jedno źródło z `packageSourceMapping`, centralne wersje, przechodnie przypinanie, lock files, `NuGetAudit=all/low` (OK); `dotnet list package --vulnerable --include-transitive` — brak (OK); brak sekretów (OK); brak kodu wykonawczego (szkielety `return 0`).
STRIDE: nowe elementy = artefakty budowy. T (tampering pakietów) — lock files + mapowanie źródeł; I (wyciek przez telemetrię testów) — Microsoft.Testing.Platform zawiera telemetrię → wyłączona zmienną środowiskową.
Znaleziska: Niskie — telemetria Microsoft.Testing.Platform domyślnie włączona (tylko testy) → naprawione w `tools/ci.sh`. Niskie — `RestoreLockedMode` aktywny tylko z `ContinuousIntegrationBuild=true`; lokalnie lock files mogą się zmieniać → akceptowalne, CI używa `--locked`.
Wysokie: brak.

## 2026-10-12 — iteracja 3 (prowadzi 3 Silnik; współpraca 11, 13; recenzja 11)
Zakres: `Kobian.Crypto` (RepoKeys — D3.9, AesGcmCipher, Nonces), `Kobian.Format.Pack` (PackWriter/PackReader — D3.10, limity R2.3.8), `Kobian.Domain` (KobianException, ErrorClass, KbCode, port IAuthenticatedCipher), `docs/kb-codes.yaml`, wektory V-01/V-02/V-05/V-06 z niezależnej implementacji (pyca/cryptography).
Lista kontrolna: AEAD AES-256-GCM z BCL (OK); nonce deterministyczne per pack z unikalnym K_pack, trailer na zarezerwowanym nonce FF…FF, nieosiągalnym dla blobów (górne 4 B = 0) (OK); commitTag porównywany `FixedTimeEquals` przed odszyfrowaniem (OK); MAC nagłówka — weryfikacja stałoczasowa (OK); zerowanie: klucze RepoKeys, plaintext ramek i trailera po użyciu (OK; K_pack zwracany jako tablica — wywołujący odpowiada za zerowanie — Niskie, patrz niżej); parser: limity frameLength/MaxFrames/rawLength/trailer, kanoniczny CBOR, sztywna struktura bez rekursji, ścisła ciągłość ramek, arytmetyka `checked` (OK); brak sekretów w komunikatach wyjątków (OK); brak zakazanych API (OK).
Testy SEC: 13 testów negatywnych (bit flip, zamiana ramek, obcy pack, commitTag, stopka poza plikiem, blobCount, wersja, CBOR niekanoniczny/nieokreślony/zagnieżdżony, limity).
STRIDE: T/I dla packa na celu — AEAD z AAD wiążącym repoId, packId, pozycję, chunkId; S (podmiana packa z innego repo) — commitTag + packId; D (DoS parsera) — limity przed alokacją (trailer ≤ 8 MiB, ramka ≤ 8 MiB+64).
Znaleziska: Niskie — `DerivePackKey`/`DeriveObjectKey` zwracają `byte[]`, zerowanie po stronie wywołującego (w testach `AesGcmCipher` kopiuje klucz; oryginał niezerowany) → zadanie: owijka `SecretBuffer` w iteracji silnika (BL-24). Niskie — `ReadBlob` alokuje bufor wg frameLength z trailera (≤ 8 MiB) — akceptowalne w limicie.
Wysokie: brak.

## 2026-10-12 — iteracja 4 (prowadzi 4 Transport; współpraca 3, 13; recenzja 11)
Zakres: kontrakt `Kobian.Application.Storage` (D4.6), `StorageSessionBase` (walidacja ścieżek, rola przy usuwaniu, wykrywanie współbieżności), dostawca `local` (D4.7), mapa błędów (D4.8), 8 nowych kodów KB-2xxx (`tools/kb.py`), 20 testów.
Lista kontrolna: walidacja ścieżek — biała lista znaków, brak `..`/`.`/pustych segmentów/absolutnych, kontrola `StartsWith(root+sep)` po `GetFullPath` (obrona warstwowa) (OK); dowiązania — odmowa, gdy korzeń lub dowolny istniejący składnik ścieżki jest linkiem (KB-2303), listowanie pomija reparse (OK); brak nadpisywania — `FileMode.CreateNew`, `File.Move(overwrite:false)` + kontrola istnienia (OK); usuwanie w sesji Backup — `InvalidOperationException` niezależnie od uprawnień systemu plików (OK, test); trwałość — `Flush(flushToDisk:true)` przed rename (OK); limity — zapis ponad `expectedLength` odrzucony (OK); brak sekretów w komunikatach (OK).
STRIDE (dostawca local): T — atak przez podłożony symlink/junction w repo → odmowa; wyścig TOCTOU między sprawdzeniem linku a otwarciem pliku (Niskie, patrz niżej); D — zapełnienie dysku → KB-2201 trwały; E — brak (proces działa z uprawnieniami usługi; ACL katalogu repo — D2.12, wymaga Windows).
Znaleziska: Niskie — TOCTOU: link może zostać utworzony między `Resolve` a `FileStream` (lokalny napastnik z prawem zapisu do katalogu repo). Mitygacja docelowa na Windows: otwieranie z `FILE_FLAG_OPEN_REPARSE_POINT` i weryfikacja uchwytu (`GetFinalPathNameByHandle`) w `Platform.Windows` → kandydat do backlogu (BL-50). Niskie — `AbortAsync` w trybie StagedRename usuwa własny plik tymczasowy także w sesji Backup — zgodne z D4.7 (artefakt uruchomienia), ale odnotowane względem zasady D4.1.
Wysokie: brak.

## 2026-10-12 — iteracja 5 (prowadzi 5 Bazy danych/ERP; współpraca 3, 8; recenzja 11)
Zakres: `Providers.Sources.MsSql` (MsSqlQueries, MsSqlBackupCommand, TSql, PermissionScript, SqlErrorMap, SqlVersionPolicy), `Providers.Sources.Erp` (profile-dane JSON, ErpDetector), 13 kodów KB-3xxx, 37 testów.
Lista kontrolna: SQL — nazwy baz jako parametry `sysname`, QUOTENAME wyłącznie po stronie serwera, ścieżka stagingu jako parametr `sp_executesql` (SEC-5.1) (OK, test z próbą wstrzyknięcia); `USE [db]` z D-03 zastąpione dynamicznym SQL z QUOTENAME(@db) — brak sklejania po stronie klienta (OK); skrypt uprawnień — escaping `]`→`]]`, `'`→`''`, odrzucenie znaków sterujących, brak `sa`/`sysadmin`/`dbcreator`, `CREATE DATABASE` tylko w wariancie z weryfikacją z ostrzeżeniem (D5.7, SEC-5.3) (OK); profile ERP — JSON z `UnmappedMemberHandling.Disallow`, `MaxDepth = 8`, zapytania kontrolne ograniczone do pojedynczego SELECT bez `;`/komentarzy (obrona przed złośliwą/błędną edycją profilu) (OK); zapytania kontrolne nie odczytują danych osobowych (tylko COUNT/MAX dat — D5.11) (OK).
STRIDE: T (profil ERP podmieniony na dysku) — profile wbudowane jako zasoby podpisanego zestawu; E (skrypt uprawnień nadający za dużo) — test negatywny; I (zapytania kontrolne) — tylko agregaty.
Znaleziska: Niskie — walidacja „pojedynczy SELECT” w profilach jest heurystyczna (np. `SELECT … INTO` przeszłoby) → wykonawca musi uruchamiać zapytania kontrolne na połączeniu z `SET TRANSACTION ISOLATION LEVEL READ COMMITTED` i loginie bez prawa zapisu (rola db_backupoperator nie ma INSERT) — wymaganie dla BL-40 dopisane tutaj. Niskie — `SqlErrorMap` nieznane numery → KB-0001 (trwały); do uzupełnienia o więcej numerów po testach W-3.
Wysokie: brak.

## 2026-10-12 — iteracja 6 (prowadzi 6 UX/UI; współpraca 2, 14; recenzja 11)
Zakres: `Kobian.UI.Wizard.WizardFlow` (D6.7), `Kobian.Application.Jobs` (ValidationIssue, RetentionPolicy, JobValidator S1/S4/S5, lista popularnych haseł), `Kobian.Crypto.RecoveryKey` (K14.1, AC-28.1), 5 kodów KB-0xxx, 28 testów.
Lista kontrolna: klucz odzyskiwania — 256 bitów losowych + CRC-16 (wykrywa 100% pojedynczych literówek — test wyczerpujący 55 × 31), dekodowanie kanoniczne (bity dopełnienia = 0), zerowanie buforów, porównanie grup w czasie stałym (OK); hasła — min. 12, maks. 256, lista popularnych, zakaz nazwy użytkownika/komputera, brak reguł złożoności (NIST) (OK); walidacja wspólna dla UI/CLI/importu (D6.7 reguła 3) (OK); kreator — po utworzeniu repo brak drogi wstecz przez ekran klucza (AC-28.1) (OK, test niezmienników 2 000 losowych sekwencji).
STRIDE: I (klucz odzyskiwania na ekranie/schowku) — WDA_EXCLUDEFROMCAPTURE i czyszczenie schowka po 60 s to zadania UI na Windows (BL-46, kolejka W); S/E — brak nowych interfejsów.
Znaleziska: Średnie — lista popularnych haseł to tymczasowa lista własna (~30 słów); pełna lista wymaga weryfikacji licencji źródła (plan: „Do weryfikacji licencji”, SEC-6.5) → pytanie Qb5 do właściciela/zadanie audytu; do tego czasu ochrona słabsza niż w planie. Niskie — kolejność bajtów CRC (big-endian) i wariant CCITT-FALSE nie były określone w K14.1 → zapisane w kodzie i do dopisania w specyfikacji formatu (docs/format-spec).
Wysokie: brak.

## 2026-10-12 — iteracja 7 (prowadzi 7 Harmonogramy; współpraca 2, 13; recenzja 11)
Zakres: `Kobian.Scheduler` — RunStateMachine (D7.8), CronSchedule + MisfirePlanner (D7.2, D7.9 OnStart, AC-03.1), LockManager (D7.10), 8 kodów KB-4xxx; zmiana globalna `InvariantGlobalization=false`; 33 testy (w tym współbieżny test blokad 4 000 operacji).
Lista kontrolna: maszyna stanów bez I/O, przejście spoza tabeli → Failed + KB-4902 + Critical (NFR-10) (OK); DoS — skanowanie wystąpień cron ograniczone do 100 000 (wyrażenie „co minutę” przez długą nieobecność) (OK); blokady — sekcja krytyczna, brak hold-and-wait, semafor globalny nie „wycieka” przy pauzie (OK, test); brak sekretów (OK); zależność Cronos z listy przyjętych, wersja przypięta (OK).
STRIDE: D (zalew wystąpień po długim wyłączeniu) — limit skanu + polityka misfire tworzy co najwyżej 1 uruchomienie; T (zmiana zegara przez użytkownika) — detekcja skoku D7.9 jeszcze niezaimplementowana (pętla — następny przyrost); E — brak.
Znaleziska: Niskie — wyłączenie InvariantGlobalization zwiększa powierzchnię zależną od ICU systemu (Server 2016 — W-4); w zamian CA1305 wymusza kulturę niezmienną w kodzie deterministycznym (naprawiono 10 miejsc w PermissionScript). Niskie — `MisfirePlanner.UniformJitter` używa `Random.Shared` (nie dotyczy bezpieczeństwa).
Wysokie: brak.

## 2026-10-12 — iteracja 8 (prowadzi 8 Odtwarzanie/DR; współpraca 3, 11; recenzja 11)
Zakres: `Kobian.Engine.Restore` — WindowsPathMapper (D8.9 R1–R6, R9), port IRestoreTargetProbe (R7/R8), RestoreRangePlanner (R2.8.1), 7 kodów KB-5xxx, 27 testów.
Lista kontrolna: traversal — odrzucenie `\`, `/`, `:` (ADS), NUL, znaków sterujących, `.`/`..` przed jakimkolwiek łączeniem ścieżek (R1/R2) (OK); nazwy urządzeń Windows w tym warianty z rozszerzeniem, spacją i indeksami górnymi ¹²³ (R3) (OK); długość ścieżki z uwzględnieniem prefiksu `\\?\` (R6) (OK); konflikty wielkości liter bez nadpisania (R9, AC-08.7) (OK); „nic po cichu” — każda zmiana nazwy w notatkach raportu (OK); planer odrzuca nakładające się ramki (obrona przed złośliwym indeksem) (OK).
STRIDE: T/E (złośliwe repo z nazwami `..`, `CON`, ADS) — R1–R3; T (junction podłożony w folderze docelowym) — R8 za portem, implementacja Windows wymagana (W-5); wykonawca restore jeszcze nie istnieje, więc brak ekspozycji.
Znaleziska: Niskie — R8 niezaimplementowane → warunek DoD BL-43 przed uruchomieniem wykonawcy restore. 
Wysokie: brak.

## 2026-10-12 — iteracja 9 (prowadzi 9 Monitoring; współpraca 11, 7; recenzja 11)
Zakres: `Kobian.Domain.Serialization.Jcs` (RFC 8785, podzbiór bez float), `Kobian.Notifications` — AuditChain (D9.9), WebhookSignature + WebhookUrlPolicy (D9.10, SEC-9.3), AlertStateMachine (D9.7), CloudEvent (D9.6); 5 kodów KB-6xxx; 28 testów.
Lista kontrolna: łańcuch audytu — kanoniczny JSON, hash z poprzednim, wykrywa modyfikację, usunięcie, przepisanie ogona (przez kotwicę) (OK); porównania hashy/podpisów stałoczasowe (OK); webhook — znacznik czasu w podpisie, okno 300 s, wiele podpisów przy rotacji, brak wczesnego wyjścia (OK); SSRF — tylko https:443, bez userinfo, bez localhost/.local/.internal/nazw jednoczłonowych, bez literałów IP prywatnych/link-local/CGNAT/multicast/IPv6 ULA/zmapowanych IPv4 (OK); JCS — odrzuca duplikaty kluczy (ochrona przed niejednoznacznością), limit głębokości 32 (OK); CloudEvents — tryb minimalny bez nazwy zadania, limit 64 KiB (OK).
STRIDE: R (wypieranie się operacji) — audyt w łańcuchu + kotwice; T (przepisanie audytu przez admina z dostępem do katalogu) — wykrywalne tylko z kotwicą zewnętrzną (e-mail/repo) — kotwice do zaimplementowania; S (podszycie pod webhook) — HMAC z timestampem; I (SSRF do metadanych chmury 169.254.169.254) — blokada statyczna.
Znaleziska: Średnie — walidacja URL jest statyczna; DNS rebinding (nazwa publiczna rozwiązywana do adresu prywatnego) wymaga sprawdzenia adresu w `SocketsHttpHandler.ConnectCallback` przy każdym połączeniu — wymaganie dla nadawcy (BL-45), bez niego wysyłka webhooków NIE może zostać włączona. Niskie — `AuditEntry.ParamsJson` musi być zamaskowany przed wpisem (SEC-11.5) — odpowiedzialność wywołującego; do testu w warstwie usług.
Wysokie: brak.

## 2026-10-12 — iteracja 10 (prowadzi 10 Jakość/CI; współpraca 3, 1; recenzja 11)
Zakres: `tests/Kobian.Tests.Support` (CorpusGenerator — D10.9), `tools/Kobian.CorpusGen`, `tests/corpus/small-mixed.json`, `tools/ac-coverage.py` (raport AC Must 11/56 oznaczonych), `tools/windows/run-queue.ps1`, ADR-0001 (JSON zamiast YAML).
Lista kontrolna: generator — tylko dane syntetyczne, odmowa nadpisania (`FileMode.CreateNew`, katalog docelowy musi być pusty), brak danych osobowych (OK); AES-ECB na liczniku użyty WYŁĄCZNIE do generowania danych testowych (nie w produkcie; projekt testowy poza `src/`) (OK, odnotowane); skrypt PowerShell — bez pobierania z internetu, bez podnoszenia uprawnień, zapis wyniku tylko do `budowa\` (OK); ac-coverage — tylko odczyt planu i testów (OK).
STRIDE: brak nowych elementów produktu.
Znaleziska: Niskie — `run-queue.ps1` wymaga `-ExecutionPolicy Bypass` dla jednego uruchomienia (bez zmiany polityki systemowej) — udokumentowane; podpis skryptów — przy potoku wydań (D10.6).
Wysokie: brak.

## 2026-10-12 — iteracja 11 (prowadzi 11 Bezpieczeństwo; współpraca 10, 3; przegląd przekrojowy)
Zakres: przegląd całości kodu po it. 2–10; nowe: `BannedApiTests` (10 reguł: BinaryFormatter, ScpClient, obejście walidacji certyfikatów, UseShellExecute, cmd/powershell, MD5/SHA-1, DES/ECB, Random, składanie SQL, unsafe — z listą wyjątków i testami reguł), `SecretScanner` + skan repozytorium (NFR-11), fuzzing mutacyjny 4 parserów (50 000 przypadków, ziarna stałe), audyt zależności.
Audyt zależności: `dotnet list package --vulnerable --include-transitive` — 0 podatnych we wszystkich 21 projektach (2026-10-12). Pakiety bezpośrednie: System.Formats.Cbor 10.0.12, Cronos 0.13.0, xunit.v3 4.0.2 (testy) — wszystkie z listy przyjętych w planie.
Skan sekretów: 0 trafień w src/docs/tools/tests (wyjątki: definicje wzorców skanera i przykłady w jego testach).
**Znalezisko (Średnie, NAPRAWIONE w tej iteracji):** `PackReader.DecodeTrailer` przepuszczał `OverflowException` z `CborReader.ReadUInt64` dla liczby ujemnej (typ główny 1) — wyjątek spoza modelu błędów mógł przerwać proces restore/check zamiast zgłosić KB-1304 (DoS przez spreparowany trailer; trailer jest uwierzytelniony AEAD, więc wymaga klucza — stąd Średnie, nie Wysokie). Poprawka: mapowanie na KB-1304 + test regresyjny z wektorem.
Inne: Niskie — fuzzing bez pokrycia kodu (mutacje losowe) — kandydat SharpFuzz (do audytu, plan „Do audytu przed użyciem”). Otwarte z poprzednich iteracji: TOCTOU dostawcy local (BL-50), R8 restore (W-5), DNS rebinding webhooków (BL-45), lista haseł (Qb5).
STRIDE (przekrojowo, stan po rundzie 1 do it. 11): S — commitTag, MAC nagłówka, podpis webhooka; T — AEAD z AAD, łańcuch audytu; R — audyt; I — szyfrowanie zawsze, tryb minimalny zdarzeń, brak sekretów w komunikatach; D — limity parserów, fuzzing; E — brak usuwania w sesji Backup, ścieżki ograniczone do korzenia. Brak jeszcze: autoryzacji API (D2.9 — Agent nie istnieje), ACL usługi (D2.12 — Windows).
Wysokie: brak.

## 2026-10-12 — iteracja 12 (prowadzi 12 R&D; współpraca 7, 13; recenzja 11)
Zakres: prototyp kandydata RD-5 (waga 40,5; zgodny z planem — D7.8 „symulacja deterministyczna”): `SchedulerCore` (pętla D7.9: OnStart z misfire, Tick z detekcją skoku zegara ściennego vs monotonicznego, unikalność wystąpień przez port magazynu) + symulacja 8 ziaren × 20 dni (wyłączenia 1 h–3 dni, skoki zegara ±) z niezmiennikami I1–I4.
**Znalezisko (Średnie, NAPRAWIONE):** po cofnięciu zegara systemowego (`lastHandled` w przyszłości) start usługi kończył się `ArgumentException` z Cronos w `CronSchedule.Between` — usługa nie startowałaby po ręcznej zmianie daty wstecz (DoS harmonogramu; brak kopii bez alertu). Poprawka: pusty zakres przy from ≥ to + test symulacyjny (ziarno 6) jako regresja.
Lista kontrolna: brak I/O w rdzeniu, brak sekretów, brak nowych zależności (OK). Skok w przód: wystąpienia z przeskoczonego okresu NIE są uruchamiane seriami (ochrona przed zalewem uruchomień; A17/KB-4201 do alertu) (OK).
Wysokie: brak.

## 2026-10-12 — iteracja 13 (prowadzi 13 Wydajność; współpraca 3, 12; recenzja 11)
Zakres: `Kobian.Engine.Compression.BlockCompressor` (zstd 3, magazynowanie bez kompresji przy braku zysku), `tools/Kobian.Bench`, pomiar w `budowa/pomiary.md`, nowa zależność ZstdSharp.Port 0.8.8 (z listy przyjętych).
Lista kontrolna: dekompresja do bufora o długości dokładnie `rawLength` ≤ limit (bomba dekompresji → KB-1302, test 4 MiB zer deklarowane jako 1 KiB) (OK); błędne dane zstd → wyjątek domenowy (2 000 przypadków losowych, w tym z poprawną magią) (OK); audyt zależności — 0 podatnych (OK); pomiar opisany uczciwie jako nie-referencyjny (OK).
Znaleziska: Niskie — ZstdSharp.Port to port zarządzany jednego opiekuna; plan B (Brotli BCL) zachowany w enum `CompressionAlgorithm.Brotli`.
Wysokie: brak.

## 2026-10-12 — iteracja 14 (prowadzi 14 Dokumentacja/zgodność; współpraca 1, 6; recenzja 11)
Zakres: `docs/README.md` (stan modułów), `docs/format-spec/pack-v1.md` (zaimplementowany układ + doprecyzowania: offset trailera, wariant i kolejność CRC klucza odzyskiwania), `docs/kb-codes.md` (generowany, 58 kodów), `THIRD-PARTY-NOTICES.md`, poprawka sekcji C backlogu (opisy i estymaty spike’ów z rejestru planu — wcześniej błędnie pobrane kolumny).
Lista kontrolna: dokumentacja nie ujawnia sekretów ani danych klientów (skaner NFR-11 obejmuje docs/) (OK); licencje zależności zgodne z open-core Apache-2.0 (MIT/Apache-2.0/BSD) (OK); komunikaty KB bez danych wrażliwych (przegląd 58 wpisów) (OK).
Znaleziska: Niskie — pełne teksty licencji i SBOM do instalatora (BL-48).
Wysokie: brak.

## 2026-10-12 — iteracja 15 — przegląd całościowy rundy 1 (prowadzi 11; z 12 i 14)
Audyt zależności: `dotnet list package --vulnerable --include-transitive` — 0 podatnych w 22 projektach; restore w trybie zablokowanym (`--locked`, 22 pliki `packages.lock.json`) — zielony. Bezpośrednie: System.Formats.Cbor 10.0.12, ZstdSharp.Port 0.8.8, Cronos 0.13.0 (produkt); xunit.v3 4.0.2 (testy). Wszystkie z listy „Przyjęte” w planie.
Skan sekretów: repozytorium kodu (test NFR-11) — 0; folder `budowa/` — 0 trafień (wzorce kluczy prywatnych, kluczy AWS, przypisań haseł).
Zakazane API: test `BannedApiTests` zielony (1 wyjątek udokumentowany: `Random.Shared` w jitterze misfire).
Uprawnienia instalatora/usługi: niezaimplementowane (D2.12, D2.15 — BL-17, BL-48) — przegląd w rundzie, w której powstaną.
KB/logi: przegląd 58 komunikatów — brak danych klienta i sekretów; logowania jeszcze brak.
Znaleziska rundy: 2 × Średnie (naprawione: DecodeTrailer OverflowException; start schedulera po cofnięciu zegara), 1 × Średnie otwarte (lista popularnych haseł — Qb5), Niskie otwarte: TOCTOU local (BL-50), R8 restore (W-5), DNS rebinding webhooków (BL-45), ICU na Server 2016 (W-4).
Threat model — aktualizacja: nowe elementy rundy to biblioteki bez interfejsów sieciowych/IPC; granice zaufania bez zmian (cel ↔ host: AEAD, commitTag, ścieżki; administrator SQL ↔ Kobian: skrypt uprawnień). Największe niezamodelowane jeszcze ryzyko: Agent (named pipe, autoryzacja per metoda D2.9, step-up) — wymaga Windows i SP-02.
Wysokie: brak.

## 2026-10-12 — iteracja 16 (prowadzi 1 PO; współpraca 10, 14; recenzja 11)
Zakres: `specs/*.feature` (23 pliki, 64 scenariusze; generator `tools/gen-specs.py`), poprawka `tools/ac-coverage.py` (kategorie z kanonicznej tabeli D1.4 zamiast 1.4 — K11.1; pokrycie Must: 12/59), ścieżka krytyczna rundy 2 w backlogu.
Lista kontrolna: generatory tylko czytają plan i piszą do `specs/`/`docs/` (OK); scenariusze nie zawierają danych klientów ani sekretów (OK); brak kodu produktu (OK).
Znaleziska: Niskie — wcześniejszy raport pokrycia liczył kategorie z nieaktualnej tabeli 1.4 (np. US-18 jako S zamiast M) — naprawione.
Wysokie: brak.

## 2026-10-12 — iteracja 17 (prowadzi 2 Architekt; współpraca 3, 7; recenzja 11)
Zakres: `Kobian.Catalog.Sqlite` — CatalogBootstrap (D2.11, D2.14), migracje core/job/sched/run/repo/snap (D2.11, D7.7, R2.3.9), CatalogWriter (D2.14 pkt 1); zależności Microsoft.Data.Sqlite.Core 10.0.12 + SQLitePCLRaw 3.0.5 (SQLite 3.53.4); 9 testów.
Lista kontrolna: utwardzenie — `SQLITE_DBCONFIG_DEFENSIVE` (test: modyfikacja sqlite_schema odrzucona), `trusted_schema=OFF`, `cell_size_check=ON`, `load_extension` niedostępne (test), brak FTS5/modułów wirtualnych w schemacie (OK); wersja biblioteki ≥ 3.53.2 sprawdzana przy starcie, niższa → brak startu (OK); `quick_check` przed migracją → tryb Awaryjny (OK); migracje w jednej transakcji, kopia przed migracją (OK, test); wszystkie zapytania parametryzowane (OK); klucze obce włączone (test: naruszenie FK wycofuje całą transakcję sterowania).
STRIDE: T (spreparowany plik katalogu) — DEFENSIVE + trusted_schema + zasada „nie otwieramy obcych katalogów” (D2.14 pkt 3); D (zalew zapisów masowych) — kolejka ograniczona 100 000 + priorytet sterowania (NFR-10).
Znaleziska: Niskie — ścieżka kopii `VACUUM INTO` przekazana parametrem (bez sklejania) (OK); kopie `catalog.pre-*.db` dziedziczą ACL katalogu — do weryfikacji przy ACL usługi (D2.12, Windows). Niskie — Microsoft.Data.Sqlite.Core deklaruje SQLitePCLRaw 2.1.x; wymuszenie 3.0.5 przez przypięcie przechodnie — ryzyko niezgodności API przy aktualizacji (testy katalogu są bramką).
Wysokie: brak.

## 2026-10-12 — iteracja 18 (prowadzi 3 Silnik; współpraca 11, 12; recenzja 11)
Zakres: `Kobian.Format.Metadata` — TreeCodec (D3.8, R2.3.5: kanoniczny CBOR, sortowanie bajtowe UTF-8, walidacja nazw R2.3.8), MetadataEnvelope (nagłówek 48 B + AES-GCM z AAD = nagłówek ‖ repoId); wektory V-07 (drzewo, treeId) i V-08 (niekanoniczne) z implementacji referencyjnej; KB-1305/1306; 15 testów (w tym fuzzing dekodera 20 000).
Lista kontrolna: dekoder — tryb Canonical, sztywny kształt (klucze 1–15, nieznany klucz = błąd), limity liczby wpisów/ADS/zakresów sparse/identyfikatorów, długości identyfikatorów, brak rekursji (drzewa są osobnymi blobami), OverflowException mapowany (lekcja z it. 11) (OK); nazwy — zakaz `/`, `\`, NUL, `.`/`..`, samotnych surogatów, > 1 024 B (OK); koperta — pola zarezerwowane muszą być zerowe, objectId z nazwy porównywany, typ obiektu w magii i w kluczu K_obj (podwójne wiązanie), AAD z repoId (OK).
STRIDE: T (podmiana snapshotu na indeks lub z innego repo) — test odrzucenia 3 wariantów; D — limity przed alokacją (`List` z ograniczoną pojemnością początkową).
Znaleziska: Niskie — xUnit zamienia samotne surogaty w danych `InlineData` na U+FFFD — test przeniesiony do `Fact` (pułapka narzędzia testowego, odnotowana w konwencjach do dopisania).
Wysokie: brak.

## 2026-10-12 — iteracja 19 (prowadzi 4 Transport; współpraca 13, 7; recenzja 11)
Zakres: `Kobian.Providers.Storage.Throttling` — TokenBucket (D4.9, zegar monotoniczny, pojemność 2 s, zmiana profilu bez resetu), BandwidthLimiter (cel → globalny, porcje ≤ 256 KiB); 9 testów (NFR-33 algorytmicznie).
Lista kontrolna: DoS — limit porcji 256 KiB, wskazówka oczekiwania ≤ 100 ms (szybka reakcja na anulowanie), brak akumulacji tokenów ponad 2 s po bezczynności (OK); niezależność od zegara ściennego (zmiana czasu/DST nie odblokowuje ruchu) (OK); wątkobezpieczeństwo — sekcja krytyczna (OK).
Znaleziska: Niskie — minimalny limit 128 KiB/s na strumień (D4.9 „Ryzyka”) jest stałą, ale automatyczne zmniejszanie równoległości jeszcze niezaimplementowane (pula sesji).
Wysokie: brak.

## 2026-10-12 — iteracja 20 (prowadzi 5 Bazy danych/ERP; współpraca 8, 11; recenzja 11)
Zakres: `Kobian.Providers.Sources.MsSql.Staging` (D5.10, K16.1), `ChainEvaluator` (D5.2, reguła R2.5.3), `ControlComparison` (D5.11), port `IStagingAcl`; 16 testów.
Lista kontrolna: nazwy plików stagingu losowe (nie ujawniają nazwy bazy) (OK); runId walidowany wzorcem (brak traversal) (OK); sprzątanie — tylko katalogi o nazwie runId, bez aktywnych, bez dowiązań (test: symlink poza staging nie jest czyszczony), usuwa wyłącznie pliki bezpośrednio w katalogu uruchomienia (OK); usunięcie z weryfikacją braku (AC-02.1, K16.1) (OK).
STRIDE: I (plik .bak z danymi ERP w stagingu) — ACL per uruchomienie (D5.10) wymaga Windows (port), krótki czas życia + sprzątanie po 1 h; E (podłożenie linku w stagingu przez konto SQL z prawem zapisu) — sprzątanie nie podąża za linkami.
Znaleziska: Średnie (otwarte, zależne od Windows) — do czasu implementacji `IStagingAcl` katalog stagingu dziedziczy ACL `%ProgramData%` → wykonawca backupu MSSQL NIE może zostać włączony bez ACL (warunek DoD BL-40, wpis W-6).
Wysokie: brak.

## 2026-10-12 — iteracja 21 (prowadzi 6 UX/UI; współpraca 14, 2; recenzja 11)
Zakres: `Kobian.UI.Wizard.WizardViewModel` (D6.7: „Dalej” = walidacja w usłudze, potem przejście), port `IWizardService`, `KbText` (zasoby KbMessages PL/EN generowane `tools/kb.py resx` z rejestru D1.8), zależność CommunityToolkit.Mvvm 8.4.2; 5 testów.
Lista kontrolna: UI nie zawiera reguł walidacji (jedno źródło po stronie usługi — D6.7 reguła 3) (OK); komunikaty z rejestru bez danych technicznych/sekretów (SEC-11.5) (OK); brak zakazanych API; audyt zależności 0 podatnych (OK).
Znaleziska: Niskie — rejestr KB ma akcję tylko po polsku; w kulturze EN akcja spada do PL (do uzupełnienia pola `action.en` — dług dokumentacyjny). Niskie — test ustawia `CurrentUICulture` globalnie (wpływ tylko na testy).
Wysokie: brak.

## 2026-10-12 — iteracja 22 (prowadzi 7 Harmonogramy; recenzja 11; przebieg zaplanowany, bez .NET SDK)
Zakres: tylko weryfikacja DDL D7.7 (migracje job/sched/run) skryptem `tools/verify-sched-ddl.py` (Python sqlite3, dane syntetyczne, baza w pamięci); zero zmian kodu C#.
Lista kontrolna: ograniczenia CHECK dla type/misfire/kind/cause, klucze obce, kaskady i unikalność `run_occurrence` (KB-4901) działają (14/14); zapytania parametryzowane (OK); brak sekretów.
Znaleziska: Niskie — test uruchomiono na SQLite 3.37.2 (Python), nie na 3.53.x z DEFENSIVE; zachowanie DDL tożsame, ale test C# na docelowej bibliotece pozostaje bramką.
Wysokie: brak.

## 2026-10-12 — iteracja 23 (prowadzi 8 Odtwarzanie i DR; recenzja 11; przebieg zaplanowany, bez .NET SDK)
Zakres: migracja `run/0002_restore.sql` (dziennik D8.8), `tools/verify-restore-ddl.py` (Python sqlite3, dane syntetyczne, baza w pamięci, 13/13 OK), test C# `RestoreJournalSchemaTests` — **niezweryfikowany kompilatorem**.
Lista kontrolna: DDL bez dynamicznego SQL, brak sekretów (kolumna `caller_sid` to SID, nie poświadczenie); `plan_sha256 NOT NULL` wymusza wiązanie wznowienia z planem; FK blokuje osierocone pliki/sesje; migracja dopisuje się do istniejącego katalogu (kopia VACUUM INTO przed migracją — D2.11).
STRIDE: T (podmiana celu między przerwaniem a wznowieniem) — kontrola `plan_sha256` po stronie wykonawcy (BL-43, jeszcze nie ma); R (ścieżka `dst` w dzienniku to dane, nie polecenie — przy wznowieniu musi przejść ponownie `MapPath` D8.9 i R8).
Znaleziska: Niskie — `restore_done_packs` nie ma FK do `restore_sessions` (zgodnie z D8.8 dosłownie); osierocone wiersze możliwe, sprzątanie w wykonawcy. Niskie — brak CHECK `chunks_done <= chunks_total` (D8.8 go nie przewiduje); wymusić w wykonawcy/teście. Niskie — weryfikacja na SQLite z Pythona (nie 3.53.x z DEFENSIVE).
Wysokie: brak.

## 2026-10-13 — iteracja 24 (prowadzi 9 Monitoring i powiadomienia; recenzja 11; kompilator: SDK 10.0.401 w środowisku budowy)
Zakres: reguły A01–A17 jako dane (`Alerts/Rules/alert-rules.json`, zasób osadzony), `AlertRuleCatalog` (walidacja schematu), `AlertConditions` (8 funkcji), `AlertEvaluator`; kody KB-6111/6116/6117.
Lista kontrolna: brak interpretera wyrażeń (SEC-19.3) — nieznana funkcja/argument/flaga/pole, duplikat, komentarz w JSON, brak A01–A17 = FormatException; cisza nocna wyłącznie dla ostrzeżeń (reguła nie może jej włączyć dla krytycznych/bezpieczeństwa); zakres alertu to nieprzejrzysty identyfikator `^[A-Za-z0-9_.:-]{1,128}$` — ścieżki, UNC i puste odrzucane (SEC-9.6, brak ścieżek w kluczu deduplikacji i temacie); brak I/O, sieci, sekretów i Process.Start; zero nowych pakietów; nieznana data łatki sama nie wyzwala A16 (brak fałszywych alarmów, ale też brak sygnału — do decyzji przy implementacji odczytu WMI).
STRIDE: T (podmiana pliku reguł) — zasób w assembly, podpis assembly w BL-10/instalatorze; nadpisanie progów przez Admina przez katalog + audyt jeszcze nie zaimplementowane; D (zalew zakresów) — ewaluator iteruje po dostarczonych zakresach, limit liczby zakresów po stronie dostawcy faktów (do dodania z adapterem katalogu).
Znaleziska: Niskie — brak limitu liczby zakresów w `Tick`. Niskie — A12 (zewnętrzny) nie ma jeszcze producenta wyniku. Wysokie: brak.
Weryfikacja: build -warnaserror 0 ostrzeżeń; testy Unit 316/316, Architecture 13/13 (Linux, bez Category=Windows); audyt zależności: 0 podatnych; test skanera sekretów zielony. Przy okazji potwierdzono kompilatorem test z it. 23 (RestoreJournalSchemaTests) i it. 22 — wcześniejsze oznaczenie „niezweryfikowany kompilatorem” nieaktualne.

## 2026-10-13 — iteracja 25 (prowadzi 10 Jakość/CI; recenzja 11; kompilator: SDK 10.0.401 w środowisku budowy, SHA-512 zgodny)
Zakres: `tools/ci.sh` (etapy, weryfikatory DDL, audyt jako bramka), `docs/konwencje.md`; zero zmian C#, zero nowych pakietów.
Lista kontrolna: skrypt nie składa poleceń z danych, brak `eval`, Python uruchamiany z `-I`; brak sekretów i sieci poza restore NuGet; audyt zależności: 0 podatnych (wcześniej błąd audytu nie powodował porażki — poprawione).
STRIDE: T (podmiana skryptu CI) — skrypt w repo, zmiany podlegają przeglądowi; R — tabela czasów nie zawiera danych klienta.
Znaleziska: Niskie — czasy etapów z środowiska budowy mogą być odczytane jako pomiar NFR-20 (opisane w skrypcie jako informacyjne). Wysokie: brak.
Weryfikacja: `ci.sh --locked`: build 0 ostrzeżeń, Unit 316/316, Architecture 13/13, DDL OK, audyt 0 podatnych.

## 2026-10-13 — iteracja 26 (prowadzi 11 Bezpieczeństwo; kompilator: SDK 10.0.401 w środowisku budowy, SHA-512 zgodny z releases.json)
Zakres: `AlertEvaluator` — limit `MaxScopesPerRule` = 10 000 zakresów na regułę w ticku (domknięcie znaleziska Niskiego z it. 24, SEC-3.3); przekroczenie = `InvalidOperationException` (fail closed — ciche obcięcie zamknęłoby alerty „znikających” zakresów). Test `Scope_count_over_limit_fails_closed_instead_of_truncating`. Zero nowych pakietów.
Lista kontrolna: brak I/O, sieci, sekretów i Process.Start w zmianie; komunikat wyjątku zawiera tylko kod reguły i liczby (bez zakresów/ścieżek); walidacja zakresu regexem bez zmian.
STRIDE: D (zalew zakresów) — ograniczone limitem; wywołujący (host Agenta) musi obsłużyć wyjątek jako błąd ticku, nie jako zatrzymanie usługi (do zrobienia przy hoście, BL-15).
Znaleziska: Niskie — A12 (zewnętrzny) nadal bez producenta wyniku. Wysokie: brak.
Weryfikacja: `tools/ci.sh --locked`: build -warnaserror 0 ostrzeżeń, Unit 317/317, Architecture 13/13, weryfikatory DDL OK, audyt 0 podatnych.

## 2026-10-13 — iteracja 27 (prowadzi 12 R&D; recenzja 11; przebieg zaplanowany, bez .NET SDK)
Zakres: `tools/model-d3-14-chaos.py` (model protokołu D3.14, Python, pamięć, dane syntetyczne), wpis w `pomiary.md`; zero zmian C#, zero nowych pakietów (tylko biblioteka standardowa).
Lista kontrolna: brak sieci, plików poza pamięcią, `Process.Start`, sekretów; losowość wyłącznie deterministyczna (ziarno), uruchamiane `python3 -I`.
STRIDE: T/R — model nie zastępuje testu na kodzie; wynik oznaczony jako MODEL, nie jako zamknięcie NFR-26/SP-18.
Znaleziska: Niskie — przy DirectFinal ucięta końcówka snapshotu po awarii (4/300) musi być odrzucana przez czytnik (suma/AEAD) i sprzątana przez rekoncyliację D8.2; do testu w implementacji (Q54). Wysokie: brak.

## 2026-10-13 — iteracja 28 (prowadzi 13 Wydajność i niezawodność; recenzja 11; przebieg zaplanowany, bez .NET SDK, dysk środowiska pełny)
Zakres: `tools/model-d3-15-bloom.py` (model filtra Bloom D3.15), wpis w `pomiary.md`, status SP-19 w backlogu; zero zmian C#, zero nowych pakietów (tylko biblioteka standardowa).
Lista kontrolna: brak sieci, plików poza pamięcią, `Process.Start`, sekretów; dane syntetyczne deterministyczne; uruchamiane `python3 -I`; SHA-256 użyty jako funkcja mieszająca (nie do celów bezpieczeństwa uwierzytelnienia).
STRIDE: T/R — model nie zastępuje pomiaru kodu; wynik oznaczony jako MODEL, SP-19/NFR-22 pozostają otwarte. D — rozmiar filtra ograniczony 10 bitami/wpis; w implementacji limit wpisów i rozmiaru filtra (SEC-3.3) do dodania.
Znaleziska: Niskie — wartość FP ≈ 0,8% zależy od równomiernego haszowania; chunkId (HMAC-SHA256) jest jednostajny, ale implementacja musi użyć niezależnych fragmentów chunkId do podwójnego haszowania (nie ponownie haszować w sposób wprowadzający korelację). Wysokie: brak.
Weryfikacja: model uruchomiony (3 rozmiary, wyniki w pomiary.md); kompilator niedostępny — zmiany nie dotyczą C#.


## 2026-10-13 — iteracja 29 (prowadzi 14 Dokumentacja; recenzja 11; przebieg zaplanowany, bez .NET SDK)
Zakres: `docs/README.md` (stan modułów, tabela narzędzi repo, zasada „zmierzone/model/niezmierzone”); zero zmian C#, zero nowych pakietów.
Lista kontrolna: zmiana wyłącznie dokumentacyjna; brak sekretów, adresów klientów, ścieżek produkcyjnych; brak wykonywania poleceń.
STRIDE: R — dokumentacja jawnie rozróżnia model od pomiaru (przeciwdziała zawyżaniu statusu NFR).
Znaleziska: Wysokie: brak.

## 2026-10-13 — iteracja 30 (przegląd rundy 2; prowadzi 11 z 12 i 14; SDK 10.0.401 w chmurze sesji, SHA-512 zgodny)
Zakres: przegląd całościowy rundy. `tools/ci.sh --locked`: restore/build -warnaserror/testy/DDL/audyt — OK; 0 podatnych pakietów (z przechodnimi); SecretScanner i BannedApiTests zielone. Uprawnienia instalatora/usługi: bez zmian (BL-17/48 nierozpoczęte). KB-kody: brak danych klienta w komunikatach (przegląd kb-codes.yaml bez zmian od it. 24).
Znaleziska: Wysokie: brak. Niskie (przeniesione): DNS rebinding webhooków, TOCTOU local (Windows), A12 bez producenta.

## 2026-10-13 — iteracja 32 (prowadzi 2 Architekt; współpraca 3; recenzja 11; SDK 10.0.401 w chmurze sesji)
Zakres: `RepoPaths` (układ obiektów R2.3), `IndexCodec`, `SnapshotCodec` (R2.3.5, D3.14 pkt 4). Zero nowych pakietów.
Lista kontrolna: parsery z limitami (MaxPacks 100 000, MaxFrames, MaxStrings 4 096, 32 KiB/napis — SEC-3.3), Canonical CBOR, nieznane klucze < 32 odrzucane, ≥ 32 pomijane; identyfikatory o stałej długości; `TryParseId` przyjmuje wyłącznie 32 znaki hex (brak traversal z listingu celu). Fuzzing mutacyjny 3 000 przypadków indeksu w teście.
STRIDE: T — snapshot/indeks szyfrowane kopertą MetadataEnvelope (AEAD), kodek uruchamiany po uwierzytelnieniu; D — limity.
Znaleziska: Niskie — `sourceRoots` (klucz 16) zawiera ścieżki klienta: dopuszczalne wyłącznie wewnątrz zaszyfrowanego obiektu, nigdy w logach/zdarzeniach (do sprawdzenia w it. 39). Wysokie: brak.
Weryfikacja: ci.sh --locked: build 0 ostrzeżeń, Unit 323/323, Arch 13/13, audyt 0 podatnych.

## 2026-10-13 — iteracja 33 (prowadzi 3 Silnik; współpraca 4, 11; SDK 10.0.401 w chmurze sesji)
Zakres: `BackupPipeline` (BL-22, R2.3.6, D3.14), `VerifiedUploader` (D4.14 V1-a + odczyt kontrolny SHA-256), `MetadataStore` (zstd + koperta K_obj, losowy nonce), `FileSystemSource` (port `ISourceTree`). Zero nowych pakietów.
Lista kontrolna: klucze pakietów i obiektów zerowane po użyciu; nonce metadanych losowe (D3.9), packi — deterministyczne per indeks bloku z kluczem per pack (D3.10); dekompresja metadanych ograniczona MaxPayload (bomba); źródło: ścieżki względne sprawdzane względem korzenia, dowiązania zapisywane jako Symlink i nieśledzone; ścieżki pominiętych plików nie trafiają do wyjątków ani KB (zwracane tylko w wyniku w procesie); brak usuwania w sesji Backup; porównanie hashy stałoczasowe.
STRIDE: T/I — błąd zapisu repo nie może dać Succeeded (I-1, test AC-08.4 dla packa/indeksu/snapshotu), I-2 potwierdzenie bloku dopiero po zweryfikowanym uploadzie, I-3 sprawdzane przed commit; D — pack w pamięci ≤ cel + 4 MiB.
Znaleziska: Średnie (otwarte, nie blokuje) — dedup trzymany w słowniku hex w RAM bez limitu (D3.15 warstwy 2–3 i NFR-05 nie zaimplementowane; RD-20/SP-19). Niskie — brak D3.17 (plik zmieniany w trakcie odczytu) w porcie platform-neutralnym. Wysokie: brak.
Weryfikacja: ci.sh --locked: build 0 ostrzeżeń, Unit 330/330 (w tym AC-08.4 ×4 punkty awarii), Arch 13/13, audyt 0 podatnych.

## 2026-10-13 — iteracja 34 (prowadzi 4 Transport; współpraca 3, 13; recenzja 11; SDK w chmurze sesji)
Zakres: `RepoReader` (odczyt bloku z uwierzytelnionego packa, kontrola rozmiaru i SHA-256 packa z indeksu, dekompresja z limitem, `chunkId = HMAC(raw)` stałoczasowo), test `MemoryStorage` z wstrzykiwaniem awarii (tylko w projekcie testów), test SP-18 in-proc. Zero nowych pakietów.
Lista kontrolna: restore traktowany jako parser niezaufanych danych (pack > 2 GiB odrzucany, typ bloku sprawdzany dwukrotnie, indeks porównywany z hashem ze snapshotu); klucze packów zerowane; MemoryStorage egzekwuje kontrakt (brak nadpisywania, Backup nie usuwa).
STRIDE: T — podmiana packa/indeksu wykrywana hashem; D — RepoReader buforuje cały pack w RAM (≤ rozmiar z indeksu) — do zastąpienia odczytem zakresowym R2.8.1 (Niskie).
Znaleziska: Niskie — częściowe snapshoty po awarii przy DirectFinal i po cichym obcięciu zostają na celu (nieczytelne); rekoncyliacja D8.2 musi je ignorować i raportować. Wysokie: brak.
Weryfikacja: ci.sh --locked: build 0 ostrzeżeń, Unit 332/332, Arch 13/13, audyt 0 podatnych.

## 2026-10-13 — iteracja 35 (prowadzi 5 Bazy/ERP; współpraca 3, 8; recenzja 11; SDK w chmurze sesji)
Zakres: `StagedFileSource` (plik ze stagingu jako źródło potoku), `DbSnapshotMeta` (klucz 14 dbMeta), test trybu Database. Zero nowych pakietów.
Lista kontrolna: ścieżka stagingu (losowa, zawiera runId) nie trafia do snapshotu — tylko nazwa logiczna `database.bak` i etykieta; nazwa wpisu = jeden segment; plik stagingu będący dowiązaniem odrzucany; wymóg `WITH CHECKSUM` egzekwowany przy budowie dbMeta; brak poświadczeń w dbMeta (nazwa serwera i bazy — dane klienta wyłącznie wewnątrz zaszyfrowanego snapshotu).
STRIDE: I — `server`/`database` w dbMeta nie mogą trafić do zdarzeń i logów (do weryfikacji it. 39); T — LSN jako tekst invariant.
Znaleziska: Wysokie: brak. Uwaga: brak testu z prawdziwym SQL Server (S5c — W-3).
Weryfikacja: ci.sh --locked: build 0 ostrzeżeń, Unit 334/334, Arch 13/13, audyt 0 podatnych.

## 2026-10-13 — iteracja 36 (prowadzi 6 UX/UI; współpraca 2, 14; recenzja 11; SDK w chmurze sesji)
Zakres: CLI `kobian` (BL-47): `CliApp` (status, job run, snapshots, restore), port `IAgentClient`, `UnavailableAgentClient` (do czasu BL-14), kody 0/1/2/3/4/64, JSON `kobian.cli.<rodzaj>/1`; KB-7006, KB-7010. Zero nowych pakietów (test Unit dostał referencję projektu — lock: 20 pakietów bez zmian).
Lista kontrolna: CLI nie referuje silnika ani dostawców (test architektury zielony — granica D2.x zachowana); komunikaty na stderr zawierają tylko kod KB, bez ścieżek i danych zadania; JSON budowany Utf8JsonWriter (escaping); brak Process.Start; brak sekretów w argumentach (sekrety tylko przez secretRef — US-29, do eksportu/importu).
STRIDE: S — uwierzytelnienie wywołującego po stronie Agenta (D2.9, interceptor — BL-14), CLI nie podejmuje decyzji o uprawnieniach; D — brak usługi = natychmiastowy kod 3.
Znaleziska: Wysokie: brak. Niskie — `restore --to` przyjmuje dowolną ścieżkę: walidacja i AccessCheck wywołującego muszą być w usłudze (D8.9, CallerContext) — test przy BL-14.
Weryfikacja: ci.sh --locked: build 0 ostrzeżeń, Unit 341/341, Arch 13/13, audyt 0 podatnych.

## 2026-10-13 — iteracja 37 (prowadzi 7 Harmonogramy; współpraca 3; recenzja 11; SDK w chmurze sesji)
Zakres: port `IJobRunner` + `JobOutcome` (Application), mapowanie `BackupResult → JobOutcome` (Engine, D3.14 pkt 1), `RunExecutor` (Scheduler, D7.8: Queued → Running → wynik → RetryDue). Zero nowych pakietów.
Lista kontrolna: Failed nigdy nie staje się Succeeded; ponowienie tylko dla Repo-Write (przejściowy) i w limicie prób; Integrity (KB-1403) i brak źródła (KB-1405) bez ponowień; wyjątek JobRunnera = Failed KB-0001 (komunikat wyjątku nie trafia do rekordu — brak wycieku danych); anulowanie = Cancelled.
STRIDE: D — pętla ponowień ograniczona RetryLimit; opóźnienie wstrzykiwane (backoff ustala Agent).
Znaleziska: Wysokie: brak. Niskie — `catch (Exception)` na granicy wykonawcy (świadome, z komentarzem; log wyjątku z maskowaniem — przy hoście Agenta).
Weryfikacja: ci.sh --locked: build 0 ostrzeżeń, Unit 348/348, Arch 13/13, audyt 0 podatnych.

## 2026-10-13 — iteracja 38 (prowadzi 8 Odtwarzanie; współpraca 3, 14; recenzja 11; SDK w chmurze sesji)
Zakres: `RestoreExecutor` (BL-43, D8.1, V1 fileSha256), `OfflineRestore` (logika `kobian-restore`, BL-44, D8.3), `DevKeyFile` (ADR-0002), KB-5105. Zero nowych pakietów.
Lista kontrolna: nazwy z repo traktowane jako niezaufane (ValidateName + kontrola wyjścia poza katalog docelowy + porównanie nazwy pliku); katalog docelowy i istniejące katalogi będące dowiązaniami odrzucane (KB-5104); FileMode.CreateNew — nigdy nie nadpisuje (KB-5105); plik z niezgodnym hashem usuwany (tylko własny, właśnie utworzony) i raportowany KB-5201; podmiana packa wykryta (SHA-256 packa z indeksu + AEAD) — Failed; dowiązania z kopii nie są odtwarzane; restore offline otwiera repo poświadczeniem Backup (bez usuwania); w komunikatach tylko kody KB i liczności, bez ścieżek.
STRIDE: E — restore jako usługa musi dodatkowo sprawdzać prawa wywołującego do ścieżki docelowej (CallerContext/AccessCheck, D2.9) — w Agencie, BL-14; T — TOCTOU między kontrolą dowiązania a utworzeniem pliku na Windows (BL-50, W-5).
Znaleziska: **Średnie (otwarte, ADR-0002)** — klucz główny w jawnym pliku deweloperskim do czasu BL-24; dopuszczalne wyłącznie dla danych syntetycznych, pytanie Qb11. Niskie — TOCTOU jak wyżej. Wysokie: brak.
Weryfikacja: ci.sh --locked: build 0 ostrzeżeń, Unit 352/352 (backup→restore bit w bit, brak nadpisywania, podmiana packa, kobian-restore list/restore/zły klucz), Arch 13/13, audyt 0 podatnych.

## 2026-10-13 — iteracja 39 (prowadzi 9 Monitoring; współpraca 7, 14; recenzja 11; SDK w chmurze sesji)
Zakres: `RunFinishedEvent` (`pl.kobian.run.finished.v1`, D9.6), README stanu modułów. Zero nowych pakietów.
Lista kontrolna (domknięcie uwag z it. 32 i 35): dane zdarzenia z zamkniętej listy pól — bez `sourceRoots`, `dbMeta` (serwer, baza), ścieżek pominiętych plików i komunikatów wyjątków; identyfikatory tylko [A-Za-z0-9-] (odrzuca ścieżki), kod KB tylko KB-dddd (odrzuca dowolny tekst), status/cause z listy; tryb minimalny usuwa `job.name`; limit 64 KiB z koperty.
STRIDE: I — wyciek danych klienta przez webhooki zablokowany typami i walidacją (test); T — podpis webhooka D9.10 istnieje (it. 9).
Znaleziska: Wysokie: brak. Niskie (przeniesione): DNS rebinding webhooków.
Weryfikacja: ci.sh --locked: build 0 ostrzeżeń, Unit 358/358, Arch 13/13, audyt 0 podatnych.

## 2026-10-13 — iteracja 40 (rola 10, recenzja roli 11)
Zakres: tools/ci.sh — nowy etap `ac-raport` (python3 -I tools/ac-coverage.py). Bez zmian C#, bez nowych pakietów.
STRIDE: etap uruchamia lokalny skrypt repo w trybie izolowanym (-I), bez sieci, bez sekretów, zapis tylko docs/ac-coverage.md — brak nowych powierzchni.
Znaleziska: brak (Wysokie 0, Średnie 0, Niskie 0). Weryfikacja: `bash -n tools/ci.sh` OK; ac-coverage.py uruchomiony (AC 64, Must z testem 18/59). Budowa .NET niezweryfikowana — brak SDK na tym urządzeniu.

## 2026-10-13 — iteracja 41 (rola 11, przegląd BL-24: pliki kluczy)
Zakres: `KeyFiles`, `KeyFileLoader`, zmiana `OfflineRestore`, wektory V-03/V-04. Build -warnaserror 0 ostrzeżeń, Unit 374/374; audyt zależności: brak podatnych, brak nowych pakietów.

| Obszar | Wynik |
|---|---|
| Krypto | PBKDF2-HMAC-SHA256 z BCL (600 000 iter, sól 32 B z CSPRNG), AES-256-GCM, nonce 12 B z CSPRNG (jednorazowy klucz KEK+nonce na plik), AAD = JCS(obiekt bez wrapped/revoked); bez własnych konstrukcji |
| Downgrade (SEC-13.2, L4) | `iter` poza [600 000; 10 000 000] odrzucane PRZED derywacją (test z int.MaxValue nie zawisa); zmiana iter/salt/keyId/type/createdAt/pola dodatkowego → błąd AAD; wszystkie przyczyny → KB-1102 (brak wyroczni) |
| Zerowanie | KEK, bajty hasła, bufor pliku hasła, MK po wyprowadzeniu RepoKeys — `ZeroMemory`; `string` hasła nie da się wyzerować (ograniczenie .NET, Niskie) |
| Wejścia | plik klucza ≤ 16 KiB, MaxDepth 8, plik hasła ≤ 1 KiB; hasło tylko z pliku (`--password-file`), nie z argumentów/zmiennych środowiskowych (brak wycieku do listy procesów) |
| Logi | komunikaty wyjątków bez hasła i MK (test w kobian-restore: stderr nie zawiera hasła) |
| Znaleziska | Niskie: (1) hasło jako `string` w pamięci do GC; (2) `DevKeyFile` (ADR-0002) nadal akceptowany przez kobian-restore — usunąć w M1; (3) `repoId` jawnie z `--repo-id` do czasu `kobian.repo` (Qb12). Średnie/Wysokie: brak |
| Do przeglądu zewnętrznego (SEC-13.1) | specyfikacja D3.9–D3.11 + V-01…V-04 w zakresie przeglądu przed M2 |

## 2026-10-13 — iteracja 42 (rola 12 prowadzi, rola 11 recenzuje) — zakres: etap E2E w ci.sh + EndToEndTests
| Obszar | Wynik |
|---|---|
| Dane testowe | wyłącznie syntetyczne (losowe bajty, klucze losowe w teście), katalog tymczasowy usuwany w Dispose; brak sekretów w repo/logach |
| ci.sh | brak `Process.Start` z danych, brak pobierania skryptów; etap E2E wymaga ≥ 1 testu (nie przechodzi „na pusto”) |
| Toolchain | SDK z oficjalnego źródła, SHA-512 zgodny; audyt zależności: brak podatnych pakietów (dotnet list --vulnerable, chmura) |
| Znaleziska | Wysokie/Średnie: brak. Niskie: `.toolchain/dl/sdk.tgz` pozostaje w folderze (brak zgody na usuwanie) |

## 2026-10-13 — iteracja 43 (rola 11, przegląd BL-24 / RepoHeader)
Zakres: RepoHeader (kobian.repo, JCS+MAC), KeyFileLoader (header), OfflineRestore.
| Znalezisko | Waga | Status |
|---|---|---|
| Brak wysokich. Kolejność weryfikacji: rozmiar ≤ 64 KiB, MaxDepth 8 → MAC (FixedTimeEquals) → dopiero potem features/repoId/keys; jednolity KB-1104 bez wyroczni; podrobiony nagłówek (inny MK, to samo repoId) odrzucony testem (SEC-7.3, AS-04 po stronie biblioteki) | — | OK |
| PeekRepoId czyta repoId przed MAC (konieczne do derywacji kluczy) — wynik używany wyłącznie jako wejście HKDF, potem Verify porównuje stałoczasowo | Niskie | zaakceptowane |
| Sprawdzenie „znanych snapshotów” z SEC-7.3 (wyzwalacz USB) nie jest częścią biblioteki — wymaga Agenta/BL-15 | Średnie | otwarte (BL-35/USB) |
| Dedup bez limitu RAM (D3.15) z it. 41 | Średnie | otwarte |

## 2026-10-13 — iteracja 44 (rola 11, przegląd BL-24 / RepoProvisioner)
Zakres: RepoProvisioner.Create/Revoke, KeyFiles.MarkRevoked, testy.
| Znalezisko | Waga | Status |
|---|---|---|
| Brak wysokich. MK i repoId z CSPRNG, MK zerowany po opakowaniu (finally); wynik Create nie zawiera materiału tajnego | — | OK |
| Revoke weryfikuje MAC nagłówka przed przepisaniem (podróbka → KB-1104); odmawia unieważnienia ostatniego klucza i klucza spoza listy; stary plik klucza bez revoked po unieważnieniu odrzucany przez KeyFileLoader (klucz spoza body.keys) — test | — | OK |
| Unieważnienie nie zmienia MK: dostęp kogoś, kto skopiował wcześniej MK/plik klucza + hasło, pozostaje (rotacja MK = re-encrypt repo, poza zakresem; udokumentować w docs przy podłączeniu do UI) | Średnie | otwarte (BL-24/D8.3) |
| Dane testowe: hasła losowe, katalog tymczasowy usuwany w Dispose | — | OK |

## 2026-10-13 — iteracja 45 (przegląd rundy 3, rola 11)
Zakres: audyt zależności, skan sekretów, banowane API, fuzzing kodeków indeksu i snapshotu (SEC-3.3), przegląd znalezisk rundy.
| Znalezisko | Waga | Status |
|---|---|---|
| `dotnet list package --vulnerable --include-transitive`: 0 podatnych we wszystkich 22 projektach (SDK 10.0.401, chmura) | — | OK |
| Skan sekretów (test SecretScanner/NFR-11) i test BannedApi w Unit/Arch zielone; Unit 399/399, Arch 13/13, E2E 1 | — | OK |
| Nowy test fuzzingu IndexCodec + SnapshotCodec (3 × 20 000 mutacji/obcięć, ziarno 4545): wyłącznie KobianException, brak innych wyjątków i zawieszeń. To fuzzing losowy, nie sterowany pokryciem | — | OK |
| Dedup bez limitu RAM (D3.15) — nadal otwarte, kandydat RD-20 | Średnie | otwarte |
| Unieważnienie klucza nie zmienia MK (rotacja MK poza zakresem) — kandydat RD-21 | Średnie | otwarte |
| Wysokie/Krytyczne: brak | — | — |

## 2026-10-13 — iteracja 45 (przegląd rundy 3, rola 11)
Zakres: audyt zależności, skan sekretów, banowane API, fuzzing kodeków indeksu i snapshotu (SEC-3.3), przegląd znalezisk rundy.
| Znalezisko | Waga | Status |
|---|---|---|
| `dotnet list package --vulnerable --include-transitive`: 0 podatnych we wszystkich projektach (SDK 10.0.401, chmura) | — | OK |
| Skan sekretów (SecretScanner/NFR-11) i BannedApi zielone; Unit 399/399, Arch 13/13, E2E 1 | — | OK |
| Nowy test fuzzingu IndexCodec + SnapshotCodec (3 × 20 000 mutacji/obcięć, ziarno 4545): wyłącznie KobianException. To fuzzing losowy, nie sterowany pokryciem | — | OK |
| Dedup bez limitu RAM (D3.15) — nadal otwarte, kandydat RD-20 | Średnie | otwarte |
| Unieważnienie klucza nie zmienia MK (rotacja MK poza zakresem) — kandydat RD-21 | Średnie | otwarte |
| Wysokie/Krytyczne: brak | — | — |

## Iteracja 46 (rola 1 prowadzi; recenzja roli 11) — kontrakt gRPC, host Agenta, klient CLI
Zakres: agent.proto, AgentCallInterceptor, AgentHost, CatalogAgentBackend, GrpcAgentClient. STRIDE: S — brak uwierzytelnienia SID do czasu SP-02 (pipe CurrentUserOnly, fail-closed; PROWIZORYCZNIE, ADR-0004); T — limit wiadomości 64 KiB serwera / 4 MiB klienta; R — brak audytu wywołań API (BL-45); I — komunikaty błędów tylko KB-xxxx, EnableDetailedErrors=false, brak ścieżek i danych klienta; D — termin 30 s na wywołania bez wait, ConnectTimeout 5 s; E — metoda spoza mapy ról = odmowa (test), Reader nie wywoła RunJob/Restore (test).
| Zapytania SQL w CatalogAgentBackend parametryzowane ($job), połączenie ReadOnly z utwardzeniem CatalogBootstrap | — | OK |
| Klient CLI nie przyjmuje nazwy pipe ze zmiennej środowiskowej (podstawienie pipe, SP-02) | — | OK |
| Gniazdo Unix (dev) 0600 w katalogu tymczasowym — nie dla produkcji (ADR-0004) | Niskie | zaakceptowane |
| Rola stała Operator w Program.cs Agenta do czasu SP-02 (Windows) | Średnie | otwarte (BL-17/SP-02), mitygacja: CurrentUserOnly |
| Brak ACL pipe dla grup Kobian i brak limitu równoległych wywołań — do BL-17 | Niskie | otwarte |
| Wysokie/Krytyczne: brak; audyt zależności czysty; SecretScanner/BannedApi zielone | — | OK |

## Iteracja 47 (rola 2 prowadzi; recenzja roli 11) — CatalogJobService, JobDefinition v1, RunJob w Agencie
Zakres: Agent/Jobs/CatalogJobService.cs, JobDefinition.cs, CatalogAgentBackend.RunJobAsync, BackupRequest.RunId. STRIDE: S — wywołujący RunJob = stała rola Operator do SP-02 (bez zmian, pipe CurrentUserOnly); T — definicja zadania parsowana ściśle (≤ 64 KiB, MaxDepth 8, ścieżki bezwzględne, 1 źródło, tylko „local”, packTargetSize 1–512 MiB); R — wiersze run_runs/run_transitions z kodem KB (brak audytu API — BL-17); I — komunikaty KB-0103 bez ścieżek (test), hasło tylko z pliku wskazanego w definicji, nie trafia do katalogu/logów; D — jedno uruchomienie zadania naraz (KB-4103), definicja ograniczona rozmiarem, wait=false nie kolejkuje bez limitu (jedno aktywne na zadanie); E — Agent czyta pliki wskazane w definicji (źródło, klucz): zapis definicji wymaga roli Admin (BL-17, do czasu SP-02 zamknięty pipe).
| Kontrola | Waga | Status |
|---|---|---|
| Zapytania SQL w CatalogJobService parametryzowane; zapisy wyłącznie przez CatalogWriter (D2.14) | — | OK |
| Brak sekretów w katalogu/logach; klucz zwalniany (using) po uruchomieniu | — | OK |
| Źródło/klucz/cel z definicji = zaufanie do roli zapisującej definicję (traversal nie dotyczy: ścieżki bezwzględne od administratora; FileSystemSource nie podąża za dowiązaniami) | Niskie | zaakceptowane do BL-17 |
| wait=false: wyjątek zadania obserwowany, wynik w katalogu; brak anulowania po rozłączeniu klienta | Niskie | otwarte (BackgroundTaskRegistry) |
| Wysokie/Krytyczne: brak; audyt zależności czysty (0 nowych pakietów) | — | OK |


## Iteracja 48 (rola 3 prowadzi; recenzja roli 11) — RepoInitService, usunięcie DevKeyFile i --repo-id (BL-24)
Zakres: Agent/Jobs/RepoInitService.cs, Crypto/KeyFileLoader.cs (nagłówek i hasło obowiązkowe), Engine/Restore/OfflineRestore.cs, usunięty Crypto/DevKeyFile.cs (→ _to_delete/). STRIDE: S — brak nowego uwierzytelnienia (pipe CurrentUserOnly, SP-02 bez zmian); T — init nie nadpisuje istniejącego kobian.repo (KB-1003), klucz wysyłany przed nagłówkiem, odczyt kontrolny V1-b przez VerifiedUploader; R — brak; I — hasło tylko w pliku wskazanym ścieżką (nie w definicji, wyniku ani komunikatach; test sprawdza definicję i plik klucza); D — limit 1 KiB pliku hasła, nazwa zadania ≤ 128 znaków; E — rola Backup (bez usuwania) na celu.
| Kontrola | Waga | Status |
|---|---|---|
| Brak ścieżek ADR-0002/0003 w kodzie: klucz jawny nie jest akceptowany (test: KB-1002), repoId tylko z nagłówka z MAC | — | OK |
| Zapytania SQL parametryzowane, zapis przez CatalogWriter (D2.14); kolejność: cel → lokalne kopie → wiersz job_jobs | — | OK |
| Ścieżki w InitJobRequest muszą być bezwzględne; walidacja przed jakimkolwiek zapisem (test: brak kobian.repo po odmowie) | — | OK |
| Hasło jako string w pamięci do GC (ReadPasswordFile) | Niskie | otwarte (znane od it. 38) |
| Lokalne kopie klucza opakowanego w katalogu stanu Agenta bez ACL (Windows: D2.12 / BL-17) | Niskie | otwarte do BL-17 |
| Init nie jest jeszcze wystawiony przez gRPC/CLI (brak powierzchni ataku) | — | OK |
| Wysokie/Krytyczne: brak; audyt zależności czysty (0 nowych pakietów); SecretScanner wyłapał zmienną „password =” → przemianowana (skaner działa) | — | OK |

## Iteracja 49 (rola 4 prowadzi; recenzja roli 11) — RPC InitJob + `kobian init` (BL-24, BL-14, BL-15)
Zakres: agent.proto (InitJob/InitJobArgs/InitJobReply), MethodPolicy (Operator, modyfikująca), AgentGrpcService, CatalogAgentBackend.InitJobAsync, Program.cs (wiring RepoInitService), Cli: InitRequest/InitResult, GrpcAgentClient.InitJobAsync, CliApp `init`. STRIDE: S — bez zmian (pipe CurrentUserOnly, rola Operator + rewizja klienta jak RunJob; test: Reader → kod 3); T — walidacja ścieżek bezwzględnych w Agencie, NUL w ścieżce odrzucany w CLI (kod 64); R — brak; I — hasło wyłącznie jako ścieżka pliku (nie w argumentach, wiadomości gRPC, odpowiedzi ani stdout/stderr — test E2E sprawdza brak hasła w wyjściu); D — limit wiadomości gRPC 64 KiB, nazwa zadania ≤ 128, plik hasła ≤ 1 KiB; E — Agent czyta wskazany plik hasła z własnymi uprawnieniami.
| Kontrola | Waga | Status |
|---|---|---|
| Błędy domenowe zwracane jako kod KB (bez komunikatów z danymi klienta/sekretami); nieznane wyjątki nie są łapane (idą do interceptora) | — | OK |
| Agent czyta dowolny plik wskazany przez wywołującego jako „plik hasła” (≤ 1 KiB, treść nie wraca do klienta; użyta tylko jako hasło opakowujące) | Niskie | otwarte do BL-17 (autoryzacja per SID, ACL katalogu haseł; Windows) |
| Brak nowych pakietów; audyt `dotnet list package --vulnerable` czysty | — | OK |
| Wysokie/Krytyczne: brak | — | OK |

## Iteracja 50 (rola 5 prowadzi; recenzja roli 11) — dostawca SFTP na porcie transportu (BL-33)
Zakres: src/Kobian.Providers.Storage/Sftp (ISftpTransport/ISftpConnector, SftpStorageSession, SftpObjectWriter, SftpErrorMap), FakeSftpServer + SftpStorageTests, kody KB-2102/KB-2112. STRIDE: S — tożsamość serwera wystawiona jako ServerIdentity, pinning w adapterze (it. 51); T — brak nadpisywania: rename zwykły (nie posix-rename, K24.3), wyścig z innym zapisem → KB-2301, długość sprawdzana przed i po rename (KB-2401); R — brak; I — komunikaty błędów bez ścieżek/danych klienta, wyjątki transportu tylko jako InnerException; D — listowanie iteracyjne bez rekursji stosowej po dowiązaniach, limity ścieżki 200 zn.; E — Delete tylko w sesji Maintenance (D4.1, test), korzeń bez „..”/„.”/znaków sterujących.
| Kontrola | Waga | Status |
|---|---|---|
| Walidacja ścieżek repo (StoragePath) i korzenia; dowiązania symboliczne w drodze do obiektu → KB-2303 (test) | — | OK |
| Brak ScpClient/Process.Start/SQL; brak nowych pakietów (adapter SSH.NET jeszcze nie dodany) | — | OK |
| Sesja Backup nie sprząta plików .tmp-* po przerwanym zapisie (D4.1) — porządkuje prune/Maintenance | Niskie | otwarte do BL-25 (prune) |
| Pre-check istnienia + O_EXCL ma okno wyścigu; chroni rename bez nadpisania i weryfikacja długości, pełna ochrona wymaga P2 (D4.12, probe SP-21) | Niskie | otwarte do SP-21 |
| Wysokie/Krytyczne: brak; audyt zależności czysty | — | OK |

## Iteracja 51 (rola 6 prowadzi; recenzja roli 11) — adapter SSH.NET z pinningiem klucza hosta (BL-33)
Zakres: Sftp/SshNetSftpConnector.cs, SshNetSupport.cs, KB-2104, SSH.NET 2026.0.0, ADR-0006. STRIDE: S — odcisk SHA-256 klucza hosta porównywany stałoczasowo, brak przypięcia = odmowa, niezgodność = KB-2104 (Security, bez ponowień); T — zwykły rename, brak ScpClient (CVE-2026-48798); R — brak; I — sekrety tylko przez ISftpCredentialProvider/SecretRef, klucz prywatny zerowany po połączeniu, komunikaty wyjątków bez haseł i ścieżek, brak logowania komend; D — timeout połączenia 1–300 s, walidacja host/port; E — rozdzielone poświadczenia Backup/Maintenance (rola przekazana do providera).
| Kontrola | Waga | Status |
|---|---|---|
| Nowy pakiet SSH.NET 2026.0.0 — audyt w zaleznosci.md, `--vulnerable --include-transitive` czysty (ci.sh, chmura) | — | OK |
| Adapter niezweryfikowany na prawdziwym serwerze (LSTAT bez podążania za dowiązaniem, O_EXCL, rename) | Średnie | otwarte — W-13 |
| Skaner sekretów wykrył wzorzec `Password = password;` (fałszywy alarm) — zmieniono nazwę parametru; reguła skanera bez zmian | Niskie | OK |
| Wysokie/Krytyczne: brak | — | OK |

## Iteracja 52 (2026-10-13) — wiring SFTP w Agencie (zakres: Agent/Secrets, AgentProviders, Program.cs)
| Znalezisko / kontrola | Waga | Status |
|---|---|---|
| Walidacja SecretRef: wzorzec nazwy, zakaz `..`, `/`, `\`; klucz prywatny tylko z katalogu sekretów (testy traversal) | — | OK |
| Plik sekretu: limit 64 KiB, odmowa linków, odmowa gdy dostępny dla grupy/innych lub katalog zapisywalny przez innych (Unix); komunikaty bez sekretów i ścieżek (test) | — | OK |
| Rozdział ról Backup/Maintenance bez przejścia na drugą rolę (D4.1, test) | — | OK |
| Sekrety w jawnych plikach JSON do czasu DPAPI (ADR-0007/Qb16); ACL katalogu na Windows nie egzekwowane kodem | Średnie | otwarte — BL-17, W-14, DPAPI w BL-24 |
| Brak nowych pakietów; `--vulnerable --include-transitive` czysty | — | OK |
| Wysokie/Krytyczne: brak | — | OK |

## Iteracja 53 (2026-10-13) — restore w Agencie (zakres: Agent/Jobs/CatalogRestoreService, CatalogAgentBackend, KB-5002)
| Znalezisko / kontrola | Waga | Status |
|---|---|---|
| Wejście `snapshot`: tylko `latest` albo hex 8–64 znaków (prefiks → LIKE z parametrem, bez metaznaków; test `100%`, `zzzz`, pusty); niejednoznaczny prefiks = KB-5002, nie „pierwszy z brzegu” | — | OK |
| Cel restore: wymagana ścieżka w pełni kwalifikowana (relatywna → KB-5001), katalog musi istnieć (nie tworzony), link jako cel odrzucany przez RestoreExecutor (KB-5104); nie nadpisuje plików (KB-5105, test) | — | OK |
| Poświadczenie celu Backup (bez usuwania, D4.6); komunikaty błędów bez ścieżek i sekretów (kody KB w odpowiedzi RPC) | — | OK |
| Uszkodzony pack → Failed + KB-5201/5202, wiersz run_runs Failed (test AC-07.3) | — | OK |
| Dziennik wznowień restore_* (D8.8) niezapisywany — restore po awarii zaczyna od początku; brak ryzyka utraty danych (nic nie jest nadpisywane), ale V2/wznowienie niezrealizowane | Niskie | otwarte — BL-43 |
| Rola Operator + FixedRoleResolver (pipe CurrentUserOnly): każdy proces użytkownika może odtworzyć dowolny snapshot do dowolnego katalogu zapisywalnego przez użytkownika; SID/ACL — Windows (BL-17) | Średnie | otwarte — PROWIZORYCZNIE, jak w it. 46 |
| Brak nowych pakietów; `--vulnerable --include-transitive` czysty | — | OK |
| Wysokie/Krytyczne: brak | — | OK |

## Iteracja 54 (2026-10-13) — wyzwalacze czasowe (zakres: Agent/Jobs/CatalogScheduler, CatalogSchedulerStore, RunQueuedAsync)
| Znalezisko / kontrola | Waga | Status |
|---|---|---|
| SQL tylko parametryzowany (spec/ID jako parametry); w testach interpolowane wyłącznie stałe | — | OK |
| `spec` wyzwalacza: limit 1 KiB, MaxDepth 4, brak ścieżek/poleceń — tylko wyrażenie cron; błąd → KB-0201 bez treści specyfikacji w komunikacie | — | OK |
| Wyzwalacz nie przekazuje żadnych parametrów uruchomienia (definicja zadania jest jedynym źródłem; brak wstrzyknięcia ścieżek/sekretów przez katalog zdarzeń) | — | OK |
| DoS: min. 5 min między wystąpieniami (walidacja przy zapisie), brak pętli nadrabiania przy skoku zegara (KB-4201), limit równoległości 2, jedno uruchomienie zadania naraz (Skipped KB-4103) | — | OK |
| Duplikaty wystąpień: indeks `run_occurrence` + `MAX(scheduled_for_utc)` jako stan (test restartu) | — | OK |
| Zapisy przez CatalogWriter (D2.14 pkt 1); synchroniczne wywołanie portu ISchedulerStore blokuje wątek pętli, nigdy wątek puli żądań gRPC | Niskie | zaakceptowane — SchedulerCore bez async z założenia |
| Brak RPC do wyzwalaczy → nikt przez pipe nie zmieni harmonogramu (brak nowej powierzchni ataku); zarządzanie wyzwalaczami wymagać będzie roli Operator + audytu | — | OK (do it. 55+) |
| Brak nowych pakietów | — | OK |
| Wysokie/Krytyczne: brak | — | OK |

## Iteracja 55 (2026-10-13) — `check` V2/V3 + domknięcie RD-27 (zakres: Engine/Repo/RepoChecker, RepoReader.VerifyPackAsync/AddIndex, OfflineRestore check, Agent/Jobs/CatalogCheckService, RPC Check/ListSchedules/SetSchedule, CLI check/schedule)
| Znalezisko / kontrola | Waga | Status |
|---|---|---|
| `check` tylko czyta: sesja Backup (D4.6, brak DeleteAsync), test „repo bajtowo identyczne po check” | — | OK |
| Dane z celu niezaufane: obiekty przez MetadataStore (limit MaxPayload), pack ≤ 2 GiB, dekompresja z limitem Tree/Data, HMAC chunkId i SHA porównywane stałoczasowo (FixedTimeEquals) | — | OK |
| Nazwy z listingu parsowane ściśle (`data/<pp>/<32 hex małe>.pack`, pp = prefiks id; inne → ignorowane; test traversal `..`) — listing nie steruje ścieżkami odczytu (ścieżki z RepoPaths) | — | OK |
| DoS: lista znalezisk ≤ 1000 (Truncated), w RPC ≤ 100; V3 bez terminu, kończy ct; `--sample` 0–100 walidowane w CLI, kobian-restore i Agencie | — | OK |
| Raport/RPC/run_runs zawierają tylko kody KB i identyfikatory hex (losowe/HMAC), bez nazw plików, treści, haseł (testy DoesNotContain a.bin / hasła) — NFR-11 | — | OK |
| Błędy nieoczekiwane (nie KobianException/StorageException) nie są maskowane jako „uszkodzenie” — propagują; w Agencie wiersz Failed KB-0001 | — | OK |
| RPC Check = Operator + modyfikujące (wymaga rewizji klienta, zapis run_runs); ListSchedules = Reader, SetSchedule = Operator; FixedRoleResolver PROWIZORYCZNIE jak w it. 46 (SID/ACL — BL-17) | Średnie | otwarte — jak it. 46 |
| Kod RD-27 z przerwanego przebiegu (20:00–20:03, bez wpisu w dzienniku) przejrzany: SQL parametryzowany, walidacja cron przed zapisem (KB-0201, ≥ 5 min), `enabled=0` zamiast usuwania (historia run_runs), nazwa zadania ≤ 200 | — | OK |
| Brak nowych pakietów; `dotnet list package --vulnerable --include-transitive` czysty | — | OK |
| Wysokie/Krytyczne: brak | — | OK |

## Iteracja 56 (2026-10-14) — V2 po każdym backupie + wymuszenie pełnej kopii (zakres: Engine/Repo/RepoChecker.CheckSnapshotAsync, Agent/Jobs/CatalogJobService, CatalogCheckService.MarkSuspect)
| Znalezisko / kontrola | Waga | Status |
|---|---|---|
| V2 po commit działa na tej samej sesji Backup (tylko odczyt, brak DeleteAsync — D4.6); test „repo bajtowo identyczne” dla CheckSnapshotAsync | — | OK |
| PM-15 (cicha utrata danych): pack o innym rozmiarze po commit → uruchomienie Failed KB-1502, bez ponowień, snapshot suspect; test na sesji symulującej utratę bajtów | — | OK |
| Przyrost nigdy na bazie podejrzanego snapshotu ani „przeskokiem” do starszego — po suspect zawsze pełna kopia (3.7); remis time_utc rozstrzygany rowid (wcześniej niedeterministyczny wybór rodzica przy tej samej milisekundzie) | Średnie (naprawione) | OK |
| SQL parametryzowany (snap_id, repo_id z raportu jako parametry; MarkSuspect ograniczony do repo_id z klucza zadania) | — | OK |
| DoS: V2 pojedynczego snapshotu = Stat na używany pack (bez listowania celu), lista znalezisk ≤ 1000 | — | OK |
| Wynik zawiera tylko kody KB i id hex (NFR-11); brak nowych pakietów, audyt czysty | — | OK |
| Błąd sieci przy Stat w V2 (StorageException) → uruchomienie Failed i ewentualne ponowienie całego backupu przez RunExecutor; snapshot zostaje z verify_level=1 | Niskie | zaakceptowane — RPO liczone od verify_level ≥ 2 (do wdrożenia w UI/raportach) |
| CatalogRestoreService „latest” nadal sortuje tylko po time_utc (remis) | Niskie | otwarte — dług techniczny |
| Wysokie/Krytyczne: brak | — | OK |

## Iteracja 57 (2026-10-14) — UI Avalonia: pulpit, tokeny, adapter gRPC (zakres: src/Kobian.UI, tests/Kobian.Tests.UI, Directory.Build.targets)
| Znalezisko / kontrola | Waga | Status |
|---|---|---|
| Nowe pakiety (Avalonia 12.1.4 i przechodnie SkiaSharp/HarfBuzz/Tmds.DBus) — audyt w zaleznosci.md przed użyciem, lock-i, `--vulnerable` czysty | — | OK |
| **Avalonia.BuildServices wysyła telemetrię podczas kompilacji** (cel AvaloniaStats) — ruch sieciowy z budowy, ujawnienie metadanych projektu | Średnie | naprawione: cel nadpisany pustym (Directory.Build.targets, zweryfikowane w logu MSBuild) + AVALONIA_TELEMETRY_OPTOUT=1 w ci.sh |
| UI nie zna ścieżek, haseł ani treści: model pulpitu = nazwy zadań, czasy, kody KB, nazwy celów (NFR-11); brak logowania w UI | — | OK |
| Akcje modyfikujące (RunJob) wyszarzone dla roli Viewer; autoryzację egzekwuje Agent (MethodPolicy) — UI nie jest granicą bezpieczeństwa | — | OK |
| Klient pipe nie weryfikuje jeszcze tożsamości serwera (podszycie się pod Agenta przez inny proces — D2.9, KB-7004) | Średnie | otwarte — Windows (W-17), ta sama luka co CLI (it. 46) |
| Kanał: gniazdo Unix tylko poza Windows (dev, ADR-0004); limit odpowiedzi 4 MiB, termin 10 s na wywołanie (DoS) | — | OK |
| Brak dekodowania obcych obrazów w UI (tylko własne geometrie ikon); SkiaSharp natywny — audyt co rundę | Niskie | zaakceptowane |
| Wysokie/Krytyczne: brak | — | OK |

## Iteracja 58 (2026-10-14) — UI: SCR-02 zadania, SCR-10 historia (zakres: src/Kobian.UI/Jobs, History, Shell, Services/GrpcDashboardSource, Theme/Controls.axaml, tests/Kobian.Tests.UI)
| Znalezisko / kontrola | Waga | Status |
|---|---|---|
| Nowe akcje modyfikujące (RunJob, SetSchedule) wyszarzone dla roli Viewer (test), autoryzację egzekwuje Agent (MethodPolicy: Operator); UI nie jest granicą bezpieczeństwa | — | OK |
| „Wstrzymaj” nie usuwa niczego: SetSchedule bez cron wyłącza wyzwalacz (DisableTriggerAsync), wyrażenie zostaje — brak operacji usuwania (D4.6) | — | OK |
| Dane w UI: nazwy zadań, czasy, tryb, liczby, rozmiary, skrót ID, kody KB — bez ścieżek i treści (NFR-11); komunikaty błędów z KbMessages.resx, nie z wyjątków | — | OK |
| DoS w UI: historia ograniczona do 500 wierszy w widoku, odpowiedź gRPC ≤ 4 MiB, termin wywołania; CronText parsuje tylko 5 pól liczbowych bez regexów/rekursji | — | OK |
| Odmowa usługi bez kodu → KB-0001 (nie zgaduje sukcesu) | — | OK |
| Tożsamość serwera pipe niezweryfikowana (D2.9, W-17) — bez zmian względem it. 57 | Średnie | otwarte — Windows |
| Brak nowych pakietów; audyt `--vulnerable` czysty | — | OK |
| Wysokie/Krytyczne: brak | — | OK |

## Iteracja 59 (2026-10-14) — UI: SCR-03 kreator zadania (zakres: src/Kobian.UI/Wizard, Shell, Theme/Controls.axaml, resx, tests/Kobian.Tests.UI)
| Kontrola | Waga | Status |
|---|---|---|
| Hasło repozytorium: tylko w VM do wywołania CreateRepository, potem referencje usunięte; nie trafia do `WizardDraft`, logów, komunikatów ani zrzutów (pole z PasswordChar). `string` nie da się wyzerować — ograniczenie TextBox | Niskie | zaakceptowane (udokumentowane w kodzie) |
| Klucz odzyskiwania: wybór 2 RÓŻNYCH grup przez `RandomNumberGenerator`, porównanie `CryptographicOperations.FixedTimeEquals`, klucz usuwany z VM po potwierdzeniu, brak „Wstecz” (AC-28.1). Brak: czyszczenie schowka po 60 s i `WDA_EXCLUDEFROMCAPTURE` (D6.9) — Windows, W-18 | Średnie | otwarte — Windows |
| Tożsamość SFTP: odcisk z probe przypinany do szkicu dopiero po zatwierdzeniu (test), bez TOFU w tle (D4.11) | — | OK |
| Odrzucony wariant adaptera: hasło do pliku tymczasowego dla `InitJob` (sekret na dysku) — ADR-0010 | — | OK (nie wdrożono) |
| Walidacja: reguły po stronie usługi (port); UI nie przyjmuje wyrażeń cron od użytkownika (tylko presety + HH:mm parsowane `TimeOnly.TryParseExact`); ścieżki źródeł bez dostępu do systemu plików w UI | — | OK |
| Zrzuty docs/ui: klucz odzyskiwania na zrzucie jest losowy z atrapy (nie sekret); skaner sekretów zielony (wyłapał `Password = …;` w teście — poprawione) | — | OK |
| Brak nowych pakietów; audyt `--vulnerable --include-transitive` czysty | — | OK |
| STRIDE (nowy element: kreator UI): S — tożsamość serwera pipe (W-17, bez zmian); T — szkic tylko w pamięci; R — zapis/odmowa po stronie Agenta (audyt D2.9); I — sekrety poza szkicem; D — brak pętli/regexów na danych użytkownika; E — role egzekwuje Agent | — | OK |
| Wysokie/Krytyczne: brak | — | OK |

## Iteracja 60 (2026-10-14) — przegląd rundy 4 (zakres: cała runda 46–60; kod: src/Kobian.Agent/Secrets/FileSftpCredentialProvider.cs, tests/.../Security/Round4FuzzTests.cs)
| Kontrola / znalezisko | Waga | Status |
|---|---|---|
| Audyt zależności `dotnet list Kobian.sln package --vulnerable --include-transitive` (23 projekty, SDK 10.0.401) | — | OK, 0 podatnych |
| Skan sekretów (wzorce + losowa wartość testowa) | — | OK |
| Zakazane API w src/ (ScpClient, BinaryFormatter, wyłączanie walidacji certyfikatów, Process.Start/UseShellExecute, MD5/SHA-1) | — | OK, brak (tylko komentarze) |
| SQL: dynamiczne T-SQL tylko przez TSql.QuoteName/NLiteral (PermissionScript); katalog SQLite — zapytania parametryzowane | — | OK |
| Fuzzing SEC-3.3 (nowe parsery rundy 4): JobDefinition, RepoHeader.PeekRepoId/Verify, KeyFiles.UnwrapWithRecoveryKey/MarkRevoked, CronSchedule — 4 × 20 000 mutacji, ziarna stałe; tylko KobianException z kodem parsera, bez ścieżek w komunikacie; zagnieżdżenie > 8, > 64 KiB i 1e400 odrzucone | — | OK (test w ci.sh) |
| FileSftpCredentialProvider: kontrola dowiązań tylko poza Windows (na Windows symlink/junction w katalogu sekretów był czytany) | Średnie | NAPRAWIONE — dowiązanie/ReparsePoint odrzucane na każdej platformie; test Windows W-19 |
| FileSftpCredentialProvider: nazwy CON/NUL/COMn/LPTn oraz kropka końcowa dopuszczone w SecretRef i privateKeyFile (D8.9) | Niskie | NAPRAWIONE — IsSafeName + 7 nowych przypadków testowych |
| Tożsamość serwera pipe (D2.9, W-17); schowek/WDA klucza odzyskiwania (D6.9, W-18) | Średnie | otwarte — Windows |
| Dedup bez limitu RAM (D3.15, RD-22); unieważnienie klucza bez rotacji MK (RD-23) | Średnie | otwarte — runda 5 |
| STRIDE rundy (Agent jako host): S — pipe CurrentUserOnly + MethodPolicy, tożsamość serwera W-17; T — MAC nagłówka, AEAD kluczy, V2 po backupie; R — run_runs/run_transitions; I — KB bez ścieżek/sekretów (fuzz potwierdza dla JobDefinition); D — limity parserów 16/64 KiB, MaxDepth, limit równoległości schedulera 2; E — role Operator/Viewer w MethodPolicy, brak usuwania w sesji Backup | — | OK |
| Wysokie/Krytyczne: brak | — | OK |

## Iteracja 61 (2026-10-14) — BL-60 cz. 1: GetStatus rozszerzony + ListRuns (zakres: agent.proto, Agent/Hosting/CatalogStatusReader.cs, AgentGrpcService, MethodPolicy, CLI runs, UI GrpcDashboardSource)
| Kontrola | Waga | Status |
|---|---|---|
| Autoryzacja: ListRuns w MethodPolicy jako Reader, niemodyfikujące (metoda spoza mapy = odmowa); test | — | OK |
| Rola w odpowiedzi z tego samego resolvera co interceptor (brak rozjazdu UI ↔ egzekwowanie); UI i tak nie jest bramką (D2.9) | — | OK |
| NFR-11: odpowiedź zawiera tylko nazwę zadania, schemat celu, stany, kody KB, liczniki, czasy; test sprawdza brak ścieżek z definicji w `GetStatusResponse` | — | OK |
| SQL: wszystkie zapytania parametryzowane (`$job`, `$n`), limit przycinany 1–1000, nazwa zadania > 200 znaków → pusta odpowiedź (bez zapytania) | — | OK |
| DoS/SEC-3.3: parsowanie definicji (64 KiB, MaxDepth 8) i specyfikacji cron (1 KiB, MaxDepth 4) istniejącymi parserami; błąd → puste pole, bez wyjątku z danymi; zapytanie statusu z LIMIT 1000 | — | OK |
| Starszy klient/Agent: pola addytywne, wartości domyślne bezpieczne (`disabled=false`, rola `UNSPECIFIED` → UI Operator — tylko widoczność przycisków) | Niskie | zaakceptowane (Qb18/Qb22) |
| STRIDE (ListRuns): S — pipe CurrentUserOnly; T — tylko odczyt; R — n/d; I — bez ścieżek/treści; D — limit 1000, indeks run_by_job (sortowanie po COALESCE czasu bez indeksu — akceptowalne do ~10^5 wierszy, RD-33); E — Reader | Niskie | otwarte (RD-33) |
| Brak nowych pakietów; audyt `--vulnerable` w ci.sh czysty | — | OK |
| Wysokie/Krytyczne: brak | — | OK |

## Iteracja 62 (2026-10-14) — BL-60 cz. 2: ProbeDestination + UpsertJob(validate_only) (zakres: agent.proto, Agent/Jobs/DestinationProbeService.cs, JobDraftValidator.cs, Sftp/SshNetSupport.cs, SshNetSftpConnector.cs, SftpErrorMap.cs, StorageException)
| Kontrola | Waga | Status |
|---|---|---|
| Pinning SFTP (D4.11): tryb identity_probe tylko przy pustym przypięciu i jawnej opcji „1”; klucz nigdy nie zaufany (`CanTrust=false`, `Matches("")=false` — test); brak rozwiązywania SecretRef i wysyłania poświadczeń w sondzie; zwykłe połączenie bez przypięcia nadal odmawia | — | OK (W-20 na prawdziwym serwerze) |
| Zmiana klucza przypiętego serwera → FAILED KB-2104 (alarm), nie pytanie o zatwierdzenie (test) | — | OK |
| Probe: wyłącznie sesja Backup (asercja w teście), brak zapisu na celu i w katalogu (test: katalog docelowy pusty po sondzie) | — | OK |
| Autoryzacja: ProbeDestination Operator (połączenia sieciowe na koncie usługi — ryzyko SSRF z roli Reader), UpsertJob Operator + rewizja; test MethodPolicy | — | OK |
| SSRF/skanowanie z Agenta przez ProbeDestination (Operator może kazać usłudze łączyć się z dowolnym host:port) | Średnie | otwarte — ograniczenie częstotliwości/listy hostów do decyzji (RD-34); dziś rola Operator = zaufany administrator kopii |
| Walidacja wejść (D8.9): ścieżki tylko bezwzględne, ≤ 1024 znaki, bez znaków sterujących; host ≤ 253, port 1–65535; nazwa zadania bez znaków sterujących/spacji brzegowych; SQL parametryzowany | — | OK |
| NFR-11: odpowiedzi bez wartości pól (test na UpsertJob), odcisk klucza hosta to dana publiczna | — | OK |
| Brak nowych pakietów; audyt czysty | — | OK |
| Wysokie/Krytyczne: brak | — | OK |

## Iteracja 63 (2026-10-14) — kreator na Agencie: InitRepository + klucz odzyskiwania + GrpcWizardService (zakres: agent.proto, CatalogAgentBackend, RepoInitService, RepoProvisioner, ScheduleService, UI/Services/GrpcWizardService.cs, App)
| Kontrola | Waga | Status |
|---|---|---|
| Hasło w wiadomości gRPC: tylko po pipe z autoryzacją Operator + rewizja (test: Reader dostaje odmowę i nic nie powstaje na dysku); limit wiadomości 64 KiB; hasło nie w odpowiedzi/logach (test przeszukuje pliki poza plikiem sekretu) | — | OK |
| Plik hasła w stanie Agenta: CreateNew (bez nadpisania/podmiany przez dowiązanie), Unix 0600 od utworzenia (test), bufor UTF-8 zerowany; Windows ACL/DPAPI | Średnie | otwarte — W-21 (przed M1) |
| Klucz odzyskiwania: CSPRNG 32 B, MK i RK zerowane, plik „recovery” AEAD (KeyFiles), keyId na liście nagłówka pod MAC; tekst klucza nie trafia na dysk (test) | — | OK |
| `string` hasła w pamięci procesu Agenta/UI nie da się wyzerować | Niskie | zaakceptowane (jak it. 59) |
| Porzucony kreator: zadanie wyłączone (brak uruchomień), repozytorium na celu zostaje | Niskie | zaakceptowane (Qb24 c) |
| Walidacja: JobDraftValidator + polityka hasła SEC-6.5 przed jakimkolwiek zapisem; istniejące repo → KB-1003 bez nadpisania (test) | — | OK |
| STRIDE (InitRepository): S — pipe + rola; T — MAC nagłówka, AEAD kluczy; R — wiersz job_jobs; I — sekrety tylko w stanie Agenta; D — PBKDF2 600k na żądanie Operatora (koszt CPU, akceptowalny); E — Operator | — | OK |
| Brak nowych pakietów; audyt czysty | — | OK |
| Wysokie/Krytyczne: brak | — | OK |

## Iteracja 64 (2026-10-14) — SCR-10 z ListRuns (zakres: UI/History, GrpcDashboardSource.ListRunsAsync, Theme/Controls.axaml, resx)
| Kontrola | Waga | Status |
|---|---|---|
| NFR-11: wiersz uruchomienia pokazuje tylko nazwę zadania, stan, kod KB, czas — bez ścieżek i treści (kontrakt ListRuns z it. 61) | — | OK |
| Odtwarzanie tylko z wiersza kopii (test: wiersz uruchomienia nie wywołuje SCR-06) | — | OK |
| Zgodność wstecz: starszy Agent (Unimplemented) → pusta lista, bez stanu offline; limit 500 po stronie klienta i Agenta (DoS UI) | — | OK |
| WCAG 1.4.1: wynik niesiony tekstem pigułki („nieudana”, „pominięta”, „weryfikacja z uwagami”) + kod KB, kolor dodatkowo; kod KB w nazwie dostępnej wiersza | — | OK |
| Brak nowych pakietów; audyt czysty | — | OK |
| Wysokie/Krytyczne: brak | — | OK |

## Iteracja 65 (2026-10-14) — retencja GFS (zakres: Engine/Retention/RetentionPlanner.cs, Agent/Jobs/CatalogRetentionService.cs, JobDefinition.retention, RPC ApplyRetention, CLI forget)
| Kontrola | Waga | Status |
|---|---|---|
| Brak usuwania danych na celu w tej ścieżce (D4.1/D4.6): tylko `forgotten_at` w katalogu; sesja dostawcy nie jest otwierana | — | OK |
| Właściwości bezpieczeństwa danych (AC-10.1): nigdy najnowszy, nigdy młodszy niż lock, nigdy suspect, brak reguł = nic — 400 losowych historii (ziarno stałe) | — | OK |
| SEC-17.4: cofnięty zegar nie zdejmuje lock-u (test); skok w przód — brak wykrycia (retencja tylko ręczna, Qb25 c) | Średnie | otwarte — przed automatyczną retencją |
| Walidacja polityki (SEC-3.3): tylko znane pola, liczby całkowite 0–10 000, lock ≤ najkrótszy okres; te same granice w InitRepository (inaczej definicja nie do wczytania) | — | OK |
| Autoryzacja: ApplyRetention Operator + rewizja (także dry-run); test MethodPolicy | — | OK |
| SQL parametryzowany; zapomnienie ograniczone do `job_id` zadania (test: inne zadanie nietknięte) | — | OK |
| Brak nowych pakietów; audyt czysty | — | OK |
| Wysokie/Krytyczne: brak | — | OK |

## Iteracja 66 (2026-10-14) — podgląd retencji w kreatorze + zapis polityki z S5 (zakres: RPC PreviewRetention, UpsertJob.retention, ScheduleService.SetRetentionAsync, JobDefinition.WithRetention, UI S5)
| Kontrola | Waga | Status |
|---|---|---|
| PreviewRetention (Reader): czysta symulacja bez katalogu i celu; ograniczenie kosztu — okno ≤ 3 lata i ≤ ~20 000 wystąpień, Between ≤ 100 000, cron ≤ 256 znaków (DoS) | — | OK |
| Zapis retencji: definicja walidowana przed i po zmianie w tej samej transakcji, nowy SHA-256 definicji, pozostałe pola bez zmian (test równości rekordu); granice 0–10 000 jak parser | — | OK |
| UpsertJob z retencją: Operator + rewizja; retencja nigdy nie uruchamia zapominania sama (tylko polityka; wykonanie — `kobian forget`) | — | OK |
| UI: tekst podglądu bez danych klienta; region LiveSetting=Polite; neutralny styl „info” (kolory stanu zarezerwowane dla ochrony) | — | OK |
| Brak nowych pakietów; audyt czysty | — | OK |
| Wysokie/Krytyczne: brak | — | OK |

## Iteracja 67 (2026-10-14) — plan prune, dry-run (zakres: Engine/Repo/PrunePlanner.cs, Agent/Jobs/CatalogPruneService.cs, RPC PlanPrune, CLI prune)

| Kontrola | Waga | Status |
|---|---|---|
| PM-18 (usunięcie żywego packa): żywość przez indeksy z klucza 11 żywych snapshotów (indeksy uwierzytelnione i porównane z SHA-256 odwołania); test właściwości 5 ziaren + AC-10.3 (dwa zadania w repo) + przyrost po prune; test mutacyjny planera czerwony (7/9) | — | OK |
| Bezpieczniki: nieczytelny żywy snapshot/indeks → KB-1601 i pusty plan (nic do usunięcia, bez zdjęcia oznaczeń); zero żywych przy niepustym repo → KB-1602 (ochrona przed wyczyszczonym/złym katalogiem); snapshot nieznany katalogowi = żywy | — | OK |
| Karencja: now − markAt ≥ 24 h; oznaczenie z przyszłości (cofnięty zegar, SEC-17.4) nie skraca karencji — test | — | OK |
| Tylko odczyt: sesja Backup (D4.6), brak operacji Delete/Write; test porównuje SHA-256 wszystkich plików celu i liczbę run_runs przed/po | — | OK |
| D2.9: PlanPrune w MethodPolicy (Operator, niemodyfikujące); test „każda metoda ma politykę” | — | OK |
| NFR-11: odpowiedź i CLI zawierają tylko liczby i „rodzaj:hex”; nazwy plików nie występują | — | OK |
| Parsowanie spisu: tylko ścisłe nazwy (`TryParseId`, `TryParsePackPath`), inne pliki ignorowane; limity rozmiaru obiektów metadanych jak w restore (SEC-3.3) | — | OK |
| DoS: spis trzyma w pamięci wszystkie id obiektów repo (O(liczba obiektów)); klient bez terminu wywołania (spis może trwać) — limit liczby obiektów / postęp do dodania przy fazie 2 | Niskie | otwarte |
| Faza 2 (usuwanie) WYMAGA: blokady wyłącznej D3.13 (inaczej wyścig z trwającym backupem: pack bez indeksu = „sierota”), poświadczenia Maintenance, kolejności snapshot → index → pack | Średnie (projektowe, niewdrożone) | otwarte — warunek przed fazą 2 |
| Brak nowych pakietów; audyt `--vulnerable` czysty (ci.sh); skaner sekretów czysty | — | OK |
| Wysokie/Krytyczne: brak | — | OK |

## Iteracja 68 (2026-10-14) — blokady D3.13 + prune wykonawczy (zakres: Crypto/RepoLockFile.cs, Engine/Repo/RepoLocks.cs, Engine/Repo/PruneExecutor.cs, Agent/Jobs/CatalogPruneService.cs, CatalogJobService (blokada kopii), RPC Prune, CLI prune, migracja repo/0002)

| Kontrola | Waga | Status |
|---|---|---|
| Warunek z it. 67 (faza 2 tylko pod blokadą wyłączną): `PruneExecutor` odmawia bez sesji Maintenance i bez dzierżawy wyłącznej (testy); backup bierze współdzieloną przed pierwszym zapisem | — | OK (zamknięte) |
| PM-18: faza 2 robi PONOWNY spis i plan; usuwa tylko przecięcie z zatwierdzoną listą przy zgodnym rozmiarze; kolejność snapshot → index → pack; stop przy pierwszej niezgodności (KB-1603, test z obiektem, który „wrócił”); po pełnym cyklu check V2+V3 100 % + restore bit w bit + przyrost | — | OK |
| STRIDE blokad: Spoofing/Tampering — HMAC-SHA256 K_mac nad JCS, weryfikacja w czasie stałym, odrzucenie duplikatów pól, nazwa pliku = lockId, `magic` rozdziela domenę od kobian.repo (test: podpisany nagłówek nie przechodzi jako blokada); DoS — fałszywe blokady bez klucza ignorowane (KB-1201), plik ≤ 4 KiB, ≤ 4096 plików (nadmiar = zajęte, nie awaria); Repudiation — wiersz run_runs reason „prune”; Information disclosure — hostId to skrót, brak nazw/ścieżek w odpowiedzi | — | OK |
| D4.1: Delete wyłącznie w sesji Maintenance (blokady kopii nie są usuwane przez backup; sprzątanie przeterminowanych w prune). Ścieżki usuwanych obiektów budowane z hex id (`RepoPaths`), pliki blokad tylko z `TryParseId` — brak traversal | — | OK |
| Zegar: blokada „z przyszłości” = żywa (test); karencja odporna na cofnięty zegar (it. 67) | — | OK |
| SQL: parametry; CHECK na `object_key` (rodzaj:hex 32/64, małe litery), FK do repo_repositories; `tools/verify-prune-ddl.py` w ci.sh | — | OK |
| Autoryzacja: Prune = Operator + kontrola rewizji (MethodPolicy, test „każda metoda ma politykę”) | — | OK |
| Odporność: odświeżanie w tle na osobnej sesji (sesje jednowątkowe); błąd odświeżenia nie przerywa kopii (druga linia: karencja 24 h) | Niskie | zaakceptowane (ADR-0016) |
| Dostępność: blokada kopii wygasa dopiero po 30 min (sesja Backup nie usuwa) — prune przy częstych kopiach może być stale pomijany | Niskie | otwarte (Qb27, alternatywa: plik zwolnienia) |
| Spis prune trzyma id wszystkich obiektów w pamięci; brak postępu/limitu (z it. 67) | Niskie | otwarte |
| Brak nowych pakietów; audyt `--vulnerable --include-transitive` czysty (ci.sh); skaner sekretów czysty (klucze w testach losowe) | — | OK |
| Wysokie/Krytyczne: brak | — | OK |

## Iteracja 69 (2026-10-14) — retencja automatyczna + wstrzymanie po skoku zegara (zakres: Agent/Jobs/RetentionHoldService.cs, CatalogRetentionService, CatalogJobService.AutoRetention, CatalogScheduler/SchedulerCore.ClockJumps, migracja core 0002, RPC ReleaseRetentionHold + GetStatus.retention_hold, CLI `retention release`, baner SCR-01)

| Kontrola | Waga | Status |
|---|---|---|
| SEC-17.4: skok zegara spełniający A17 (> 24 h w przód / > 2 min w tył; próg z `alert-rules.json`, nie z kodu) wstrzymuje retencję hosta; przy wstrzymaniu ani ręczne, ani automatyczne forget niczego nie zapomina (testy: próg, scheduler, retencja, kopia z AutoRetention) | — | OK |
| STRIDE `core_retention_hold`: Tampering — katalog pod ACL usługi (D2.12, W-kolejka), CHECK id=1 / format KB / źródło z listy (verify-hold-ddl.py w ci.sh); Spoofing zwolnienia — RPC tylko Administrator (MethodPolicy, test), CLI wymaga `--confirm`; Repudiation — zdarzenia held/released w core_events; Information disclosure — zdarzenia tylko z kodem i przesunięciem (test: brak ścieżek) | — | OK |
| Kierunek awarii: nieczytelny/obcy stan = wstrzymanie (fail-safe), pierwsze wstrzymanie wygrywa (kolejne skoki nie nadpisują przyczyny); tryb deweloperski (rola stała Operator) nie może zwolnić — bezpieczny kierunek | — | OK |
| DoS przez zegar: napastnik z prawem zmiany zegara może trwale wstrzymać porządkowanie → rośnie zajętość celu; łagodzenie: baner/status/stan ≥ Warning, A17 jako alert krytyczny (wysyłka kanałami — przyszła iteracja) | Niskie | zaakceptowane (ADR-0017) |
| Skok zegara przy WYŁĄCZONYM Agencie (BIOS) nie jest wykrywany pętlą (wiek liczony od max(zegar, najnowsza kopia) — chroni tylko przed cofnięciem) | Średnie | otwarte → RD-37 |
| Retencja automatyczna nie zmienia wyniku kopii (wyjątki KB/SQLite/IO/anulowanie przechwycone; transakcja = brak częściowego zapomnienia); działa pod blokadą zadania (brak wyścigu z kolejną kopią); tylko po Succeeded z V2 OK | — | OK |
| SQL: wszystkie zapytania parametryzowane; brak składania SQL z danych | — | OK |
| CLI: PermissionDenied z Agenta mapowane na kod 3 „usługa niedostępna” (istniejące zachowanie) — myląca diagnostyka dla nie-Administratora przy `retention release` | Niskie | otwarte (dług: osobny kod wyjścia dla odmowy uprawnień) |
| Brak nowych pakietów; audyt `--vulnerable --include-transitive` czysty (ci.sh); skaner sekretów czysty | — | OK |
| Wysokie/Krytyczne: brak | — | OK |

## Iteracja 70 (2026-10-14) — BL-40 cz. 1: MsSqlBackupService (zakres: Providers.Sources/MsSql/MsSqlBackupService.cs, IMsSqlSession.cs, MsSqlQueries.BackupSetByDevice, MsSqlBackupCommand.VerifyOnly, KB-3005/KB-3203)

| Kontrola | Waga | Status |
|---|---|---|
| SEC-5.1: nazwa bazy i ścieżka stagingu wyłącznie jako parametry (`@db`, `@stagingPath`, `@path`), QUOTENAME po stronie serwera; test: tekst żadnego polecenia nie zawiera nazwy bazy; walidacja sysname (znaki sterujące) i runId przed kontaktem z serwerem | — | OK |
| NFR-11: `MsSqlServerException` przenosi tylko numery błędów (bez tekstu serwera z nazwami obiektów); komunikaty KobianException bez nazwy bazy i ścieżki (testy); dbMeta bez ścieżki stagingu (test) | — | OK |
| Integralność: zestaw w msdb identyfikowany po urządzeniu (nie „najnowszy”) — równoległa kopia innego narzędzia nie zostanie przypisana; wymagane type=D ∧ COPY_ONLY ∧ CHECKSUM (KB-3203); VERIFYONLY wyłącznie z uprawnieniem jawnie nadanym (D5.7); porażka VERIFYONLY = Integrity | — | OK |
| SEC-5.2/D5.10: katalog uruchomienia tworzony świeżo (istniejący = odmowa, nie nadpisujemy), plik o losowej nazwie, sprzątanie przy każdej porażce i po DisposeAsync z weryfikacją braku; plik będący dowiązaniem odrzucony (LinkTarget) | — | OK |
| ACL stagingu (MSSQL$X tylko w katalogu uruchomienia) — port IStagingAcl bez implementacji Windows; do tego czasu staging dziedziczy ACL %ProgramData%\Kobian | Średnie | otwarte (Windows, BL-40 cz. 2 / W-23) |
| DoS: oczekiwanie na kolizję ograniczone `ConflictWait` i tokenem anulowania; brak limitu czasu BACKUP po stronie klienta (zgodnie z R2.5.1 — przerwanie tokenem zadania) | Niskie | zaakceptowane |
| Czas zakończenia z msdb (czas lokalny serwera) interpretowany w strefie Agenta — poprawne tylko gdy SQL na tym samym hoście (MVP); wartość informacyjna, nie używana do retencji | Niskie | otwarte (adapter: `backup_finish_date` + `SYSDATETIMEOFFSET`) |
| STRIDE nowych elementów: Tampering pliku .bak między BACKUP a odczytem — okno ograniczone ACL (otwarte wyżej) i skrótem strumienia w potoku; Spoofing serwera SQL — Encrypt=Mandatory + pin certyfikatu w adapterze (KB-3004, BL-40 cz. 2); Repudiation — wiersz run_runs (przy podłączeniu w Agencie) | — | OK / w planie |
| Brak nowych pakietów (SqlClient dopiero w cz. 2 z re-audytem); audyt `--vulnerable --include-transitive` czysty (ci.sh); zakazane API brak | — | OK |
| Wysokie/Krytyczne: brak | — | OK |

## Iteracja 71 (2026-10-14) — rola 11 prowadzi: .NET 10.0.13 (biuletyn 2026-10-13) + BL-40 cz. 2 (adapter SqlClient 7.1.1, zadanie „database” w Agencie)

| Kontrola | Waga | Status |
|---|---|---|
| **Środowisko uruchomieniowe 10.0.12** (SDK 10.0.401): biuletyn .NET 10.0.13 z 2026-10-13 — m.in. ASP.NET Core CVE-2026-70188 i CVE-2026-88092 (GHSA: CRITICAL, Security Feature Bypass), CVE-2026-70175 (EoP), DoS CVE-2026-70181/70186/96236 (HIGH); runtime: CVE-2026-70197 (RCE, HIGH), DoS 58280/69651/96233. Agent używa Kestrel (gRPC po named pipes) → dotyczy produktu i testów | **Wysokie** | **naprawione w iteracji**: SDK 10.0.402 / runtime 10.0.13 (SHA-512 OK) w chmurze i na urządzeniu (`.toolchain/dotnet`), `global.json` = 10.0.402, `RuntimePatchLevelTests` (testy padają na runtime < 10.0.13 — także współdzielony ASP.NET Core). Na maszynie docelowej minimalny runtime egzekwuje instalator (W-25, BL MSI) |
| System.Security.Cryptography.Pkcs 9.0.18 (przechodni z SqlClient 7.1.1): GHSA-2fmv-rv2c-59x7 (CVE-2026-70162) i GHSA-846f-f764-76mx (CVE-2026-84008), DoS | Wysokie (gdyby wprowadzony) | uniknięte: przypięcie przechodnie 10.0.13 (`Directory.Packages.props`) PRZED pierwszym użyciem; `dotnet list package --vulnerable --include-transitive` czysty |
| Microsoft.Data.Sqlite.Core / System.Formats.Cbor 10.0.12 → 10.0.13 (seria serwisowa; OSV: brak wpisów dla 10.0.12) | Niskie | podniesione |
| SEC-5.6: `Encrypt=Mandatory` zawsze (Strict opcjonalnie), pin = `ServerCertificate`, `TrustServerCertificate` tylko localhost + jawna zgoda (fabryka odrzuca dla zdalnych; test), nowa reguła BannedApiTests `TrustServerCertificate = true` w src/** | — | OK |
| SEC-5.1: parametry tylko z jawnym typem (`SqlParameterMapper`, bez AddWithValue; walidacja nazw `@ident`); serwer z definicji: odrzucane `;'=`, spacje, prefiksy `tcp:/np:/lpc:/admin:` (DAC), wiele `\`; testy wstrzyknięcia do łańcucha połączenia | — | OK |
| NFR-11 / sekrety: hasło SQL tylko przez SqlCredential (nie w łańcuchu, PersistSecurityInfo=false), czytane z magazynu przy każdym połączeniu, bufor char[] zerowany (test na pętli zwrotnej); `MsSqlServerException` bez tekstu serwera i bez InnerException; ToString opcji/definicji bez nazw serwera, bazy, ścieżek (testy); nieznane pola definicji (np. „password”) odrzucane | — | OK |
| `SecureString` (wymóg SqlCredential) poza Windows nie szyfruje pamięci | Niskie | zaakceptowane (ADR-0018) |
| TLS: błąd uzgodnienia bez numeru (zarządzany SNI) → numer zastępczy → KB-3004 klasy Security (wygrywa z innymi) | — | OK; realne kody SChannel — W-24 |
| DoS: BACKUP bez limitu czasu po stronie klienta (R2.5.1, przerwanie tokenem), zapytania 60 s, połączenie 15 s, `conflictWaitMinutes` ≤ 1440 | Niskie | zaakceptowane |
| Staging: ścieżka SQL musi być Windows-absolutna (walidacja definicji), sprzątanie zawsze (try/finally + DisposeAsync) — test „błąd SQL → brak *.bak”; ACL stagingu Windows nadal otwarte (it. 70) | Średnie | otwarte (W-23/W-24) |
| STRIDE nowych elementów: Spoofing SQL — Encrypt+pin; Tampering .bak — okno ACL (otwarte) + skrót w potoku + V2 po kopii; Information disclosure — brak tekstów serwera w kodach/logach; Elevation — konto usługi z minimalnymi uprawnieniami (skrypt PermissionScript), DAC zablokowany | — | OK / w planie |
| Wysokie: 1 (runtime) — naprawione w tej iteracji; Krytyczne w zależnościach: brak po aktualizacji | — | OK |

## 2026-10-14 — iteracja 72 (rola 12 R&D prowadzi BL-40 cz. 3; recenzja rola 11)
Zakres: MsSqlDiscoveryService, DatabaseDiscoveryService, RPC DiscoverDatabases (proto, MethodPolicy, AgentGrpcService, CatalogAgentBackend), AgentMsSql.CreateConnectionFactory, IWizardService/GrpcWizardService.DiscoverDatabasesAsync.

| Kontrola | Wynik | Waga | Status |
|---|---|---|---|
| Autoryzacja (D2.9): DiscoverDatabases w MethodPolicy — Operator, niemodyfikujące; test „każda metoda ma politykę”; Reader odrzucony PRZED połączeniem z SQL (test przez pipe) | OK | — | OK |
| SQL (SEC-5.1): tylko stałe szablony D-01/D-02/D-03, nazwa bazy wyłącznie jako parametr @db (QUOTENAME po stronie serwera); zero ExecuteAsync w discovery (test) | OK | — | OK |
| Wejście: serwer przez MsSqlConnectionFactory (bez np:/admin:/;), auth integrated/sql, secret_ref ≤ 256 bez znaków sterujących, pin wyłącznie nazwa `.cer` (regex z timeoutem, bez separatorów i „..”, plik musi istnieć) — 13 przypadków odmowy bez połączenia | OK | — | OK |
| Sekrety (AC-01.3, NFR-11): login SQL tylko jako secret_ref; hasło z magazynu przez SqlCredential (bez zmian w adapterze); odpowiedź bez nazwy hosta/instancji; nazwy baz tylko w odpowiedzi dla Operatora (brak logów/zdarzeń); komunikaty wyjątków bez tekstu serwera | OK | — | OK |
| DoS (SEC-3.3): limit 500 baz (D-03 per baza), teksty z serwera przycięte i bez znaków sterujących, termin RPC 2 min po stronie UI, anulowanie między bazami (test) | OK | — | OK |
| Uczciwość wyniku: zerwane połączenie/TLS w trakcie D-03 = porażka całości (KB-3001/KB-3004), nie „brak uprawnień” | OK | — | OK |
| NOWE: Operator może wskazać dowolny host SQL z uwierzytelnieniem Windows → Agent (konto usługi/konto komputera w sieci) uwierzytelnia się do obcego hosta (ryzyko przechwycenia/przekazania NTLM). Łagodzenie dziś: Encrypt=Mandatory z walidacją łańcucha przed logowaniem (TLS w PRELOGIN), Operator = zaufana grupa lokalna. Propozycja: lista dozwolonych serwerów (RD-39) — to samo dotyczy zadań database i celów SMB | Średnie | otwarte (RD-39, pytanie przy Qb30) |
| STRIDE: Spoofing serwera SQL — TLS + pin; Tampering — brak zapisu; Repudiation — wywołanie RPC w logu dostępu Agenta (bez danych); Info disclosure — lista baz tylko dla Operatora; DoS — limity; Elevation — skrypt uprawnień minimalny, wykonuje administrator SQL | — | OK |
| Zależności: brak nowych pakietów; audyt `dotnet list package --vulnerable --include-transitive` czysty | OK | — | OK |
Wysokie: 0.

## 2026-10-14 — iteracja 73 (rola 13 prowadzi; 13,6,5,11) — BL-40 cz. 4: kreator S1-db + zadanie „database” z UpsertJob/InitRepository

| Kontrola | Wynik | Waga | Status |
|---|---|---|---|
| Wejście: źródło bazodanowe walidowane TĄ SAMĄ funkcją co discovery (`DatabaseDiscoveryService.Validate`) + sysname nazwy bazy; 8 przypadków odmowy przez pipe (protokół `np:`, wstrzyknięcie `;Trust Server Certificate`, sql bez secret_ref, integrated z secret_ref, pin `../..`, pin niezatwierdzony, pusta nazwa, znak sterujący) — bez połączenia z SQL (atrapa rzuca przy próbie) | OK | — | OK |
| Definicja: sekcja `database` budowana w Agencie i parsowana kodem uruchomienia (`MsSqlSourceDefinition.Parse`) PRZED zapisem na cel; błąd = KB-0103 bez plików na celu (test) | OK | — | OK |
| Sekrety (AC-01.3, NFR-11): UI przekazuje tylko `file:<nazwa>` i nazwę .cer; hasło repozytorium bez zmian (tylko InitRepository); `DatabaseInit.ToString()` bez serwera/bazy; skrypt uprawnień zawiera tylko nazwę konta usługi | OK | — | OK |
| Autoryzacja (D2.9): UpsertJob/InitRepository bez zmian polityki (Operator); nowe pole nie dodaje metody | OK | — | OK |
| Staging (D5.10): ścieżki z konfiguracji administratora (`KOBIAN_DB_STAGING`/`KOBIAN_DB_SQL_STAGING`), nie z UI; ACL katalogu — W-24/W-27 (Windows) | OK | — | OK (Windows w kolejce) |
| Ryzyko odziedziczone: Operator może zapisać zadanie z dowolnym hostem SQL i uwierzytelnieniem Windows (przekazanie NTLM konta usługi) — teraz także trwale w zadaniu, nie tylko w discovery | Średnie | — | otwarte (RD-39, Qb30/Qb31) |
| UI: „Dalej” nie wysyła żądania dla bazy bez prawa kopii (KB-3003 lokalnie); zmiana serwera/logowania/pinu czyści listę i wybór (brak zapisu bazy z innego serwera — test) | OK | — | OK |
| STRIDE (nowe: pole database w zapisie zadania): Spoofing — TLS + pin przy każdym połączeniu; Tampering — definicja w katalogu z SHA-256; Repudiation — run_runs; Info disclosure — nazwy baz tylko w katalogu Agenta; DoS — brak nowych połączeń; Elevation — brak nowych metod | — | — | OK |
| Zależności: brak nowych pakietów; audyt `--vulnerable --include-transitive` czysty (ci.sh) | OK | — | OK |
Wysokie: 0.

## 2026-10-14 — iteracja 74 (rola 14 prowadzi; 14,4,5,6,11) — BL-40 cz. 5: zatwierdzanie certyfikatu TLS serwera SQL
| Punkt kontrolny | Waga | Znalezisko | Status |
|---|---|---|---|
| Walidacja wejść: serwer przez `MsSqlConnectionFactory.ValidateServer` (bez np:/admin:/;) PRZED połączeniem; nazwa pliku `^[A-Za-z0-9][A-Za-z0-9_.-]{0,63}\.cer$` bez „..” (traversal — testy `../x.cer`, `sub/x.cer`); odcisk 64 hex | OK | — | OK |
| Krypto: SHA-256 nad DER; porównanie `CryptographicOperations.FixedTimeEquals`; brak własnych konstrukcji; callback walidacji TLS zawsze `false` (nie łamie reguły CertValidationBypass — BannedApiTests zielone) | OK | — | OK |
| Brak logowania do niezatwierdzonego serwera: LOGIN7 nigdy nie jest wysyłany (test: serwer zastępczy nie otrzymuje danych aplikacyjnych po TLS) | OK | — | OK |
| Zaufanie: Agent zapisuje pin wyłącznie z certyfikatu, który sam odebrał (klient przesyła tylko odcisk); zmiana między pobraniem a zatwierdzeniem → KB-3007, nic nie zapisane (test) | OK | — | OK |
| Zapis pliku: tmp + `File.Move(overwrite:false)`; inna treść pod tą samą nazwą → KB-3008, stary pin nietknięty (test); tmp sprzątany w finally | OK | — | OK |
| Parsery sieciowe: pakiet TDS ≤ 32 767 B i typ 0x12 w handshake; odpowiedź PRELOGIN ≤ 4 KiB, typ 0x04; SSRP ≤ 1 KiB, długość ≤ danych, port 1–65535, instancja musi się zgadzać; limit czasu 15 s (testy złośliwych/obcych odpowiedzi) | OK | — | OK |
| TOFU: atakujący w sieci przy pierwszym pobraniu może podsunąć swój certyfikat — mitygacja: porównanie odcisku z administratorem SQL (UI, przewodnik z poleceniem PowerShell), KB-3007; step-up Administratora do decyzji (Qb32 d) | Niskie | ryzyko szczątkowe | zaakceptowane prowizorycznie (Qb32) |
| Autoryzacja (D2.9): FetchSqlCertificate Operator/niemodyfikujące, ApproveSqlCertificate Operator/modyfikujące (kontrola rewizji); Reader odrzucony przed połączeniem z serwerem SQL (test przez pipe: 0 nowych połączeń) | OK | — | OK |
| Ryzyko odziedziczone (RD-39): Operator może skierować Agenta na dowolny host (teraz także sondowanie TCP/TLS dowolnego hosta:portu z konta usługi — bez danych uwierzytelniających) | Średnie | — | otwarte (RD-39) |
| NFR-11: nazwa serwera i podmiot certyfikatu tylko w odpowiedzi dla Operatora, nie w logach/zdarzeniach; komunikaty KB bez danych klienta | OK | — | OK |
| Zależności: brak nowych pakietów (System.Net.Security z runtime 10.0.13); audyt `--vulnerable --include-transitive` czysty (ci.sh) | OK | — | OK |
| STRIDE (nowe: TdsCertificateFetcher, RPC Fetch/Approve): Spoofing — TOFU + porównanie odcisku; Tampering — Agent nie przyjmuje bajtów certyfikatu od klienta; Repudiation — brak wpisu audytu zatwierdzenia (BL-n do dodania z łańcuchem audytu D9.9); Info disclosure — certyfikat jest publiczny; DoS — limit 15 s, rozmiary ramek; Elevation — Operator dodaje zaufanie (Qb32 d) | Niskie | brak audytu zatwierdzenia | otwarte (RD-42) |
Wysokie: 0.

## 2026-10-14 — iteracja 75 (przegląd rundy 5; 11,12,14) — fuzzing parserów rundy 5, audyt, threat model
| Punkt kontrolny | Waga | Znalezisko | Status |
|---|---|---|---|
| Audyt `dotnet list package --vulnerable --include-transitive` (SDK 10.0.402, runtime 10.0.13) | OK | 0 podatnych | OK |
| Skan sekretów (SecretScanTests) + grep zakazanych API w src/ | OK | brak | OK |
| Fuzzing (Round5FuzzTests, ziarna 7501–7506): definicja `database` 20k (tylko KB-0103, bez ścieżek i nazwy serwera; przyjęte zachowują niezmienniki — brak loginu, wait w zakresie), SqlEndpoint 20k (tylko KB-0103, host/instancja z listy znaków), ENCRYPTION 20k, SSRP 20k (port 1–65535 albo null, bez wyjątków), ramkowanie TLS w PRELOGIN 5k, odpowiedź PRELOGIN 5k | OK | — | OK |
| DoS: zalew pustymi pakietami TDS od niezatwierdzonego serwera (pętla do limitu 15 s, 100% CPU wątku) | Niskie | naprawione: `TdsPreloginStream.MaxEmptyPackets = 16`, `MaxPreloginPackets = 16` + test | OK |
| Threat model rundy 5 (STRIDE): Fetch/Approve certyfikatu (TOFU — Niskie, Qb32), discovery i zadanie `database` (NTLM konta usługi do obcego hosta — Średnie, RD-39), prune/blokady (MAC K_mac, PM-18 test właściwości), retencja automatyczna (wstrzymanie przy skoku zegara), brak audytu zmian zaufania (Repudiation — RD-42) | Średnie | RD-39, RD-42 | otwarte |
| Uprawnienia: nowe RPC rundy 5 w MethodPolicy (Reader: ListRuns/PreviewRetention; Operator: Probe/Upsert/Init/Discover/Fetch/Approve/Prune; Administrator: ReleaseRetentionHold) — test polityki zielony | OK | — | OK |
Wysokie: 0.

## 2026-10-14 — iteracja 76 (rola 1 prowadzi; 1,8,6,11) — BL-63: SCR-06 przeglądarka kopii (RPC BrowseTree)
| Punkt kontrolny | Waga | Znalezisko | Status |
|---|---|---|---|
| Walidacja ścieżki z klienta (D8.9): segmenty przez `TreeCodec.ValidateName` (bez `..`, `.`, `\`, NUL), ≤ 64 segmenty, ≤ 4096 znaków, segment musi być FOLDEREM kopii; porównanie porządkowe (bez case-folding) — 8 przypadków odmowy w teście (KB-5003) | OK | — | OK |
| Nazwy z repozytorium niezaufane: wpis z niepoprawną nazwą pomijany (jak w restore); UI wyświetla nazwy jako tekst (bez interpretacji ścieżek) | OK | — | OK |
| DoS: limit 5000 wpisów w odpowiedzi (`truncated`), tylko drzewa (bez danych plików), sesja Backup bez usuwania; czas daty spoza zakresu nie rzuca wyjątku (adapter UI) | OK | — | OK |
| Autoryzacja (D2.9): BrowseTree = Reader, niemodyfikujące, zgodnie z R2.6.1 (SCR-06 Viewer); test polityki | Niskie | Reader widzi nazwy plików w kopii (dane klienta) — zgodne z planem; do rozważenia osobna rola przy wielu użytkownikach | zaakceptowane (plan R2.6.1) |
| NFR-11: nazwy plików tylko w odpowiedzi RPC, nie w logach/run_runs/zdarzeniach; komunikat KB-5003 bez ścieżki | OK | — | OK |
| STRIDE (BrowseTree): Spoofing — pipe z ACL (W-17 otwarte); Tampering — tylko odczyt; Repudiation — brak wpisu run (odczyt, jak ListSnapshots); Info disclosure — j.w. (Niskie); DoS — limity; Elevation — brak | — | — | OK |
| Zależności: brak nowych pakietów; audyt czysty (ci.sh) | OK | — | OK |
Wysokie: 0.

## 2026-10-14 — iteracja 77 (rola 2 prowadzi; 2,8,6,11) — BL-64: SCR-07 odtwarzanie plików w UI
| Punkt kontrolny | Waga | Znalezisko | Status |
|---|---|---|---|
| Brak nadpisywania (D8.9): UI nie ma opcji nadpisania; Agent bez zmian (KB-5105 → ostrzeżenie widoczne w wyniku); tekst zasady stale na ekranie | OK | — | OK |
| Walidacja folderu: UI — ścieżka bezwzględna bez NUL; Agent — istnienie, dowiązanie celu (KB-5001/5104), walidacja nazw z repo (bez zmian) | OK | — | OK |
| Filtr z nazw kopii: `*`/`?` w nazwie pliku działają jak wzorzec (nadmiarowe pliki z tego samego folderu, bez nadpisań) | Niskie | ADR-0022/Qb34 — `exact_paths` przed 1.0 | otwarte |
| Autoryzacja (D2.9): Restore = Operator, modyfikujące (bez zmian); odmowa → KB-7011 w UI, bez śladu stosu | OK | — | OK |
| Okno wyboru folderu działa w procesie UI (konto użytkownika); Agent na tym samym komputerze (pipe lokalny) — ścieżka nie przenosi uprawnień użytkownika na usługę (W-5/W-17: ACL folderu docelowego na Windows) | Niskie | Agent pisze z konta usługi do folderu wskazanego przez Operatora — zgodne z D2.12; test ACL w kolejce Windows | otwarte (W-29) |
| NFR-11: wynik w UI to kod KB i tekst z rejestru; ścieżki tylko w UI użytkownika, nie w logach | OK | — | OK |
| Zależności: brak nowych pakietów; audyt czysty (ci.sh) | OK | — | OK |
Wysokie: 0.

## 2026-10-14 — iteracja 78 (rola 3 prowadzi; 3,5,11,2) — RD-39: lista dozwolonych serwerów SQL
| Punkt kontrolny | Waga | Znalezisko | Status |
|---|---|---|---|
| Znalezisko Średnie z it. 72–74 (NTLM/Kerberos konta usługi do dowolnego hosta przez Operatora): egzekwowanie w discovery, Fetch/Approve certyfikatu, UpsertJob/InitRepository (Validate), uruchomieniu zadania — testy: 0 połączeń/0 pobrań przy odmowie | Średnie → zamknięte | domyślnie tylko localhost | zamknięte (Qb35 prowizorycznie) |
| Konfiguracja tylko po stronie administratora hosta (zmienna usługi), niezmienialna przez API; niepoprawny wpis = błąd startu | OK | — | OK |
| Dopasowanie bez DNS (brak TOCTOU/rebinding); adres niepoprawny = odmowa; `*` jawne wyłączenie | OK | — | OK |
| NFR-11: KB-3009 bez nazwy serwera w komunikacie (test) | OK | — | OK |
| Ryzyko szczątkowe: alias DNS z listy wskazujący inny host (decyzja administratora DNS) | Niskie | poza zakresem Agenta | zaakceptowane |
| Zależności: brak nowych; audyt czysty (ci.sh) | OK | — | OK |
Wysokie: 0.

## 2026-10-14 — iteracja 79 (rola 4 prowadzi; 4,11,9,2) — RD-42: łańcuch audytu i audyt API
| Punkt kontrolny | Waga | Znalezisko | Status |
|---|---|---|---|
| Repudiation z it. 74 (zatwierdzenie certyfikatu SQL / klucza hosta bez śladu): wpis audytu z celem i odciskiem; test | Niskie → zamknięte | — | zamknięte |
| Integralność: append-only (wyzwalacze UPDATE/DELETE), seq ciągły (wyzwalacz), hash łańcuchowy JCS (AuditChain), zapis w jednej transakcji pisarza (25 równoległych wpisów → 1..25), ręczna zmiana pliku → KB-6501 z seq (SEC-19.1) | OK | — | OK |
| SEC-11.5: parametry tylko z białej listy (Describe), bez haseł (test z losowym hasłem w InitRepository), obcięte do 256 znaków | OK | — | OK |
| Tożsamość wywołującego = stała „local-pipe” (bez SID) | Średnie | brak rozliczalności na poziomie użytkownika do W-30 | otwarte (Qb36) |
| Brak kotwic (SYSTEM z dostępem do pliku może przepisać cały łańcuch od nowa) | Średnie | D9.9 kotwice — następna iteracja audytu | otwarte |
| DoS: wpis audytu na każdą odmowę — zalew odmowami rośnie katalog; limit wiadomości 64 KiB, pipe tylko lokalny | Niskie | rozważyć próbkowanie odmów | otwarte |
| Zależności: brak nowych; audyt czysty | OK | — | OK |
Wysokie: 0.

## 2026-10-14 — iteracja 80 (rola 5 prowadzi; 5,8,11,10) — BL-62 + exact_paths (Qb34)
| Punkt kontrolny | Waga | Znalezisko | Status |
|---|---|---|---|
| BL-62: runtime i ASP.NET Core < 10.0.13 → KB-7013 w statusie (Warning) i zdarzenie; test macierzy wersji + proces testów niezgłoszony | OK | — | OK |
| Qb34 (Niskie z it. 77): nazwy z `*`/`?` dobierały sąsiadów — exact_paths porównuje dosłownie (test z plikami „x*y.txt”/„xay.txt”), walidacja ścieżek jak BrowseTree, foldery spoza wyboru nieczytane | Niskie → zamknięte | — | zamknięte |
| Restore exact: ścieżki `..`, `.`, puste, nieistniejące → KB-5003 i NIC nie zostaje zapisane (test pustego katalogu) | OK | — | OK |
| Zgodność wersji: starszy Agent ignoruje exact_paths → pełne odtworzenie (bez nadpisań) | Niskie | wdrożenie UI+Agent razem (MSI) | zaakceptowane (Qb37) |
| Zależności: brak nowych; audyt czysty | OK | — | OK |
Wysokie: 0.

## 2026-10-14 — iteracja 81 (rola 6 prowadzi; 6,9,11,14) — SCR-11 Ustawienia
| Punkt kontrolny | Waga | Znalezisko | Status |
|---|---|---|---|
| Plik ustawień UI w profilu użytkownika: ≤ 4 KiB, MaxDepth 4, tylko dwie wartości z zamkniętych list, nieczytelny/obcy → domyślne (test); zapis tmp + zamiana; bez sekretów | OK | — | OK |
| Ustawienia nie wpływają na usługę (tylko proces UI) — brak eskalacji; zmiana języka nie przeładowuje widoków (brak niespójności danych) | OK | — | OK |
| Informacje o usłudze: tylko GetStatus (Reader) i VerifyAudit (Reader) — bez nowych metod/uprawnień | OK | — | OK |
| NFR-11: wersja .NET i liczba wpisów audytu — dane techniczne, bez danych klienta | OK | — | OK |
Wysokie: 0.

## 2026-10-14 — iteracja 82 (rola 7 prowadzi; 7,4,6,11) — SCR-09 Cele (ListDestinations)
| Punkt kontrolny | Waga | Znalezisko | Status |
|---|---|---|---|
| Ujawnienie konfiguracji: ścieżki/hosty celów i nazwy zadań dla Operatora (plan: Admin) | Niskie | Qb38 | zaakceptowane prowizorycznie |
| Brak sekretów w odpowiedzi (definicja v1 nie zawiera haseł; plik hasła/klucza nie jest zwracany) — test pól | OK | — | OK |
| Sprawdzenie miejsca = istniejący ProbeDestination (Operator, sesja Backup bez usuwania); niezmienione | OK | — | OK |
| Nieczytelne definicje liczone (bez wyjątku, bez treści w odpowiedzi) | OK | — | OK |
| WCAG 1.4.1: mało miejsca także tekstem, nie tylko kolorem | OK | — | OK |
Wysokie: 0.

## 2026-10-14 — iteracja 83 (rola 8 prowadzi; 8,5,11) — SCR-08 cz. 1: MsSqlRestoreService
| Punkt kontrolny | Waga | Znalezisko | Status |
|---|---|---|---|
| Brak utraty danych klienta: nigdy REPLACE, istnienie bazy sprawdzane przed czymkolwiek (KB-3501, test: zero poleceń RESTORE) | OK | — | OK |
| SQL injection (SEC-5.1): nazwa bazy, ścieżki i nazwy logiczne wyłącznie jako zmienne T-SQL (RESTORE … @db, MOVE @lN TO @pN); tekst zależy tylko od liczby plików; TRUSTWORTHY przez QUOTENAME w sp_executesql | OK | — | OK |
| Ścieżki: nazwy plików wyłącznie z nazwy docelowej (znaki spoza [A-Za-z0-9_-] → „_”), ścieżki fizyczne z kopii ignorowane (test), katalogi tylko bezwzględne Windows | OK | — | OK |
| Integralność: VERIFYONLY + CHECKSUM przed i w trakcie; lista plików ≤ 256, nazwy logiczne ≤ 128 bez znaków sterujących | OK | — | OK |
| Uprawnienia: CREATE ANY DATABASE sprawdzane (KB-3502); RESTORE wykonuje konto usługi — rola wywołującego w RPC (cz. 2): Operator dla nowej bazy, Administrator+step-up dla nadpisania (AC-32.2) | — | do egzekwowania w cz. 2 | otwarte |
| Kolejka Windows W-31 (prawdziwy SQL Server) | — | — | w kolejce |
Wysokie: 0.

## 2026-10-14 — iteracja 84 (rola 9 prowadzi; 9,8,5,11) — BL-68 cz. 2: RestoreDatabase w Agencie
| Punkt kontrolny | Waga | Znalezisko | Status |
|---|---|---|---|
| Lista dozwolonych serwerów SQL także przy odtwarzaniu (KB-3009 przed połączeniem — test) | OK | — | OK |
| Staging: świeży katalog `restore-<runId>` w stagingu Agenta, `database.bak` przez RestoreExactAsync (V1 fileSha256), sprzątany zawsze (sukces, KB-3501, błąd) — test | OK | — | OK |
| Brak nadpisania baz (KB-3501, zero poleceń RESTORE) — test przez Agenta | OK | — | OK |
| Autoryzacja: RestoreDatabase = Operator, modyfikujące → audyt (cel = snapshot, parametr target_database) | OK | — | OK |
| Rozmiar: .bak pełnej bazy w stagingu — brak kontroli wolnego miejsca przed pobraniem (P-02 jest tylko przy kopii) | Niskie | dodać kontrolę 1,1× rozmiaru pliku przed odtworzeniem | otwarte |
Wysokie: 0.

## 2026-10-14 — iteracja 85 (rola 10 prowadzi; 10,8,6,11) — BL-68 cz. 3: SCR-08 + miejsce w stagingu
| Punkt kontrolny | Waga | Znalezisko | Status |
|---|---|---|---|
| Znalezisko Niskie z it. 84 (brak kontroli miejsca): przed pobraniem `.bak` — wolne ≥ 1,1 × rozmiar, inaczej KB-3102 bez połączenia z SQL i bez zapisu (test) | Niskie → zamknięte | — | zamknięte |
| UI: nazwa bazy walidowana (≤ 128, bez sterujących), puste = nazwa domyślna Agenta; ostateczna walidacja sysname w Agencie (MsSqlRestoreService) | OK | — | OK |
| UI nie oferuje nadpisania; komunikat KB-3501 z akcją; z kopii bazy brak „Odtwórz…” plików (przegląd zrzutu) | OK | — | OK |
| Odmowa roli → KB-7011, starszy Agent → KB-7012 (bez wyjątków w UI) | OK | — | OK |
Wysokie: 0.

## 2026-10-14 — iteracja 86 (rola 11 prowadzi; 11,12,2) — RD-44 fuzzing gRPC Agenta
| Punkt kontrolny | Waga | Znalezisko | Status |
|---|---|---|---|
| `GrpcFuzzTests`: wszystkie metody MethodPolicy (31) × 120 ładunków przez prawdziwy host (Kestrel + gRPC, gniazdo Unix), połowa strukturalnie poprawnych (losowe pola z reflection protobuf: ścieżki z `..`, UNC, NUL, Unicode, skrajne liczby), połowa losowych bajtów; rola Reader (modyfikujące odrzucane po deserializacji — bez skutków na dysku); dodatkowo 8 ziaren ręcznie (KOBIAN_FUZZ_SEED) | OK | — | OK |
| ZNALEZISKO: PreviewRetention/UpsertJob/InitRepository — ogromne wartości RetentionSpec (np. keep_yearly ≈ 58 000) przepełniały int/TimeSpan w `RetentionPolicy.ShortestTimeRetention` PRZED kontrolą zakresu → nieobsłużony wyjątek (gRPC Unknown, host żyje) | Niskie | naprawione: kontrola zakresu przed walidacją + liczenie w double z nasyceniem; test regresji z ładunkiem z fuzzera | zamknięte |
| Niepoprawny protobuf → status Unknown z ogólnym tekstem (grpc-dotnet, przed interceptorem) — bez wycieku i bez awarii; brak kodu KB w trailerze | Niskie | RD-47: własny marshaller → InvalidArgument + KB-7014 | otwarte |
| Wiadomość > 64 KiB → ResourceExhausted przed backendem (test) | OK | — | OK |
| Przegląd przekrojowy RPC rundy 6 (BrowseTree, VerifyAudit, ListDestinations, RestoreDatabase, exact_paths): polityka ról w teście, audyt modyfikujących, limity wejścia | OK | — | OK |
Wysokie: 0.

## 2026-10-14 — iteracja 87 (rola 12 prowadzi; 12,3,13,11) — RD-22 warstwa 1: DedupTable
| Punkt kontrolny | Waga | Znalezisko | Status |
|---|---|---|---|
| Znalezisko Średnie (dedup bez limitu RAM, od it. 41): pamięć ≈ 4–5× mniejsza (zmierzone), ale nadal bez twardego limitu | Średnie → częściowo | warstwy 2–3 D3.15 | otwarte (częściowo) |
| Niezmienniki I-2/I-3 bez zmian: test modelowy 50 × 400 operacji vs implementacja referencyjna (słownik + zbiór hex), pełne E2E i testy potoku zielone | OK | — | OK |
| Skrót słownika z pierwszych 8 B HMAC — kolizji nie da się wymusić bez K_id (klucz repozytorium) | OK | — | OK |
Wysokie: 0.

## 2026-10-14 — iteracja 88 (rola 13 prowadzi; 13,3,10,11) — pomiary pełnego cyklu i strażnik pamięci dedup
| Punkt kontrolny | Waga | Znalezisko | Status |
|---|---|---|---|
| Narzędzie pomiarowe działa wyłącznie na danych syntetycznych w katalogu tymczasowym (sprzątany w finally), klucze losowe w pamięci | OK | — | OK |
| Nowa referencja Bench → Providers.Storage (projekt własny), lock-i zaktualizowane, audyt czysty | OK | — | OK |
| Szczyt pamięci: kopia całego packu przy wysyłce (ToArray) | Niskie | RD-48 | otwarte |
Wysokie: 0.

## 2026-10-14 — iteracja 89 (rola 14 prowadzi; 14,13,3,11) — RD-48 wysyłka packu bez kopii + przewodnik admina
| Punkt kontrolny | Waga | Znalezisko | Status |
|---|---|---|---|
| RD-48: bufor `MemoryStream.GetBuffer()` wysyłany jako wycinek `[0, Length)` — zapas pojemności nie trafia na cel (test: rozmiar i SHA-256 każdego packu na celu = indeks); bufor odłączony od pól przed wysyłką, PackWriter zakończony — brak modyfikacji w trakcie V1-a/V1-b | OK | — | OK (zamknięte znalezisko Niskie z it. 88) |
| Hash V1 liczony z tych samych bajtów co zapis (jedno źródło `ReadOnlyMemory`), porównanie odczytu kontrolnego nadal stałoczasowe | OK | — | OK |
| Dokumentacja admina: bez sekretów, przykładowe nazwy serwerów fikcyjne (`sql01`), ścieżki ogólne | OK | — | OK |
| `restore`/`restore-db --snapshot latest` — najnowszy snapshot ze wszystkich zadań (nie nadpisuje danych — KB-5105/KB-3501 — ale może odtworzyć kopię innego zadania) | Niskie | BL-70; udokumentowane w przewodniku | otwarte |
Wysokie: 0.

## 2026-10-14 — iteracja 90 (przegląd rundy 6; 11,12,14,8) — fuzzing parserów rundy 6, BL-70, audyt
| Punkt kontrolny | Waga | Znalezisko | Status |
|---|---|---|---|
| `dotnet list package --vulnerable --include-transitive` (ci.sh, SDK 10.0.402, SHA-512 zgodny): 0 podatnych we wszystkich projektach | OK | — | OK |
| Skan sekretów (SecretScanTests) zielony; zakazane API (ScpClient, BinaryFormatter, bypass walidacji certyfikatu, MD5/SHA-1, Process.Start, UseShellExecute): brak użyć w src/ (grep) | OK | — | OK |
| Fuzzing `Round6FuzzTests` (≈ 35 000 przypadków, ziarna 9001–9005): SplitPath (ścieżka z klienta BrowseTree) — tylko KB-5003, brak segmentów „.”/„..”/separatorów/NUL, komunikat bez ścieżki | OK | — | OK |
| PlanMoves (wiersze FILELISTONLY z serwera SQL): tylko KB-3202/KB-3503; pliki fizyczne wyłącznie w katalogu danych/dziennika, nazwy z bezpiecznego alfabetu, bez duplikatów, nic ze ścieżek z kopii; nazwa logiczna tylko jako parametr | OK | — | OK |
| SqlServerAllowList: niepoprawna konfiguracja = KB-0103 (JobDefinitionInvalid) przy starcie, IsAllowed nigdy nie rzuca i nie przepuszcza hosta spoza listy; LocalOnly tylko aliasy lokalne („(local)” odrzuca już walidacja adresu) | OK | — | OK |
| ui-settings.json: niepoprawne UTF-8 wewnątrz napisu JSON → `InvalidOperationException` z `JsonElement.GetString` poza filtrem wyjątków → **uszkodzony plik blokował start GUI** (DoS lokalny, plik w profilu użytkownika) | Niskie | naprawione: filtr + test regresji (fuzz, ziarno 9004) | zamknięte |
| VerifyAudit po ręcznej zmianie wiersza (zdjęty wyzwalacz, 300 zmian kolumn tekstowych/params/hash): zawsze wskazuje zmieniony seq (params nieczytelne → 1, równoważny JSON po JCS → brak zmiany), nigdy nie rzuca | OK | — | OK |
| Odbudowa tabeli audit_log bez STRICT/CHECK (zmiana typu kolumny) mogłaby dać InvalidCastException w Verify → RPC Unknown zamiast „łańcuch przerwany” | Niskie | RD-51 | otwarte |
| BL-70: `latest:<zadanie>` — nazwa zadania jako parametr SQL (`j.name = $job`, nie LIKE — `%`/`_` nie są wzorcem, test), długość ≤ 256, nieznane zadanie = KB-5002 bez wiersza run_runs | OK | — | OK (zamknięte Niskie z it. 89) |
| STRIDE rundy 6 (RestoreDatabase, ListDestinations, VerifyAudit, BrowseTree): S — tożsamość pipe nadal „local-pipe” (W-30, Średnie, otwarte); T — łańcuch audytu wykrywa zmiany wierszy (test), odbudowa schematu → RD-51; R — metody modyfikujące i odmowy w audycie; I — ListDestinations bez sekretów (Operator, Qb38), komunikaty KB bez ścieżek/nazw serwerów; D — limity BrowseTree 5000/64/4096, gRPC 64 KiB (RD-44); E — RestoreDatabase tylko do NOWEJ bazy, bez REPLACE, TRUSTWORTHY OFF (Qb39) | OK | W-30 | otwarte (Średnie, wymaga Windows) |
Wysokie: 0. Otwarte Średnie: W-17/W-30 (tożsamość klienta pipe — Windows), RD-22 (dedup: warstwa 1 zrobiona, bez twardego limitu).

## 2026-10-14 — iteracja 91 (rola 1 prowadzi; 1,2,10,11) — BL-72 host usługi Windows
| Punkt kontrolny | Waga | Znalezisko | Status |
|---|---|---|---|
| Nowy pakiet Microsoft.Extensions.Hosting.WindowsServices 10.0.13 (+4 przechodnie): przyjęty w planie, OSV brak, wiersz w zaleznosci.md, lock-i zaktualizowane (`restore --force-evaluate`), audyt `--vulnerable --include-transitive` 0 | OK | — | OK |
| Ścieżki z konfiguracji (D8.9): UNC, `\\?\`, `\\.\`, NUL/CR/LF odrzucane zawsze, w usłudze tylko bezwzględne; komunikaty bez wartości zmiennych (test) | OK | — | OK |
| Pojedyncza instancja (D2.15 pkt 3, CE-12): blokada przed otwarciem katalogu; test procesowy: kod 75 + KB-7007, SHA-256 i czas modyfikacji `catalog.db` bez zmian, brak WAL | OK | — | OK |
| Mutex `Global\Kobian.Agent.Instance` z domyślnym ACL — squatting przez zwykłego użytkownika (SEC-22.4) zablokuje start usługi (wykrywane jako KB-7007, nie ciche niedziałanie; UnauthorizedAccessException obsłużony) | Średnie | W-33; jawne ACL mutexu po teście Windows | otwarte (wymaga Windows) |
| ACL pipe w usłudze (D2.9/D2.15): Deny NETWORK pierwszy, klienci tylko ReadWrite (bez CreateNewInstance), brak Users/Everyone, SID-y walidowane (test planu ACL) | OK | W-34 (zachowanie na Windows) | OK / wymaga Windows |
| Dopuszczony klient pipe = Operator (FixedRoleResolver) — w usłudze każdy lokalny administrator i członek „Kobian Operators” może uruchamiać/odtwarzać; brak eskalacji do Admin | Niskie | BL-17 (role z tokenu, SP-02), Qb41 | otwarte |
| `sc.exe` — tylko lista argumentów z profilu (bez powłoki, test: ścieżka względna/UNC/`"`/CR-LF odrzucona); Process.Start w src/ brak (test procesowy tylko w tests/, UseShellExecute=false) | OK | — | OK |
| Event Log: komunikaty bez danych klienta (KB, nazwa zmiennej); brak zarejestrowanego źródła → pominięcie wpisu bez wyjątku | OK | — | OK |
| STRIDE (nowe: tryb usługi, plik blokady, ACL pipe): S — podszycie pipe przy zatrzymanej usłudze nadal otwarte (weryfikacja PID z SCM w kliencie, D2.9 — później); T — `catalog.db.lock` zawiera tylko PID, nie jest zaufanym wejściem; R — bez zmian; I — brak; D — squatting mutexu (Średnie, wyżej); E — konto wirtualne bez Administrators, uprawnienia ograniczone listą D2.12 (dane profilu, MSI) | OK | W-33 | — |
Wysokie: 0.

## 2026-10-14 — iteracja 92 (rola 2 prowadzi; 2,4,11) — BL-73 dostawca SMB cz. 1
| Punkt kontrolny | Waga | Znalezisko | Status |
|---|---|---|---|
| Walidacja korzenia UNC (D8.9): `\\?\`, `\\.\`, litery dysku, ADS `:`, `.`/`..`, nazwy urządzeń, kropka/spacja na końcu, znaki `<>"|?*`, sterujące, > 1024 znaków, udziały administracyjne, IPv6/port — 23 przypadki odrzucenia w teście, komunikaty bez ścieżki | OK | — | OK |
| Nazwa serwera „liczbowa” niebędąca IPv4 (`300.1.1.1`) przechodziła regułę nazwy hosta — znalezione testem w tej iteracji | Niskie | naprawione przed zamknięciem (odrzucenie, test) | zamknięte |
| Brak powłoki i składania poleceń: `WNetAddConnection2W`/`WNetCancelConnection2W` (mpr.dll, System32), bez `net use`/Process.Start | OK | — | OK |
| Sekrety: hasło w buforze niezarządzanym zerowanym (`ZeroFreeGlobalAllocUnicode`), `SmbCredential.ToString` maskuje hasło, wyjątki KB-2102 bez konta i hasła (test); hasło jako `string` zarządzany nie jest zerowane (jak SFTP) | Niskie | ograniczenie .NET; DPAPI W-21 | zaakceptowane |
| D4.1/D4.6: sesja Backup bez DeleteAllowed (usunięcie odrzucone — test), Maintenance osobnym kontem; brak nadpisania (KB-2301 — test) | OK | — | OK |
| AC-01.2 / blokada konta: błąd uwierzytelnienia trwały, dokładnie jedna próba połączenia (test) | OK | — | OK |
| DoS: `WNetAddConnection2` jest blokujące i nie respektuje CancellationToken; `SharedSmbConnector` trzyma blokadę w czasie łączenia — wolny/nieosiągalny serwer wstrzymuje łączenie z innymi udziałami | Średnie | BL-73 cz. 2: blokada per serwer zamiast globalnej + watchdog/timeout D4.13/D4.16 | otwarte |
| STRIDE (nowe: konektor SMB, wspólne połączenia, plik sekretu SMB): S — podszycie serwera: podpisywanie SMB wymusza system, Kobian nie obniża polityki (D4.15); T — integralność przez podpisywanie/szyfrowanie SMB 3 + V1/V2 Kobian; R — bez zmian (operacje w run_runs); I — poświadczenia w pliku tylko-właściciel (Qb16/Qb42), komunikaty bez nazw kont/serwerów; D — wyżej (Średnie); E — udziały administracyjne odrzucone, połączenie bez litery dysku niewidoczne dla użytkownika interaktywnego (W-35) | OK | W-35 | wymaga Windows |
| Zależności: brak nowych pakietów; `dotnet list package --vulnerable --include-transitive` (ci.sh, SDK 10.0.402 SHA-512 zgodny): 0 podatnych | OK | — | OK |
Wysokie: 0.

## 2026-10-15 — iteracja 93 (rola 3 prowadzi; 3,4,2,11) — BL-73 cz. 2: cel smb/sftp w definicji zadania
| Punkt kontrolny | Waga | Znalezisko | Status |
|---|---|---|---|
| Przed zmianą: 10 miejsc w Agencie budowało `DestinationConfig` bez `SecretRef`/opcji — zadanie zdalne nie mogłoby się uwierzytelnić (awaria, nie wyciek); zastąpione `JobDefinition.ToDestination`, test end-to-end init→backup na udziale (SecretRef w każdym otwarciu, brak roli Maintenance w backupie — D4.6) | Niskie | naprawione | zamknięte |
| Walidacja wejść (D8.9): korzeń wg schematu (UNC przez UncPath, SFTP tylko `/…` bez `..` i `\`), host (znaki, ≤ 253), port 1–65535, `secretRef` = `file:<bezpieczna nazwa>` (traversal `file:../x` odrzucony), `hostKey` = `SHA256:`+base64 ≤ 128; 21 przypadków w teście | OK | — | OK |
| Sekrety w definicji: nieznane pola sekcji `destination` → KB-0103 (np. `user`/hasło nie wejdą do job_jobs.definition); definicja po init SMB nie zawiera hasła (test) | OK | — | OK |
| D4.11: zadanie SFTP bez przypiętego klucza = KB-2104; opcja `identity_probe` nigdy w zadaniu (test) | OK | — | OK |
| CLI: UNC i ścieżka SFTP nie przechodzą przez `Path.GetFullPath` (na Windows `/srv` stałoby się `C:\srv`); port parsowany bez znaku, poza zakresem → 64 | OK | — | OK |
| Średnie z it. 92 (globalna blokada w SharedSmbConnector): blokada per serwer, I/O poza lock słownika, rozłączenie pod blokadą serwera (brak wyścigu 1219); testy: wolny serwer nie blokuje innego, anulowane oczekiwanie nie zostawia zajętej bramki | Średnie | naprawione | zamknięte |
| DoS: słownik bramek per serwer rośnie z liczbą różnych nazw serwerów (ProbeDestination — rola Operator) i nie jest czyszczony; WNetAddConnection2 nadal bez timeoutu (blokuje tylko swój serwer) | Niskie | eviction bramek + watchdog D4.13 w BL-73 reszta | otwarte |
| STRIDE (nowe: sekcja celu zdalnego, pola InitJob): S — podszycie serwera SFTP: pin wymagany; T — definicja z SHA-256 w job_jobs (bez zmian); R — init/backup w run_runs; I — tylko odwołanie do sekretu, nie sekret; D — jak wyżej; E — InitJob nadal Operator (MethodPolicy bez zmian) | OK | — | OK |
| Zależności: brak nowych pakietów; `dotnet list package --vulnerable --include-transitive` (ci.sh, SDK 10.0.402 SHA-512 zgodny): 0 podatnych | OK | — | OK |
Wysokie: 0.

## 2026-10-15 — iteracja 94 (rola 4 prowadzi; 4,7,6,11) — BL-73 cz. 3: kreator SMB, kolejka D7.4, watchdog łączenia D4.13
| Punkt kontrolny | Waga | Znalezisko | Status |
|---|---|---|---|
| Niskie z it. 93 (słownik bramek rośnie, WNet bez limitu): bramki z licznikiem użytkowników i usuwaniem (test: 40 serwerów → 0 bramek), watchdog 30 s z zamknięciem spóźnionego połączenia (testy: timeout, anulowanie, spóźniony błąd, inny serwer niezablokowany) | Niskie | naprawione | zamknięte |
| DoS: porzucone wywołanie systemowe trzyma wątek puli do powrotu — najwyżej jeden na serwer (kolejna próba czeka ≤ limit zamiast tworzyć nowy wątek); wiele różnych martwych serwerów = tyle wątków (ProbeDestination tylko rola Operator) | Niskie | ograniczone konstrukcją; pomiar w SP-23 / W-36 | otwarte (akceptowane) |
| Anulowanie: po anulowaniu czekającej próby wyjątek OCE ma pierwszeństwo przed diagnozą serwera (regresja wykryta testem it. 93 i naprawiona przed zamknięciem) | OK | — | OK |
| Kolejka D7.4/D7.10: nabywanie „wszystko albo nic” (brak hold-and-wait), zwolnienie w `finally`; wpis `_inFlight` przed startem zadania (brak wycieku); blokady tylko w pamięci, nic nie trafia do logu | OK | — | OK |
| Konto w blokadzie = `secretRef` (odwołanie, nie sekret); jeden wpis z różnymi kontami backup/maintenance w dwóch równoległych uruchomieniach → możliwe KB-2101 (awaria, nie wyciek) | Niskie | udokumentowane (cel-smb.md), RD-55 | otwarte |
| Ręczne RunJob omija kolejkę → KB-2101 zamiast czekania (awaria, nie wyciek) | Niskie | BL-74 | otwarte |
| Kreator SMB (AC-01.3): brak pola hasła; do usługi idzie UNC i `file:<nazwa>` (walidacja D8.9 i nazwy wpisu po stronie usługi — jedno źródło reguł); pole SFTP nie przecieka do celu SMB (test) | OK | — | OK |
| Brak powłoki, SQL bez zmian, zakazane API brak, krypto bez zmian | OK | — | OK |
| STRIDE (nowe: kolejka zasobów SMB, watchdog): S — bez zmian (podszycie serwera — podpisywanie SMB, D4.15); T — brak nowych danych trwałych; R — uruchomienia nadal w run_runs (czekające jako Queued); I — komunikaty 121/1219 bez ścieżek i kont; D — jak wyżej; E — brak nowych metod API | OK | — | OK |
| Zależności: brak nowych pakietów; `dotnet list package --vulnerable --include-transitive` (ci.sh, SDK 10.0.402 SHA-512 zgodny): 0 podatnych | OK | — | OK |
Wysokie: 0.

## 2026-10-15 — iteracja 95 (rola 5 prowadzi; 5,2,11) — BL-17: tożsamość klienta pipe i role z tokenu (D2.9)
| Punkt kontrolny | Waga | Znalezisko | Status |
|---|---|---|---|
| Klienci CLI/UI łączyli się z domyślnym poziomem impersonacji (Impersonation) — usługa mogłaby działać jako klient; D2.9 wymaga Identification | Średnie | naprawione: `TokenImpersonationLevel.Identification` w GrpcAgentClient i GrpcDashboardSource | zamknięte |
| Każdy dopuszczony ACL klient = Operator (Qb41), aktor audytu „local-pipe” (Qb36) | Średnie | naprawione: TokenCallerResolver + AgentRoleMap; actor_sid = SID (test integracyjny) | zamknięte (Windows: W-37) |
| Fail-closed: wyjątek odczytu, brak tokenu, Anonymous, token procesu zamiast klienta, niepoprawny SID, SID NETWORK → KB-7001/None, nigdy rola domyślna (14 przypadków + test resolwera + test przez host) | OK | — | OK |
| UAC: grupa Administratorzy liczy się tylko jako włączona (deny-only pomijane przez WindowsIdentity.Groups) — do potwierdzenia na Windows | Niskie | W-37 | otwarte |
| ACL: „Kobian Admins” tylko ReadWrite, bez CreateNewInstance (test), Deny NETWORK pierwszy; NETWORK jako grupa odrzucony | OK | Qb45 | OK |
| Odmowy bez limitu (A15 „3 odmowy w 10 min”), brak `operators[]` per zadanie (SEC-21.3), UI nie zna roli (GetCallerInfo) | Niskie | RD-56/RD-57 | otwarte |
| Komunikaty KB-7001 bez szczegółów (status = kod, bez typu wyjątku); brak sekretów w logach; brak powłoki/SQL/krypto w zmianie; zakazane API brak | OK | — | OK |
| STRIDE (nowe: odczyt tokenu klienta): S — podszycie klienta niemożliwe bez jego tokenu, podszycie serwera dalej KB-7004 (W-17); T — brak; R — audyt z SID; I — SID w audycie (dane konta, nie klienta końcowego; katalog z ACL D2.6); D — cache per połączenie (słaba tabela, bez wzrostu); E — Identification zamiast Impersonation zmniejsza możliwości usługi | OK | — | OK |
| Zależności: brak nowych pakietów; `dotnet list package --vulnerable --include-transitive` (ci.sh, SDK 10.0.402 SHA-512 zgodny): 0 podatnych | OK | — | OK |
Wysokie: 0.

## 2026-10-15 — iteracja 96 (rola 6 prowadzi; 6,7,4,11) — BL-74 kolejka dla RunJob, RD-57 w1 baner trybu podglądu
| Punkt kontrolny | Waga | Znalezisko | Status |
|---|---|---|---|
| Niskie z it. 94 (ręczne RunJob omija kolejkę → KB-2101): wspólny LockManager, test: ręczne czeka, startuje po zwolnieniu i kończy Succeeded; mutacja (bez kolejki) → 4 testy czerwone | Niskie | naprawione | zamknięte |
| DoS: czekanie ograniczone 6 h (KB-4102), ponawianie co 1 s bez I/O (tylko sekcja krytyczna w pamięci); wait=false nie blokuje wywołującego; liczba czekających ograniczona KB-4103 (jedno na zadanie) | OK | — | OK |
| Wyciek blokad/zadania: zwolnienie w `finally`; anulowanie i przekroczenie limitu zdejmują `_active` (testy: kolejne uruchomienie bez KB-4103, GlobalFree wraca) | OK | — | OK |
| Uprawnienia: rola nadal wyłącznie z Agenta (D2.9); `TokenElevationInfo` czyta tylko typ podniesienia WŁASNEGO tokenu UI (TOKEN_QUERY), wynik dobiera tekst — nie odblokowuje akcji; błąd → tekst domyślny | OK | — | OK |
| Brak Process.Start/powłoki (świadomie bez przycisku „uruchom ponownie jako administrator”); P/Invoke z `DefaultDllImportSearchPaths(System32)`, uchwyt tokenu zamykany w `finally` | OK | — | OK |
| Teksty banera bez danych klienta i bez SID; SQL bez zmian (jeden parametryzowany INSERT z kodem KB); krypto bez zmian; zakazane API brak | OK | — | OK |
| Administrator bez podniesienia odrzucony przez ACL pipe widzi „usługa niedostępna” zamiast wskazówki (użyteczność, nie bezpieczeństwo) | Niskie | RD-58 | otwarte |
| STRIDE (nowe: czekanie w kolejce, odczyt typu tokenu UI): S — bez zmian; T — brak nowych danych trwałych; R — pominięcie po limicie zapisane w run_runs (KB-4102); czekanie bez wiersza (BL-76); I — brak; D — jak wyżej; E — brak (UI nie podnosi uprawnień, Agent decyduje o roli) | OK | — | OK |
| Zależności: brak nowych pakietów; `dotnet list package --vulnerable --include-transitive` (ci.sh, SDK 10.0.402 SHA-512 zgodny): 0 podatnych | OK | — | OK |
Wysokie: 0.

## 2026-10-15 — iteracja 97 (rola 7 prowadzi; 7,4,6,11,13) — BL-76 kolejka ręczna w katalogu, BL-77 dławienie otwarć sesji
| Punkt kontrolny | Waga | Znalezisko | Status |
|---|---|---|---|
| SQL: nowe/zmienione zapytania (INSERT Queued, UPDATE Queued→stan końcowy, podzapytanie active_state) wyłącznie z parametrami; przejście tylko `WHERE state='Queued'` (bez nadpisania stanu końcowego) | OK | — | OK |
| Spójność maszyny stanów D7.8: ręczne czekające = jeden wiersz (Queued→Running→wynik / Skipped KB-4102 / Cancelled), scheduler bierze tylko schedule/misfire (test z osieroconym wierszem manual), awaria → Interrupted | OK | — | OK |
| NFR-11: `active_state` to tylko nazwa stanu; brak ścieżek/hostów w GetStatus (istniejący test DoesNotContain) | OK | — | OK |
| SEC-24.4 / PM-26: ≤ 3 nieudane otwarcia na godzinę na cel, potem 10 min bez kontaktu z serwerem (KB-2114, przejściowy); błędy uwierzytelnienia przechodzą bez zmian i nie są ponawiane przez governor; anulowanie nie liczy się jako porażka | OK | — | OK |
| DoS: słownik celów ograniczony (1024, przycinanie bezczynnych), oczekiwanie kubełka anulowalne, rezerwacja zwracana przy anulowaniu; jitter z `Random.Shared` — nie krypto (wpis Allow w BannedApiTests z uzasadnieniem) | OK | — | OK |
| Klucz breakera per cel, nie per serwer/konto: wiele celów na jednym NAS z tym samym kontem może łącznie przekroczyć 3 nieudane logowania/h | Niskie | Qb47 (c), RD-59 | otwarte |
| STRIDE (nowe: wiersz kolejki ręcznej, governor otwarć): S — bez zmian; T — stan breakera tylko w pamięci (restart zeruje — świadome, ADR-0030); R — czekanie i pominięcie widoczne w run_runs (wcześniej brak śladu czekania); I — brak; D — patrz wyżej; E — brak | OK | — | OK |
| Zależności: brak nowych pakietów; `dotnet list package --vulnerable --include-transitive` (ci.sh, SDK 10.0.402 SHA-512 zgodny): 0 podatnych; zakazane API — test architektury zielony | OK | — | OK |
Wysokie: 0.

## 2026-10-17 — iteracja 98 (rola 8 prowadzi; 8,6,2,11) — BL-64 PlanRestore (podgląd odtworzenia)
| Punkt kontrolny | Waga | Znalezisko | Status |
|---|---|---|---|
| Ścieżki (D8.9): planer używa tych samych reguł co wykonawca (wydzielone `RestoreTarget.OpenRoot/TryChild`, `ExactPathSelection`): korzeń bezwzględny, istniejący, nie dowiązanie (KB-5104); nazwy z kopii walidowane, bez wyjścia poza korzeń; testy: względny, nieistniejący, symlink, `../repo`, `sub/..` | OK | — | OK |
| Brak zapisu: planer nie tworzy folderów; jedyny zapis to plik próbny o losowej nazwie (CreateNew + DeleteOnClose) w korzeniu — test: folder pusty po planie | OK | — | OK |
| Uprawnienia: PlanRestore = Operator (jak Restore), niemodyfikujące; MethodPolicy pokryta testem zgodności z proto i fuzzerem gRPC (rola Odczyt → PermissionDenied) | OK | — | OK |
| SEC-6.3/AC-07.2: kontrola zapisu na koncie usługi, nie tokenem klienta — Operator może odtworzyć do folderu, do którego sam nie ma prawa zapisu (luka istniała już w Restore; plan jej nie pogłębia) | Średnie | W-40, Qb48 (b) | otwarte |
| Ujawnienie informacji: `conflict_sample` zdradza istnienie plików o nazwach z kopii w dowolnym folderze czytelnym dla usługi (wyrocznia istnienia ograniczona do nazw już znanych z SCR-06) | Niskie | jak wyżej (AccessCheck tokenem klienta) | otwarte |
| DoS: przejście drzew ograniczone rozmiarem kopii; pamięć 32 B/unikalny blok (`ChunkKey`), ≤ 20 przykładów; termin klienta 1 h; anulowanie sprawdzane w pętli | OK | — | OK |
| Niezaufane drzewo: identyfikator bloku ≠ 32 B liczony jako brakujący (bez wyjątku), brak bloków → KB-5202 blokuje start | OK | — | OK |
| NFR-11: odpowiedź bez ścieżki folderu docelowego i bez sekretów; komunikaty KB bez ścieżek | OK | — | OK |
| STRIDE (nowe: RPC PlanRestore): S — bez zmian (D2.9); T — brak danych trwałych; R — operacja niemodyfikująca, audyt odmów jak dla każdej metody; I — patrz wyżej; D — patrz wyżej; E — brak | OK | — | OK |
| Zależności: brak nowych pakietów; `dotnet list package --vulnerable --include-transitive` (ci.sh, SDK 10.0.402 SHA-512 zgodny): 0 podatnych; zakazane API — test architektury zielony | OK | — | OK |
Wysokie: 0.

## 2026-10-17 — iteracja 99 (rola 9 prowadzi; 9,11,2) — kotwice audytu D9.9, RD-51 kontrola schematu
| Punkt kontrolny | Waga | Znalezisko | Status |
|---|---|---|---|
| SEC-19.1 / D9.9: przepisanie historii z przeliczeniem łańcucha (atakujący z dostępem do pliku, wyzwalacze odtworzone identycznie) wykrywane kotwicą (test); skrócenie dziennika wykrywane kotwicą z katalogu albo zewnętrzną (test) | OK | — | OK |
| RD-51 (Niskie z it. 90): zdjęty/dodatkowy wyzwalacz, odbudowana tabela bez CHECK → KB-6502 zamiast wyjątku/„OK” (3 testy) | Niskie | naprawione | zamknięte |
| Kotwice tylko w katalogu: kto przepisze `audit_log` i `audit_anchors` razem, zostanie wykryty wyłącznie kotwicą zewnętrzną — dziś przepisywaną ręcznie (e-mail/webhook/`journal/` brak) | Średnie | Qb49, BL-45 | otwarte |
| Wejście `--anchor`: ścisły format `seq:hex64` (seq ≥ 1), porównanie hash w czasie stałym; zły format → KB-0103 bez weryfikacji | OK | — | OK |
| SQL: nowe zapytania parametryzowane; `MigrationSchema` wykonuje wyłącznie osadzony zasób migracji w bazie `:memory:` | OK | — | OK |
| DoS: pętla co minutę, jedno zapytanie MAX(at_utc); błąd katalogu nie zatrzymuje usługi; Verify jak dotąd O(n) | OK | — | OK |
| NFR-11: kotwica = numer i hash (bez treści wpisów); VerifyAudit nadal Reader | OK | — | OK |
| STRIDE (nowe: pętla kotwic, kotwica zewnętrzna): S — bez zmian; T — patrz wyżej; R — poprawa (kotwice); I — brak; D — patrz wyżej; E — brak | OK | — | OK |
| Zależności: brak nowych pakietów; audyt ci.sh: 0 podatnych; zakazane API — test architektury zielony | OK | — | OK |
Wysokie: 0.

## 2026-10-17 — iteracja 100 (rola 10 prowadzi; 10,2,11,14) — szkielet MSI, custom action `--provision`
| Punkt kontrolny | Waga | Znalezisko | Status |
|---|---|---|---|
| SEC-10.7: custom action bez skryptów i powłoki — stały argument `--provision`/`--unprovision`, plik z pakietu (FileRef), deferred + Impersonate=no; test XML pilnuje braku atrybutu Script | OK | — | OK |
| D2.12/SEC-1.4: lista uprawnień tokenu w MSI = AgentServiceProfile.RequiredPrivileges, żadne z ForbiddenPrivileges (test); konto wirtualne bez hasła w pakiecie | OK | — | OK |
| 2.6 ACL danych: bez dziedziczenia, właściciel Administrators, tylko SYSTEM/Administrators/konto usługi; grupy Kobian, Users, Everyone, Authenticated Users bez dostępu (test planu); katalog danych będący dowiązaniem → odmowa (adapter) | OK | — | OK |
| Odinstalowanie nie usuwa danych ani kluczy (D10.2) — brak RemoveFolder/RemoveFile i katalogu ProgramData w pakiecie (test) | OK | — | OK |
| Adapter Windows niezweryfikowany na systemie (P/Invoke LSA, NetLocalGroupAdd, ACL) — możliwe błędy marshallingu do wykrycia w W-42; błędy zwracane jako kod Win32 bez danych klienta | Niskie | W-42 | otwarte |
| `--provision` dostępne dla każdego, kto uruchomi Agenta: bez uprawnień administratora kroki kończą się odmową systemu (ACL/LSA wymagają uprawnień) — brak eskalacji; plan nie przyjmuje ścieżek z argumentów (tylko ProgramData systemu) | OK | — | OK |
| Supply-chain: WiX nie jest pobierany ani uruchamiany w pętli (projekt poza solucją); wiersz w zaleznosci.md, audyt przed pierwszą budową (W-42/Q37) | OK | — | OK |
| STRIDE (nowe: CA instalatora): S — plik z podpisanego pakietu (podpis poza pętlą); T — ACL danych ustawiany przed startem usługi; R — wynik kroków w logu MSI; I — brak; D — błąd kroku wycofuje instalację; E — CA jako LocalSystem wykonuje wyłącznie stały plan | OK | — | OK |
| Zależności: brak nowych pakietów w solucji; audyt ci.sh: 0 podatnych | OK | — | OK |
Wysokie: 0.

## 2026-10-17 — iteracja 101 (rola 11 prowadzi; 11,8,2) — SEC-6.3 AccessCheck tokenem klienta (Restore/PlanRestore)
| Punkt kontrolny | Waga | Znalezisko | Status |
|---|---|---|---|
| Średnie z it. 98 (W-40): Operator mógł odtwarzać do folderu bez własnego prawa zapisu — bramka `RestoreTargetGate` w adapterze gRPC przed backendem (Restore z filtrem i z dokładnymi ścieżkami, PlanRestore), adapter AccessCheck z tokenem klienta; testy przez host: odmowa przed backendem (0 wywołań), Administrator bez kontroli | Średnie | naprawione (Linux: logika i host; Windows: W-43) | zamknięte warunkowo |
| Niskie z it. 98 (wyrocznia istnienia plików przez `conflict_sample`): Operator dostaje plan tylko dla folderu z własnym prawem zapisu | Niskie | naprawione wraz z powyższym | zamknięte warunkowo |
| Fail-closed: wyjątek adaptera, brak pipe/tokenu, token procesu zamiast klienta (ImpersonationLevel < Identification), dowiązanie, brak deskryptora → KB-5001 (test bramki z wyjątkiem) | OK | — | OK |
| D2.12/Q44: AccessCheck wymaga tylko tokenu poziomu Identification i TOKEN_QUERY — bez SeImpersonatePrivilege; P/Invoke z `DefaultDllImportSearchPaths(System32)` | OK | — | OK |
| TOCTOU: ACL folderu może się zmienić między kontrolą a zapisem (zapis kontem usługi) — okno krótkie, folder wskazany przez samego Operatora; dowiązania w trakcie odrzuca wykonawca (KB-5104) | Niskie | zaakceptowane (Qb51) | otwarte |
| STRIDE (nowe: bramka folderu docelowego): S — tożsamość z tokenu pipe (BL-75); T — brak; R — odmowa w audycie (Restore modyfikujące); I — zmniejszone (plan tylko dla folderów klienta); D — AccessCheck O(1) na wywołanie; E — zamknięta ścieżka „zapis kontem usługi tam, gdzie klient nie może” | OK | — | OK |
| Zależności: brak nowych pakietów; audyt ci.sh: 0 podatnych | OK | — | OK |
Wysokie: 0.

## 2026-10-17 — iteracja 102 (rola 12 prowadzi; 12,3,13,11) — RD-22 w2: tablica posortowana + filtr Blooma
| Punkt kontrolny | Waga | Znalezisko | Status |
|---|---|---|---|
| I-2/I-3 (brak utraty danych): bloki bazowe potwierdzone z definicji, nowe śledzone jak dotąd; test modelowy względem implementacji referencyjnej (50 rund × 400 operacji) w obu wariantach (z/bez Blooma) zielony; E2E backup→restore zielony | OK | — | OK |
| Bloom: fałszywie dodatni → tylko dodatkowe wyszukiwanie binarne (potwierdzenie); fałszywie ujemny niemożliwy (test 20 000 kluczy) — brak ryzyka pominięcia wysyłki bloku | OK | — | OK |
| Skróty Blooma z słów HMAC: klucz HMAC zna tylko repozytorium — nie da się celowo zatkać filtra z zewnątrz (i tak wyłączony domyślnie) | OK | — | OK |
| DoS/pamięć: 32 B/blok + chwilowy szczyt 2× przy zamrożeniu (pojemność z dokładnej liczby wpisów indeksów) | OK | — | OK |
| STRIDE: brak nowych granic zaufania (struktura w procesie) | OK | — | OK |
| Zależności: brak nowych pakietów; audyt ci.sh: 0 podatnych | OK | — | OK |
Wysokie: 0.

## 2026-10-17 — iteracja 103 (rola 13 prowadzi; 13,2,11,3) — protokół usługa ↔ Worker (D2.8)
| Punkt kontrolny | Waga | Znalezisko | Status |
|---|---|---|---|
| SEC-3.3 parser: ramka 1 B … 1 MiB, odczyt dokładny, EOF w środku = naruszenie; JSON ścisły (nieznane pola, MaxDepth 4, zamknięta lista operacji); fuzzing 2 000 losowych wejść — pętla Workera kończy się kodem 0/3, migawki zawsze zwolnione | OK | — | OK |
| SEC-2.5: Agent nie ufa Workerowi — Id, kod z rejestru KB (`KbCode.IsKnown`), ścieżki urządzeń migawki tylko `\\?\GLOBALROOT\Device\…`, bez „..”, ≤ 260; ramka ponad limit od Workera → klient nieużywalny (test) | OK | — | OK |
| DoS: jedno żądanie naraz, limit czasu 10 min, jedna migawka na proces (druga → KB-1406) | OK | — | OK |
| Brak uruchamiania procesu (Process.Start) w tej iteracji — job object, brak dziedziczenia uchwytów, weryfikacja podpisu Workera (SEC-2.7), KB-7008 — do zrobienia przy launcherze | Niskie | W-44, następna iteracja Workera | otwarte |
| `CoInitializeSecurity` z jawnym SD (SEC-12.4) — dotyczy adaptera VSS (SP-01), dziś zaślepka bez COM | Niskie | SP-01 | otwarte |
| STRIDE (nowe: kanał stdin/stdout): S — anonimowy pipe dziedziczony tylko przez proces potomny; T — ramki walidowane; R — brak (operacje techniczne); I — brak sekretów w protokole; D — limity; E — Worker nie przyjmuje ścieżek plików ani poleceń | OK | — | OK |
| Zależności: brak nowych pakietów; audyt ci.sh: 0 podatnych | OK | — | OK |
Wysokie: 0.

## 2026-10-17 — iteracja 104 (rola 14 prowadzi; 14,6,9,11) — SCR-11 ostatnia kotwica, dokumentacja rundy 7
| Punkt kontrolny | Waga | Znalezisko | Status |
|---|---|---|---|
| UI pokazuje tylko kotwicę „seq:sha256” (bez treści wpisów audytu, bez SID); pole tylko do odczytu; po utracie połączenia kotwica znika (brak nieaktualnej wartości) | OK | — | OK |
| Dokumentacja: odinstalowanie zostawia dane i klucze (świadomie, D10.2) — opisane z instrukcją ręcznego usunięcia; brak przykładowych haseł/kluczy w dokumentach (skan sekretów w ci.sh zielony) | OK | — | OK |
| Zależności: brak nowych pakietów; audyt ci.sh: 0 podatnych | OK | — | OK |
Wysokie: 0.

## 2026-10-17 — iteracja 105 (przegląd rundy 7; 11,12,14,2) — audyt, fuzzing, threat model
| Punkt kontrolny | Waga | Znalezisko | Status |
|---|---|---|---|
| `dotnet list package --vulnerable --include-transitive` (24 projekty, SDK 10.0.402 z weryfikacją SHA-512) | OK | 0 podatnych | OK |
| Skan sekretów (NFR-11) i zakazane API (test architektury) w ci.sh | OK | — | OK |
| Fuzzing Round7FuzzTests: UncPath (20k), AgentPaths (20k), ExactPathSelection (20k), AuditAnchor.TryParse (20k), WorkerJson żądania/odpowiedzi (2×20k), AgentInstallPlan.DataDir (20k); GrpcFuzzTests z ziarnami 105 i 7777 | OK | — | OK |
| AgentPaths: `KOBIAN_CATALOG` = korzeń („/”, „C:\”) albo katalog z ukośnikiem → katalog stanu „.” (względny; w usłudze katalog bieżący = System32 → sekrety i klucze poza ACL katalogu danych) | Niskie | naprawione (błąd konfiguracji, kod 64; test regresji) | zamknięte |
| Kotwice audytu tylko w katalogu (Qb49) | Średnie | BL-45, RD-61 | otwarte |
| Launcher Workera bez job object/weryfikacji podpisu (SEC-2.7, NFR-19) | Niskie | RD-65 | otwarte |
| Threat model rundy: elementy i tabele STRIDE w wpisach it. 92–104 (usługa, SMB, role z tokenu, PlanRestore/AccessCheck, kotwice, CA instalatora, Worker); nowe granice zaufania: proces potomny (Worker) i custom action MSI | OK | — | OK |
| Uprawnienia instalatora/usługi: lista uprawnień tokenu zgodna z D2.12 (test wxs), ACL danych bez grup użytkowników (test planu), brak operacji usuwania w sesji Backup (bez zmian, D4.6) | OK | — | OK |
| KB-kody i logi: nowe kody rundy (KB-1406, 2001, 2101, 2103, 2114, 5106, 6502, 6503, 7001, 7008, 7014) bez danych klienta w komunikatach (rejestr) | OK | — | OK |
Wysokie: 0.

## 2026-10-17 — iteracja 106 (rola 1 prowadzi; 1,13,11,2) — launcher Workera (RD-65), plan rundy 8
| Punkt kontrolny | Waga | Znalezisko | Status |
|---|---|---|---|
| Zakaz powłoki i składania poleceń: `UseShellExecute = false`, `ArgumentList` (rola z listy zamkniętej), plik wykonywalny ze stałej konfiguracji — test architektury (zakazane API) zielony | OK | — | OK |
| SEC-2.7: podpis sprawdzany PRZED startem, dowiązanie odrzucane przed weryfikacją (podmiana celu dowiązania po sprawdzeniu); test: niezaufany → KB-7015 i brak procesu | OK | — | OK |
| SEC-7.1/SEC-2.5: środowisko Workera z listy dozwolonej (bez KOBIAN_*, bez PATH użytkownika), katalog roboczy = katalog Workera, tylko stdin/stdout przekierowane | OK | — | OK |
| NFR-19: obiekt zadania (KILL_ON_JOB_CLOSE, 150 MB, 1 proces, bez WER); błąd przypisania = zabicie procesu; okno start→przypisanie (Qb55 a) | Niskie | zaakceptowane prowizorycznie | otwarte |
| TOCTOU: plik Workera może zostać podmieniony między weryfikacją podpisu a startem — katalog instalacji w Program Files (zapis tylko Administrators/SYSTEM) | Niskie | ACL instalacji (MSI) | otwarte |
| Authenticode bez odwołań przez sieć i bez porównania wydawcy (RD-67) | Niskie | RD-67 | otwarte |
| Worker umierający bez odpowiedzi → błąd protokołu w limicie czasu, bez zawieszenia (test z /bin/cat) | OK | — | OK |
| STRIDE (nowe: start procesu potomnego): S — podpis; T — dowiązania odrzucane, ACL katalogu programu; R — brak; I — środowisko oczyszczone; D — limit pamięci i jednego procesu; E — Worker dziedziczy token usługi (świadomie, VSS tego wymaga — D2.8) | OK | — | OK |
| Zależności: brak nowych pakietów; audyt ci.sh: 0 podatnych | OK | — | OK |
Wysokie: 0.

## 2026-10-17 — iteracja 107 (rola 2 prowadzi; 2,8,11) — dziennik wznowień odtwarzania (D8.8)
| Punkt kontrolny | Waga | Znalezisko | Status |
|---|---|---|---|
| D8.9 brak nadpisywania: plik roboczy CreateNew + `File.Move(overwrite: false)`; obcy plik docelowy o innej treści przy wznowieniu — pominięty (test); plik roboczy usuwany tylko, gdy dziennik mówi „rozpoczęty” i nie jest dowiązaniem | OK | — | OK |
| AC-07.3: uszkodzona treść usuwana z nazwy roboczej, nigdy nie pojawia się pod nazwą docelową (wcześniej usuwana z nazwy docelowej po zapisie) | OK | poprawa | OK |
| Porównanie treści przy wznowieniu: rozmiar + SHA-256 w czasie stałym, odczyt pliku docelowego kontem usługi tylko w trybie wznowienia (folder już autoryzowany SEC-6.3) | OK | — | OK |
| SQL: wszystkie zapytania dziennika parametryzowane; FK sesja→run_runs i plik→sesja zachowane | OK | — | OK |
| Dziennik zawiera ścieżki względne plików klienta (dane osobowe — nazwy plików): katalog z ACL D2.6; retencja dziennika — brak (rośnie z każdym odtworzeniem) | Niskie | RD-69 (sprzątanie sesji Done/Superseded po 30 dniach) | otwarte |
| caller_sid = „agent” zamiast SID (Qb56 d) | Niskie | do podłączenia z interceptora | otwarte |
| STRIDE (nowe: dziennik restore): S — brak; T — zmiana dziennika przez kogoś z dostępem do katalogu mogłaby oznaczyć plik jako „zweryfikowany”: skutek = pominięcie odtworzenia pliku, NIE nadpisanie (raport: plik policzony jako odtworzony) | Niskie | zaakceptowane (ACL katalogu) | otwarte |
| Zależności: brak nowych pakietów; audyt ci.sh: 0 podatnych | OK | — | OK |
Wysokie: 0.

## 2026-10-17 — iteracja 108 (rola 3 prowadzi; 3,8,6,11) — postęp odtwarzania (RD-60), PlanRestore z dziennikiem (RD-68)
| Punkt kontrolny | Waga | Znalezisko | Status |
|---|---|---|---|
| PlanRestore czyta dziennik wyłącznie do odczytu (osobne połączenie readOnly), nie przejmuje sesji (test); identyczna treść sprawdzana SHA-256 w czasie stałym tylko dla plików z wpisem „rozpoczęty” | OK | — | OK |
| Zapis postępu: jedno parametryzowane UPDATE ograniczone do `state='Running'` (nie nadpisze wyniku końcowego), najwyżej co 1 s; błąd zapisu nie przerywa odtwarzania | OK | — | OK |
| NFR-11: postęp = liczby (bez ścieżek) | OK | — | OK |
| Zależności: brak nowych pakietów; audyt ci.sh: 0 podatnych | OK | — | OK |
Wysokie: 0.

## 2026-10-17 — iteracja 109 (rola 4 prowadzi; 4,8,3,11) — `check --repair` (ocena szkód)
| Punkt kontrolny | Waga | Znalezisko | Status |
|---|---|---|---|
| SEC-18.5: ocena działa na sesji Backup (tylko odczyt), niczego nie zapisuje w repozytorium ani nie usuwa (test: pack nietknięty) | OK | — | OK |
| D2.9/D8.10: `repair = true` wymaga roli Administrator — sprawdzane w adapterze gRPC tym samym resolverem co interceptor (test przez host: Operator → PermissionDenied, zwykły check dozwolony) | OK | — | OK |
| Dane niezaufane z celu: drzewa czytane tak jak przy restore (limity dekoderów), drzewo w uszkodzonym packu nie jest czytane, wyjątek drzewa → folder uszkodzony (bez przerwania oceny) | OK | — | OK |
| NFR-11: odpowiedź RPC bez nazw plików (tylko liczby); próbka ścieżek tylko w obiekcie w procesie | OK | — | OK |
| SQL: migracja snap 0002 (ALTER ADD COLUMN z CHECK), aktualizacja parametryzowana tylko wierszy tego repozytorium | OK | — | OK |
| STRIDE (nowe: repair): T — kto może zmienić katalog, może ukryć „damage” (to tylko informacja; prawdę daje ponowny check) | Niskie | zaakceptowane | otwarte |
| Zależności: brak nowych pakietów; audyt ci.sh: 0 podatnych | OK | — | OK |
Wysokie: 0.

## 2026-10-17 — iteracja 110 (rola 5 prowadzi; 5,8,3,11) — odbudowa indeksu z trailerów, fallback indeksów (D3.10/D8.10)
| Punkt kontrolny | Waga | Znalezisko | Status |
|---|---|---|---|
| Zaufanie do packów-sierot: lista bloków bierzemy wyłącznie z packa, którego stopka/commitTag (czas stały) i trailer (AEAD) uwierzytelniają się kluczami repozytorium; pack obcy/losowy → „unreadable”, nic nie zapisane (test) | OK | — | OK |
| Append-only: nowy obiekt indeksu o losowym id przez VerifiedUploader (odczyt zwrotny), sesja Backup; nic nie nadpisane ani usunięte (SEC-18.5) | OK | — | OK |
| Fallback indeksów w odtwarzaniu: indeks z innym skrótem niż w snapshocie NIE jest używany; zamiast tego wszystkie czytelne indeksy — integralność danych bez zmian (AEAD ramki + HMAC chunkId każdego bloku), wynik oznaczony KB-1505 (widoczny, nie cichy) | OK | — | OK |
| DoS: pack ≤ 2 GiB (jak w odczycie), pobierany w całości do pamięci przy odbudowie — przy wielu dużych sierotach koszt pamięci (sekwencyjnie, jeden naraz) | Niskie | zaakceptowane (operacja ręczna Administratora) | otwarte |
| Zależności: brak nowych pakietów; audyt ci.sh: 0 podatnych | OK | — | OK |
Wysokie: 0.

## 2026-10-17 — iteracja 111 (rola 6 prowadzi; 6,9,11) — webhook D9.10 z outboxem
| Punkt kontrolny | Waga | Znalezisko | Status |
|---|---|---|---|
| SSRF: URL tylko https + host publiczny (statycznie), ponowna kontrola adresów po DNS przy każdym połączeniu (ConnectCallback — DNS rebinding), przekierowania nieśledzone, bez proxy użytkownika (test PublicOnly, 302 → Retry) | OK | — | OK |
| DoS po stronie odbiorcy złośliwego: połączenie 10 s, całość 30 s, odpowiedź ≤ 64 KiB czytana i porzucana, partia 20, harmonogram ponowień ograniczony (5 prób) | OK | — | OK |
| Sekret: ≥ 32 B z pliku katalogu sekretów (ACL D2.6), nie w zmiennej środowiskowej ani logach; zerowany przy Dispose; podpis HMAC-SHA256 z timestampem, porównanie stałoczasowe po stronie Verify | OK | — | OK |
| Prywatność (SEC-9.6/NFR-11): zdarzenie bez ścieżek; tryb minimalny bez nazwy zadania (test) | OK | — | OK |
| Sekret w pliku zamiast DPAPI; brak alertu przy dead | Niskie | Qb58 (c)/(b), BL-45 cz. 2 | otwarte |
| SQL parametryzowany; zależności: brak nowych pakietów | OK | — | OK |
Wysokie: 0.

## 2026-10-17 — iteracja 112 (rola 7 prowadzi; 7,9,11) — raport dzienny z kotwicą audytu (D9.11, RD-61)
| Punkt kontrolny | Waga | Znalezisko | Status |
|---|---|---|---|
| Kotwica audytu poza katalogiem: `seq:sha256` w raporcie u odbiorcy — podmiana katalogu (SYSTEM) wykrywalna `audit verify --anchor` (test: kotwica z raportu parsuje się i weryfikuje) | OK | — | OK |
| Prywatność: raport bez nazw zadań, ścieżek i serwerów; identyfikatory walidowane (ASCII/cyfry/'-'); koperta ≤ 64 KiB | OK | — | OK |
| Idempotencja i spójność: znacznik `core_events` + outbox w jednej transakcji pisarza; drugi przebieg w tym samym terminie nic nie dodaje (test + mutacja) | OK | — | OK |
| Martwe powiadomienia: wpis audytu KB-6301 (łańcuch nienaruszony, test); parametry tylko liczba prób i wynik | OK | — | OK |
| Kotwica w raporcie to skrót łańcucha (nie sekret), ale ujawnia liczbę operacji audytu — akceptowalne | Niskie | Qb59 | otwarte |
| SQL parametryzowany; brak nowych pakietów | OK | — | OK |
Wysokie: 0.

## 2026-10-17 — iteracja 113 (rola 8 prowadzi; 8,3,11) — strumieniowa baza przyrostu (BL-83, RD-64)
| Punkt kontrolny | Waga | Znalezisko | Status |
|---|---|---|---|
| Integralność: każdy indeks bazowy nadal uwierzytelniany przez MetadataStore.LoadIndexAsync (AEAD) przed dodaniem bloków; para (id, SHA-256) w snapshocie bez zmian (test porównuje z bazą listową) | OK | — | OK |
| I-3 bez zmian: bloki bazowe potwierdzone z indeksów uwierzytelnionych; nowe śledzone w słowniku (test dedup identyczny dla obu ścieżek) | OK | — | OK |
| Błąd użycia: zestaw jednorazowy (Take) i zakaz łączenia Baseline + BaselineStream — wyjątek zamiast cichej podwójnej bazy (test) | OK | — | OK |
| DoS pamięci: limit indeksów w snapshocie (MaxIndexes) bez zmian; pamięć trzymana ≈ 3,8× mniejsza (pomiar w pomiary.md) | OK | — | OK |
| Zależności: brak nowych pakietów | OK | — | OK |
Wysokie: 0.

## 2026-10-17 — iteracja 114 (rola 9 prowadzi; 9,4,11) — AIMD równoległości celu (D4.16 pkt 2, BL-84)
| Punkt kontrolny | Waga | Znalezisko | Status |
|---|---|---|---|
| Ochrona przed blokadą IP/kontem (PM-26): KB-2112 zmniejsza równoległość ×0,5 i blokuje wzrost na 5 min (zegar monotoniczny); razem z istniejącym dławieniem otwarć (BL-77) | OK | — | OK |
| Bramka nie przekracza limitu także przy zmniejszeniu w trakcie (test 10 operacji / limit 4 → 2); anulowanie w kolejce nie zajmuje miejsca (test) | OK | — | OK |
| Wartości z katalogu: CHECK 1…64, clamp do rekomendowanej, TTL 7 dni, wpis z przyszłości (skok zegara) ignorowany — złośliwy wpis nie podniesie równoległości ponad rekomendowaną | OK | — | OK |
| SQL parametryzowany (UPSERT/DELETE); brak nowych pakietów | OK | — | OK |
Wysokie: 0.

## 2026-10-17 — iteracja 115 (rola 10 prowadzi; 10,6,11) — znacznik uszkodzonej kopii w UI/CLI (RD-70)
| Punkt kontrolny | Waga | Znalezisko | Status |
|---|---|---|---|
| Dane z usługi: UI przyjmuje tylko „partial”/„unrecoverable” (inna wartość = brak znacznika), liczba plików ≥ 0 (test mapowania) | OK | — | OK |
| Odtwarzanie kopii nieodtwarzalnej zablokowane w trzech miejscach SCR-06 (folder, wiersz, baza) i w SCR-10 — obrona w UI; Agent i tak odmówi (brak indeksów/korzenia) | OK | — | OK |
| NFR-11: w odpowiedzi tylko stan i liczba plików, bez nazw uszkodzonych plików | OK | — | OK |
| Zgodność proto: nowe pola 7/8 (starszy Agent = pusty znacznik) | OK | — | OK |
Wysokie: 0.

## 2026-10-17 — iteracja 116 (rola 11 prowadzi; 11,14,5) — minimalizacja danych w katalogu (RD-69, RODO)
| Punkt kontrolny | Waga | Znalezisko | Status |
|---|---|---|---|
| RODO art. 5 ust. 1 lit. e: nazwy plików i ścieżki docelowe klienta w restore_files/restore_sessions znikają po 30 dniach (test: 452 sesje, 454 pliki) | OK | — | OK |
| Bezpieczeństwo operacji: uruchomienia Queued/Running/Paused nigdy nie są sprzątane (test „old-running”); partie ≤ 200 sesji — bez długiej blokady pisarza | OK | — | OK |
| Treść powiadomień (identyfikatory, ewentualnie nazwy zadań) — sent/dead po 30 dniach; pending zostaje (test) | OK | — | OK |
| Łańcuch audytu nietknięty (append-only, wyzwalacze) — sprzątanie nie dotyka audit_* | OK | — | OK |
| Usunięte wiersze mogą pozostać w wolnych stronach pliku SQLite do VACUUM/secure_delete | Niskie | rozważyć PRAGMA secure_delete=ON w CatalogConnection (RD-74) | otwarte |
| SQL parametryzowany; brak nowych pakietów | OK | — | OK |
Wysokie: 0.

## 2026-10-17 — iteracja 117 (rola 12 prowadzi; 12,11,13) — PRAGMA secure_delete w katalogu (RD-74)
| Punkt kontrolny | Waga | Znalezisko | Status |
|---|---|---|---|
| Resztki danych: usunięte wiersze (nazwy plików, treść powiadomień) nadpisywane zerami w pliku głównym — test z kontrolą negatywną (bez pragmy znacznik zostaje) | OK | — | OK |
| WAL: stare obrazy stron mogą być w pliku -wal do checkpointu (auto-checkpoint SQLite); kopie zapasowe katalogu sprzed usunięcia nadal zawierają dane | Niskie | udokumentowane; checkpoint po sprzątaniu — rozważyć | otwarte |
| Pakiety: Bench dostaje referencję do Kobian.Catalog.Sqlite (pakiety już audytowane: Microsoft.Data.Sqlite.Core, SQLitePCLRaw) — lock odświeżony, bez nowych nazw spoza listy | OK | — | OK |
Wysokie: 0.

## 2026-10-17 — iteracja 118 (rola 13 prowadzi; 13,3,4,11) — równoległe wysyłki packów (RD-73)
| Punkt kontrolny | Waga | Znalezisko | Status |
|---|---|---|---|
| I-2 per pack: bloki potwierdzane dopiero po weryfikacji (V1-a/V1-b) TEGO packa; indeks i snapshot po zakończeniu wszystkich wysyłek (test + mutacja: bez potwierdzenia partii run pada na I-3) | OK | — | OK |
| Sesje jednowątkowe (D4.10): każda wysyłka na własnej sesji z puli; test wykrywa współbieżne użycie sesji | OK | — | OK |
| Błąd w trakcie: wysyłki w toku są dokończone przed zwolnieniem buforów (AbandonUploadsAsync) — brak użycia zwolnionej pamięci; snapshot nie powstaje (test KB-2112) | OK | — | OK |
| DoS pamięci: N × rozmiar packa w toku; N ≤ liczba sesji i ≤ limit AIMD (test: limit 1 → 1 naraz; mutacja wykryta) | Niskie | Qb62 (d) — budżet pamięci NFR-05 do sprawdzenia przy podłączeniu w Agencie | otwarte |
| Brak nowych pakietów; brak zmian formatu repozytorium | OK | — | OK |
Wysokie: 0.

## 2026-10-17 — it. 119 (rola 14 prowadzi; recenzja rola 11) — RD-73 cz. 2: sesje wysyłki w Agencie

Zakres: `CatalogJobService.OpenUploadSessionsAsync`/`UploadSessions`, `UploadParallelism`, Program.cs (`KOBIAN_PARALLEL_UPLOADS`), Bench `--e2e [MiB] [sesje]`.

| Kontrola | Wynik | Waga | Status |
|---|---|---|---|
| Rola poświadczenia: wszystkie sesje dodatkowe `CredentialRole.Backup` (bez usuwania, D4.6) | OK | — | OK |
| Governor otwarć (D4.16 pkt 3, SEC-24.4): sesje dodatkowe przez tę samą fabrykę co główna (sftp/smb = GovernedStorageProviderFactory) | OK | — | OK |
| Pinning/tożsamość: błąd bezpieczeństwa przy otwarciu dodatkowej sesji NIE jest połykany (tylko KB-2112 i przejściowe) — kopia nie jedzie po sesji z innym kluczem hosta | OK | — | OK |
| Wyciek sesji: sesje dodatkowe zamykane w `DisposeAsync` także po błędzie i po wyjątku w trakcie otwierania (test: otwarcia = zamknięcia) | OK | — | OK |
| Sesje jednowątkowe (D4.10): sonda w teście wykrywa współbieżne użycie sesji; główny uploader w puli tylko w fazie packów (indeks/snapshot po DrainAsync) | OK | — | OK |
| Wejście z katalogu: limit z `repo_dest_parallelism` przycinany do 1…zalecenie (konstruktor AIMD), zalecenie dostawcy przycięte do 1…64 | OK | — | OK |
| DoS pamięci: N buforów packów naraz — zmierzone +130–180 MiB szczytu przy N=2 (Bench, Local 16 MiB) | Średnie | Qb63/ADR-0038; W-48 (NFR-05 na Windows); wyłącznik `KOBIAN_PARALLEL_UPLOADS=0` | otwarte |
| Sekrety/logi: brak nowych komunikatów z danymi celu; brak nowych KB; brak nowych pakietów; format repo bez zmian | OK | — | OK |

STRIDE (nowy element: pula sesji wysyłki): S — tożsamość serwera sprawdzana przy każdym otwarciu (jak sesja główna); T — każdy pack weryfikowany V1 przez swój uploader, I-2 per pack; R — bez zmian (wynik w run_runs); I — brak; D — liczba sesji ≤ zalecenie dostawcy i ≤ AIMD, governor; E — rola Backup.
Wysokie: 0.

## 2026-10-17 — iteracja 120 (przegląd rundy 8 + BL-45 e-mail + BL-34 FTP/FTPS + RD-75)
Zakres: Round8FuzzTests (15 przypadków, ≈ 71 tys. wejść: WebhookConfig/UrlPolicy, DailyReport.Build/Tick, AIMD + CatalogParallelismStore, RepoIndexRecovery), skan zakazanych API, skaner sekretów, `--vulnerable` (0 podatnych), nowe elementy: e-mail SMTP (MailKit), dostawca FTP/FTPS (FluentFTP), pula buforów packów.
| Znalezisko | Waga | Działanie | Status |
|---|---|---|---|
| F1 check --repair: rozmiar packa-sieroty od celu → Overflow/OOM, krótki obiekt → EndOfStream | Niskie | limit Array.MaxLength, KB-2401, MaxPackSize ≈ 528 MiB, test regresji | naprawione |
| F2 AIMD: nieparsowalne learned_at → każda kopia na cel Failed | Niskie | TryParse → start od rekomendowanej | naprawione |
| F3 raport dzienny: job_id spoza formatu → pętla kończy się po cichu | Niskie | filtr + łapanie ArgumentException | naprawione |
| F4 SSRF webhook: IPv6 z osadzonym prywatnym IPv4 (NAT64, 6to4, ::a.b.c.d, Teredo) | Niskie | odrzucane | naprawione |
| F5 webhook: host niepoprawny wg IDNA → wyjątek przy starcie | Niskie | odrzucenie z KB-6302 | naprawione |
| F7 wznowienie restore usuwa podstawiony `*.kobian-partial` | Niskie | RD-76 | otwarte |
| F8 / e-mail: brak kontroli ACL plików sekretów (webhook.secret, smtp.credentials), sekret nie zerowany | Niskie | RD-71 (DPAPI) | otwarte |
| FTP: FluentFTP nie waliduje certyfikatu kanału danych | Średnie | dane AEAD + adres sterowania; SP-04 (upstream/CustomStream) | otwarte |
| FTP: okno wyścigu przed RNTO/STOR (serwery uniksowe nadpisują) | Niskie | Qb71, dokumentacja | zaakceptowane prowizorycznie |
| E-mail: brak alertu dla dead (tylko audyt + licznik w raporcie) | Niskie | — | otwarte |
STRIDE (nowe): e-mail — S: walidacja certyfikatu zawsze, AUTH tylko po TLS; T: nagłówki przez API MimeKit, CR/LF odrzucane; I: bez ścieżek i sekretów w treści; D: limit odbiorców 10, timeouty 30/90 s; FTP — S: pin SPKI/łańcuch przed USER/PASS; T: V1 rozmiarem + AEAD; I: logger biblioteki odłączony (SEC-4.6); D: limity sesji, AIMD; E: rola Backup bez DELE. Wysokie: 0.

## 2026-10-17 — iteracja 121 (D4.11 tożsamość serwera, FTPS w kreatorze, US-29 eksport/import)
| Znalezisko | Waga | Działanie | Status |
|---|---|---|---|
| Brak step-up (SEC-4.1) przed zatwierdzeniem klucza serwera | Średnie | Qb82; ochrona: Administrator + audyt z SID | otwarte |
| Lista tożsamości pokazuje SID zatwierdzającego Operatorowi | Niskie | Qb78 | otwarte |
| Brak zdarzenia/alertu A09 przy nowym kluczu oczekującym i zmianie SPKI z CA | Niskie | do alertów w Agencie (it. 122) | otwarte |
| Kreator: zatwierdzony odcisk przenosił się na zmieniony host/port | Niskie | odcisk kasowany przy zmianie typu/hosta/portu | naprawione |
| Import: heurystyka sekretów może dać fałszywy alarm (bezpieczne odrzucenie) | Niskie | Qb84 | zaakceptowane |
| Import: dry-run ze źródłem UNC łączy się z SMB na koncie usługi (jak UpsertJob validate_only) | Niskie | — | otwarte |
| Skaner sekretów wykrył literały w nowym teście (klucz PEM, `var password`) | Niskie | poprawione przed scaleniem | naprawione |
STRIDE: dest_identity — S: brak TOFU (pierwszy kontakt = oczekujący, bez poświadczeń), T: zatwierdzić można tylko dokładnie oczekujący odcisk (24 h), R: audyt z SID, D: limity 8/32/500 wierszy, E: Approve/SetPolicy tylko Administrator. Import — T: ścisły schemat, nieznane pola odrzucane, limity SEC-3.3, fuzzing 4000 mutacji; E: brak akcji/skryptów, zadania wyłączone. Wysokie: 0.

## 2026-10-17 — iteracja 122 (alerty A01–A17 w Agencie, V3 z harmonogramu, konsolidacja indeksów D3.16)
| Znalezisko | Waga | Działanie | Status |
|---|---|---|---|
| Ręczne `kobian check` nie bierze blokady kolejki ani dzierżawy celu — równoległy prune z innego hosta może dać fałszywe KB-1501 | Średnie | do zrobienia (ta sama ścieżka co V3 z harmonogramu) | otwarte |
| Przejścia alertów nie trafiają do łańcucha audytu | Niskie | — | otwarte |
| A01 mógłby zadziałać za wcześnie, gdyby ponowienia startowały > 2 min po błędzie (dziś natychmiast) | Niskie | Qb90 | otwarte |
| Wiersze zweryfikowanych packów nie są sprzątane po prune; V3 bez starzenia priorytetu; zegar w tył opóźnia termin | Niskie | — | otwarte |
| Spis prune czyta każdy indeks dwukrotnie; przyspieszenie dopiero po usunięciu oryginałów | Niskie | — | otwarte |
STRIDE: alerty — I: zamknięta lista pól, tryb minimalny bez nazwy zadania, test z losowymi nazwami; D: limit 200 aktywnych w GetStatus, pętla odporna na obce wiersze. V3 — T: próbka po id packa obejmuje całe repo w ⌈100/%⌉ cyklach; KB-1511 dla brakującej kopii na celu (pusty nośnik nie przechodzi jako Succeeded); E: tylko odczyt, nic nie usuwa. Konsolidacja — T: indeks skonsolidowany używany tylko, gdy SHA z klucza 32 = SHA z odwołania snapshotu; awarie w każdym kroku testowane; usuwanie wyłącznie przez prune z karencją 24 h. Wysokie: 0.

## Iteracja 123 (rola 11; BL-40 D5.11 — zapytania kontrolne ERP)
Zakres: `ControlQueryRunner`, `ControlSet`/`ControlVerifier`, `ErpDetector.BuildDatabaseQuery`, integracja MsSqlBackupService/MsSqlRestoreService/CatalogRestoreService. Lista kontrolna: SQL — nazwa bazy wyłącznie parametr + `QUOTENAME` po stronie serwera (test: tekst polecenia nie zawiera nazwy), treść zapytania tylko z wbudowanego profilu i ponownie walidowana tuż przed wykonaniem (jedna instrukcja SELECT, bez `;` i komentarzy; test „Unsafe_sql_never_reaches_the_server”); SEC-15.3 — do snapshotu trafiają tylko liczby całkowite i daty (test: kolumna tekstowa → zapytanie pominięte; mutacja wykryta); `controls` odczytywane z metadanych traktowane jako dane niezaufane (limit 30 KiB/512 rekordów, głębokość JSON 4, wzorzec identyfikatora; test odrzucenia zniekształconych); wynik restore niesie tylko identyfikatory zapytań, nie wartości (NFR-11); limity DoS: 30 s na zapytanie, 256 wierszy, LOCK_TIMEOUT 5 s; anulowanie wywołującego nie jest połykane. Brak sekretów i ścieżek w nowych komunikatach/KB.
| Znalezisko | Waga | Status |
|---|---|---|
| Wykrywanie profilu czyta `sys.tables` bazy klienta przy każdej kopii (koszt/metadane) — tylko-odczyt, 1 zapytanie | Niskie | akceptowane |
| Zapytanie kontrolne działa z uprawnieniami konta kopii (db_datareader opcjonalnie) — szersze niż samo db_backupoperator | Niskie | opisane w docs/mssql-source.md, Qb103 |
STRIDE: T — podmiana `controls` w metadanych: snapshot szyfrowany AEAD, błąd parsowania = brak kontroli (nie fałszywy sukces); I — wartości z bazy klienta poza liczbami/datami nie opuszczają serwera; D — limity czasu/wierszy/rozmiaru.

## Iteracja 124 (2026-10-17, rola 11 recenzuje rolę 4)
Zakres: tools/kb.py (action.en), docs/kb-codes.yaml (+139 pól action.en), KbMessages.en.resx (akcje EN), testy KbResources/KbRegistry. Wejścia: wyłącznie stałe teksty z repo, brak ścieżek, SQL, procesów ani sekretów. Treść akcji EN przejrzana pod kątem wycieku danych klienta (SEC-11.5): brak. Znaleziska: Wysokie 0, Średnie 0, Niskie 0. Audyt zależności ci.sh: czysty (bez nowych pakietów).

## It. 125 (2026-10-17) — repozytorium złote v1 (tests/golden/v1-basic)
Zakres: GoldenRepoTests + fixtura. Klucz główny/sól to publiczne stałe testowe (syntetyczne, nie sekrety; skan SecretScanner w ci.sh OK). Test błędnego klucza (SEC-3) i mutacja bajtu packa → czerwony (zmierzone). Brak nowych pakietów, brak zmian w kodzie produkcyjnym. STRIDE: Tampering — wykrywany (AEAD). Znaleziska: Wysokie 0, Średnie 0, Niskie 0.

## It. 126 (2026-10-17) — kobian-restore na celach zdalnych (rola 11)
Zakres: OfflineRestore (argumenty celu zdalnego), StandaloneRemoteProviders, przeniesienie File*CredentialProvider do Providers.Storage. Wejścia: host/port walidowane przez FtpOptions/SshNetOptions (znaki, zakres portu); secret-ref przez IsSafeName (traversal, nazwy urządzeń Windows) — logika niezmieniona, testy FileSftpCredentialProviderTests zielone. SFTP bez --hostkey = odmowa (usage 64); brak katalogu tożsamości — pin z argumentów, brak TOFU. Rola Backup (bez usuwania). Hasło wyłącznie z pliku, brak sekretów w komunikatach. Jawny FTP zablokowany. STRIDE: Spoofing — pin hosta/SPKI; Tampering — AEAD repo bez zmian. Brak nowych pakietów. Znaleziska: Niskie — nagłówek kobian.repo z lokalnej kopii (nie z serwera) świadomie, by serwer nie dostarczał materiału KDF. Wysokie: brak.

## Iteracja 127 (2026-10-17, rola 11) — złote repo #2 `v1-keyed`
Zakres: tylko testy i fixtura (`tests/golden/v1-keyed`, `GoldenKeyedRepoTests`); brak zmian w kodzie produkcyjnym, brak nowych pakietów. Fixtura zawiera wyłącznie publiczne stałe TESTOWE (MK, hasło „…NOT-A-SECRET”, Qb104) i dane syntetyczne; hasło czytane z pliku jak w `kobian-restore`. Testy: zły klucz/hasło → KB-1xxx KeyUnwrapFailed, podrobiony nagłówek → KB-1104 (RepoHeaderInvalid, bez wyroczni); kopia robocza fixtury (odczyt nie modyfikuje zatwierdzonych plików). STRIDE: Tampering — MAC nagłówka (pokryte testem); Spoofing — klucz spoza listy (pokryte w RepoProvisionerTests). Znaleziska: Niskie — stałe hasło testowe w repo może wywołać fałszywy alarm skanera sekretów (oznaczone w nazwie i komentarzu). Wysokie: brak.

## 2026-10-18, it. 128 (rola 11) — złote repo #3 (tylko testy)
Zakres: GoldenPrunedRepoTests + fixtura syntetyczna. Klucz/sól to publiczne stałe testowe (Qb104), brak sekretów; testy na kopiach w katalogu tymczasowym; brak nowych pakietów. Znaleziska: brak (Wysokie 0, Średnie 0, Niskie 0). Audyt zależności ci.sh: czysty.

## 2026-10-18, it. 129 (rola 11) — OAuth2 SMTP (client credentials)
Zakres: OAuth2.cs, EmailConfig, MailKitSmtpTransport, EmailSender.Classify. STRIDE: Spoofing — tylko HTTPS do login.microsoftonline.com, walidacja certyfikatu domyślna, brak przekierowań; tenant ograniczony do [A-Za-z0-9.-] (brak wstrzyknięcia w ścieżkę URL); Information disclosure — sekret/token poza logami, wyjątkami i ToString (testy), sekret tylko z pliku (bez linków, ≤ 4 KB), brak treści odpowiedzi w komunikatach; DoS — odpowiedź ≤ 16 KB, token ≤ 8 KB, limit 20 s, MaxDepth JSON 4; Tampering — token_type=Bearer wymagany, znaki sterujące odrzucone (wstrzyknięcie do SASL); OAuth2 odrzucone przez jawne SMTP. Brak nowych pakietów, brak banowanych API. Znaleziska: Wysokie 0, Średnie 0, Niskie 1 (sekret w pliku zamiast DPAPI — Qb105/SEC-9.4, znane).

## 2026-10-18, it. 130 (rola 11) — AckAlert / SilenceAlert / `kobian alerts`
Zakres: AlertService.ActAsync, RPC AckAlert/SilenceAlert, MethodPolicy, AgentCallAudit, CLI alerts, KB-6120. STRIDE: Spoofing/Elevation — Ack = Operator+, Silence = Administrator (SEC-4.1; ciche wyłączenie alertu to osłabienie wykrywania), odmowy audytowane testem (denied) z SID; Tampering — `alert_id` walidowany jako 32 znaki hex [0-9a-f] przed zapytaniem, zapytania parametryzowane (test z `'; DROP TABLE`), zmiana tylko z `notified` (maszyna stanów), czas ograniczony 1…10080 min po stronie backendu + 7 dni/24 h w maszynie stanów; Repudiation — każde wywołanie w łańcuchu audytu (cel = alert_id, minutes); Information disclosure — alert_id losowy, bez danych klienta, odpowiedź bez treści alertu; DoS — brak nowych pętli, operacja w kolejce zapisów katalogu (jedna transakcja); stan pamięci unieważniany przy błędzie. Brak nowych pakietów. Znaleziska: brak (Wysokie 0, Średnie 0, Niskie 1: wyciszenie nie wymaga step-up — jak SetServerIdentityPolicy, odłożone do BL step-up Qb82). Audyt zależności ci.sh: czysty.

## 2026-10-18, it. 131 (rola 11) — lista alertów i akcje na pulpicie SCR-01
Zakres: `AlertModel.cs`, `DashboardViewModel` (Alerts, Ack/Silence), `GrpcDashboardSource` (MapAlerts, AckAlert/SilenceAlert), widok, zasoby PL/EN. STRIDE: Spoofing/Elevation — przyciski wg roli (Ack Operator+, Silence Administrator), UI nie wywołuje RPC bez roli (test `Silence_uses_default_duration_and_requires_admin`), egzekucja i audyt w Agencie (it. 130); odmowa roli PermissionDenied → KB-7011 jako komunikat, nie wyjątek; Tampering — `alert_id` pochodzi z Agenta i nie jest składany z danych użytkownika, Agent waliduje 32 hex; Info disclosure — wiersz niesie tylko kod reguły, nazwę zadania/identyfikator, czas i kod KB (bez ścieżek, hostów, nazw plików — NFR-11/SEC-9.6), tytuły ze stałych zasobów; DoS — Agent ogranicza listę do 200 alertów; nieznane wartości severity/state mapowane na najłagodniejsze (bez wyjątku); komunikat błędu = KbText.Message + kod. Skan sekretów, `--vulnerable`: audyt ci.sh czysty. Znaleziska: Wysokie 0, Średnie 0, Niskie 1 — wyciszenie bez step-up (Qb82/Qb106, decyzja właściciela; Agent i tak wymaga roli Administrator i audytuje).

## 2026-10-18, it. 132 (rola 12 prowadzi, rola 11 recenzuje) — licznik alertów w `kobian status`
Zakres: `AlertSummary` (Cli/Commands), mapowanie w `GrpcAgentClient.GetStatusAsync`, wyjście `CliApp.Status`. STRIDE: Information disclosure — wyjście zawiera tylko 4 liczby (active/unacknowledged/critical/silenced), bez nazw zadań, celów, ścieżek ani alert_id (NFR-11, SEC-9.6); mapowanie nie kopiuje pola Subject do wyniku (przekazywane do rekordu pośredniego, nie drukowane). Tampering/Elevation — brak nowych RPC, brak nowych uprawnień (GetStatus bez zmian). DoS — liczenie O(n) po liście już zwróconej przez Agenta. Lista kontrolna: brak SQL, brak Process.Start, brak sekretów, brak krypto. Znaleziska: brak (Wysokie 0, Średnie 0, Niskie 0). Audyt zależności: 0 podatnych (ci.sh, etap audyt); brak nowych pakietów.

## 2026-10-18, it. 133 (rola 13 prowadzi, rola 11 recenzuje) — wybór czasu wyciszenia alertu (SCR-01)
Zakres: `AlertModel.cs` (SilenceOption, SilenceOptions, SelectedSilence), `DashboardViewModel.SilenceAlertAsync`, widok (ComboBox), zasoby PL/EN. STRIDE: Tampering/Elevation — czasy to stałe z whitelisty 60/240/1440/10080 min (zakres RPC 1…10080 egzekwowany też w Agencie, CatalogAgentBackend); alerty bezpieczeństwa bez opcji 7 dni (Agent skraca do 24 h — UI nie obiecuje więcej); rola Administrator nadal wymagana (CanSilence), egzekucja i audyt w Agencie. Information disclosure — etykiety to czas, bez danych klienta; komunikat wyniku zawiera tylko etykietę czasu. Znaleziska: brak Wysokich/Średnich. Niskie: step-up (Qb82) nadal otwarty.

## 2026-10-18, it. 134 (rola 14 prowadzi, rola 11 recenzuje) — WatchRun (strumień postępu uruchomienia)
Zakres: agent.proto (WatchRun/RunProgress), `AgentCallInterceptor` (ServerStreamingServerHandler, wspólna bramka AuthorizeAsync, odrzucenie strumieni klienta/dwukierunkowych), `MethodPolicy`, `CatalogStatusReader.ReadRun`, `CatalogAgentBackend.WatchRunAsync`, `GrpcAgentClient.WatchRunAsync`, CLI `kobian watch`, KB-0107. STRIDE: **Elevation of privilege — znalezisko Wysokie PRZED wdrożeniem (naprawione w tej samej iteracji):** interceptor obsługiwał wyłącznie metody unarne, więc nowy RPC strumieniowy ominąłby sprawdzanie ról i audyt odmów; dodano ServerStreamingServerHandler z tą samą bramką, a test „Streaming_method_is_denied_without_role…” przechodzi i FAILUJE po usunięciu bramki (mutacja sprawdzona), test architektoniczny: żadna metoda strumieniowa nie może być modyfikująca. Tampering/Injection — identyfikator biegu: długość ≤ 64 i znaki [0-9A-Za-z_-], zapytanie parametryzowane; test z `x'; DROP TABLE…`. Information disclosure — strumień zawiera te same pola co ListRuns (bez ścieżek, bez sekretów); CLI drukuje stan, liczniki i kod KB. DoS — odczyt jednego wiersza/s na obserwatora, limit 12 h na strumień, anulowanie przez klienta kończy pętlę; brak limitu liczby obserwatorów (lokalny uwierzytelniony pipe, Qb107). Repudiation — odmowy strumienia trafiają do audytu jak dla unarnych; udane odczyty niemodyfikujące nie są audytowane (jak ListRuns). Lista kontrolna: brak Process.Start, brak sekretów w logach, parametryzowany SQL, brak zakazanych API. Znaleziska końcowe: Wysokie 0 (jedno naprawione), Średnie 0, Niskie 1 (brak limitu liczby obserwatorów — Qb107).

## 2026-10-18, it. 135 (przegląd rundy 9; rola 11 prowadzi) — RD-76 + audyt rundy
Zakres: `RestoreExecutor.FileAsync` (usuwanie `.kobian-partial` przy wznowieniu), test `Resume_keeps_foreign_oversized_partial_and_removes_own_small_partial`; przegląd całościowy rundy 9 (audyt zależności, skan sekretów, zakazane API). STRIDE: Tampering/Denial — podstawiony przez osobę trzecią plik `.kobian-partial` (dowiązanie lub większy niż rozmiar pliku z kopii) nie jest już kasowany: wpis jest pomijany z KB RestoreTargetExists, brak utraty cudzych danych. Wynik: Wysokie — 0; Średnie — 0; Niskie — otwarte: mały podstawiony plik zwykły nadal może zostać usunięty (pełna tożsamość pliku roboczego → RD-78). Audyt NuGet: 0 podatnych (23 projekty). Skan sekretów: tylko wektory testowe skanera. Zakazane API: brak.

## 2026-10-18, it. 136 (rola 1 prowadzi, rola 11 recenzuje) — widok postępu SCR-07 (RD-79)
Zakres: `Kobian.UI/Progress/*`, `GrpcDashboardSource.WatchAsync/MapProgress`, teksty PL/EN, `RunProgressTests`. STRIDE: Spoofing/Elevation — adapter używa tego samego kanału i nagłówka rewizji co reszta UI, autoryzację (rola Reader) egzekwuje bramka Agenta z it. 134, UI nie decyduje o uprawnieniach; odmowa → KB-7011 bez ponawiania (brak pętli odpytań na odmowie). Denial of Service — ponawianie ograniczone (5 prób, odstępy 1…15 s, licznik zerowany tylko po realnej wiadomości), „przestań obserwować” zamyka strumień; jeden strumień = jedno zapytanie/s po stronie Agenta (ADR-0042). Information disclosure — widok pokazuje wyłącznie stan, nazwę zadania, liczniki i komunikat KB (NFR-11); brak ścieżek, sekretów ani tekstów wyjątków (komunikaty wyjątków transportu nie trafiają do UI). Tampering — identyfikator biegu przekazywany do RPC bez składania poleceń/zapytań, walidacja po stronie Agenta (KB-0107). Lista kontrolna: SQL — brak; Process.Start — brak; krypto — brak; sekrety w logach — brak; zakazane API — brak. Wynik: Wysokie — 0; Średnie — 0; Niskie — 1 (otwarte): strumień zakończony limitem 12 h jest ponawiany jak zerwanie — przy wielu otwartych widokach to wielokrotne połączenia/s po stronie Agenta (limit obserwatorów, Qb107).

## 2026-10-18, it. 137 (rola 2 prowadzi, rola 11 recenzuje) — Restore w tle (RD-81)
Zakres: agent.proto (`RestoreRequest.detach`, `RunOutcome.run_id`), `AgentGrpcService.Restore`, `CatalogAgentBackend.RestoreDetachedAsync`, `CatalogRestoreService.StartRestoreAsync/PrepareAsync/RunPreparedAsync`, testy `CatalogRestoreServiceTests`. STRIDE: Elevation — ta sama bramka roli (Operator) i `RestoreTargetGate` co Restore synchroniczny (DenyTarget przed gałęzią detach; test istniejący na bramkę nie zmieniony); Tampering — walidacja celu i wybór snapshotu przed utworzeniem wiersza (błąd = brak wiersza, brak zadania w tle); Repudiation — wiersz run_runs + run_transitions + dziennik wznowień (D8.8) jak wcześniej; Info disclosure — run_id to losowe 16 B, nie zawiera danych klienta; Denial of Service — **znalezisko Średnie**: brak limitu liczby jednoczesnych odtworzeń w tle (Operator może uruchomić wiele; rolę mają tylko zaufani lokalni użytkownicy, ale limit należy dodać) → RD-82; detach + filtr + ścieżki dokładne naraz = KB-0104 JobDefinitionInvalid (jak synchronicznie). Zadanie w tle łapie wszystkie wyjątki (odnotowane w wierszu Failed), CancellationToken.None (rozłączenie klienta nie anuluje — zamierzone). Wysokich: 0. Skan zakazanych API: czysty (ci.sh --locked).

## It. 138 (2026-10-18) — rola 11, RD-81 cz. 2 (klient CLI/gRPC odtwarzania w tle)
Zakres: `IAgentClient.RestoreDetachedAsync` (domyślnie AgentUnavailable), `GrpcAgentClient.RestoreDetachedAsync` (detach=true, deadline 2 min), `RunOutcome.RunId`. Brak nowych powierzchni serwera; bramka roli i celu (RestoreTargetGate) działa jak dla Restore (test integracyjny przez pipe).
Znaleziska: brak Wysokich/Średnich. Niskie: brak limitu równoległych odtworzeń w tle po stronie serwera (RD-82, bez zmian). STRIDE: klient nie niesie sekretów; run_id to identyfikator, nie poświadczenie. Status: OK.
