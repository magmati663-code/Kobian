# API Agenta kobian.v1 — odczyt stanu i historii (BL-60, it. 61)

Kontrakt: `src/Kobian.Api.Contracts/Protos/kobian/v1/agent.proto`. Pola tylko dodawane (D2.16); wartość domyślna nowego pola = zachowanie starszego Agenta.

## GetStatus (rola Reader)
| Pole `JobStatus` | Znaczenie | Źródło w katalogu |
|---|---|---|
| `disabled` | zadanie wyłączone (pokazywane, ale nie wpływa na stan ogólny) | `job_jobs.enabled` |
| `next_run_unix_seconds` | najbliższe wystąpienie włączonego wyzwalacza cron (brak = zadanie ręczne / harmonogram wyłączony / zadanie wyłączone) | `sched_triggers` (kind `cron`, enabled) + strefa zadania |
| `interval_seconds` | oczekiwany cykl = odstęp dwóch najbliższych wystąpień (kontrola zaległości 6.3) | j.w. |
| `last_success_unix_seconds`, `last_success_verify_level` | ostatnia kopia niezapomniana i nieoznaczona `suspect`; poziom 1/2/3 | `snap_snapshots` |
| `repository_suspect` | najnowsza kopia oznaczona jako podejrzana (V2/check) → stan ogólny Failed | `snap_snapshots.suspect` |
| `destination_scheme` | tylko nazwa schematu (`local`, `sftp`) — nigdy ścieżka ani host (NFR-11) | definicja v1 zadania |
| `active_state` (it. 97, BL-76) | uruchomienie w toku: `Running` (także `Paused`) albo `Queued` (np. ręczne czekające na zasób); pusty = nic nie trwa / stary Agent. Nie zmienia `last_result` ani stanu ogólnego | `run_runs.state` w (`Running`,`Paused`,`Queued`) |
| `last_verify_unix_seconds`, `last_verify_code`, `next_verify_unix_seconds` (BL-26) | ostatni **ukończony** cykl V3 z harmonogramu i jego kod (KB-15xx = uszkodzenie, KB-1504 = sieroty, brak = OK); termin następnego wg polityki `verify` (w przeszłości = zaległy; brak = wyłączona/brak kopii). Nie zmienia stanu ogólnego (wynik V3 jest i tak ostatnim uruchomieniem zadania) | `sched_verify_jobs`, `snap_snapshots`, definicja zadania |

`GetStatusResponse.caller_role` — rola wywołującego z tego samego resolvera co autoryzacja (D2.9); `UNSPECIFIED` = starszy Agent (UI przyjmuje wtedy PROWIZORYCZNIE Operator, Qb18). Uszkodzona definicja lub specyfikacja wyzwalacza nie przerywa odpowiedzi — pole zostaje puste.

Niezaimplementowane (BL-60 cz. 2): wolne miejsce na celu (`ProbeDestination`), `UpsertJob validate_only`.

## ListRuns (rola Reader)
`ListRunsRequest { job?, limit? }` — limit 1–1000 (domyślnie 200, wartości spoza zakresu przycinane), najnowsze pierwsze (czas zakończenia, startu, kolejki). `RunInfo`: `run_id`, `job`, `kind` (`backup`/`restore`/`check` z pierwszego przejścia `run_transitions.reason`), `cause`, `state`, `result`, czasy, `code` (KB), `snapshot_id`, `files`, `bytes_uploaded`. Bez ścieżek i treści.

CLI: `kobian runs [--job <nazwa>] [--limit <1-1000>] [--json]` (schemat `kobian.cli.runs/1`, kod wyjścia 0; zły `--limit` → 64; brak usługi → 3).

## ProbeDestination (rola Operator, niemodyfikujące) — it. 62
`ProbeDestinationArgs { DestinationSpec destination }`; `DestinationSpec { scheme, root, host?, port?, secret_ref?, host_key? }` — bez sekretów (AC-01.3).
Agent otwiera sesję celu z poświadczeniem **Backup** (bez prawa usuwania, D4.6) i zwraca: `status` (OK / UNKNOWN_IDENTITY / FAILED), `code` (KB), `identity_kind`/`identity_fingerprint` (publiczny odcisk), `free_bytes`, `total_bytes` (tylko local), `repository_exists` (`kobian.repo` na celu → init odmówi KB-1003). Niczego nie zapisuje.

