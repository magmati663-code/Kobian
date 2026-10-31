# language: pl
# Generowane z plan.md (R2.1.1, R2.1.2) przez tools/gen-specs.py — nie edytuj ręcznie.
@US-12 @?
Funkcja: US-12 — (brak w tabeli zakresu)

  @AC-12.1
  Scenariusz: AC-12.1
    Zakładając serwer FTP startujący w trybie ASCII
    Gdy upload packa 8 MiB
    Wtedy TYPE I wysłane przed STOR i SIZE; rozmiar po zapisie równy oczekiwanemu; weryfikacja poziomu V1-b lub V1-a zapisana w raporcie

  @AC-12.2
  Scenariusz: AC-12.2
    Zakładając FTPS z dwoma zatwierdzonymi pinami SPKI
    Gdy 20 połączeń naprzemiennie z obu węzłów
    Wtedy 0 zdarzeń Mismatch; trzeci, nieznany SPKI → KB-2109 i zatrzymanie
