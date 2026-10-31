# ADR-0009: SCR-02 (zadania) i SCR-10 (historia) na istniejących RPC

- Status: PROWIZORYCZNIE (Qb19), iteracja 58, 2026-10-14
- Dotyczy: R2.6.1 (SCR-02, SCR-10), D6.6, D2.10, BL-61, BL-60; uzupełnia ADR-0008

## Kontekst
Priorytet właściciela: UI. Kontrakt kobian.v1 ma GetStatus, ListSnapshots, ListSchedules, SetSchedule, RunJob — bez listy uruchomień (run_runs) i bez pól z BL-60.

## Decyzja
1. SCR-02: stan zadania tą samą funkcją co pulpit (ProtectionEvaluator 6.3); harmonogram z ListSchedules. Wstrzymanie = SetSchedule bez cron (wyzwalacz wyłączony, wyrażenie zostaje w katalogu), wznowienie = SetSchedule z zachowanym wyrażeniem. Wstrzymany harmonogram = stan „Wstrzymane”. Cykl do kontroli zaległości wyprowadzony z typowych wzorców cron (godzina/dzień/dni robocze = 3 dni/tydzień), inne wzorce — bez kontroli zaległości.
2. SCR-10: tylko udane kopie (ListSnapshots); limit 500 wierszy w widoku; „Odtwórz” prowadzi do SCR-06 (ekran w budowie).
3. Jeden adapter gRPC (`GrpcDashboardSource`) implementuje porty IDashboardSource, IJobsSource, IHistorySource (jeden kanał).

## Konsekwencje / koszt zmiany zdania
- Nieudane uruchomienia nie są widoczne w historii, dopóki nie powstanie RPC ListRuns (BL-60) — koszt: nowe RPC + mapowanie, mały/średni.
- Opis cron po stronie UI (CronText) dubluje wiedzę schedulera dla 4 wzorców — przy rozszerzeniu kontraktu o „następne uruchomienie” i „cykl” (BL-60) UI przestaje zgadywać.
- Alternatywa: najpierw kontrakt (BL-60), potem ekrany — wolniejsza ścieżka do widocznego UI.