SFTP bez `host_key`: opcja dostawcy `identity_probe=1` — klucz hosta **nigdy** nie jest zaufany, uwierzytelnienie „none” bez poświadczeń, połączenie kończy się na wymianie kluczy; wynik UNKNOWN_IDENTITY z odciskiem do zatwierdzenia przez użytkownika (D4.11, bez TOFU w tle). Z `host_key` niezgodnym → FAILED KB-2104 + odcisk (zmiana klucza serwera = alarm). Test na prawdziwym serwerze: W-20.

## UpsertJob (rola Operator, modyfikujące) — it. 62
`UpsertJobArgs { job_name, sources[], destination, cron?, validate_only }` → `UpsertJobReply { result, code, issues[] }`, `FieldIssue { field, code }`:

| Pole | Kod | Reguła |
|---|---|---|
| name | KB-0103 / KB-0106 | pusta, > 200 znaków, znaki sterujące, spacje na brzegach / nazwa zajęta |
| sources | KB-0101 / KB-0103 | brak, nieistniejący lub nieczytelny dla konta Agenta katalog, ścieżka względna / więcej niż jedno źródło (definicja v1) |
| destination | KB-0103 / KB-2106 / KB-0102 | nieobsługiwany schemat (definicja v1: tylko `local`) / zły kształt celu / cel wewnątrz źródła lub odwrotnie |
| schedule | KB-0201 | zła składnia, < 5 min między wystąpieniami (strefa = strefa systemu Agenta, PROWIZORYCZNIE Qb23) |

`validate_only=false` → PROWIZORYCZNIE KB-7012 (zapis nowego zadania idzie przez inicjalizację repozytorium — it. 63).

## InitRepository (rola Operator, modyfikujące) — it. 63
`InitRepositoryArgs { job_name, sources[], destination, cron?, password, create_recovery_key }` → `InitRepositoryReply { result, code, issues[], job_id, repo_id, recovery_key? }`.
- Walidacja jak UpsertJob + hasło (SEC-6.5: ≥ 12 znaków, nie z listy popularnych, ≠ nazwa komputera; pole `password`, KB-0301).
- Hasło w wiadomości po uwierzytelnionym pipe (Qb20/Qb24); Agent zapisuje je wyłącznie w `<stan>/secrets/repo-<repoId>.pw` (nowy plik, 0600; Windows: ACL/DPAPI — W-21) dla kolejnych uruchomień; nie zwraca i nie loguje.
- `create_recovery_key`: drugi klucz typu „recovery” (K14.1, 32 B → 14 grup Base32 z CRC) na uwierzytelnionej liście `kobian.repo`, plik `keys/<id>.key` na celu; tekst klucza wraca **jednorazowo** w odpowiedzi — Agent go nie przechowuje (AC-28.1).
- Zadanie powstaje **wyłączone**, z harmonogramem w strefie systemu Agenta; `UpsertJob(validate_only=false)` je włącza i ustawia/wyłącza harmonogram. Istniejące repozytorium na celu → pole `destination`, KB-1003.

Adapter UI: `GrpcWizardService` (`GrpcDashboardSource.CreateWizardService()`), podłączony w `App` — „Nowe zadanie” otwiera działający kreator.

## PlanPrune (rola Operator, niemodyfikujące) — it. 67
`PlanPruneArgs { job }` → `PlanPruneReply { result, code?, blocked_object?, live_snapshots, live_packs, live_bytes, dead_snapshots, dead_indexes, dead_packs, dead_bytes, grace_seconds }`.
- Wyłącznie podgląd (D3.7): Agent czyta spis repozytorium zadania na celu (sesja Backup) i zapomniane snapshoty całego repozytorium z katalogu; nic nie zapisuje.
- `code`: KB-1601 (nieczytelny obiekt żywego snapshotu; `blocked_object` = „rodzaj:hex”), KB-1602 (nie zostałaby żadna kopia), KB-0104/KB-0103/KB-2xxx (brak zadania / zła definicja / cel), KB-7012 (host bez usługi prune).
- Brak nazw plików i ścieżek w odpowiedzi (NFR-11). CLI: `kobian prune <zadanie> --dry-run [--json]`. Szczegóły: `docs/retention.md`.

