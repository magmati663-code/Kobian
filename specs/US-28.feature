# language: pl
# Generowane z plan.md (R2.1.1, R2.1.2) przez tools/gen-specs.py — nie edytuj ręcznie.
@US-28 @M
Funkcja: US-28 — Klucz odzyskiwania pokazany raz, z dowodem zapisania

  @AC-28.1
  Scenariusz: AC-28.1
    Zakładając ekran klucza odzyskiwania
    Gdy próba przejścia dalej
    Wtedy wymagane przepisanie 2 losowych grup; schowek czyszczony po 60 s; ponowne wyświetlenie tylko Admin + step-up + wpis audytu

  @AC-28.2
  Scenariusz: AC-28.2
    Zakładając inny komputer, tylko klucz odzyskiwania (bez hasła)
    Gdy kobian-restore
    Wtedy pełny dostęp do repo

  @AC-28.3
  Scenariusz: AC-28.3
    Zakładając klucz odzyskiwania z błędem w jednym znaku
    Gdy wprowadzenie w kobian-restore
    Wtedy komunikat „Błąd w grupie N” (CRC-16, K14.1) przed jakąkolwiek próbą odszyfrowania
