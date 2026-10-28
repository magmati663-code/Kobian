# ADR-0005 — Definicja zadania „files” v1: klucz wskazywany ścieżkami plików (PROWIZORYCZNIE)

Data: 2026-10-13 (iteracja 47). Status: prowizoryczny (Qb13). Dotyczy: D7.7 (job_jobs.definition), D3.11, BL-15, BL-42, BL-24.

## Kontekst
Agent musi uruchomić zadanie z katalogu (RunJob), ale inicjalizacja repozytorium i magazyn poświadczeń (DPAPI/SecretRef) nie istnieją (it. 48+, Windows). Bez tego `job run` nie zrobi backupu.

## Decyzja
1. `definition` = JSON UTF-8 ≤ 64 KiB, `version: 1`: `sources` (dokładnie 1 ścieżka bezwzględna), `destination {scheme: "local", root}`, `key {file, passwordFile?, repoHeader?}`, `packTargetSize?` (1–512 MiB). Parser: `Kobian.Agent.Jobs.JobDefinition` (błędy KB-0103 bez ścieżek w komunikacie).
2. Klucz ładowany `KeyFileLoader` (opakowany + hasło z pliku + `kobian.repo`, albo klucz deweloperski ADR-0002). Hasło nie jest w katalogu ani w logach.
3. Wiersze: `run_runs` (+`run_transitions`) na każde podejście, `snap_snapshots` odczytany z repozytorium po backupie (źródło prawdy), `repo_repositories` (INSERT OR IGNORE). `RunId` potoku = `run_id` z katalogu.
4. Przyrost, gdy jest poprzedni czytelny snapshot zadania w tym repo; w przeciwnym razie pełny.
5. Jedno uruchomienie zadania naraz (KB-4103); po awarii wiersze Running/Queued → Interrupted przy starcie.

## Alternatywy
SecretRef do magazynu poświadczeń od razu (wymaga Windows/DPAPI — niemożliwe do zweryfikowania w Linuksie); hasło w definicji (odrzucone: sekret w katalogu).

## Koszt zmiany zdania
Niski: zmiana ogranicza się do `JobDefinition.Parse` i ładowania kluczy w `CatalogJobService.ExecuteAsync`; schemat katalogu bez zmian (wersja definicji w polu `version`).