## Prune (rola Operator, modyfikujące) — it. 68
`PruneArgs { job }` → `PruneReply { result, code?, stop_object?, marked, unmarked, deleted_snapshots, deleted_indexes, deleted_packs, deleted_bytes, pending, grace_seconds, locks_cleaned, run_id?, indexes_consolidated, indexes_written, active_indexes }`. Prune najpierw konsoliduje indeksy (D3.16, `docs/retention.md`): `indexes_consolidated` = zastąpione indeksy, `indexes_written` = nowe, `active_indexes` = aktywne po kroku (0 = krok nie uruchomiony); KB-1604 w `code` (Warning) = konsolidacja niedokończona, repozytorium kompletne.
- Prune dwufazowy (D3.7) pod blokadą wyłączną (D3.13), sesja Maintenance. Odczekanie blokady ≈ 10 min — klient bez krótkiego terminu.
- `result`: OK; WARNING = KB-1603 (zatrzymano — reszta w kolejnym przebiegu), KB-1201 (fałszywe blokady na celu), KB-1202 (repozytorium zajęte — nic nie zrobiono); FAILED = KB-1601/KB-1602, cel, KB-7012.
- `pending` = nieżywe obiekty czekające na koniec karencji. Bez nazw plików (NFR-11). CLI: `kobian prune <zadanie> [--json]` (kody 0/1/2). Szczegóły: `docs/retention.md`.

## ReleaseRetentionHold (rola Administrator, modyfikujące) — it. 69
`ReleaseRetentionHoldArgs {}` → `ReleaseRetentionHoldReply { result, code?, released }`.
- Zwalnia wstrzymanie retencji po skoku zegara (SEC-17.4, A17, D9.3); idempotentne (`released=false`, gdy nic nie było wstrzymane). Bez usługi wstrzymań → KB-7012.
- `GetStatusResponse.retention_hold { held_at_unix_seconds, code (KB-6117), clock_delta_seconds }` — obecne tylko przy wstrzymaniu; stan ogólny co najmniej WARNING.
- `ApplyRetentionReply.held` — przy wstrzymaniu `result=WARNING`, `code=KB-6117`, nic nie zapomniano.
- CLI: `kobian retention release --confirm [--json]` (bez `--confirm` → 64, nic nie jest wysyłane).

## DiscoverDatabases (rola Operator, niemodyfikujące) — it. 72
`DiscoverDatabasesArgs { server, auth? ("integrated"|"sql"), secret_ref?, certificate? (nazwa .cer w <stan>/certs), strict, allow_localhost_untrusted }` →
`DiscoverDatabasesReply { result, code?, server_major, server_version, server_edition, express, support ("supported"|"conditional"), databases[] { name, recovery_model, data_bytes, read_only, encrypted, compatibility_level, can_backup, can_verify_only, warnings[] }, truncated, warnings[], permission_script? }`.
Tylko odczyt (D-01/D-02/D-03) na koncie Agenta; walidacja przed połączeniem (KB-0103); błędy połączenia KB-3001/3002/3004; wersja < 2014 KB-3401. `result = WARNING` przy ostrzeżeniach serwera (KB-3401 dla 2014/2016). Bez nazwy hosta/instancji w odpowiedzi; nazwy baz wyłącznie w odpowiedzi (nie w logach/zdarzeniach — NFR-11). Reader → PERMISSION_DENIED przed połączeniem z serwerem SQL.

## Źródło bazodanowe w UpsertJob / InitRepository — it. 73 (ADR-0020, PROWIZORYCZNIE Qb31)
`DatabaseSourceSpec { server, auth? ("integrated"|"sql"), secret_ref? (file:<nazwa>), certificate? (nazwa .cer w <stan>/certs), name (sysname), allow_localhost_untrusted }`
jako `UpsertJobArgs.database` (7) i `InitRepositoryArgs.database` (8); zamiast `sources`. Problemy na polu `database`: KB-0103 (kształt serwera, auth↔secret_ref, pin, nazwa bazy, staging Agenta),
KB-7012 (Agent bez obsługi baz). `sources` obok `database` → KB-0103 na `sources`. Walidacja nie łączy się z SQL Server.
InitRepository tworzy `job_jobs.type = 'database'` z sekcją `database` (staging: `KOBIAN_DB_STAGING`, domyślnie `<stan>/staging`; `KOBIAN_DB_SQL_STAGING` opcjonalnie).

