# ADR-0014 — Retencja GFS: czysta funkcja Keep + logiczne zapomnienie (BL-25 cz. 1, it. 65)

Status: PROWIZORYCZNIE (Qb25). Realizuje D3.12; prune (D3.7, D3.13) — osobny ADR.

## Decyzja
- `RetentionPlanner.Plan(S, policy, now, zone, paused)` w Kobian.Engine — czysta funkcja zgodna z pseudokodem D3.12 (restic forget; tydzień ISO; strefa zadania; zawsze: najnowszy, lock, bazy DIFF, suspect; brak reguł/paused = zachowaj wszystko). Wiek od max(zegar, najnowsza kopia).
- Polityka w definicji v1 (`retention`, ścisła walidacja, KB-0401), zapisywana przez InitRepository (S5 kreatora).
- `ApplyRetention` (Operator + rewizja): dry-run albo logiczne zapomnienie (`forgotten_at`) w jednej transakcji z wierszem `run_runs` (reason/kind „forget”). Dane na celu zostają.

## Konsekwencje
+ AC-10.1 (V-10) i właściwości (nigdy najnowszy/lock/suspect; idempotentność) testowane; NFR-25 zmierzony w CI (nie referencyjnie).
− Miejsce na celu nie jest odzyskiwane do czasu prune; ręczne uruchamianie (Qb25 c).
