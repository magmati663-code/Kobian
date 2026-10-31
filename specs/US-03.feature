# language: pl
# Generowane z plan.md (R2.1.1, R2.1.2) przez tools/gen-specs.py — nie edytuj ręcznie.
@US-03 @M
Funkcja: US-03 — Harmonogram kalendarz/cron z polityką misfire

  @AC-03.1
  Scenariusz: AC-03.1
    Zakładając „codziennie 22:00 Europe/Warsaw”, komputer wyłączony 3 dni, RunOnceIfMissed
    Gdy start usługi
    Wtedy dokładnie 1 uruchomienie 5–10 min po starcie; 3 wpisy Missed

  @AC-03.2
  Scenariusz: AC-03.2
    Zakładając harmonogramy z 7.9
    Gdy przypadki E1, E2, E5
    Wtedy wszystkie zgodne z oczekiwaniem 7.9

  @AC-03.3
  Scenariusz: AC-03.3
    Zakładając edytor cron (tryb Zaawansowany)
    Gdy wpisanie */30 7-20 * * 1-5 / wyrażenia błędnego
    Wtedy podgląd 5 najbliższych uruchomień zgodny z Cronos / zapis zablokowany z komunikatem wskazującym pole
