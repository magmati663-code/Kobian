# language: pl
# Generowane z plan.md (R2.1.1, R2.1.2) przez tools/gen-specs.py — nie edytuj ręcznie.
@US-04 @M
Funkcja: US-04 — SFTP (Ed25519, pinning) i SMB (bez litery dysku)

  @AC-04.1
  Scenariusz: AC-04.1
    Zakładając serwer SFTP o nieznanym kluczu hosta, CLI bez --host-key
    Gdy konfiguracja celu
    Wtedy odmowa (brak TOFU w trybie bezobsługowym); z --host-key SHA256:… zgodnym — sukces

  @AC-04.2
  Scenariusz: AC-04.2
    Zakładając zapisany odcisk; serwer zmienia klucz
    Gdy uruchomienie
    Wtedy Failed: HostKeyMismatch bez ponowień, A09 wysłany ≤ 1 min, 0 bajtów danych wysłanych

  @AC-04.3
  Scenariusz: AC-04.3
    Zakładając udział SMB 3.x z szyfrowaniem
    Gdy zadanie
    Wtedy brak litery dysku i wpisu w net use w sesji interaktywnej użytkownika; połączenie zamknięte po zadaniu; serwer tylko-SMB1 → Failed z komunikatem, bez obejścia

  @AC-04.4
  Scenariusz: AC-04.4
    Zakładając Windows 11 24H2, NAS z wyłączonym podpisywaniem SMB
    Gdy zadanie
    Wtedy Failed z KB-2111, 0 bajtów danych wysłanych, komunikat z instrukcją; brak zmiany ustawień klienta SMB przez Kobian

  @AC-04.5
  Scenariusz: AC-04.5
    Zakładając upload w toku, symulowane wznowienie z uśpienia
    Gdy zdarzenie zasilania
    Wtedy sesje puli zamknięte i otwarte ponownie ≤ 10 s, breaker nie otwarty, zadanie kończy się Succeeded

  @AC-04.6
  Scenariusz: AC-04.6
    Zakładając SFTP z MaxSessions 2, domyślna równoległość 3
    Gdy zadanie
    Wtedy równoległość spada do 2 w ≤ 10 s, KB-2112 (Info), zadanie Succeeded; ≤ 3 nieudane otwarcia sesji w godzinie
