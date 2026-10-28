# Kandydaci R&D (budowa)

| ID | Iteracja | Rola | Pomysł | W | Z | P | K | Waga | Status |
|---|---|---|---|---|---|---|---|---|---|
| RD-1 | 3 | 12 R&D (z 3) | Wektory formatu generowane przez niezależną implementację referencyjną (Python) publikowane ze specyfikacją Q17 | 4 | 5 | 0.9 | 1 | 40.5 | kandydat |
| RD-2 | 4 | 12 R&D (z 4) | Dostawca 'memory' (w pamięci) z wstrzykiwaniem błędów D4.8 do testów potoku bez kontenerów (S5c=nie) | 4 | 4 | 0.9 | 2 | 36.0 | kandydat |
| RD-3 | 5 | 12 R&D (z 5) | Generator syntetycznych baz Subiekt GT/Optima (schemat + losowe dane) do testów wykrywania i V4 bez baz demo producenta | 4 | 4 | 0.8 | 3 | 16.0 | kandydat |
| RD-4 | 6 | 12 R&D (z 6) | Sprawdzanie hasła z k-anonimowością (zakres SHA-1 HIBP) — offline niemożliwe; zamiast tego filtr Bloom 1 mln haseł w zasobach (~1,2 MB) | 3 | 4 | 0.7 | 3 | 12.2 | kandydat |
| RD-5 | 7 | 12 R&D (z 7) | Symulacja deterministyczna schedulera (wirtualny TimeProvider + losowe zdarzenia zasilania/zegara) jako test właściwości E1–E10 | 4 | 5 | 0.9 | 2 | 40.5 | kandydat |
| RD-6 | 8 | 12 R&D (z 8) | Korpus złośliwych nazw (CON, ADS, indeksy górne, długie, case-collision) jako wspólny generator testów MapPath i fuzzingu drzew | 3 | 5 | 0.9 | 1 | 36.0 | kandydat |
| RD-7 | 9 | 12 R&D (z 9) | Publikacja kotwic audytu jako podpisany plik w repo + opcjonalnie w e-mailu PGP/S-MIME (weryfikacja niezależna od hosta) | 3 | 4 | 0.7 | 3 | 12.2 | kandydat |
| RD-8 | 10 | 12 R&D (z 10) | Mutacyjne testy (Stryker.NET — do audytu) na Kobian.Format i Kobian.Crypto jako bramka nocna | 3 | 4 | 0.7 | 2 | 24.5 | kandydat |
| RD-9 | 11 | 12 R&D (z 11) | Fuzzing sterowany pokryciem (SharpFuzz + libFuzzer) dla Kobian.Format w nocnym CI — po audycie licencji i utrzymania | 4 | 4 | 0.7 | 3 | 14.0 | kandydat |
| RD-10 | 12 | 12 R&D (z 12) | Wspólny wirtualny TimeProvider + MemoryStore jako biblioteka testowa dla symulacji schedulera, alertów (A03 RPO) i retencji (retentionLock) | 3 | 4 | 0.9 | 1 | 31.5 | kandydat |
| RD-11 | 13 | 13 Wydajność (z 12) | HMAC chunkId liczony równolegle z kompresją (potok 2-etapowy) — wąskie gardło wg pomiaru it. 13 | 3 | 3 | 0.8 | 2 | 24.0 | kandydat |
| RD-18 | 23 | 8 Odtwarzanie i DR | Test właściwościowy wznowienia restore: przerwanie po każdym packu + wznowienie daje bity identyczne z restore bez przerwy (V1/V2) | 4 | 5 | 0.8 | 3 | 18.0 | kandydat |
| RD-19 | 27 | 12 R&D (z 3,11) | Harness chaos D3.14 jako wspólna specyfikacja: model Pythona (tools/model-d3-14-chaos.py) generuje punkty zabicia i oczekiwane wyniki, a test C# SP-18 odtwarza te same przypadki na rzeczywistym potoku | 4 | 5 | 0.8 | 2 | 36.0 | kandydat |
| RD-20 | 28 | 13 Wydajność i niezawodność | Filtr Bloom D3.15: dobór k i bitów/wpis na podstawie FP zmierzonego modelem (0,8% przy 10 b/wpis) - rozważyć 9 b/wpis, jeśli pamięć wymaga | 2 | 3 | 0.7 | 1 | 17.5 | kandydat |
| RD-21 | 30 | 11 Bezpieczeństwo (z 12) | Test SP-18 na rzeczywistym potoku in-proc: dekorator magazynu z wstrzykiwaniem awarii (ENOSPC, zerwanie, zabicie w N-tym zapisie) zamiast kontenerów | 4 | 5 | 0.9 | 2 | 40.5 | kandydat |

