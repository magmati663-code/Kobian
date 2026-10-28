# Notatki — it. 120: przegląd rundy 8 (rola 11 + rola 13) — kopia `/tmp/kb/w-review`

## Co zrobiono
- **ZADANIE A (rola 11, SEC-3.3):** `Round8FuzzTests` — fuzzing mutacyjny elementów rundy 8 (bez nowych zależności, ziarna stałe 1201–1208, klasa ok. 3 s); 6 znalezisk Niskich naprawionych z testami regresji; skan zakazanych API; skaner sekretów (NFR-11) zielony; audyt pakietów (0 podatnych, 0 nieaktualnych); STRIDE nowych elementów rundy 8.
- **ZADANIE B (RD-75, rola 13; Średnie z it. 119):** budżet pamięci buforów packów w `BackupPipeline` + pula buforów o stałej pojemności (bez nowego `MemoryStream` na każdy pack, bez podwajania tablicy po przekroczeniu celu); Bench `--e2e` przed/po (liczby zmierzone niżej).
- Uwaga z it. 119: numer RD w `rd-kandydaci.md` dla budżetu buforów to duplikat RD-72 — właściwy **RD-75** (w kodzie i testach użyto RD-75; `budowa/` nie edytowano — do poprawy przez koordynatora).

## Zmienione / nowe pliki
- NOWY `tests/Kobian.Tests.Unit/Security/Round8FuzzTests.cs`
- `tests/Kobian.Tests.Unit/Engine/ParallelUploadTests.cs` (+4 testy RD-75, parametr budżetu w pomocniczym `Pipeline`)
- `src/Kobian.Engine/Backup/BackupPipeline.cs` (RD-75: `DefaultPackBufferBudget`, `PackBufferBudget`, `PackBufferCapacity()`, `PeakPackBuffers`, `PackBuffersCreated`, pula buforów w `RunState`, limit w `Limit()`)
- `src/Kobian.Engine/Repo/MetadataStore.cs` (`ReadAllAsync`: rozmiar > Array.MaxLength/ujemny → KB-1303; ucięty odczyt → StorageException KB-2401)
- `src/Kobian.Engine/Repo/RepoIndexRecovery.cs` (`MaxPackSize` = 512 MiB + ramka + trailer + nagłówek/stopka + 64 KiB zamiast 2 GiB)
- `src/Kobian.Agent/Jobs/CatalogParallelismStore.cs` (`TryParse` daty, clamp limitu)
- `src/Kobian.Agent/Notifications/DailyReport.cs` (`IsReportableJobId`, filtr w `Collect`, `RunAsync` łapie też `ArgumentException`)
- `src/Kobian.Notifications/Webhooks/WebhookSignature.cs` (`WebhookUrlPolicy.IsAllowed`: `IdnHost` w try/catch; `IsPublic`: ::/96, NAT64, 6to4, Teredo)
- `tools/Kobian.Bench/Program.cs` (`--e2e [MiB] [sesje] [pack MiB] [budżet MiB]`, szczyt po kopii pełnej i `PeakPackBuffers`)
- `docs/admin/powiadomienia(.en).md` (reguły adresu webhooka), `docs/admin/odtwarzanie-i-ustawienia(.en).md` (`KOBIAN_PARALLEL_UPLOADS`: bufor ok. 20,6 MiB, budżet 192 MiB)
- Nowe pakiety: brak. Nowe kody KB: brak (użyte istniejące KB-1303 MetadataTooLarge, KB-2401 SizeMismatch, KB-6302).

