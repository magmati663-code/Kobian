# Notatki — rola 9 (monitoring i powiadomienia): ewaluacja alertów w Agencie (BL-45 / BL-82)

Repo: `/tmp/kb/w-alerts`. Plan: D9.6, D9.7, D9.8, 9.4 (higiena alertów), 9.5 (A03 lokalnie), 9.7 (e-mail), R2.9.6 (DDL `alert_alerts`, KB-61xx).

## Co zrobiono
1. **Trwały stan alertów** — nowy moduł katalogu `alert` (`Migrations/alert/0001_init.sql`, `CatalogBootstrap.ModuleOrder` += `"alert"`; plan D2.11 przewiduje moduł `alert`). Tabela `alert_alerts` wg R2.9.6 (+ CHECK-i formatu, + `acknowledged_at`), indeks częściowy `alert_open_dedup(code, scope) WHERE state <> 'resolved'` — jeden nierozwiązany alert na klucz. `next_reminder` nie jest kolumną — liczony z `last_notified_at` + `reminderHours` reguły.
2. **`AlertService`** (`src/Kobian.Agent/Notifications/AlertService.cs`) — pętla co 60 s (`PeriodicTimer` z `TimeProvider`, testowalna wirtualnym zegarem; plan: „co 60 s”, A03 periodic 900 s pilnuje ewaluator). Jeden takt = JEDNA transakcja `CatalogWriter`: `AlertFactsCollector.Collect` (fakty z katalogu) → `AlertEvaluator.Tick` (reguły z JSON) → `AlertStateMachine` → INSERT/UPDATE `alert_alerts` + `core_outbox` `pl.kobian.alert.{opened,reminder,resolved}.v1` dla każdego kanału (`NotifyChannel`, jak `run.finished` w `CatalogJobService`), wspólny `id` zdarzenia, ładunek per kanał w jego trybie minimalnym. Po restarcie nierozwiązane alerty wczytywane z katalogu (brak duplikatów, przypomnienie liczone od wysyłki sprzed restartu). Błąd transakcji → stan w pamięci odrzucany i wczytywany ponownie (pamięć nie wyprzedza katalogu). Wiersz z nieznanym rodzajem zakresu (wstawiony poza Agentem) pomijany — nie blokuje pętli. Podłączony w `Program.cs` (tylko `CatalogMode.Normal`; błędne reguły = komponent wyłączony, komunikat na stderr, usługa działa).
3. **Fakty / reguły aktywne**: A01 (ostatni rozstrzygnięty run `Failed`, ponowienia wyczerpane), A02 (run `Warning` — tylko stan/pulpit), A03 (brak nieuszkodzonej kopii > 1,5 × cykl cron, min. 26 h; tylko zadania z włączonym cron), A04 (repo `corrupt` / snapshot z `damage` / najnowszy snapshot zadania `suspect`), A09 (`repo_dest_identities.state='pending'` — nowy/zmieniony klucz serwera czeka na zatwierdzenie; zakres `dest:<dest-…>`), A14 (RuntimeGuard KB-7013 — niezałatany .NET), A15 (≥ 5 odmów API w `audit_log` w 1 h), A17 (`core_retention_hold` — skok zegara, ClockDelta z `delta_sec`).
4. **Reguły nieaktywne (jawnie, `AlertFactsCollector.InactiveRules` + test pokrycia A01–A17)**: A05 (brak wyników V4 w katalogu), A06 (wyniki probe/wolne miejsce nie są zapisywane), A07 (rekoncyliacja niezaimplementowana), A08 (alert „jednorazowy” z audytu wymaga przepływu potwierdzania — inaczej opened+resolved), A10 (test zdolności D4.5 nie zapisywany), A11 (heurystyka ransomware Plannera nie istnieje; `suspect` w katalogu oznacza błąd V2/V3, nie ransomware), A12 (z założenia zewnętrzny heartbeat), A13 (daty wygaśnięcia nie zbierane), A16 (WMI/Windows Update nie zaimplementowane).
5. **Zdarzenie** `Kobian.Notifications.Events.AlertEvent` (+ `AlertNotice`, `AlertTransition`): koperta CloudEvents (D9.6), `subject` = zakres, dane z zamkniętej listy (alertId hex32, code A\d\d, severity, category, kb KB-dddd, transition, escalated, reminders, scope{kind,id}, openedUtc, resolvedUtc, lastSuccessUtc, details z białej listy kluczy liczbowych, messageKey `Alert.Axx.Title`, job{id,name}). Tryb minimalny pomija `job.name` (mechanizm CloudEvent jak run.finished). Pole `message` (tekst PL z przykładu planu) pominięte — tekst renderują kanały z szablonów (brak nazwy zadania w trybie minimalnym).
6. **E-mail**: `EmailMessageBuilder` obsługuje alert.* (temat `[Kobian] <Alert|Przypomnienie|Eskalacja|Rozwiązano> A03: <tytuł> — <zadanie|cel|repozytorium|ten komputer> (host)`, ≤ 120), `EmailTemplates` PL/EN: tytuły i zalecane działania A01–A17, etykiety; tytuł/działanie wyłącznie z szablonu po kodzie (nie z ładunku). Alerty idą e-mailem zawsze (`KOBIAN_SMTP_NOTIFY` dotyczy tylko run.finished). Webhook — bez zmian w kanale (dowolna koperta), test end-to-end przez `OutboxDispatcher`.
7. **RPC**: `GetStatusResponse.alerts = 8` (`repeated AlertInfo` na końcu wiadomości; `AlertInfo{code,severity,state,scope_kind,subject,opened_unix_seconds,kb,reminders,escalated}`), wypełniane w `CatalogStatusReader.ReadAlerts` (security → critical → warning, maks. 200; brak tabeli w katalogu tylko-do-odczytu = pusta lista). Stan ogólny `state` NIE zmieniony. UI/CLI nie przebudowywane — wyświetlenie listy to następny krok.
8. **Dokumentacja**: `docs/admin/powiadomienia.md` / `.en.md` — sekcja „Alerty / Alerts” (przejścia, kanały, reguły aktywne/nieaktywne, przykład zdarzenia, e-mail).
9. **Weryfikator DDL**: `tools/verify-alert-ddl.py` (16 kontroli, OK) + dopisany do pętli w `tools/ci.sh`.

