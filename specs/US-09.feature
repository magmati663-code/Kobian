# language: pl
# Generowane z plan.md (R2.1.1, R2.1.2) przez tools/gen-specs.py — nie edytuj ręcznie.
@US-09 @M
Funkcja: US-09 — Weryfikacja V1/V2 i RESTORE VERIFYONLY

  @AC-09.1
  Scenariusz: AC-09.1
    Zakładając snapshot zapisany; jeden pack usunięty z celu
    Gdy V2
    Wtedy Failed + A04; przy kompletnym repo Succeeded z plakietką V2

  @AC-09.2
  Scenariusz: AC-09.2
    Zakładając .bak uszkodzony w stagingu przed wysyłką
    Gdy VERIFYONLY
    Wtedy Failed, snapshot nie zostaje zatwierdzony (manifest nie powstaje)