Zmiany statusu kandydatów: `rd-statusy.md` (osobny plik — wiersze statusu w tym pliku psuły `rd-table`).
| RD-16 | 34 | 4 Transport (z 11) | Rekoncyliacja D8.2 wykrywająca nieczytelne częściowe obiekty snapshots/ po awarii (DirectFinal, ciche obcięcie) i raportująca je bez usuwania w sesji Backup | 3 | 5 | 0.9 | 1 | 36.0 | kandydat |
| RD-17 | 41 | 11 | Interaktywny prompt hasla bez echa w kobian-restore/CLI (zamiast pliku hasla) + zeroing przez SecureString-free bufor znakow | 3 | 3 | 0.8 | 2 | 24.0 | kandydat |
| RD-18 | 42 | 12 R&D | Etap E2E na korpusie D10.9 (CorpusGen) + przez kobian-restore w ci.sh | 4 | 5 | 0.9 | 2 | 40.5 | kandydat |
| RD-22 | 45 | 11 (z 3) | Limit RAM dedup w BackupPipeline: filtr Bloom + zapytanie do katalogu (model D3.15 z it. 28 przeniesiony na kod; test pamieci na korpusie syntetycznym) | 4 | 5 | 0.8 | 3 | 18.0 | kandydat |
| RD-23 | 45 | 12 (z 3) | Rotacja MK (re-encrypt repo) jako osobne polecenie - zamyka znalezisko Srednie z it. 44 (uniewaznienie klucza nie zmienia MK) | 4 | 4 | 0.6 | 5 | 12.0 | kandydat |
| RD-24 | 45 | 11 | Fuzzing sterowany pokryciem (SharpFuzz) dla IndexCodec/SnapshotCodec/TreeCodec po audycie narzedzia (20k mutacji z it. 45 to dolna granica) | 3 | 4 | 0.7 | 3 | 12.2 | kandydat |
| RD-25 | 47 | 2 Architekt systemowy | Anulowanie uruchomienia w tle (BackgroundTaskRegistry + CancelRun w kobian.v1) zamiast fire-and-forget wait=false | 3 | 4 | 0.8 | 3 | 14.0 | kandydat |
| RD-26 | 47 | 2 Architekt systemowy | Wyzwalacze czasowe: SchedulerCore nad sched_next_fire_at wołający CatalogJobService (cause=schedule, misfire) | 5 | 5 | 0.8 | 4 | 20.0 | kandydat |
| RD-27 | 54 | 12 R&D | RPC SetSchedule + kobian schedule set/list oraz przeladowanie wyzwalaczy bez restartu Agenta (hot-reload po sched_triggers) | 4 | 5 | 0.9 | 3 | 20.2 | kandydat |
| RD-28 | 55 | 13 | check: pamięć podręczna wyników poddrzew (treeId → zbiór packów) współdzielona między snapshotami — V2 w czasie proporcjonalnym do zmian, nie do liczby snapshotów | 3 | 4 | 0.8 | 2 | 28.0 | kandydat |
| RD-29 | 58 | 12,6,10 | Regresja wizualna UI: porównanie zrzutów RenderTests z wzorcami (różnica pikseli z progiem, AC-UX.3/4) w ci.sh | 4 | 4 | 0.8 | 2 | 32.0 | kandydat |
| RD-30 | 59 | 12 | Kreator SCR-03: walidacja S1-S5 z JSON Schema definicji zadania 7.8 (jedno zrodlo regul UI/CLI/import, D6.7) zamiast recznych regul w adapterze | 3 | 4 | 0.7 | 3 | 12.2 | kandydat |
| RD-31 | 60 | 11 (z 12) | Fuzzing warstwy gRPC Agenta: zmutowane wiadomosci protobuf dla kazdej metody agent.proto przez AgentHost in-proc (limity rozmiaru, kody KB, brak wyjatkow runtime) | 4 | 4 | 0.8 | 2 | 32.0 | kandydat |
| RD-32 | 60 | 11 (z 8) | Wspolna regula bezpiecznej nazwy (IsSafeName D8.9) dla sekretow, nazw zadan i MapPath z korpusem zlosliwych nazw RD-6 jako jednym zrodlem testow | 3 | 5 | 0.9 | 1 | 36.0 | kandydat |
| RD-33 | 61 | 11 | Indeks run_runs po czasie zakonczenia (COALESCE finished/started/queued) dla ListRuns i historii SCR-10 przy duzej liczbie uruchomien | 2 | 3 | 0.8 | 1 | 20.0 | kandydat |
| RD-34 | 62 | 11 | Ograniczenie ProbeDestination (limit czestotliwosci na wywolujacego, odrzucanie adresow petli zwrotnej i link-local) przeciw SSRF z uslugi | 3 | 4 | 0.8 | 1 | 28.0 | kandydat |
| RD-35 | 67 | 12,3,7 | Opcja zadania fullEvery (kopia pełna co N przyrostów) - prune z żywością przez indeksy (Qb26 A) odzyskuje wtedy miejsce bez przepisywania indeksów | 4 | 5 | 0.9 | 2 | 40.5 | kandydat |
| RD-36 | 68 | 8 | Plik zwolnienia blokady kopii (release marker z MAC) - prune nie czeka 30 min po kazdej kopii | 3 | 4 | 0.8 | 2 | 28.0 | kandydat |
| RD-37 | 69 | 9,11 | Wykrywanie skoku zegara przy wylaczonym Agencie: przy starcie porownanie zegara z ostatnim zapisanym czasem sciennym Agenta i czasem modyfikacji kobian.repo/locks na celu (SEC-17.4 offline) | 4 | 4 | 0.8 | 2 | 32.0 | kandydat |
| RD-38 | 71 | 11 | Etap ci.sh: zapytanie OSV o wersje runtime/SDK z global.json i shared framework (nie tylko pakiety NuGet) - wykrywa biuletyny .NET jak 10.0.13 bez recznego sprawdzania | 4 | 5 | 0.9 | 1 | 40.5 | kandydat |
| RD-39 | 72 | 12,11 | Lista dozwolonych serwerow SQL dla uwierzytelnienia Windows w discovery i zadaniach (ochrona przed przekazaniem NTLM konta uslugi do obcego hosta przez Operatora) | 4 | 4 | 0.8 | 2 | 32.0 | kandydat |
| RD-40 | 72 | 12,13 | Rownolegle D-03 w discovery MSSQL (limit 4 polaczen) dla serwerow z setkami baz - czas kroku S1-db | 2 | 2 | 0.7 | 1 | 14.0 | kandydat |
| RD-41 | 73 | 13,6 | Benchmark kroku S1-db w Kobian.Bench: discovery na SQL zastepczym z 500 bazami i opoznieniem D-03 (czas do pierwszej listy, pamiec UI przy 500 wierszach) - dane do decyzji RD-40 i wirtualizacji listy | 3 | 3 | 0.8 | 1 | 24.0 | kandydat |
| RD-42 | 74 | 14,11 | Wpis lancucha audytu D9.9 dla zatwierdzenia certyfikatu SQL i klucza hosta SFTP (kto, kiedy, odcisk, nazwa pliku) - zmiana zaufania Agenta bez sladu to luka Repudiation z it. 74 | 3 | 5 | 0.9 | 1 | 36.0 | kandydat |
| RD-43 | 74 | 9,14 | Alert zblizajacego sie wygasniecia przypietego certyfikatu SQL (NotAfter z pliku .cer w stan/certs, regula-dane jak A01-A17) - kopia nie powinna nagle stanac na KB-3004 po odnowieniu | 3 | 4 | 0.8 | 1 | 28.0 | kandydat |
| RD-44 | 75 | 11,12 | Fuzzing gRPC Agenta na poziomie wiadomosci protobuf (mutacje bajtow dla kazdej metody przez pipe, limit rozmiaru i czas odpowiedzi) - kontynuacja RD-31 po rozszerzeniu kontraktu w rundzie 5 | 3 | 4 | 0.8 | 2 | 28.0 | kandydat |
| RD-45 | 75 | 11,4 | Wspolny czytnik ramek sieciowych z limitami (liczba ramek, bajty, ramki puste) dla TDS, SSRP i przyszlych protokolow FTP - jedna implementacja limitow SEC-3.3 zamiast lokalnych petli | 2 | 4 | 0.7 | 2 | 21.0 | kandydat |
| RD-46 | 76 | 1,8 | Wyszukiwanie plikow w kopii (SearchFiles z R2.6.1) przez indeks nazw w katalogu SQLite budowany przy backupie - SCR-06 bez chodzenia po drzewach dla duzych kopii | 4 | 4 | 0.7 | 4 | 14.0 | kandydat |
| RD-47 | 86 | 11,2 | Wlasny marshaller gRPC dla Agenta: niepoprawny protobuf jako InvalidArgument z kodem KB w trailerze zamiast ogolnego Unknown (diagnostyka klientow, spojnosc z D2.9) | 2 | 3 | 0.8 | 1 | 20.0 | kandydat |
| RD-48 | 88 | 13,3 | Wysylka packu bez kopiowania bufora (MemoryStream.GetBuffer/ReadOnlyMemory w VerifiedUploader zamiast ToArray) - mniejszy szczyt pamieci przy packach 64 MiB | 2 | 3 | 0.9 | 1 | 22.5 | kandydat |
| RD-49 | 89 | 13,14 | Bench --e2e raportuje GC.GetTotalAllocatedBytes i alokacje na MiB jako strażnik Perf (weryfikacja RD-48 i przyszłych zmian potoku) | 3 | 3 | 0.9 | 1 | 27.0 | kandydat |
| RD-50 | 90 | 11,6 | Wspolny bezpieczny czytnik plikow konfiguracyjnych JSON (limit rozmiaru, glebokosci, lapanie bledow dekodowania UTF-8 w GetString) dla ui-settings, definicji zadan i plikow stanu - jedno miejsce zamiast lokalnych filtrow wyjatkow | 3 | 4 | 0.9 | 1 | 31.5 | kandydat |
| RD-51 | 90 | 11,9 | VerifyAudit sprawdza schemat audit_log (STRICT, CHECK, wyzwalacze append-only z sqlite_schema) i zwraca osobny wynik schemat zmieniony zamiast wyjatku przy odbudowanej tabeli | 3 | 5 | 0.9 | 1 | 36.0 | kandydat |
| RD-52 | 91 | 1,10 | Konfiguracja Agenta w pliku agent.json w ProgramData Kobian (ACL Administrators i konto uslugi) zamiast zmiennych srodowiskowych uslugi - MSI zapisuje plik, AgentPaths czyta z limitami (RD-50) | 3 | 4 | 0.8 | 2 | 28.0 | kandydat |
| RD-53 | 92 | 4 | Wykrywanie dialektu i podpisywania SMB po połączeniu (GetFileInformationByHandleEx FileRemoteProtocolInfo) → KB-2107/KB-2111 bez czekania na pełny SP-22 | 4 | 4 | 0.7 | 3 | 14.0 | kandydat |
| RD-54 | 93 | 4 | kobian probe (CLI) z wyswietleniem odcisku klucza hosta SFTP do potwierdzenia i wklejenia w --host-key (bez TOFU) | 3 | 4 | 0.9 | 1 | 31.5 | kandydat |
| RD-55 | 94 | 4 | Konto blokady smb z magazynu sekretów (nazwy kont backup i maintenance, bufor w pamięci) zamiast secretRef — dokładna zgodność D7.4 bez KB-2101 w fazie retencji | 3 | 4 | 0.8 | 1.5 | 28.0 | kandydat |
| RD-56 | 95 | 11 | Limit odmów API per SID (A15: 3 odmowy w 10 min → zdarzenie i opóźnienie) w AgentCallInterceptor z TokenCallerResolver | 3 | 4 | 0.8 | 1.5 | 28.0 | kandydat |
| RD-57 | 95 | 6,2 | RPC GetCallerInfo (rola i SID wywołującego) dla UI - wyszarzanie akcji i komunikat Uruchom jako administrator (D2.9, 6.3) | 3 | 4 | 0.9 | 1 | 31.5 | kandydat |
| RD-58 | 96 | 6,11 | Ekran KB-9001 rozpoznaje odmowe ACL pipe (UnauthorizedAccess) i przy tokenie ograniczonym UAC podpowiada Uruchom jako administrator zamiast usluga niedostepna | 3 | 3 | 0.8 | 0.5 | 24.0 | kandydat |
| RD-59 | 97 | 4,11 | Breaker otwarc sesji kluczowany serwerem i kontem (nie celem) - wspolny limit 3 nieudanych logowan na godzine dla wielu celow na jednym NAS (SEC-24.4) | 3 | 4 | 0.8 | 1 | 28.0 | kandydat |
| RD-60 | 98 | 8,6 | Postep odtwarzania w SCR-07 i CLI (WatchRun) z mianownikiem z PlanRestore (pliki, bajty, packi) | 3 | 4 | 0.8 | 2 | 28.0 | kandydat |
| RD-61 | 99 | 9,11 | Kotwica audytu w raporcie dziennym i zdarzeniu audit.anchor (webhook) po BL-45 oraz typ metadanych anchor w journal/ (v1.1) | 4 | 5 | 0.8 | 4 | 18.0 | kandydat |
| RD-62 | 100 | 10,12 | Generator instalatora z AgentInstallPlan: wspolne zrodlo dla Package.wxs i trybu --provision oraz test instalacji MSI na runnerze Windows (gdy S4 sie zmieni) | 3 | 4 | 0.7 | 3 | 12.2 | kandydat |
| RD-63 | 101 | 11,8 | Wlasciciel plikow odtworzonych przez Operatora ustawiany na konto klienta (SetOwner z SeRestorePrivilege) zamiast konta uslugi - zgodnosc z quota i audytem NTFS (SEC-6.3) | 3 | 3 | 0.7 | 2 | 21.0 | kandydat |
| RD-64 | 102 | 12,13 | Strumieniowe ladowanie indeksow bazowych do DedupTable bez trzymania IndexObject w pamieci (po it. 102 dominuja w szczycie przyrostu) | 4 | 4 | 0.8 | 3 | 16.0 | kandydat |
| RD-65 | 103 | 13,11 | Launcher Workera: job object (KILL_ON_JOB_CLOSE, limit 150 MB), brak dziedziczenia uchwytow poza stdin/stdout, weryfikacja Authenticode przed startem, preflight ASR/WDAC KB-7008 (D2.8, SEC-2.7, NFR-19) | 4 | 5 | 0.8 | 3 | 18.0 | kandydat |
| RD-66 | 105 | 14,10 | Kampania testow Windows: skrypt tools/windows/run-queue.ps1 uruchamia W-33...W-44 i zapisuje wyniki do budowa/sync dla petli (domkniecie S4=C) | 4 | 5 | 0.7 | 2 | 31.5 | kandydat |
| RD-67 | 106 | 11,10 | Zgodnosc wydawcy podpisu Kobian.Worker.exe z podpisem uslugi (ten sam certyfikat lub lancuch) oprocz WinVerifyTrust (SEC-2.7) | 3 | 4 | 0.8 | 1 | 28.0 | kandydat |
| RD-68 | 107 | 8,6 | PlanRestore swiadomy dziennika wznowien: pliki z przerwanej proby tego planu pokazywane jako do pominiecia, nie konflikty (D8.8) | 3 | 4 | 0.9 | 1 | 31.5 | kandydat |
| RD-69 | 107 | 14,11 | Sprzatanie dziennika restore_*: sesje Done/Superseded starsze niz 30 dni usuwane (RODO - nazwy plikow) migracja i zadanie utrzymaniowe | 3 | 4 | 0.9 | 1 | 31.5 | kandydat |
| RD-70 | 109 | 6,8 | Znacznik uszkodzonej kopii w SCR-10/SCR-06 (ListSnapshots.damage z check --repair) i blokada wyboru kopii nieodtwarzalnej do odtworzenia | 3 | 4 | 0.9 | 1 | 31.5 | kandydat |
| RD-71 | 111 | 6,11 | Webhook: wielu odbiorcow i filtr zdarzen (Qb58 a/b) oraz sekret w DPAPI zamiast pliku | 3 | 3 | 0.9 | 2 | 27.0 | kandydat |
| RD-72 | 112 | 7,6 | Raport dzienny w UI (SCR-11) i przycisk Skopiuj kotwice oraz test odbiorcy webhooka (skrypt weryfikacji podpisu dla administratora) | 3 | 3 | 0.9 | 1 | 27.0 | kandydat |
| RD-73 | 114 | 9,3 | Rownolegle wysylki packow w potoku przez AdaptiveParallelGate (AIMD D4.16) z zachowaniem kolejnosci I-2 i weryfikacji V1 | 4 | 4 | 0.8 | 3 | 16.0 | kandydat |
| RD-74 | 116 | 11,13 | PRAGMA secure_delete w katalogu Agenta (usuniete wiersze z nazwami plikow nie zostaja w wolnych stronach) - pomiar kosztu zapisu | 3 | 4 | 0.9 | 1 | 31.5 | kandydat |
| RD-75 | 119 | 13,3 | Budzet pamieci buforow packow w toku (pula MemoryStream o stalej pojemnosci = rozmiar packa, limit N x pack w NFR-05) - pomiar Bench --e2e przed/po | 4 | 4 | 0.8 | 2 | 32.0 | kandydat |
| RD-76 | 120 | 11,8 | Wznowienie odtwarzania: podstawiony zwykly plik .kobian-partial nie jest usuwany - weryfikacja identyfikatora pliku roboczego przed usunieciem (F7 it. 120) | 2 | 4 | 0.9 | 1 | 27.0 | kandydat |
| RD-77 | 120 | 13,3 | Mniej smieci LOH w BackupPipeline.FileAsync: wspolny bufor odczytu 4 MiB z puli i bloki bez kopii ToArray (F9 it. 120) - pomiar szczytu Bench | 3 | 3 | 0.8 | 2 | 24.0 | kandydat |
| RD-78 | 135 | 11,8 | Dziennik wznowien odtwarzania zapisuje tozsamosc pliku roboczego (rozmiar+mtime+ID pliku) i usuwa .kobian-partial tylko przy zgodnosci - domyka RD-76 do pelnej weryfikacji (dzis: tylko rozmiar i brak dowiazania) | 3 | 4 | 0.9 | 2 | 31.5 | kandydat |
| RD-79 | 135 | 6,2 | Widok postepu SCR-07 w UI na IAgentClient.WatchRunAsync (port IRunProgressSource + adapter gRPC + pasek postepu, wznowienie po zerwaniu strumienia) | 4 | 4 | 0.9 | 3 | 18.0 | kandydat |
| RD-80 | 135 | 11,9 | Step-up (ponowne uwierzytelnienie) dla wyciszania alertow i akcji destrukcyjnych (Qb82/Qb106) - ostatni otwarty punkt BL-45 | 3 | 5 | 0.8 | 3 | 16.0 | kandydat |
| RD-81 | 136 | 6,2 | Restore (RPC) zwraca run_id od razu (tryb bez czekania) i SCR-07 podpina RunProgressView na WatchRun - dzis Restore blokuje do konca i nie ma jak pokazac postepu | 4 | 4 | 0.8 | 3 | 16.0 | kandydat |