## Pliki
Nowe: `src/Kobian.Catalog.Sqlite/Migrations/alert/0001_init.sql`, `src/Kobian.Agent/Notifications/AlertService.cs` (AlertService, AlertFactsCollector, AlertFactSet, AlertDetail, AlertTickResult), `src/Kobian.Notifications/Events/AlertEvent.cs`, `tests/Kobian.Tests.Unit/Notifications/AlertServiceTests.cs` (klasy `AlertServiceTests`, `AlertEmailTests`), `tools/verify-alert-ddl.py`.
Zmienione: `src/Kobian.Catalog.Sqlite/CatalogBootstrap.cs` (ModuleOrder), `src/Kobian.Notifications/Email/EmailMessageBuilder.cs`, `src/Kobian.Notifications/Email/EmailTemplates.cs` (+`Has`), `src/Kobian.Api.Contracts/Protos/kobian/v1/agent.proto`, `src/Kobian.Agent/Hosting/CatalogStatusReader.cs`, `src/Kobian.Agent/Program.cs`, `tools/ci.sh`, `docs/admin/powiadomienia.md`, `docs/admin/powiadomienia.en.md`.

## Pakiety / KB
Brak nowych pakietów. Brak nowych kodów KB (użyte istniejące KB-6101, KB-6103, KB-6111, KB-6116, KB-6117 z reguł; A09/A04 bez kodu KB — `kb: null` w regułach, jak dotąd).