## Testy (faktycznie uruchomione, SDK 10.0.402 / runtime 10.0.13, Linux, chmura 2 rdzenie)
- Build Release `-warnaserror -m:1`: `Kobian.Tests.Unit` (z zależnościami), `Kobian.Agent`, `Kobian.Bench` — 0 ostrzeżeń, 0 błędów.
- `Round8FuzzTests`: 15/15 (ok. 3 s): webhook config 3 000 przypadków (URL × plik sekretu, w tym niepoprawne UTF-8, 5 000 znaków, brak pliku), polityka adresów 20 000 IP + 20 000 losowych URL + 11 zapisów nietypowych, IDN 3 przypadki, `DailyReport.Build` 5 000 + kotwica 2 000, `TickAsync` na wrogich wierszach katalogu 120 przebiegów, AIMD 20 000 konstrukcji × ≤ 60 operacji, wiersz `repo_dest_parallelism` 400, odbudowa indeksu 25 repozytoriów × 30 wrogich packów (+ autentyczne), cel kłamiący o rozmiarze 3 przypadki, limit packa 1 — razem ok. 71 tys. przypadków.
- `ParallelUploadTests`: 7/7 (3 istniejące + 4 nowe RD-75).
- Zestaw obszaru: Round8Fuzz, ParallelUpload, ParallelUploadJob, AimdParallelism, DailyReport, WebhookDelivery, CheckRepair, SecretScan — 56/56.
- **Pełny `Kobian.Tests.Unit`: 1175/1175** (86 s). UI/Arch/E2E nie uruchamiane (nie dotykano UI ani referencji projektów; Arch bez zmian).
- Mutacje (wykryte): (1) `Collect` bez filtra identyfikatorów → `Daily_report_tick_survives_hostile_catalog_rows` czerwony (ArgumentException); (2) `Limit()` bez budżetu → 2 testy RD-75 czerwone; (3) pula wyłączona (bufor zawsze zwalniany) → 2 testy RD-75 czerwone. Każdy test regresji fuzzingu był czerwony przed poprawką (wyjątki: OverflowException, OutOfMemoryException, EndOfStreamException, FormatException, UriFormatException, asercja NAT64).

## Znaleziska (przegląd rundy 8) — wszystkie naprawione w tej iteracji, chyba że zaznaczono
| # | Waga | Element | Opis | Stan |
|---|---|---|---|---|
| F1 | Niskie | `RepoIndexRecovery` / `MetadataStore.ReadAllAsync` (check --repair, D8.10) | Rozmiar packa-sieroty z listingu celu (niezaufany): 2 GiB → `OverflowException`, ~2^31−2 → `OutOfMemoryException` (poza kontraktem — `check --repair` przerwany zamiast „unreadable”), a obiekt krótszy niż zgłoszony → `EndOfStreamException`; pod limitem 2 GiB jeden fałszywy plik wymuszał alokację do 2 GiB. | Naprawione: limit = największy możliwy pack (≈ 528 MiB), guard Array.MaxLength, ucięcie → KB-2401; testy `Index_recovery_counts_lying_object_sizes_as_unreadable` (3), `Recovery_pack_limit_covers_the_largest_pack_a_job_can_write` |
| F2 | Niskie | `CatalogParallelismStore.Create` (AIMD, D4.16) | Nieparsowalny `learned_at` (np. „2026-02-30…”) → `FormatException` przed otwarciem sesji wysyłki — każda kopia na ten cel kończyła się błędem (optymalizacja blokuje kopie). Wymaga zapisu do katalogu. | Naprawione: TryParse → start od rekomendowanego; test `Learned_parallelism_row_with_any_date_text_never_fails_the_backup` |
| F3 | Niskie | `DailyReportService` (D9.11) | `job_id` spoza formatu w `run_runs` (wiersz spoza Agenta, import) → `ArgumentException` z `Build` → `RunAsync` go nie łapał → pętla raportu ginęła po cichu do restartu usługi. | Naprawione: filtr w `Collect` + `ArgumentException` w pętli; test `Daily_report_tick_survives_hostile_catalog_rows` |
| F4 | Niskie | `WebhookUrlPolicy.IsPublic` (SSRF, SEC-9.3) | IPv6 z osadzonym IPv4 prywatnym przechodziły: NAT64 `64:ff9b::a00:1`, 6to4 `2002:a00:1::`, `::127.0.0.1`; w sieci z bramą NAT64/DNS64 rozwiązanie nazwy mogło dać adres wewnętrzny (DNS rebinding przez NAT64). | Naprawione (+ Teredo, 64:ff9b:1::/48); test `Webhook_address_policy_never_admits_internal_targets` z niezależną klasyfikacją wzorcową |
| F5 | Niskie | `WebhookUrlPolicy.IsAllowed` (KOBIAN_WEBHOOK_URL) | `Uri.IdnHost` rzuca `UriFormatException` dla hosta niepoprawnego wg IDNA (np. U+FFFF, U+FDD0, U+0378) — start usługi kończył się nieobsłużonym wyjątkiem zamiast komunikatu KB-6302 i kodu 64; to samo w konstruktorze `WebhookSender`. | Naprawione; teoria `Webhook_url_with_invalid_idn_host_is_refused_not_thrown` (3, z warunkiem wstępnym) |
| F6 | Średnie (rola 13, z it. 119) | `BackupPipeline` równoległe wysyłki | Bufory packów bez budżetu i bez ponownego użycia: nowy `MemoryStream` na pack, tablica podwajana po przekroczeniu celu (pack 16 MiB → 32 MiB), szczyt rośnie z liczbą sesji i rozmiarem packa. | Naprawione (RD-75) — patrz ZADANIE B |
| F7 | Niskie (otwarte) | `RestoreExecutor` (dziennik wznowień, it. 107) | Przy wznowieniu `X.kobian-partial` (zwykły plik) jest usuwany, jeśli dziennik oznaczył plik jako rozpoczęty — w folderze docelowym zapisywalnym przez innych użytkowników można podstawić plik o tej nazwie, który zostanie usunięty (nie nadpisany; `CreateNew` nie podąża za dowiązaniem). Skutek ograniczony do plików o nazwie `*.kobian-partial`. | Do RD: zapisywać w dzienniku identyfikator pliku roboczego (FileId/inode) i usuwać tylko zgodny |
| F8 | Niskie (otwarte) | `WebhookConfig.Load` | `webhook.secret`: brak sprawdzenia ACL pliku (opiera się na ACL katalogu sekretów D2.6); na Linuksie FIFO o tej nazwie zawiesiłoby start (`File.ReadAllText`); `Settings.Secret` nie jest zerowany po przekazaniu do `WebhookSender` (kopia żyje do końca procesu). | Do kolejki Windows/RD (ACL przy starcie jak dla innych sekretów) |
| F9 | Info | `BackupPipeline.FileAsync` | Pomiar Bench pokazuje, że po RD-75 szczyt nadal 275–811 MiB: nowy bufor 4 MiB na KAŻDY plik (`new byte[ChunkSize]`) i kopia bloku `block.ToArray()` na każdy blok to śmieci LOH zwalniane leniwie. | Kandydat RD (pula bufora bloku); nie zmieniano — poza zakresem |
| F10 | Info | `CatalogMaintenance` | Porównanie dat jako tekst: `finished_at` nieparsowalny ≤ cutoff → wcześniejsze usunięcie dziennika (bez szkody), data z przyszłości → dziennik nigdy nieusunięty (RODO, minimalizacja); uruchomienia bez żadnej daty — nigdy. | Bez zmian (dane pisze tylko Agent); odnotowane |

