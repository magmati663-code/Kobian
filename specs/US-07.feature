# language: pl
# Generowane z plan.md (R2.1.1, R2.1.2) przez tools/gen-specs.py — nie edytuj ręcznie.
@US-07 @M
Funkcja: US-07 — Odtworzenie wybranych plików z wybranej wersji

  @AC-07.1
  Scenariusz: AC-07.1
    Zakładając 30 snapshotów; plik usunięty 10 dni temu
    Gdy Operator: wyszukiwanie nazwy → „Do innego folderu”
    Wtedy plik odtworzony bajt w bajt (SHA-256 = manifest), znaczniki czasu zachowane; czas NFR-02

  @AC-07.2
  Scenariusz: AC-07.2
    Zakładając Operator bez prawa zapisu do wskazanego folderu
    Gdy restore
    Wtedy odmowa przed odczytem danych (SEC-6.3)

  @AC-07.3
  Scenariusz: AC-07.3
    Zakładając jeden blok w packu uszkodzony
    Gdy restore folderu 100 plików
    Wtedy 99 plików odtworzonych; uszkodzony nie trafia do miejsca docelowego, raport wskazuje plik (D8.1)