## Testy (uruchomione)
- `Kobian.Tests.Unit.Notifications.AlertServiceTests` — 15 testów: otwarcie/deduplikacja/przypomnienie+eskalacja/rozwiązanie z dokładnie 1 wpisem outboxa na przejście i kanał (2 kanały), ponowne otwarcie = nowy wiersz; karencja ponowień A01; restart bez duplikatów (nowy writer + nowa instancja, przypomnienie od trwałego last_notified_at); A03 tylko dla zadań z harmonogramem (KB-6103, lastSuccessUtc); A09 pending → resolved po zatwierdzeniu, adres serwera nie wychodzi; A14/A15/A17 host; A04 suspect → resolved nowszą kopią; A02 tylko stan; brak danych klienta (losowa nazwa pliku, folder, nazwa zadania — minimalny bez nazwy zadania); webhook przez OutboxDispatcher (CloudEvents, Webhook-Id = id); GetStatus.alerts (kolejność, pola); pokrycie A01–A17 aktywne ∪ nieaktywne; pusty katalog = brak alertów; CHECK-i tabeli; obcy wiersz nie blokuje pętli.
- `Kobian.Tests.Unit.Notifications.AlertEmailTests` — 6 testów: PL opened (tytuł, działanie, KB, ważność, HTML), EN eskalacja/rozwiązanie, tryb minimalny bez nazwy zadania, wszystkie A01–A17 PL/EN z zakresem celu, wrogie znaki w nazwie zadania (CR/LF/RLO), odrzucanie wartości spoza formatu w AlertEvent.
- Cały `Kobian.Tests.Unit` (bez Windows/Perf): **1412/1412 OK** (przed ostatnim dodanym testem; po nim uruchomione klasy Alert*/CatalogStatusReaderTests/CatalogTests: 37/37 OK). `Kobian.Tests.Architecture` 16/16 OK, `Kobian.Tests.UI` 335/335 OK. `python3 tools/verify-alert-ddl.py` OK.
- Build Release `-warnaserror`: `Kobian.Agent` (net10.0 + net10.0-windows), `Kobian.Tests.Unit`, `Kobian.Tests.Architecture`, `Kobian.Tests.UI`, `Kobian.Cli` — 0 ostrzeżeń.

## Przegląd bezpieczeństwa
Lista kontrolna: SQL wyłącznie z parametrami ✔; brak sekretów w zdarzeniach/logach ✔ (zdarzenie nie zawiera endpointu, odcisku, ścieżek, nazw plików — test z losowymi wartościami) ✔; identyfikatory walidowane (zakres regex ewaluatora + CHECK w DDL + `AlertEvent.ParseScope`), klucze szczegółów z białej listy ✔; limity: ≤ 10 000 zakresów/regułę (zgodnie z `AlertEvaluator.MaxScopesPerRule`), ≤ 200 alertów w GetStatus, `last_payload` ≤ 4 KiB, zdarzenie ≤ 64 KiB ✔; e-mail: nagłówki tylko przez MimeKit, nazwa zadania oczyszczana (CR/LF/format), tytuły z szablonu ✔; `RandomNumberGenerator` do id ✔; brak Process.Start/krypto ✔.
STRIDE nowych elementów:
- S: alerty generuje tylko Agent (pisarz katalogu); webhook podpisany jak dotąd (D9.10).
- T: wiersz `alert_alerts` zmieniony poza Agentem (SYSTEM) — może wyciszyć alert do następnego taktu; ewaluator przelicza warunek z faktów co 60 s (alert wraca, jeśli warunek trwa; rozwiązany wiersz → nowy alert). Niskie.
- R: przejścia alertów nie trafiają do łańcucha audytu (tylko outbox/alert_alerts) — Niskie, do decyzji (Qb-NEW-alert-audit).
- I: nazwa zadania w trybie nieminimalnym (dozwolone pole SEC-9.6); adres serwera A09, ścieżki — nigdy. Niskie.
- D: obcy wiersz z nieznanym zakresem pomijany (test); błąd danych → takt pominięty, pętla żyje; transakcja krótka (kilka zapytań z LIMIT). Niskie.
- E: brak nowych metod RPC (tylko pole w GetStatus — Reader+); potwierdzanie/wyciszanie alertów niezaimplementowane.
Znaleziska: Wysokie 0, Średnie 0, Niskie: (1) brak audytu przejść alertów; (2) A01 heurystyka karencji ponowień może wysłać alert, gdy ponowienie startuje > 2 min po porażce (dziś ponowienia są natychmiastowe — `retryDelay` null w Program.cs).

