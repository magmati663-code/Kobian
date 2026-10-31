# language: pl
# Generowane z plan.md (R2.1.1, R2.1.2) przez tools/gen-specs.py — nie edytuj ręcznie.
@US-02 @M
Funkcja: US-02 — Pełny backup MSSQL COPY_ONLY bez przerywania ERP

  @AC-02.1
  Scenariusz: AC-02.1
    Zakładając SQL Express, baza 5 GB, recovery SIMPLE, obciążenie referencyjne (NFR-07)
    Gdy zadanie DB w trybie Prostym
    Wtedy w msdb.dbo.backupset is_copy_only = 1; differential_base_guid bazy niezmieniony; VERIFYONLY OK; brak *.bak w stagingu po zakończeniu; NFR-07 spełnione

  @AC-02.2
  Scenariusz: AC-02.2
    Zakładając trwa BACKUP/DBCC/RESTORE innego narzędzia na tej bazie
    Gdy start zadania
    Wtedy oczekiwanie maks. 30 min (konfigurowalne), potem Skipped z przyczyną concurrent_backup; brak nakładania się operacji

  @AC-02.3
  Scenariusz: AC-02.3
    Zakładając login bez db_backupoperator
    Gdy „Testuj połączenie”
    Wtedy komunikat wskazuje brakujące uprawnienie i oferuje wygenerowany skrypt T-SQL; Kobian nie prosi o hasło sa (SEC-5.3)
