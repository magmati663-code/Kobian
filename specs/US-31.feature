# language: pl
# Generowane z plan.md (R2.1.1, R2.1.2) przez tools/gen-specs.py — nie edytuj ręcznie.
@US-31 @M
Funkcja: US-31 — Powiadomienia przez Microsoft 365 z OAuth2

  @AC-31.1
  Scenariusz: AC-31.1
    Zakładając domena M365, aplikacja Entra z certyfikatem
    Gdy „Wyślij test”
    Wtedy doręczenie przez XOAUTH2 bez hasła użytkownika; przy odpowiedzi 550 5.7.30 dla Basic Auth kreator kieruje do OAuth
