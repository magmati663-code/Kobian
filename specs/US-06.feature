# language: pl
# Generowane z plan.md (R2.1.1, R2.1.2) przez tools/gen-specs.py — nie edytuj ręcznie.
@US-06 @M
Funkcja: US-06 — Szyfrowanie repozytorium (zawsze włączone)

  @AC-06.1
  Scenariusz: AC-06.1
    Zakładając repozytorium utworzone z korpusu „złotego”
    Gdy przeszukanie wszystkich plików repo poza kobian.repo
    Wtedy żadna nazwa pliku źródłowego ani żaden z 20 znanych ciągów treści nie występuje jawnie

  @AC-06.2
  Scenariusz: AC-06.2
    Zakładając repozytorium, błędne hasło
    Gdy otwarcie przez kobian-restore
    Wtedy odmowa z komunikatem „nieprawidłowe hasło lub klucz”; z poprawnym kluczem odzyskiwania — pełny dostęp
