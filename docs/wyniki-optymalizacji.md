# Wyniki optymalizacji (przed / po)

Pomiary: `tools/perf/perf.sh` na 1,2 GB (Linux x64, 4 rdzenie). Plan: `docs/plan-optymalizacji.md`.

| Runda | Zmiana | Przed | Po |
|---|---|---|---|
| 1–2 | Skrypt pomiarowy + bramka w CI (`tools/perf/check.sh`, zadanie `perf`) | brak | progi: szczyt ≤ 420 MB, pamięć prywatna po kopii ≤ 130 MB, rozmiar repo, czasy |
| 3 | GC bez wątku współbieżnego + `GCConserveMemory=7` (Agent) | pełna kopia 11,4–12,4 s; szczyt 474–583 MB | pełna kopia 10,3–11,2 s; szczyt **324 MB** |
| 4 | Kompaktujące sprzątanie po dużym uruchomieniu (`MemoryTrim`) | 271 MB RSS po kopii (193 MB po zmianie GC) | 166 MB RSS, z czego **78 MB prywatne** (reszta to mapowane biblioteki; w spoczynku 77 MB) |
