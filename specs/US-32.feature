# language: pl
# Generowane z plan.md (R2.1.1, R2.1.2) przez tools/gen-specs.py — nie edytuj ręcznie.
@US-32 @M
Funkcja: US-32 — Odtworzenie bazy MSSQL do nowej nazwy

  @AC-32.1
  Scenariusz: AC-32.1
    Zakładając snapshot bazy ERP
    Gdy restore z ustawieniami domyślnymi
    Wtedy nowa baza <nazwa>_restore_YYYYMMDD; zapytanie kontrolne profilu ERP zgodne z wartością z backupu; produkcja nietknięta

  @AC-32.2
  Scenariusz: AC-32.2
    Zakładając Operator lub Admin bez step-up
    Gdy nadpisanie bazy produkcyjnej
    Wtedy odmowa; Admin po step-up i wpisaniu nazwy bazy — dozwolone, wpis audytu