## FetchSqlCertificate (Operator, niemodyfikujące) / ApproveSqlCertificate (Operator, modyfikujące) — it. 74 (ADR-0021, PROWIZORYCZNIE Qb32)
`FetchSqlCertificateArgs { server, strict }` → `FetchSqlCertificateReply { result, code?, sha256 (64 hex), subject, issuer, not_before_unix_seconds, not_after_unix_seconds, self_issued, system_trusted, suggested_name }`.
`ApproveSqlCertificateArgs { server, strict, sha256, file_name }` → `ApproveSqlCertificateReply { result, code?, file_name }`.
- Fetch: PRELOGIN + handshake TLS bez logowania (LOGIN7 nigdy); niczego nie zapisuje. Błędy KB-3001 / KB-3006 / KB-0103.
- Approve: ponowne pobranie, porównanie odcisku stałoczasowo (KB-3007), zapis DER w `<stan>/certs/<file_name>` bez nadpisania (KB-3008); nazwa jak pin discovery (`^[A-Za-z0-9][A-Za-z0-9_.-]{0,63}\.cer$`). Wynikową nazwę podaje się w `certificate` (DiscoverDatabases, DatabaseSourceSpec).
- Reader → PERMISSION_DENIED przed połączeniem z serwerem SQL. Agent bez usługi certyfikatów → KB-7012. Nazwa serwera nie trafia do logów (NFR-11).


