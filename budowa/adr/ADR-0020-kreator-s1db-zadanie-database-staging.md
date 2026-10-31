# ADR-0020 — Kreator S1-db: zadanie „database” z UpsertJob/InitRepository i staging Agenta (BL-40 cz. 4, it. 73)

Status: PROWIZORYCZNIE (Qb31). Realizuje R2.5.2 (S1-db), D5.7, D5.10, AC-01.3, AC-02.3 (część UI), ADR-0018 (sekcja `database`), ADR-0019 (pin jako nazwa).

## Decyzja
- Proto (tylko dodawane pola): `DatabaseSourceSpec { server, auth?, secret_ref?, certificate? (nazwa .cer), name, allow_localhost_untrusted }`
  jako `UpsertJobArgs.database = 7` i `InitRepositoryArgs.database = 8`. Pole problemu walidacji: `"database"`.
- Walidacja źródła w Agencie = ta sama co discovery (`DatabaseDiscoveryService.Validate`: serwer bez protokołu/wstrzyknięcia, spójność auth↔secret_ref, pin w `<stan>/certs`) + nazwa bazy (sysname). Bez połączenia z serwerem.
  `sources` obok `database` → KB-0103 na polu `sources`. Agent bez obsługi baz → KB-7012.
- InitRepository dla `database`: sekcja budowana w Agencie i **parsowana kodem uruchomienia** (`MsSqlSourceDefinition.Parse`) PRZED utworzeniem repozytorium na celu; `job_jobs.type = 'database'`.
- **Staging (Qb31, PROWIZORYCZNIE):** `staging` = `KOBIAN_DB_STAGING`, domyślnie `<stan>/staging` (Agent na hoście SQL — D5.10; na Windows `%ProgramData%\Kobian\staging` z ACL W-24);
  `sqlStaging` = `KOBIAN_DB_SQL_STAGING` (gdy SQL Server widzi katalog pod inną ścieżką). Kreator nie pyta o staging.
- UI: S1 z wyborem „Pliki i foldery / Baza SQL Server” (tylko gdy `IWizardService.SupportsDatabases`), jedna baza na zadanie (definicja v1),
  „Dalej” blokowane lokalnie bez wybranej bazy albo bez prawa kopii (KB-3003 + skrypt uprawnień od razu widoczny), login SQL jako `file:<nazwa>` (jak SFTP — Qb24).

## Alternatywy
- (a) Staging wybierany w kreatorze: elastyczne, ale użytkownik biurowy nie wie, gdzie SQL Server może pisać — błędy KB-3102/ACL; zostaje jako pole zaawansowane później.
- (b) Staging w katalogu tymczasowym systemu: brak kontroli ACL (D5.10 wymaga katalogu z ograniczonym dostępem) — odrzucone.
- (c) Wiele baz w jednym zadaniu: wymaga zmiany definicji v1 (ADR-0005/0018) — odroczone (1 zadanie = 1 baza).

## Konsekwencje
+ Kreator tworzy zadanie bazodanowe end-to-end (discovery → walidacja → repozytorium → zapis → pierwsza kopia przez RunJob).
+ Niepoprawna konfiguracja stagingu kończy się KB-0103 bez śmieci na celu.
− Agent poza Windows bez `KOBIAN_DB_SQL_STAGING` odmawia (świadomie). Prawdziwy SQL Server i ACL stagingu — W-27.
Koszt zmiany zdania: mały — dwie właściwości `RepoInitService` i zmienne środowiskowe; pole w proto dodawane, gdy staging trafi do kreatora.
