# language: pl
# Generowane z plan.md (R2.1.1, R2.1.2) przez tools/gen-specs.py — nie edytuj ręcznie.
@US-34 @?
Funkcja: US-34 — (brak w tabeli zakresu)

  @AC-34.1
  Scenariusz: AC-34.1
    Zakładając heartbeat skonfigurowany, okno 26 h
    Gdy host wyłączony / usługa zatrzymana
    Wtedy zewnętrzna usługa wysyła alert; Kobian nie jest wymagany do wykrycia (D9.4)

  @AC-34.2
  Scenariusz: AC-34.2
    Zakładając usługa w trybie TylkoOdczyt
    Gdy upływ okna heartbeatu
    Wtedy heartbeat nie jest wysyłany, zewnętrzna usługa alarmuje; Event Log zawiera A14