## BrowseTree (rola Reader, niemodyfikujące) — it. 76 (BL-63, SCR-06)
- Wejście: `snapshot` (`latest`, pełny id albo prefiks ≥ 8 hex — jak `Restore`), `path` (ścieżka względna w kopii, separator `/`; `\` też akceptowany; pusta = korzeń).
- Wyjście: `entries` jednego folderu (foldery przed plikami, porządek porządkowy nazw; `kind` file/directory/link, `size` tylko dla plików, `modified_unix_ms` gdy zapisany), `truncated` przy > 5000 wpisach, `path` znormalizowana, `snapshot_id` pełny.
- Czyta tylko drzewa (bez danych plików) na sesji `Backup`; nie tworzy wiersza `run_runs`.
- Błędy: KB-5002 (snapshot), **KB-5003** (ścieżka nie jest folderem kopii: brak, plik, `..`, `.`, NUL, > 64 segmentów, > 4096 znaków), KB-1xxx/KB-2xxx (repo/cel), KB-7012 (Agent bez usługi restore / starszy Agent — UI pokazuje komunikat).
- Rola Reader zgodnie z R2.6.1 (SCR-06 Viewer): kto widzi listę kopii, widzi też nazwy plików w kopii. Nazwy są danymi klienta — nie trafiają do logów ani zdarzeń (NFR-11).

## Restore z UI (SCR-07) — it. 77 (BL-64, ADR-0022, PROWIZORYCZNIE Qb34)
- UI wywołuje istniejące `Restore` (Operator, modyfikujące) z filtrem z przeglądarki: folder `a/b` → `a/b/*`, plik → dokładna ścieżka, cała kopia → bez filtra; czeka na wynik (termin 6 h).
- Odmowa roli (PermissionDenied/Unauthenticated) → w UI wynik KB-7011 (nie wyjątek); brak połączenia → KB-9001.

## Audyt API i VerifyAudit (Reader, niemodyfikujące) — it. 79 (RD-42, D9.9, PROWIZORYCZNIE Qb36)
- Każda **odmowa** (rola, rewizja klienta) i każde wywołanie **metody modyfikującej** trafia do `audit_log` (migracja core 0003: append-only, seq ciągły, hash = SHA-256(prevHash ‖ JCS(entry))). Wynik: `allowed`, `denied` albo `failed` (pole `result`/`status` = FAILED albo problemy pól), kod KB z odpowiedzi.
- Cel i parametry tylko dla zmian zaufania/konfiguracji: zatwierdzenie certyfikatu SQL (plik, serwer, SHA-256), klucz hosta SFTP w InitRepository/UpsertJob, cron w SetSchedule; nigdy hasła (SEC-11.5). Wartości parametrów ≤ 256 znaków.
- Tożsamość: PROWIZORYCZNIE `local-pipe` (pipe CurrentUserOnly); SID z impersonacji potoku — W-17/W-30 (Windows).
- `VerifyAudit` / `kobian audit verify [--json]`: przelicza cały łańcuch; niezgodność → KB-6501 i pierwszy zły `seq` (kod wyjścia 1). Kotwice dzienne (e-mail, repo, webhook) — jeszcze nie (D9.9, dalsza iteracja).

## Restore: exact_paths — it. 80 (Qb34 domknięte)
- `RestoreRequest.exact_paths` (maks. 1000): plik albo folder z całą zawartością, porównanie dosłowne (bez wzorców `*`/`?`), ścieżki walidowane jak w BrowseTree; żadna nie istnieje → KB-5003. Razem z `path_filter` → KB-0103. Foldery spoza wyboru nie są czytane.
- UI SCR-07 wysyła wyłącznie `exact_paths`. UI i Agent z jednego instalatora — starszy Agent zignorowałby nowe pole i odtworzył całą kopię (bez nadpisań) — Qb37.

## GetStatus: runtime_code — it. 80 (BL-62)
- `runtime_code` = KB-7013, gdy .NET runtime lub współdzielony ASP.NET Core < 10.0.13; stan co najmniej WARNING, `kobian status` pokazuje linię „runtime … UNPATCHED”. Kopie działają dalej; zdarzenie `pl.kobian.host.runtime_unpatched.v1` w core_events przy starcie.

## ListDestinations (Operator, niemodyfikujące) — it. 82 (SCR-09, PROWIZORYCZNIE Qb38)
- Cele z definicji zadań: `id` (= „dest-” + SHA-256(schemat:korzeń)[..16], jak w katalogu repozytoriów), `scheme`, `root`, `jobs` (nazwy); nieczytelne definicje → `unreadable_jobs` (nie są pomijane po cichu). Bez sekretów.
- Sprawdzenie miejsca w UI = istniejące `ProbeDestination` (Operator) dla schematu i korzenia.

## ListServerIdentities / ApproveServerIdentity / SetServerIdentityPolicy — BL-35 (SCR-09, D4.11, SEC-14.5)
- `ListServerIdentities { destination_id? }` (Operator, niemodyfikujące) → `{ result, code?, identities[], require_pin_destinations[] }`; `ServerIdentityInfo`: `destination_id`, `kind` (`ssh-hostkey`/`tls-spki`), `fingerprint` (`SHA256:…`), `state` (PENDING/PINNED/REJECTED/RETIRED/EXPIRED — oczekujące > 24 h), `endpoint` (`schemat://host:port`), `display` (opis z serwera, niezaufany), czasy, `decided_by` (SID albo `system:job-definition`/`system:ca-chain`), `trusted_by_ca` (SPKI zapisany informacyjnie przy ważnym CA — nie jest pinem).
- `ApproveServerIdentity { destination_id, fingerprint, decision: ADD|REPLACE|REJECT }` (**Administrator**, modyfikujące; step-up SEC-4.1 — później) → `{ result, code?, state }`. Tylko odcisk OCZEKUJĄCY dla celu, porównanie porządkowe dokładne: inny → KB-2141, wygasły → KB-2142, ADD przy 4 pinach → KB-2143, kształt/decyzja nieokreślona → KB-0103. Audyt: cel = `destination_id`, parametry `fingerprint`, `decision`; aktor = SID wywołującego (ten sam zapisywany w `approved_by_sid`).
- `SetServerIdentityPolicy { destination_id, require_pin }` (Administrator, modyfikujące) — cel „wysokiego ryzyka”: pin SPKI wymagany mimo ważnego łańcucha CA.
- Sesje SFTP/FTPS Agenta: pin z katalogu (`ServerIdentityGate`); `hostKey` z definicji przenoszony jako zatwierdzony tylko przy braku pinu (start Agenta + pierwsze połączenie). Odmowa z odciskiem → wiersz `pending`. CLI: `kobian destinations identity list|approve|reject|require-pin`. Przewodnik: `docs/admin/tozsamosc-serwera.md`.

## RestoreDatabase (Operator, modyfikujące) — it. 84 (BL-68 cz. 2, SCR-08, Qb39)
- `snapshot` (jak Restore), `target_database` (opcjonalnie; domyślnie `<źródło>_restore_RRRRMMDD`). Agent: definicja zadania „database” → lista dozwolonych serwerów (KB-3009) → `database.bak` (dokładna ścieżka, V1) do `staging/restore-<runId>` → MsSqlRestoreService → staging sprzątany zawsze; wiersz run_runs (restore). Istniejąca baza → KB-3501 (nigdy REPLACE). Odpowiedź: `target_database`, `snapshot_id`. Audyt: cel = snapshot, parametr `target_database`.
- CLI: `kobian restore-db --snapshot <id|latest> [--as <nazwa>] [--json]`.

## PlanRestore — podgląd odtworzenia (it. 98, R2.8.1, BL-64)
- `rpc PlanRestore(RestoreRequest) returns (RestorePlanReply)` — Operator, niemodyfikujące (bez kontroli rewizji, bez wiersza `run_runs`). Ten sam wybór snapshotu („latest”, `latest:<zadanie>`, id, prefiks) i ścieżek (`path_filter` albo `exact_paths`; oba naraz → KB-0103 jak Restore) co `Restore` — wspólne `ExactPathSelection`/`RestoreTarget` w Engine.
- Wynik: `files`, `bytes`, `new_directories`, `conflicts`/`conflict_bytes` + `conflict_sample` (≤ 20 ścieżek względnych w kopii; te pliki zostaną pominięte, KB-5105), `invalid_names`, `links`, `missing_blocks`, `packs`, `read_bytes` (ramki z indeksów, blok współdzielony raz), `free_bytes` (wolumen celu; brak = nieznane), `blocking_code`: KB-5003 (ścieżki nie ma w kopii), KB-5202 (brak bloków), KB-5001 (plik próbny `.kobian-write-probe-<losowe>` z DeleteOnClose nie powstał), KB-5106 (wolne < bajty do zapisania bez konfliktów). `code` = błąd planu (KB-5001 cel nie istnieje/względny, KB-5104 cel jest dowiązaniem, KB-5002, KB-1xxx/2xxx).
- Planer nie pobiera packów i nie tworzy folderów. W usłudze plik próbny powstaje na koncie USŁUGI — kontrola tokenem klienta (SEC-6.3, AccessCheck) = W-40.
- UI SCR-07: „Odtwórz” → PlanRestore → (czysto) Restore | (pominięcia) „Odtwórz mimo to” | (blokada) komunikat KB, start wyłączony do zmiany folderu. Starszy Agent (Unimplemented) → odtworzenie bez podglądu. CLI: `kobian restore … --dry-run [--json]` (schema `kobian.cli.restore-plan/1`).

## VerifyAudit — kotwice i schemat (it. 99, D9.9, RD-51)
- `VerifyAuditArgs.anchor` (opcjonalnie, „seq:sha256hex”) — kotwica zapisana poza serwerem; zły format → Failed KB-0103.
- Kolejność kontroli: schemat `audit_log`/`audit_anchors` (+ wyzwalacze) porównany z migracją core/0003 wykonaną w pamięci (`CatalogBootstrap.MigrationSchema`) → KB-6502; łańcuch → KB-6501; kotwice z `audit_anchors` i zewnętrzna (wpis musi istnieć i mieć ten hash) → KB-6503. `broken_seq` = pierwszy zły wpis albo najniższa niezgodna kotwica.
- Odpowiedź: `anchors_checked`, `last_anchor` („seq:sha256hex”), `last_anchor_at_utc`.
- Kotwica dzienna: `AuditAnchorLoop` w Agencie (co minutę sprawdza `AuditAnchorSchedule.IsDue`: termin 06:55 czasu lokalnego; zaległość = jedna kotwica), `AuditLogService.CreateAnchorAsync` idempotentne per seq. E-mail/webhook/`journal/` — później (Qb49).

## SEC-6.3 — folder docelowy odtwarzania tokenem klienta (it. 101)
- `AgentGrpcService.Restore` i `PlanRestore`: `RestoreTargetGate.Deny(rola, target, IRestoreTargetAuthorizer)` przed backendem. Administrator — bez kontroli; Operator — `NamedPipeRestoreTargetAuthorizer` (usługa Windows): deskryptor folderu (właściciel/grupa/DACL) + token klienta pipe (RunAsClient, Identification) → `AccessCheck(FILE_ADD_FILE | FILE_ADD_SUBDIRECTORY)`; odmowa/błąd/dowiązanie/brak tokenu → KB-5001 (Restore: `RunOutcome` Failed, PlanRestore: `code`). Tryb konsoli: `SameUserRestoreTargets` (pipe CurrentUserOnly). `AgentHostOptions.RestoreTargets`.

## Dziennik wznowień odtwarzania (it. 107, D8.8, BL-80)
- `RestoreExecutor` zapisuje każdy plik jako `<nazwa>.kobian-partial`, weryfikuje SHA-256 i dopiero wtedy przenosi pod nazwę docelową bez nadpisania (R2.8.1 CreatePartial → AtomicRename); uszkodzona treść nigdy nie trafia pod nazwę docelową.
- `CatalogRestoreJournal`: `restore_sessions` (plan = SHA-256 snapshot ‖ cel ‖ wybór) + `restore_files` (pending/verified/quarantined/skipped), zapisy partiami po 256. Kolejne `Restore` z TYM SAMYM planem przejmuje niedokończone sesje (stan ≠ Done/Superseded): pliki zweryfikowane i pliki docelowe o identycznej treści (gdy dziennik mówi „rozpoczęty”) liczone jako odtworzone (bez KB-5105), plik roboczy z przyrostkiem zastępowany; obcy plik o innej treści — nigdy nie nadpisywany (KB-5105). Sesja zakończona bez porażki = Done (powtórka to zwykłe konflikty).
- PlanRestore (it. 108, RD-68) czyta dziennik niedokończonych sesji planu bez ich przejmowania: `resumable` (pole 16) = pliki odtworzone w przerwanej próbie (zweryfikowane albo identyczna treść) — nie są konfliktami, nie liczą się do miejsca. Postęp (RD-60): `run_runs.files`/`bytes_read` aktualizowane w trakcie odtwarzania najwyżej co 1 s (`kobian runs`, SCR-10); SCR-07 w trakcie pokazuje skalę z podglądu.

## ExportJobs (Administrator, niemodyfikujące, audytowane) / ImportJobs (Administrator, modyfikujące) — US-29 (BL-47, SEC-6.7)
- Format pliku, reguły i kody: `docs/admin/eksport-import-zadan.md` (+ `.en.md`). Logika: `JobExportFormat` (format, czysta), `JobTransferService` (katalog).
- `ExportJobs{jobs[]}` → `document` (kanoniczny kobian-export v1, ≤ 60 KiB = `ApiConventions.MaxJobsDocumentBytes`), `jobs`, `issues[]`; nieznana nazwa → KB-0104; za duży → KB-0121; zapisana definicja spoza schematu/z sekretem → odmowa (nie „wycięcie” pól).
- `ImportJobs{document, validate_only, on_conflict}` → `changes[]` (job, kind new|unchanged|conflict, action create|none|skip|overwrite|rename|blocked, new_name, needs_credentials, missing_key_material), `issues[]` (ścieżka JSON, KB, powód), `applied`. Zapis w jednej transakcji pisarza katalogu (konflikty sprawdzane ponownie w transakcji); zadania nowe/nadpisane `enabled=0`.
- Role wg kontraktu D2.10 (Admin dla obu). `MethodPolicy.Entry.Audited` — nowa flaga: audyt metody niemodyfikującej (ExportJobs ujawnia konfigurację). Audyt importu: `sha256`, `bytes`, `validate_only`, `on_conflict` — bez treści.

## AckAlert / SilenceAlert — BL-45 (D9.7, R2.9.6; it. 130)

- `AckAlert { alert_id }` (Operator+, modyfikujące, audytowane: cel = `alert_id`) — potwierdzenie alertu w stanie `notified` → `acknowledged`. Przypomnienia co 24 h ustają; eskalacja po 24 h od otwarcia działa nadal (jednorazowo); ponowne powiadomienie po 72 h od potwierdzenia, jeśli warunek trwa; ustąpienie warunku zamyka alert z powiadomieniem „rozwiązano”.
- `SilenceAlert { alert_id, duration_minutes }` (Administrator, modyfikujące, audytowane: cel + `minutes`) — wyciszenie `notified` → `silenced`, 1…10080 min (7 dni); alerty bezpieczeństwa stan maszyny skraca do 24 h. Po upływie: jedno przypomnienie i powrót do `notified`.
- `alert_id` — losowe 32 znaki hex z `GetStatus.alerts[].alert_id` (bez danych klienta). Odpowiedź `AlertActionReply { result, code?, state? }`: KB-6120 (alert nie istnieje, jest rozwiązany albo nie jest w stanie `notified`), KB-0103 (zły identyfikator lub czas), KB-7012 (ewaluacja alertów wyłączona).
- Zmiana wykonywana w jednej transakcji katalogu, szeregowo z taktem ewaluacji (60 s); stan ewaluatora wczytywany z katalogu przy następnym takcie.
- CLI: `kobian alerts [list] [--json]` (kod wyjścia 1 = są aktywne alerty), `kobian alerts ack <alert-id>`, `kobian alerts silence <alert-id> <minuty 1-10080>`.

## `kobian status`: licznik alertów — it. 132 (BL-45)
- Z `GetStatus.alerts` CLI liczy `AlertSummary`: `active` (open/notified/acknowledged), `unacknowledged` (open/notified), `critical` (aktywne critical/security), `silenced`. Tekst: linia „alerts N active (M unacknowledged, C critical/security) — kobian alerts” tylko gdy `active > 0`; JSON: obiekt `alerts` (zawsze, gdy usługa zwróciła listę). Kod wyjścia nadal pochodzi ze stanu ogólnego usługi; liczby nie zawierają nazw zadań ani ścieżek (NFR-11).

## WatchRun — postęp uruchomienia (it. 134, SCR-07)
`rpc WatchRun(WatchRunArgs{run_id}) returns (stream RunProgress)`, rola Reader, niemodyfikujące. Agent odczytuje wiersz `run_runs` co 1 s i wysyła stan początkowy, potem wyłącznie zmiany (stan, `files`, `bytes_uploaded`, `bytes_read`, kod KB); po stanie końcowym (Succeeded, Warning, Failed, Skipped, Missed, Interrupted, Cancelled) ostatnia wiadomość ma `final=true`. Nieznany lub zniekształcony `run_id` → jedna wiadomość `final` z `KB-0107`. Strumień kończy się po 12 h bez `final` (klient wznawia). Autoryzacja: `AgentCallInterceptor` ma osobny handler strumienia ze wspólną bramką (rola, uwierzytelnienie, audyt odmów); metody strumieniowe modyfikujące są odrzucane (ADR-0042).
CLI: `kobian watch <run-id>` — linia na zmianę (`Running  files 25  read 2500 B`), kod wyjścia = wynik końcowy (0 / 1 ostrzeżenie / 2 błąd lub KB-0107; 3 brak usługi albo odmowa).


## Widok postępu w UI (it. 136, RD-79, ADR-0043)
`Kobian.UI.Progress`: port `IRunProgressSource` (adapter `GrpcDashboardSource.WatchAsync` → RPC `WatchRun`), `RunProgressViewModel` i `RunProgressView` (SCR-07, AutomationId `SCR-07.Progress.*`). Etapy: łączenie → w toku ↔ ponawianie (1/2/4/8/15 s, maks. 5 prób z rzędu) → zakończono (OK / z uwagami / błąd z komunikatem KB) | utracono strumień (KB-7010; odmowa roli KB-7011; brak metody KB-7012; nieznany bieg KB-0107) | zatrzymano. Pasek nieokreślony; liczniki „Odczytano / Wysłano do celu / Pliki” tylko gdy usługa je zna. „Przestań obserwować” nie przerywa uruchomienia. Nie jest jeszcze podpięty do ekranu odtwarzania (Restore RPC nie zwraca run_id przed końcem — RD-81). Zrzuty: `docs/ui/SCR-07-progress-*.png`.
