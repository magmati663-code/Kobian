# ADR-0011 — GetStatus bez wartości zastępczych i ListRuns (BL-60, it. 61)

Status: PROWIZORYCZNIE (Qb22). Zastępuje część ADR-0008/Qb18 (wartości zastępcze w UI) dla Agenta ≥ it. 61.

## Kontekst
Pulpit SCR-01 i lista SCR-02 zgadywały cykl, poziom weryfikacji i rolę (Qb18), a historia SCR-10 nie widziała nieudanych uruchomień (Qb19).

## Decyzja
- `JobStatus` dostaje pola addytywne: `disabled`, `next_run_unix_seconds`, `interval_seconds`, `last_success_unix_seconds`, `last_success_verify_level`, `destination_scheme`, `repository_suspect`; `GetStatusResponse.caller_role` (enum, 0 = starszy Agent). Domyślne wartości odpowiadają zachowaniu starego Agenta (D2.16).
- Odczyt w `CatalogStatusReader` (tylko odczyt katalogu); błędna definicja/wyzwalacz → puste pole, nie błąd RPC.
- `ListRuns` (Reader): historia uruchomień z `run_runs`, rodzaj z pierwszego `run_transitions.reason`, limit 1–1000.
- Rola: `AgentGrpcService` ustawia `caller_role` tym samym `ICallerRoleResolver`, którego używa interceptor — UI nie może dostać roli innej niż ta, którą egzekwuje Agent.
- Stan ogólny: `suspect` najnowszej kopii → Failed (Qb22 b).

## Konsekwencje
+ UI bez zgadywania dla nowego Agenta; wartości zastępcze zostają tylko dla `UNSPECIFIED`/brak pól.
+ CLI `kobian runs` (schemat `kobian.cli.runs/1`).
− Wolne miejsce celu nadal brak (ProbeDestination — BL-60 cz. 2).
− Pole `destination_scheme` to nie nazwa celu; nazwane cele = przyszłe pole.
Koszt zmiany zdania: mały (pola addytywne, logika w jednej klasie).
