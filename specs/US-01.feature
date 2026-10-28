# language: pl
# Generowane z plan.md (R2.1.1, R2.1.2) przez tools/gen-specs.py — nie edytuj ręcznie.
@US-01 @M
Funkcja: US-01 — Kreator zadania plików → lokalny/USB/SMB/SFTP

  @AC-01.1
  Scenariusz: AC-01.1
    Zakładając czysta instalacja, tryb Prosty, Dokumenty 2 GB / 10 tys. plików, dysk USB NTFS
    Gdy użytkownik przechodzi 6 kroków kreatora na presetach z zaznaczonym „Wykonaj pierwszą kopię teraz”
    Wtedy snapshot Succeeded, V2 OK, mini-test D6.4 zgodny, pulpit „Chronione”; czas spełnia NFR-01; żadne ustawienie domyślne nie musiało być zmienione

  @AC-01.2
  Scenariusz: AC-01.2
    Zakładając krok 2, cel SMB, błędne hasło
    Gdy „Dalej”
    Wtedy kreator blokuje przejście, komunikat z kodem KB-2xxx i jedną akcją „Popraw hasło”; dokładnie 1 próba logowania na kliknięcie (błąd trwały, 4.7)

  @AC-01.3
  Scenariusz: AC-01.3
    Zakładając zadanie zapisane
    Gdy eksport JSON
    Wtedy plik zawiera wyłącznie secretRef, przechodzi walidację schematu; skaner sekretów (NFR-11) — 0 trafień

  @AC-01.4
  Scenariusz: AC-01.4
    Zakładając źródło 400 GB, USB 2.0
    Gdy koniec kreatora
    Wtedy stan „Pierwsza kopia w toku” z szacowanym czasem; harmonogram aktywny; NFR-01 liczony do tego stanu (K21.2)

  @AC-01.5
  Scenariusz: AC-01.5
    Zakładając zadanie w rewizji 7 otwarte w dwóch sesjach UI
    Gdy sesja A zapisuje (rewizja 8), sesja B zapisuje z expected_revision = 7
    Wtedy B dostaje KB-7005 z porównaniem zmian; rewizja 8 nienaruszona; audyt zawiera oba zdarzenia