Brak znalezisk Wysokich.

## Skan (A.2)
- Zakazane API w `src/` (grep): `BinaryFormatter`, `ScpClient` (tylko komentarze „zakazany”), `ServerCertificateCustomValidationCallback`/`DangerousAcceptAny…` — brak; jedyny `RemoteCertificateValidationCallback` (`SqlServerCertificateFetcher`, runda 5) zwraca **false** (tylko odczyt certyfikatu); `UseShellExecute = true` — brak (WorkerLauncher: `false`, `ArgumentList`, ścieżka bezwzględna, środowisko z listy); `MD5`/`SHA1` — brak; `new Random(` w `src/` — brak; `TypeNameHandling`/`DtdProcessing.Parse` — brak. `Process.Start` jedynie w `WorkerLauncher` (zgodnie z D2.8).
- Sekrety w logach/wyjątkach (kod rundy 8): komunikaty `WebhookConfig` bez URL i treści sekretu (sprawdzane w fuzzingu — asercja, że błąd nie zawiera sekretu); audyt dead → cel `outbox:<id>`, bez URL; raport dzienny bez nazw zadań/ścieżek; `RepoDamageAssessor` próbki ścieżek tylko w lokalnym raporcie administratora (NFR-11 zachowane). `SecretScanTests` zielone.
- `dotnet list Kobian.sln package --vulnerable --include-transitive`: 23/23 projektów „no vulnerable packages”. `--outdated`: 23/23 „no updates” (CPM; nuget.org, 2026-10-17). Bez zmian wersji; `packages.lock.json` nie zmienione.

