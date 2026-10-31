# Notatki — V3 z harmonogramu Agenta (BL-26 dokończenie; 3.7, D7.4, D3.13, D3.14, AC-09.1, KB-15xx)

Rola: 8 (odtwarzanie/DR) + 7 (harmonogramy). Kopia repo: /tmp/kb/w-v3.

## Co zrobiono
1. **Polityka** (3.7): opcjonalna sekcja `verify` w definicji zadania v1 — `{"enabled":true,"intervalDays":7,"samplePercent":5}`; brak sekcji = te wartości (plan: tygodniowo, 5%, pokrycie 100% w 20 cyklach). Walidacja: `intervalDays` 1–366, `samplePercent` 1–100, nieznane pole = KB-0103. Zgodne wstecz (parser v1 dotąd ignorował nieznane pola najwyższego poziomu; eksport/import `kobian-export v1` zna nowe pole).
2. **Wyzwalanie**: `CatalogVerifyScheduler` obracany w pętli `CatalogScheduler` (właściwość `Verify`, po starcie kopii z kolejki), terminy oceniane co 1 min:
   - termin = ostatni ukończony cykl (bez niego — najstarszy niezapomniany snapshot zadania) + interwał; nieukończona próba → ponowienie ≥ 1 h po niej;
   - zasoby w TYM SAMYM `LockManager` co kopie: nowy zasób `repo:<destinationId>` — kopie (harmonogram i RunJob) współdzielony, V3 wyłączny (D7.4); zadanie z uruchomieniem Queued/Running/Paused pomijane (priorytet Low); `CatalogJobService.IsActive` jako druga bariera;
   - ścieżka jak RPC Check (`CatalogCheckService.VerifyScheduledAsync`): definicja → klucz → sesja Backup; na celu dzierżawa **współdzielona** D3.13 (osobna sesja, auto-odnawianie);
   - wiersz `run_runs` cause `schedule` + przejście z powodem `verify` (trigger_id NULL — nie koliduje z run_occurrence ani z kolejką schedulera) i `sched_verify_jobs.last_attempt_at/last_run_id` w jednej transakcji; po awarii Running → Interrupted (istniejące `RecoverInterruptedAsync`), bez duplikatu;
   - **rotacja zapamiętana w katalogu** (`sched_verify_jobs.rotation`, migracja `sched/0002_verify.sql`), rośnie tylko po ukończonym cyklu (warunek `last_run_id` = bieżący przebieg).
   - **Próbka stabilna** w `RepoChecker` (`CheckOptions.StableSample`): kubełek = pierwsze 8 B id packa mod ⌈100/P⌉ — dopisane packi nie przesuwają starych (próbka pozycyjna mogła pominąć pack przy dopisywaniu — kontrprzykład w teście właściwości). `kobian check` zostaje przy próbce pozycyjnej (bez zmian).
3. **Wynik** (jedna transakcja): `sched_verify_packs` (ok/błąd), `verify_level = 3` dla nieuszkodzonych snapshotów, których WSZYSTKIE packi przeszły V3 (w tym lub wcześniejszych cyklach — `CheckOptions.KnownVerifiedPacks`, `CheckReport.FullyVerifiedSnapshots`), `suspect = 1` dla dotkniętych, `force_full = 1` → następna kopia zadania pełna bez bazy dedup (3.7; zdejmowane po udanej kopii bez bazy), kod KB-15xx w `run_runs`, zdarzenie `pl.kobian.run.finished.v1` przez outbox (wspólna funkcja `CatalogJobService.EnqueueFinished`, kanały z Program.cs). Kopia z katalogu nieobecna na celu (pusty/obcy nośnik) → KB-1511 zamiast „Succeeded”. Nic nie jest usuwane.
   Ręczne `kobian check --sample` także zapisuje packi i verify_level 3 (bez ruszania rotacji/terminu).
4. **API**: `JobStatus` + pola 13–15 na końcu: `last_verify_unix_seconds`, `last_verify_code`, `next_verify_unix_seconds`. `ListRuns`: powód `verify` → rodzaj „check”. CLI `kobian check` bez zmian; `kobian status` nie wyświetla jeszcze nowych pól.
5. `run_runs.files` = packi pobrane, `bytes_read` = bajty pobrane (koszt transferu na celach zdalnych widoczny w historii).

