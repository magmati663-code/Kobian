# language: pl
# Generowane z plan.md (R2.1.1, R2.1.2) przez tools/gen-specs.py — nie edytuj ręcznie.
@US-33 @M
Funkcja: US-33 — Podłączenie istniejącego repo na nowym komputerze

  @AC-33.1
  Scenariusz: AC-33.1
    Zakładając repo hosta A; nowy komputer B
    Gdy „Podłącz istniejące repo” + klucz odzyskiwania + „kontynuuj jako ten sam host”
    Wtedy katalog odbudowany z pełną historią; następny przyrostowy reużywa bloki (nie jest pełny); A oznaczony jako wycofany

  @AC-33.2
  Scenariusz: AC-33.2
    Zakładając A i B jednocześnie jako ten sam hostId
    Gdy uruchomienie na obu
    Wtedy drugi otrzymuje odmowę blokady i alert