## STRIDE — nowe elementy rundy 8
| Element | S | T | R | I | D | E |
|---|---|---|---|---|---|---|
| Launcher Workera (BL-79) | podpis przed startem KB-7015, ścieżka bez dowiązań | plik Workera: kontrola podpisu + brak linku (okno TOCTOU podpis→start — Qb55) | zdarzenia startu | środowisko z listy (bez KOBIAN_*) | job object 150 MB, KILL_ON_JOB_CLOSE | ArgumentList bez powłoki, lista ról; WDAC → KB-7008 |
| Protokół ramek Workera (D2.8) | — | ścisła walidacja obu stron (fuzz it. 103/105) | — | — | ramka ≤ 1 MiB, timeout | Guid/KB walidowane przed użyciem |
| Dziennik wznowień restore_* (BL-80) | — | plan SHA-256; obcy plik nigdy nadpisany | run_runs | nazwy plików w katalogu → CatalogMaintenance 30 dni, secure_delete | partie 256 | **F7** (usunięcie podstawionego `*.kobian-partial`) |
| check --repair (BL-81, RepoDamageAssessor, RepoIndexRecovery) | trailer AEAD + commitTag z nazwy packa (pack pod cudzą nazwą odrzucony — fuzz) | append-only, nowy indeks; nic nie usuwane | wynik w katalogu + audyt RPC | próbki ścieżek tylko lokalnie | **F1** (naprawione) | tylko Administrator (KB-7011) |
| Webhook (WebhookConfig/Sender/outbox) | podpis v1 HMAC-SHA256 | ponowna kontrola adresu przy połączeniu; bez przekierowań | audyt dead KB-6301 | tryb minimalny; **F4/F5** (naprawione); F8 | 10 s/30 s, odpowiedź ≤ 64 KiB, ≤ 20 na takt | bez proxy użytkownika |
| DailyReport (D9.11) | — | kotwica audytu poza katalogiem (RD-61) | znacznik core_events | identyfikatory zamiast nazw | ≤ 64 KiB, **F3** (naprawione) | — |
| BaselineSet (BL-83) | indeksy uwierzytelnione | — | — | — | 32 B/blok, rezerwa per indeks | — |
| AIMD + CatalogParallelismStore (BL-84) | — | wpis z przyszłości/przeterminowany ignorowany | — | — | **F2** (naprawione); [1, rekomendowany] dla dowolnej sekwencji (fuzz) | — |
| CatalogMaintenance + secure_delete (RD-69/74) | — | nigdy w toku | — | RODO: usuwanie + nadpisanie stron | partie 200 | — (F10 info) |
| Równoległe wysyłki (RD-73, ADR-0038) | sesje przez governor | I-2 per pack, indeks po DrainAsync | — | — | **F6/RD-75** (naprawione) | sesje Backup (bez prawa usuwania D4.6) |

## ZADANIE B — RD-75 budżet pamięci buforów packów (rola 13)
- **Mechanizm:** `PackBufferCapacity(cel) = cel + 4 MiB (blok) + cel/32 (trailer) + 64 KiB` — bufor alokowany z góry tej pojemności (pack 16 MiB → 21 561 344 B ≈ 20,6 MiB) i używany ponownie z puli (`SetLength(0)`); bufor, który mimo to urósł (nietypowo duży trailer/drzewo 16 MiB), nie wraca do puli. Limit wysyłek w toku = max(1, min(sesje, AIMD, ⌊budżet / pojemność⌋ − 1)) — (w toku + budowany) × pojemność ≤ budżet, zawsze co najmniej jedna wysyłka (w trybie równoległym minimum 2 bufory). Ścieżka sekwencyjna: 1 bufor na całe uruchomienie.
- **PROWIZORYCZNIE Qb-NEW-pack-buffer-budget:** `BackupPipeline.DefaultPackBufferBudget = 192 MiB` (opcja `PackBufferBudget` potoku; Agent używa domyślnej). Pytanie: ile pamięci przydzielić buforom packów w budżecie NFR-05 (≤ 500 MB procesu)? Wybór: 500 − 150 (NFR-22 dedup) − ~100–150 (runtime, Kestrel, SQLite, drzewa, zstd) ≈ 192 MiB; przy packu 16 MiB nie ogranicza dzisiejszych 2–4 sesji (9 buforów), ogranicza duże packi (64 MiB → 1 wysyłka + 1 budowany). Alternatywy: budżet proporcjonalny do RAM hosta albo zmienna środowiskowa `KOBIAN_PACK_BUFFER_BUDGET_MIB`; brak budżetu (stan it. 119). Koszt zmiany: jedna stała / jedna linia w CatalogJobService. Do potwierdzenia pomiarem Private Bytes na Windows (W-48).
- **Testy (Trait RD-75, NFR-05):** `Pack_buffer_budget_caps_buffers_and_uploads_in_flight_without_changing_the_backup` (budżet 3 bufory, AIMD/sesje 3 → sonda wysyłek ≤ 2, `PeakPackBuffers` ≤ 3, ten sam `RootTreeId` co kopia sekwencyjna, odtworzenie bajt w bajt), `Budget_smaller_than_two_buffers_still_uploads_one_pack_at_a_time` (sonda = 1, ≤ 2 bufory, odtworzenie bajt w bajt), `Sequential_path_reuses_a_single_pack_buffer` (≥ 8 packów, 1 bufor utworzony), `Pack_buffer_capacity_holds_target_plus_one_block_and_is_bounded`. Istniejące `ParallelUploadTests` (3) i `ParallelUploadJobTests` zielone.
- **Pomiar (Bench `--e2e 800 <sesje> <pack MiB>`, korpus syntetyczny 1 046 plików / 800 MiB, Local; chmura: Ubuntu 24.04.5, 2 rdzenie logiczne, 8 GB RAM, .NET 10.0.13; maszyna współdzielona z innymi agentami — wyniki orientacyjne; „przed” = kod sprzed it. 120 z tym samym Benchem uzupełnionym tylko o argument rozmiaru packa i wydruk szczytu po kopii pełnej; szczyt = PeakWorkingSet64 po kopii pełnej, 2 przebiegi):**

