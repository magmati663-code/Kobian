# Wyniki optymalizacji (przed / po)

Pomiary: `tools/perf/perf.sh` na 1,2 GB (Linux x64, 4 rdzenie). Plan: `docs/plan-optymalizacji.md`.

| Runda | Zmiana | Przed | Po |
|---|---|---|---|
| 1–2 | Skrypt pomiarowy + bramka w CI (`tools/perf/check.sh`, zadanie `perf`) | brak | progi: szczyt ≤ 420 MB, pamięć prywatna po kopii ≤ 130 MB, rozmiar repo, czasy |
| 3 | GC bez wątku współbieżnego + `GCConserveMemory=7` (Agent) | pełna kopia 11,4–12,4 s; szczyt 474–583 MB | pełna kopia 10,3–11,2 s; szczyt **324 MB** |
| 4 | Sprzątanie `GCCollectionMode.Aggressive` po przebiegu z dużą stertą (`MemoryTrim`) | 271 MB RSS po kopii (193 MB po zmianie GC) | 116 MB RSS, z czego **28 MB prywatne** (reszta to mapowane biblioteki, odzyskiwalne przez system) |
| 6 | Skrót „plik bez zmian” w przyroście (rozmiar + mtime + okno racy 2 s, bloki z poprzedniej migawki; pełny odczyt treści co 7 dni, `KOBIAN_CONTENT_CHECK_DAYS`) | przyrost bez zmian 3,4–4,3 s (czyta 1,2 GB) | **1,1–1,3 s**, 0 plików otwartych, 0 nowych packów |