## Decyzje PROWIZORYCZNE (do nadania numerów Qb)
- **Qb-NEW-alert-ack-at** — kolumna `acknowledged_at` poza DDL planu (maszyna stanów liczy 72 h od potwierdzenia). Alternatywa: wyliczać z audytu. Koszt zmiany: migracja alert 0002.
- **Qb-NEW-alert-escalation** — eskalacja = `alert.reminder.v1` z `escalated=true` do tych samych kanałów (brak typu `alert.escalated` w D9.6 i brak konfiguracji drugiego odbiorcy). Alternatywa: osobny typ/kanał. Koszt: mały (AlertEvent + routing kanałów).
- **Qb-NEW-a02-digest** — A02 tylko stan (pulpit), bez natychmiastowego powiadomienia (9.4 „w raporcie dziennym”); liczba otwartych alertów nie jest jeszcze w raporcie dziennym. Alternatywa: wysyłać jak inne ostrzeżenia. Koszt: mały.
- **Qb-NEW-a01-retry-grace** — „ponowienia wyczerpane” = próba ≥ 3 (RetryLimit) albo 2 min bez nowej próby i bez aktywnego uruchomienia (brak w katalogu flagi „błąd przejściowy/ponowienie zaplanowane”). Alternatywa: kolumna w run_runs. Koszt: migracja run + zapis w CatalogJobService.
- **Qb-NEW-a15-window** — okno zliczania odmów 1 h (próg 5 z reguły). Alternatywa: okno w JSON reguły (zmiana schematu reguł).
- **Qb-NEW-alert-quiet-hours** — cisza nocna bez konfiguracji (zawsze wyłączona). Koszt: zmienna/ustawienie + strefa czasowa.
- **Moduł katalogu `alert`** — dodany do `ModuleOrder` (zgodnie z D2.11, który wymienia moduł 'alert'); uwaga przy scalaniu, jeśli inny agent zmienia `ModuleOrder`.

## Testy do kolejki Windows
Brak nowych (kod bez zależności od Windows; Agent kompiluje się dla net10.0-windows bez ostrzeżeń).

## Niedokończone / następne kroki
- UI pulpit SCR-01 i `kobian status` w CLI: wyświetlenie `GetStatusResponse.alerts` (tytuły z zasobów po `code`); ewentualnie podniesienie `state` przy aktywnym alercie bezpieczeństwa.
- RPC potwierdzania/wyciszania alertów (Operator+/Admin, audyt; `AlertStateMachine.Acknowledge/Silence` już są).
- Tabela `alert_channels` z R2.9.6 (dziś kanały ze zmiennych środowiska), drugi odbiorca eskalacji, `min_severity` per kanał.
- Źródła danych dla A05/A06/A07/A08/A10/A11/A13/A16 (lista wyżej); A06 wymaga zapisu wyniku probe (wolne miejsce) w katalogu.
- Liczba otwartych alertów w raporcie dziennym (9.7) i metryka `kobian_alerts_active` (9.8).
- Sprzątanie rozwiązanych alertów po 400 dniach (CatalogMaintenance).
- Wpisy audytu przejść alertów (do decyzji).