| Konfiguracja | Przed: szczyt / przepustowość | Po: szczyt / przepustowość | Bufory naraz (po) |
|---|---|---|---|
| 3 sesje, pack 16 MiB | 630, 550 MiB / 152,3, 146,6 MiB/s | 385, 415 MiB / 173,6, 172,1 MiB/s | 4 |
| 3 sesje, pack 64 MiB | 1 319, 955, 1 271 MiB / 163,9, 151,4, 166,4 MiB/s | 585, 811 MiB / 138,2, 177,5 MiB/s | 2 (budżet: 1 w toku + budowany) |
| 1 sesja, pack 16 MiB | 366, 383 MiB / 128,5, 122,4 MiB/s | 299, 275 MiB / 143,0, 149,0 MiB/s | 1 |

  (Dla „3 sesje, 64 MiB, przed” trzy przebiegi: pierwszy z serii przerwanej limitem czasu narzędzia + dwa z pełnej serii.) Pozostały szczyt to głównie śmieci LOH z `FileAsync` (F9), nie bufory packów.

## Decyzje PROWIZORYCZNE
- `// PROWIZORYCZNIE Qb-NEW-pack-buffer-budget` — opis wyżej (w komentarzu `DefaultPackBufferBudget`).
- Limit `RepoIndexRecovery.MaxPackSize` wyprowadzony z `JobDefinition.MaxPackTarget` (512 MiB) — test pilnuje zgodności; jeśli kiedyś cel packa > 512 MiB, test wymusi podniesienie limitu.

## Testy do kolejki Windows
- W-48 (istniejące) uzupełnić: `Kobian.Bench --e2e 800 3 16` i `--e2e 800 3 64` na Windows z licznikiem Private Bytes (Menedżer zadań / `Get-Process -Name dotnet | Select PrivateMemorySize64` co 1 s) — oczekiwane: szczyt po RD-75 niższy niż przed; dla 64 MiB „bufory packów naraz (szczyt) 2”; NFR-05 ≤ 500 MB dla domyślnego packa 16 MiB z 2–4 sesjami.
- F8: ACL `webhook.secret` (tylko SYSTEM/Administratorzy) — sprawdzić `icacls <sekrety>\webhook.secret` po `--provision`.

## Niedokończone / dla koordynatora
- `runda-8.md`, rd-statusy, `rd-table`/`plan-append` „# R&D — budowa, runda 8”, korekta duplikatu RD-72 → RD-75 i nowe kandydaty RD (F7 identyfikator pliku roboczego, F9 pula bufora bloku w `FileAsync`) — w `budowa/` (nie edytowane zgodnie z BRIEF).
- Qb dla Qb-NEW-pack-buffer-budget do nadania.
- `tools/ci.sh` nie uruchamiany (koordynator po scaleniu); UI/Arch/E2E nie uruchamiane.