## Pliki
Nowe: `src/Kobian.Agent/Jobs/CatalogVerifyScheduler.cs`, `src/Kobian.Agent/Jobs/VerifyPolicy.cs`, `src/Kobian.Catalog.Sqlite/Migrations/sched/0002_verify.sql`, `tests/Kobian.Tests.Unit/Agent/VerifyScheduleTests.cs`, `docs/admin/weryfikacja-okresowa.md`, `docs/admin/weryfikacja-okresowa.en.md`.
Zmienione: `src/Kobian.Engine/Repo/RepoChecker.cs` (StableSample, KnownVerifiedPacks, VerifiedPacks/FailedPacks/BytesRead/FullyVerifiedSnapshots/SnapshotsListed, Buckets/Slot/SampleBucket), `src/Kobian.Agent/Jobs/CatalogCheckService.cs`, `CatalogJobService.cs` (ForBackup, force_full, IsActive, EnqueueFinished static), `CatalogScheduler.cs` (Verify, ForBackup), `RunResourceLocks.cs` (repo:, ForBackup, ForVerify), `JobDefinition.cs` (Verify, EffectiveVerify), `JobExportFormat.cs` (schemat `verify`), `src/Kobian.Agent/Hosting/CatalogStatusReader.cs`, `src/Kobian.Agent/Program.cs` (jedna usługa check z kanałami, `KOBIAN_VERIFY_SCHEDULE=0`), `src/Kobian.Api.Contracts/Protos/kobian/v1/agent.proto`, `docs/kb-codes.yaml` + wygenerowane `src/Kobian.Domain/KbCode.cs`, `docs/kb-codes.md`, `src/Kobian.UI/Resources/KbMessages*.resx`, `docs/check.md`, `docs/agent-job-definition.md`, `docs/agent-scheduler.md`, `docs/agent-api.md`, `docs/README.md`, `docs/admin/odtwarzanie-i-ustawienia(.en).md`, `tools/verify-sched-ddl.py` (asercje 0002), `docs/ac-coverage.md` (wygenerowany przez ac-coverage.py).

## Pakiety
Brak nowych.

## Nowe kody KB
- KB-1510 `VerifyScheduledFailed` (integrity, critical) — V3 z harmonogramu wykryła uszkodzone/brakujące dane (np. AEAD/HMAC bloku; gdy raport nie ma własnego kodu 15xx).
- KB-1511 `VerifySnapshotMissing` (integrity, critical) — kopia z katalogu nie istnieje na celu (inny nośnik / usunięcie).
Uwaga dla koordynatora: wybrałem 1510/1511 (nie 1506), by zmniejszyć ryzyko kolizji z innymi agentami.

## Testy (uruchomione)
- `Kobian.Tests.Unit.Agent.VerifyScheduleTests` — 16 testów, 16 OK (3× powtórzone przy wcześniejszej wersji 13 testów — stabilne): reguły terminu, polityka (zgodność wstecz/walidacja), eksport, **właściwość**: próbka stabilna pokrywa każdy pack dokładnie raz w N cyklach także przy dopisywaniu packów (11 wartości %, 20 prób), blokady repo (V3 wyłączna vs kopie współdzielone), mapowanie kodu 15xx, termin na wirtualnym zegarze (przed terminem nic; w terminie raz; kolejny za interwał; outbox run.finished cause schedule), **nie w trakcie kopii** (blokada repo / uruchomienie Queued), **po awarii bez duplikatu** (Running → Interrupted → ponowienie po 1 h, drugi obrót w trakcie nie dubluje, rotacja nieprzesunięta), polityka wyłączona, pętla `CatalogScheduler` obraca V3, **rotacja w katalogu pokrywa 100% packów w 3 cyklach** (z kopią dopisaną między cyklami) + verify_level 3 + GetStatus, **uszkodzony blok w packu danych wykryty dokładnie w cyklu jego kubełka** (Failed KB-1510, suspect, pack ok=0, force_full, run.finished failed/KB-1510 w outboxie, GetStatus last_verify_code, plik nieusunięty; następna kopia pełna bez parent, force_full zdjęte), pusty nośnik → KB-1511, cel niedostępny → cykl nieukończony, rotacja bez zmian, ponowienie po 1 h.
- Pełny `Kobian.Tests.Unit` (bez Windows/Perf): 1408/1408 OK. `Kobian.Tests.Architecture` 16/16, `Kobian.Tests.UI` (bez Windows) 335/335. `tools/verify-sched-ddl.py` OK (15 nowych asercji). Build Release `-warnaserror`: Unit, Agent, Architecture, UI — 0 ostrzeżeń.
- Tagi: `BL-26`, `V3`, `AC-09.1`, `Plan 3.7/D7.4/D7.8/D7.9/D3.13/D3.14`, `Category=Property`.

