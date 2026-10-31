# language: pl
# Generowane z plan.md (R2.1.1, R2.1.2) przez tools/gen-specs.py — nie edytuj ręcznie.
@US-38 @M
Funkcja: US-38 — Test zdolności celu przy konfiguracji

  @AC-38.1
  Scenariusz: AC-38.1
    Zakładając cel pozwalający usuwać / SFTP -P remove,…
    Gdy probe D4.5
    Wtedy plakietka „nie chroni przed usunięciem” + A10 / „chroni przed usunięciem”; wynik w raporcie zadania

  @AC-38.2
  Scenariusz: AC-38.2
    Zakładając trzy cele SFTP: (a) pełne prawa, (b) -P remove,rename,posix-rename@openssh.com, (c) katalog repo z chattr +a
    Gdy probe
    Wtedy (a) P0 „brak ochrony”, A10; (b) P1 „chroni przed usunięciem, nie przed nadpisaniem” + KB-2502; (c) P2 „chroni przed usunięciem i nadpisaniem”; poziom i data w raporcie zadania; spadek poziomu względem poprzedniego probe → A10
