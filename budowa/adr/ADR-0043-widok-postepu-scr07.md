# ADR-0043 — Widok postępu uruchomienia w UI (RD-79, SCR-07)

Data: 2026-10-18 (it. 136). Status: PROWIZORYCZNIE (Qb108). Powiązania: RD-79, BL-64, ADR-0042 (WatchRun), D2.9, R2.6.1 SCR-07, NFR-11.

## Kontekst
RPC `WatchRun` (it. 134) i `kobian watch` istniały, UI nie miało jak pokazać postępu biegu w toku. UI zależy tylko od `Kobian.Api.Contracts` i `Kobian.Domain` (nie od CLI), więc nie może użyć `IAgentClient`.

## Decyzja
- Port `IRunProgressSource.WatchAsync(runId, ct)` w `Kobian.UI.Progress` z własnym rekordem `RunProgressUpdate` (bez typów proto); adapter: `GrpcDashboardSource.WatchAsync` (jeden kanał z resztą UI), mapowanie `MapProgress`.
- `RunProgressViewModel` (etapy Connecting → Running ↔ Reconnecting → Done | Lost | Stopped) + `RunProgressView` (AutomationId `SCR-07.Progress.*`). Pasek nieokreślony; pokazujemy wyłącznie liczniki, które usługa przysłała (0/brak = nie pokazujemy — nic nie jest zmyślane).
- Zerwanie połączenia (`IOException`) albo strumień bez wiadomości `final` (limit 12 h z ADR-0042, restart usługi) → ponawianie 1/2/4/8/15 s, maks. 5 kolejnych prób; udana wiadomość zeruje licznik. Po wyczerpaniu: etap Lost, KB-7010. Odmowa roli → KB-7011, starszy Agent bez metody → KB-7012 (bez ponawiania). Nieznany run → KB-0107.
- „Przestań obserwować” anuluje tylko obserwację; uruchomienie trwa w usłudze (tekst widoczny przy przycisku).

## Alternatywy
Pasek procentowy z podglądu PlanRestore (tylko restore, i tylko gdy plan znany — odłożone do RD-81); odpytywanie ListRuns przy utracie strumienia (Qb108, pytanie otwarte). Koszt zmiany: stałe w VM, bez zmian kontraktu.

## Konsekwencje
Widok jest gotowy i przetestowany (headless), ale NIE jest jeszcze podpięty do ekranu odtwarzania: RPC `Restore` blokuje do końca i nie zwraca `run_id` wcześniej → RD-81. Każdy otwarty widok = jeden strumień (jedno zapytanie/s po stronie Agenta, ADR-0042).
