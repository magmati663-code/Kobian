# Wyzwalacze czasowe w Agencie (RD-26, BL-42, US-03)

Stan: it. 54. Kod: `src/Kobian.Agent/Jobs/CatalogScheduler.cs`, `CatalogSchedulerStore.cs`; rdzeń bez I/O: `Kobian.Scheduler.Timing.SchedulerCore` (D7.9).

## Przepływ
1. `sched_triggers` (kind `cron`, enabled=1) ⨝ `job_jobs` (enabled=1) → `TimeTrigger`. Strefa (`time_zone`), polityka misfire (`misfire`, `misfire_within_sec`) pochodzą z zadania (CE-5: strefa zadania, nie systemu).
2. `spec` wyzwalacza = JSON UTF-8 ≤ 1 KiB: `{"cron":"0 2 * * *"}` (5 pól, Cronos; ≥ 5 min między wystąpieniami — `UpsertCronTriggerAsync` odrzuca gęstsze i błędną strefę kodem KB-0201).
3. Pętla co 1 s (`PeriodicTimer`): `SchedulerCore.Tick` → wiersz `run_runs` w stanie `Queued` (`cause` = `schedule` albo `misfire`, `trigger_id`, `scheduled_for_utc`) + `run_transitions` seq 0.
4. Wiersze `Queued` są uruchamiane równolegle (limit 2, PROWIZORYCZNIE do czasu blokad zasobów D7.10): `CatalogJobService.RunQueuedAsync` → `Queued → Running → Succeeded/Warning/Failed` (D7.8, ponowienia jak przy uruchomieniu ręcznym).
5. Zadanie wyłączone w międzyczasie → `Skipped` + KB-0105; poprzednie uruchomienie tego zadania trwa → `Skipped` + KB-4103.

## Gwarancje
- „Ostatnio obsłużone” wynika z `MAX(scheduled_for_utc)` w `run_runs` — po restarcie nie ma osobnego stanu, który mógłby się rozjechać; duplikat wystąpienia odrzuca indeks `run_occurrence` (KB-4901 w `Diagnostics`).
- Po przestoju: wystąpienia z okresu przestoju = wiersze `Missed`; wg polityki co najwyżej jedno uruchomienie `misfire` 5–10 min po starcie (AC-03.1).
- Błędny wyzwalacz (zła specyfikacja/cron) jest pomijany z KB-0201 w `CatalogScheduler.Diagnostics`; pozostałe działają.
- Uruchomienie w stanie `Queued` w chwili awarii Agenta kończy jako `Interrupted` przy starcie (`RecoverInterruptedAsync`) i nie jest wznawiane — następne wystąpienie przyjdzie wg harmonogramu.

## Ograniczenia (PROWIZORYCZNIE)
- Zarządzanie (RD-27, it. 55): RPC `ListSchedules` (Reader) i `SetSchedule` (Operator; brak `cron` = wyłączenie) oraz CLI `kobian schedule list [--job <nazwa>]`, `kobian schedule set <nazwa> --cron '<5 pól>'`, `kobian schedule off <nazwa>`. Jeden wyzwalacz cron na zadanie (`cron-<job_id>`, PROWIZORYCZNIE); walidacja przed zapisem (KB-0201, ≥ 5 min), brak zadania → KB-0104.
- Działający `CatalogScheduler` wykrywa zmianę `sched_triggers`/`job_jobs` co obrót pętli (odcisk konfiguracji) i stosuje `SchedulerCore.Reconfigure` bez restartu: niezmienione wyzwalacze zachowują następne wystąpienie i oczekujące misfire; nowy/zmieniony liczy następne wystąpienie od „teraz” (edycja nie tworzy zaległości); wyłączony przestaje odpalać.
- Tylko `kind='cron'`; zdarzenia/interwał/zależności, okna czasowe (KB-4101), blokady zasobów (D7.10), wybudzanie (PBT_APMRESUMEAUTOMATIC) — Windows/kolejne iteracje.
- Wyłączenie pętli: zmienna `KOBIAN_SCHEDULER=0`.

## Uruchomienie ręczne w kolejce (BL-74, it. 96)
- `RunJob` („Uruchom teraz”, `kobian run`) nabywa te same zasoby co harmonogram we **wspólnym** `LockManager` (D7.10): slot globalny (PROWIZORYCZNIE 2) + `smb:<serwer>` z kontem (ADR-0027). Menedżer tworzy Program.cs i podaje w `CatalogJobService.Locks`; `CatalogScheduler` bierze go stamtąd.
- Zasób zajęty → uruchomienie czeka (próba co 1 s, licznik `CatalogJobService.ManualWaiting`); `wait=false` zwraca od razu, `wait=true` czeka na wynik. Od it. 97 (BL-76) czekanie jest wierszem `run_runs` `Queued` z `cause=manual` — to samo uruchomienie przechodzi potem `Queued → Running → wynik`, `Queued → Skipped` (KB-4102) albo `Queued → Cancelled`; wolne zasoby = start od razu bez kroku Queued.
- Limit czekania 6 h (D7.10 `queueTimeout`, PROWIZORYCZNIE Qb46 — stały) → wiersz `Skipped` z **KB-4102**, zadanie zostaje wolne (bez KB-4103 przy kolejnej próbie).
- Anulowanie w trakcie czekania (zamknięcie Agenta, zerwane `wait=true`) nie bierze blokad i zwalnia zadanie.
- Ograniczenia: kolejność ręczne ↔ harmonogram nie jest ścisłą FIFO (ręczne ponawia co 1 s, harmonogram co obrót pętli); stan „W kolejce” jest widoczny w `ListRuns` (SCR-10 „w kolejce”) i w `GetStatus.active_state` (SCR-02 pigułka „W kolejce”/„W toku”). Scheduler nie uruchamia wierszy `manual` (należą do czekającego `RunJob`); po awarii usługi stają się `Interrupted` przy starcie (`RecoverInterruptedAsync`).


## Weryfikacja V3 z harmonogramu (BL-26)
- `CatalogScheduler.Verify` (`CatalogVerifyScheduler`) obraca się w tej samej pętli 1 s, **po** starcie kopii z kolejki; terminy sprawdzane co 1 min z `sched_verify_jobs` (migracja `sched/0002_verify.sql`) i polityki `verify` z definicji zadania.
- Zasób `repo:<identyfikator celu>` w `LockManager`: kopie (harmonogram i `RunJob`) — współdzielony, V3 — wyłączny (D7.4). Kopia czeka w kolejce na koniec V3; V3 nie startuje, gdy zadanie ma uruchomienie Queued/Running.
- Po awarii wiersz V3 `Running` → `Interrupted` przy starcie; cykl powtarzany po 1 h z tym samym kubełkiem (rotacja przesuwa się tylko po ukończeniu). Wyłączenie: `KOBIAN_VERIFY_SCHEDULE=0`.
