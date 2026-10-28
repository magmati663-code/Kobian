# language: pl
# Generowane z plan.md (R2.1.1, R2.1.2) przez tools/gen-specs.py — nie edytuj ręcznie.
@US-37 @M
Funkcja: US-37 — CLI: status/uruchom/odtwórz z kodami wyjścia

  @AC-37.1
  Scenariusz: AC-37.1
    Zakładając usługa działa / zatrzymana
    Gdy kobian status --json
    Wtedy kody 0/1/2/3 wg 6.8; JSON zgodny z wersjonowanym schematem; przy braku usługi kod 3 w ≤ 5 s

  @AC-37.2
  Scenariusz: AC-37.2
    Zakładając Agent z min_client_revision wyższym niż rewizja CLI
    Gdy kobian run <zadanie>
    Wtedy kod wyjścia 4, komunikat KB-7006; polecenia tylko do odczytu działają

  @AC-37.3
  Scenariusz: AC-37.3
    Zakładając usługa działa
    Gdy drugi proces Agenta startuje
    Wtedy proces kończy się w ≤ 2 s z KB-7007, catalog.db niezmieniony (porównanie sumy), wpis w Event Log
