# ADR-0039 — Zapytania kontrolne ERP (D5.11) przy backupie i restore MSSQL

Status: PROWIZORYCZNIE (Qb103) · it. 123 · BL-40 / BL-68 · D5.9, D5.11, D8.4, SEC-15.3

## Kontekst
Plan (D5.11, L6) wymaga, by kopia bazy ERP była weryfikowana logicznie („poprawna technicznie, logicznie pusta”): zapytania kontrolne profilu wykonane przy backupie, zapisane w `dbMeta`, porównane po restore. `ControlComparison` i profile istniały, ale nic ich nie wykonywało; Agent nie wykrywał profilu ERP.

## Decyzja
1. `ControlQueryRunner` (port `IMsSqlSession`): jedno zapytanie profilu → wiersze → wyłącznie liczby/daty; błąd, limit 30 s, brak SELECT albo wynik spoza konwencji = zapytanie pominięte (KB-3010, kopia ważna).
2. `MsSqlBackupService`: wykrycie profilu (`DetectErp`, włącza Agent), pomiar przed i po BACKUP, zapis `controls` (limit 30 KiB, inaczej bez kontroli + KB-3010).
3. `MsSqlRestoreService`: te same zapytania na nowej bazie, `ControlVerifier` (tolerancje z profilu); niezgodność = Warning KB-3505, nie błąd (baza i tak jest nową bazą obok produkcji, AC-32.2).
4. Brak tabeli `dbsrc_controls` w katalogu — źródłem prawdy jest `dbMeta` snapshotu (szyfrowany, niezmienny); tabela z planu zostaje do czasu, gdy UI będzie potrzebować historii (koszt dodania: migracja + zapis w CatalogJobService, ~0,5 osobodnia).

## Alternatywy
Kontrola tylko `DBCC CHECKDB` (nie wykrywa pustej bazy); hashe tabel `CHECKSUM_AGG` (kosztowne na produkcji); kontrola jako błąd restore (fałszywe alarmy przy bazie zmieniającej się w trakcie kopii).

## Konsekwencje i koszt zmiany zdania
Uprawnienia: pełna kontrola wymaga `db_datareader` (wariant minimalny D5.7 ją pomija z KB-3010). Zmiana na „KB-3505 = błąd” to jedna linia w `CatalogRestoreService`. Włączenie kontroli domyślnie dla wszystkich profili `confirmed`; wyłączenie = `DetectErp=false` w CatalogJobService.