## Przegląd bezpieczeństwa
Lista kontrolna: brak sekretów w kodzie/testach (hasła losowe); brak nowych logów z danymi klienta — zdarzenie i wiersze zawierają tylko identyfikatory hex, kody KB i liczniki (NFR-11); SQL wyłącznie z parametrami; brak Process.Start, krypto bez zmian (V3 = istniejące `VerifyPackAsync`); rola Backup — nic nie usuwane (D4.6), dzierżawa Backup nie kasuje plików (wygasa); parsowanie polityki z limitami (zakresy, nieznane pola, MaxDepth definicji bez zmian); `SampleBucket` przyjmuje wyłącznie 32 hex (id z indeksu po uwierzytelnieniu); CHECK-i DDL na hex/statusach/kodach.
STRIDE nowych elementów:
- S: obcy/pusty nośnik pod ścieżką celu → KB-1511 (wcześniej V2/V3 dałyby „Succeeded” dla pustego repo). Obcy nośnik z innym repo → klucz/nagłówek nie pasuje → błąd (nie „OK”).
- T: modyfikacja danych na celu → AEAD/HMAC/SHA-256 (KB-1510/15xx); modyfikacja katalogu lokalnego (sched_verify_packs ok=1) mogłaby sztucznie dać plakietkę V3 — wymaga zapisu do katalogu (SYSTEM/Admin), poza modelem.
- R: każdy cykl = wiersz run_runs + przejścia + zdarzenie outbox.
- I: brak nowych danych wrażliwych w API (tylko czasy i kod).
- D: zalanie `locks/` na celu → KB-1202 (przejściowy) w każdym cyklu → cykle nieukończone co 1 h, termin zaległy widoczny w `next_verify_unix_seconds`, każdy nieudany cykl to zdarzenie failed. V3 pobiera packi w całości (limit jak restore). Pętla ocenia terminy co 1 min, ≤ 1000 zadań.
- E: brak — ta sama tożsamość usługi i rola Backup.
Znaleziska: Wysokie — brak. Średnie — (1) ręczne `kobian check` nadal nie bierze `repo:` w LockManager ani dzierżawy na celu (bez zmian zachowania; równoległy prune innego hosta może dać fałszywy KB-1501) — zalecenie: ta sama ścieżka blokad co V3. Niskie — (2) `sched_verify_packs` nie jest czyszczona po prune (wiersze packów usuniętych zostają; tylko narzut miejsca, nie wpływa na wynik, bo verify_level liczony z packów faktycznie używanych); (3) brak postarzania priorytetu V3 — przy kopiach co kilka minut na tym samym repo V3 może długo czekać (termin zaległy widoczny w API); (4) skok zegara wstecz opóźnia termin o wielkość skoku.

## Decyzje PROWIZORYCZNE (do nadania Qb)
- **Qb-NEW-verify-policy** — Pytanie: gdzie polityka V3 (globalnie w Agencie czy per zadanie) i w jakiej postaci? Wybrano: opcjonalna sekcja `verify` w definicji v1 (enabled/intervalDays/samplePercent), domyślnie z 3.7 (7 dni, 5%), interwał od ostatniego ukończonego cyklu, bez okna godzinowego. Alternatywy: konfiguracja globalna Agenta; wyzwalacz cron (sched_triggers kind 'verify'). Koszt zmiany: mały (pole → wyzwalacz; stan w sched_verify_jobs zostaje).
- **Qb-NEW-verify-retry** — Pytanie: kiedy ponawiać nieukończony cykl? Wybrano: stałe 1 h od próby (bez wykładniczego odstępu, bez limitu prób). Alternatywa: polityka ponowień zadania (D7.3). Koszt: mały.
- (ADR-podobnie, bez znacznika w kodzie) **Próbka stabilna po id** zamiast pozycyjnej dla harmonogramu — pozycyjna nie gwarantuje pokrycia przy dopisywaniu packów; ręczny `check` bez zmian dla zgodności (`--rotation` tygodniowa). Do decyzji: przełączyć także `kobian check` (koszt: mały; zmienia, które packi sprawdza dane `--rotation`).
- (ADR-podobnie) **verify_level 3** = snapshot nieuszkodzony, którego wszystkie packi przeszły V3 kiedykolwiek (ok=1 w katalogu); późniejszy błąd packa daje suspect. Alternatywa: okno ważności (np. N·interwał). Koszt: mały.
- (ADR-podobnie) **force_full** w `sched_verify_jobs` zamiast oznaczania najnowszej kopii jako suspect — realizuje „wymuszenie kopii pełnej” (3.7) bez fałszowania RPO dla nieuszkodzonej kopii.

## Testy do kolejki Windows
Brak (kod przenośny; brak nowych adapterów platformowych).

## Niedokończone / dla innych ról
- Alert A04 (rola 9): dane są — `run.finished` z cause `schedule`, status `failed`, `kb` ∈ KB-15xx (1510/1511/1501–1503); zdarzenie nie ma pola „rodzaj operacji” (rozróżnienie po kodzie 15xx lub po `ListRuns` kind „check”).
- `kobian status` / pulpit: wyświetlić `last_verify_*`/`next_verify_*` (UI: plakietka V3 i „V3 zaległa” — stan ⚠ z 6.3).
- Ręczny `kobian check` przez kolejkę zasobów i dzierżawę (znalezisko Średnie 1).
- Metryka `kobian_verify_last_success_timestamp_seconds{level="V3"}` (9.x) — źródło: `sched_verify_jobs.last_completed_at`.
- Możliwa kolizja numeru migracji `sched/0002_*` z innym agentem przy scalaniu — wtedy przenumerować.
