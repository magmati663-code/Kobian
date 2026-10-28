# language: pl
# Generowane z plan.md (R2.1.1, R2.1.2) przez tools/gen-specs.py — nie edytuj ręcznie.
@US-29 @M
Funkcja: US-29 — Eksport/import zadań JSON bez sekretów

  @AC-29.1
  Scenariusz: AC-29.1
    Zakładając JSON z akcją runSql i skryptem pre
    Gdy import
    Wtedy zadanie zaimportowane, akcje nieaktywne do zatwierdzenia przez Admin (SEC-6.7); secretRef bez sekretu → kreator prosi o poświadczenia

  @AC-29.2
  Scenariusz: AC-29.2
    Zakładając JSON niezgodny ze schematem
    Gdy import
    Wtedy odrzucenie całego pliku z listą błędów (ścieżka JSON + powód), bez częściowego zapisu
