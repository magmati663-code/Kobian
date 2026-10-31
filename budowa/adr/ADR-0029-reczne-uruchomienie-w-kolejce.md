# ADR-0029 — Uruchomienie ręczne przez wspólną kolejkę blokad; baner „tryb podglądu” w UI

- Status: PROWIZORYCZNIE (Qb46), it. 96 (rola 6 prowadzi; 6,7,4,11)
- Dotyczy: D7.4, D7.10 (queueTimeout), ADR-0027/Qb44, BL-74, D2.9, 6.7, RD-57, ADR-0028/Qb45

## Kontekst
Od it. 94 harmonogram nabywa zasoby (`smb:<serwer>` z kontem, slot globalny) w `LockManager` tworzonym wewnątrz `CatalogScheduler`. `RunJob` (ręcznie) startował od razu, więc ręczna kopia SMB innym kontem w trakcie zaplanowanej kończyła się KB-2101 (Niskie it. 94), a limit równoległości nie obejmował uruchomień ręcznych.
Rola „Podgląd” (BL-75) wyszarza akcje w UI, ale użytkownik nie wiedział dlaczego ani co zrobić (administrator bez podniesienia UAC dostaje rolę Podgląd albo odmowę ACL).

## Decyzja
1. Jeden `LockManager(2)` tworzony w Program.cs i podawany w `CatalogJobService.Locks`; `CatalogScheduler` używa `jobs.Locks`, gdy jest (inaczej własny — testy, zgodność).
2. `CatalogJobService.RunAsync` (ręczne): po zajęciu zadania (`_active`, KB-4103) czeka na `TryAcquireAll` z ponowieniem co 1 s; limit 6 h → `Skipped` + KB-4102 (**PROWIZORYCZNIE Qb46**: stały, bez pola w definicji zadania); anulowanie → OCE, bez blokad, zadanie zwolnione. Blokady zwalniane w `finally` po zakończeniu.
3. UI (RD-57 w1): baner SCR-01 „Tryb podglądu” przy roli Podgląd po udanym odczycie; drugie zdanie zależy od `IElevationInfo` (adapter `TokenElevationInfo`: `TokenElevationType` własnego procesu = Limited → „Uruchom jako administrator”, inaczej „poproś o grupę Kobian Operators”). Rola nadal wyłącznie z Agenta (`GetStatus.caller_role`); **bez nowego RPC GetCallerInfo** (rola już jest w GetStatus, SID nie jest potrzebny w UI). UI nie restartuje się z podniesieniem (zakaz Process.Start przez powłokę) — tylko instrukcja.

## Alternatywy
- (2) ręczne uruchomienie omija kolejkę, ale z priorytetem — prostsze, zostawia KB-2101 (E6).
- (2) wiersz `run_runs` w stanie `Queued` z `cause=manual` już podczas czekania (widoczny w historii) — lepsze dla UI, ale wymaga zmiany `RunQueuedAsync`/`CatalogScheduler.QueuedRuns` (dziś tylko schedule/misfire) i `RecoverInterruptedAsync`; odłożone do BL-76.
- (2) limit czekania z definicji zadania (`queueTimeout` D7.10) — wymaga wersji definicji; po decyzji właściciela.
- (3) RPC `GetCallerInfo` z SID i informacją o podniesieniu z tokenu po stronie Agenta — dokładniejsze (Agent widzi grupy deny-only), więcej kodu po obu stronach; do rozważenia z RD-58.

## Skutki i ryzyka
- Ręczne i zaplanowane uruchomienia dzielą limit 2 — „Uruchom teraz” przy dwóch trwających kopiach czeka (wcześniej startowało jako trzecie).
- Brak ścisłej FIFO między ręcznym a harmonogramem (ponawianie co 1 s); przy dużej kolejce możliwe „wyprzedzanie” — akceptowalne do czasu dyspozytora z priorytetami D7.10.
- Administrator bez podniesienia, któremu ACL pipe odmawia połączenia, widzi ekran „usługa niedostępna” (KB-9001), nie baner — RD-58.
- Koszt zmiany zdania: mały (właściwość `Locks` = null przywraca stare zachowanie; baner = jedna właściwość VM + 3 teksty).
