# ADR-0044 — Restore w tle (detach) zwraca run_id od razu (RD-81, Qb109)

Status: PROWIZORYCZNIE (Qb109). Data: 2026-10-18, it. 137.

Kontekst: SCR-07 (widok postępu, ADR-0043) śledzi bieg po run_id przez WatchRun, ale RPC Restore czekał na wynik i run_id nie był dostępny przed końcem.

Decyzja: `RestoreRequest.detach=true` → Agent waliduje cel i snapshot synchronicznie, tworzy wiersz run_runs (Running), uruchamia odtwarzanie w tle (CancellationToken.None) i zwraca `RunOutcome{Ok, run_id}`; błąd walidacji = `Failed` + kod KB, bez run_id i bez wiersza. Domyślnie (detach=false) zachowanie bez zmian — zgodność wsteczna klientów (CLI). Wynik końcowy: run_runs / WatchRun.

Alternatywy: (a) nowy RPC StartRestore — więcej powierzchni API i polityk ról; (b) run_id wcześnie w strumieniu — wymaga streamingowego Restore. Odrzucone jako droższe.

Koszt zmiany zdania: niski — pole opcjonalne w proto (numery 5 i 4 zarezerwowane), usunięcie gałęzi w AgentGrpcService.

Ryzyka: brak limitu równoległych odtworzeń w tle (RD-82), zamknięcie hosta w trakcie — wiersz Running odzyskiwany jako Interrupted (D8.8, istniejący mechanizm).
