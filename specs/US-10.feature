# language: pl
# Generowane z plan.md (R2.1.1, R2.1.2) przez tools/gen-specs.py — nie edytuj ręcznie.
@US-10 @M
Funkcja: US-10 — Retencja liczba wersji / dni + retentionLock

  @AC-10.1
  Scenariusz: AC-10.1
    Zakładając 60 snapshotów dziennych; keepLast 7, keepDaily 14, retentionLock 14 dni
    Gdy retencja
    Wtedy zachowany dokładnie oczekiwany zbiór; test właściwości: nigdy nie usuwa ostatniego udanego ani snapshotu młodszego niż lock; bez poświadczenia maintenance — usunięcie tylko logiczne (D4.1)

  @AC-10.2
  Scenariusz: AC-10.2
    Zakładając Admin po step-up
    Gdy API: usuń snapshot młodszy niż lock
    Wtedy odmowa, zdarzenie audytu, A08

  @AC-10.3
  Scenariusz: AC-10.3
    Zakładając dwa zadania w jednym repozytorium, wspólne bloki
    Gdy prune po usunięciu snapshotów jednego zadania
    Wtedy packi referencjonowane przez drugie zadanie nietknięte; restore drugiego zadania poprawny
