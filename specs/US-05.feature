# language: pl
# Generowane z plan.md (R2.1.1, R2.1.2) przez tools/gen-specs.py — nie edytuj ręcznie.
@US-05 @M
Funkcja: US-05 — E-mail o błędzie (A01) i „brak kopii od X” (A03)

  @AC-05.1
  Scenariusz: AC-05.1
    Zakładając zadanie dzienne, ostatni sukces T0
    Gdy brak sukcesu do T0 + 26 h
    Wtedy e-mail A03 wysłany w czasie NFR-08; jeden e-mail (deduplikacja), przypomnienie po 24 h, e-mail „rozwiązano” po następnym sukcesie

  @AC-05.2
  Scenariusz: AC-05.2
    Zakładając serwer SMTP niedostępny
    Gdy wyzwolenie alertu krytycznego
    Wtedy alert w trwałej kolejce z ponowieniami; wpis Warning+ w Event Log; pulpit „Zagrożone”; po przywróceniu SMTP doręczenie zaległego alertu

  @AC-05.3
  Scenariusz: AC-05.3
    Zakładając zadanie Failed po wyczerpaniu ponowień
    Gdy wysyłka A01
    Wtedy treść PL/EN z przyczyną, akcją naprawczą i kodem KB-xxxx; brak haseł; w temacie brak pełnych ścieżek i nazw baz (SEC-9.6)

  @AC-05.4
  Scenariusz: AC-05.4
    Zakładając wolumen danych pełny w trakcie kopii
    Gdy SQLITE_FULL
    Wtedy uruchomienie kończy się Interrupted zapisanym w katalogu (z rezerwy), alert przez Event Log i bezpośredni e-mail ≤ 2 min, nowe uruchomienia wstrzymane
