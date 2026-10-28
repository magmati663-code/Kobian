# language: pl
# Generowane z plan.md (R2.1.1, R2.1.2) przez tools/gen-specs.py — nie edytuj ręcznie.
@US-08 @M
Funkcja: US-08 — Kopie przyrostowe i różnicowe

  @AC-08.1
  Scenariusz: AC-08.1
    Zakładając snapshot pełny, potem 1% zmian
    Gdy przyrostowy
    Wtedy wysłane bajty ≤ 1,1 × rozmiar zmienionych bloków + metadane; usunięcie snapshotu pośredniego nie wpływa na odtwarzalność późniejszego

  @AC-08.2
  Scenariusz: AC-08.2
    Zakładając pełny, inc1, inc2
    Gdy różnicowy
    Wtedy reużyte bloki wyłącznie z packów ostatniego pełnego (kontrola indeksu); ostrzeżenie UI przy > 50% rozmiaru pełnego

  @AC-08.3
  Scenariusz: AC-08.3
    Zakładając plik podmieniony z zachowaniem rozmiaru i mtime (inny FileId)
    Gdy przyrostowy
    Wtedy plik wykryty jako zmieniony

  @AC-08.4
  Scenariusz: AC-08.4
    Zakładając cel z limitem 1 GB zapełniony w trakcie zapisu packa
    Gdy uruchomienie zadania
    Wtedy status Failed z KB-1402, kod wyjścia CLI ≠ 0, brak nowego snapshotu na celu i w katalogu, żaden zapisany indeks nie wskazuje brakującego packa; kolejne uruchomienie po zwolnieniu miejsca kończy się Succeeded

  @AC-08.5
  Scenariusz: AC-08.5
    Zakładając źródło exFAT; plik zmieniony 1 s po rozpoczęciu poprzedniego snapshotu, ten sam rozmiar i ten sam mtime
    Gdy kopia przyrostowa
    Wtedy plik jest ponownie odczytany i zapisany z nową treścią (hash różny), snapshot zawiera nową wersję

  @AC-08.6
  Scenariusz: AC-08.6
    Zakładając katalog z 200 000 plików i plik sparse 2 TiB
    Gdy kopia i restore
    Wtedy żadna strona drzewa nie przekracza 8 MiB, restore daje identyczne fileSha256 dla próbki 1 000 plików; brak KB-1302/KB-1305

  @AC-08.7
  Scenariusz: AC-08.7
    Zakładając Plik.txt i plik.txt w jednym katalogu
    Gdy kopia i restore na NTFS bez rozróżniania wielkości liter
    Wtedy oba wpisy w snapshotcie; jeden plik odtworzony, drugi raportowany jako konflikt, brak nadpisania
