# Źródło MSSQL — przygotowanie kopii bazy (BL-40 cz. 1, it. 70)

`Kobian.Providers.Sources.MsSql.MsSqlBackupService.PrepareAsync` realizuje sekwencję R2.5.1 do momentu przekazania pliku `.bak` potokowi (`StagedFileSource`, tryb `Database`, klucz 14 `dbMeta`).

| Krok | Zapytanie / akcja | Wynik negatywny |
|---|---|---|
| 1 | D-01 wersja (D5.8) | ≤ 2012 → KB-3401 (błąd); 2014/2016 → ostrzeżenie KB-3401 |
| 2 | D-02 baza ONLINE (dokładna nazwa) + `data_bytes` | KB-3005 |
| 3 | P-02 wolne miejsce stagingu ≥ 1,1 × `data_bytes` (port `IStagingVolume`; nieznane = nie blokuje) | KB-3102 |
| 4 | D-03 uprawnienia (AC-02.3) | brak `BACKUP DATABASE` → KB-3003 |
| 5 | TDE (`is_encrypted`) | ostrzeżenie KB-3301 |
| 6 | P-01 kolizje — ponawiane co `ConflictPollInterval` do `ConflictWait` (domyślnie 30 min, AC-02.2) | KB-3101 (pominięcie) |
| 7 | P-03b `differential_base_guid` przed | — |
| 8 | katalog `staging\<runId>\` (+ ACL przez `IStagingAcl`, gdy podano SID usługi SQL) | istniejący katalog → KB-0001 |
| 9 | `BACKUP DATABASE … COPY_ONLY, CHECKSUM, FORMAT, INIT` (szablon z parametrami) | numery błędów → `SqlErrorMap` (KB-3001/3002/3003/3201…) |
| 10 | zestaw w msdb **po urządzeniu** (`BackupSetByDevice`): `type = D`, `is_copy_only = 1`, `has_backup_checksums = 1` | KB-3203 |
| 11 | `RESTORE VERIFYONLY … WITH CHECKSUM` tylko przy `CREATE DATABASE` w master (D5.7) | KB-3202 |
| 12 | P-03b po — zmiana bazy różnicowej w trakcie | ostrzeżenie KB-3103 |

Każda porażka po kroku 8 usuwa plik i katalog uruchomienia. `MsSqlStagedBackup.DisposeAsync` usuwa plik po przesłaniu i ustawia `Removed` (AC-02.1, K16.1). `dbMeta.verify` = `checksum` | `verifyonly` (plakietka UI „V1 SQL”).

Konfiguracja żądania: `Database`, `StagingRoot` (widok Agenta), `SqlStagingRoot` (widok SQL Server, absolutna ścieżka Windows; domyślnie = `StagingRoot`), `RunId`, `Profile` (`LowImpact`: BUFFERCOUNT 8, MAXTRANSFERSIZE 1 MiB), `ConflictWait`, `ConflictPollInterval`, `SqlServiceSid`.

Bezpieczeństwo: nazwa bazy i ścieżka wyłącznie jako parametry (SEC-5.1); komunikaty wyjątków i ostrzeżeń bez nazw baz i ścieżek (NFR-11); `MsSqlServerException` przenosi tylko numery błędów.

## Połączenie (BL-40 cz. 2, it. 71 — ADR-0018, PROWIZORYCZNIE Qb29)

`SqlClientMsSqlSession` (Microsoft.Data.SqlClient 7.1.1) z `MsSqlConnectionOptions`:

| Opcja | Domyślnie | Uwagi |
|---|---|---|
| `Server` | — | `host`, `host\instancja`, `host,port`, `[IPv6]`; bez `tcp:/np:/lpc:/admin:` i bez `;'=`/spacji (KB-0103) |
| `Authentication` | `Integrated` (konto usługi) | `SqlLogin`: login i hasło z magazynu sekretów, hasło tylko przez `SqlCredential` |
| `ServerCertificatePath` | brak (łańcuch systemowy) | pin: plik .cer zatwierdzony w kreatorze; niezgodność → KB-3004 |
| `AllowLocalhostUntrusted` | `false` | tylko localhost (`.`, `localhost`, `127.0.0.1`, `[::1]`) — `TrustServerCertificate` |
| `Strict` | `false` (`Encrypt=Mandatory`) | `true` = TDS 8 (SQL 2022+) |
| `ConnectTimeoutSeconds` / `QueryTimeoutSeconds` | 15 / 60 | BACKUP/RESTORE bez limitu, przerwanie anulowaniem |

Zawsze: `ApplicationName = "Kobian Backup"` (NFR-07 — widoczne w `sys.dm_exec_sessions`), `InitialCatalog = master`, bez MARS, `PersistSecurityInfo = false`. Parametry poleceń przez `SqlParameterMapper` (typy jawne, bez `AddWithValue`). Błędy: tylko numery (`MsSqlServerException`), mapowanie `SqlErrorMap` (TLS → KB-3004, klasa Security).

## Zadanie „database” w Agencie

`job_jobs.type = 'database'`, definicja v1 (patrz `docs/agent-job-definition.md`) z sekcją `"database"`; `CatalogJobService` → `MsSqlBackupService` → `StagedFileSource` (`database.bak`) → `BackupPipeline` (tryb `Database`, `dbMeta`) → V2 → usunięcie stagingu (zawsze). Ostrzeżenia przygotowania → uruchomienie `Warning` (np. KB-3401 dla SQL 2014/2016). Login SQL: `secretRef` `file:<nazwa>` → `<sekrety>/<nazwa>.json` `{"backup":{"user":"…","password":"…"}}` (uprawnienia tylko właściciela).

## Discovery dla kreatora S1-db (it. 72, BL-40 cz. 3)

`MsSqlDiscoveryService` (Providers.Sources) wykonuje wyłącznie zapytania odczytu D-01 → D-02 → D-03 (per baza, `sp_executesql` + `QUOTENAME` po stronie serwera). Niczego nie wykonuje na serwerze (zero `ExecuteAsync`).

| Sytuacja | Wynik |
|---|---|
| wersja < 2014 | porażka KB-3401 (przed listą baz) |
| 2014/2016 | wynik + ostrzeżenie serwera KB-3401 (`support = conditional`) |
| baza z TDE | ostrzeżenie bazy KB-3301 |
| FULL i `log_reuse_wait_desc = LOG_BACKUP` | ostrzeżenie bazy KB-3105 |
| brak BACKUP DATABASE / brak dostępu do bazy (916, 4060, 229…) | `can_backup = false` + KB-3003 (discovery trwa dalej) |
| zerwane połączenie / TLS w trakcie D-03 | porażka całego discovery (KB-3001 / KB-3004) — nie „fałszywy brak uprawnień” |
| > 500 baz | pierwsze 500 wg nazwy, `truncated = true` (limit SEC-3.3) |
| nazwa spoza reguły sysname | pominięta |

Agent: `DatabaseDiscoveryService` waliduje żądanie PRZED połączeniem (KB-0103): `auth` = `integrated` (bez `secret_ref`) albo `sql` (wymagany `secret_ref`), serwer wg `MsSqlConnectionFactory`, pin certyfikatu wyłącznie jako **nazwa** pliku `.cer` w `<stan>/certs` (`^[A-Za-z0-9][A-Za-z0-9_.-]{0,63}\.cer$`, plik musi istnieć) — PROWIZORYCZNIE Qb30/ADR-0019. Dla uwierzytelnienia Windows i baz bez uprawnień odpowiedź zawiera skrypt `PermissionScript` (wariant minimalny, login `NT SERVICE\KobianAgent`) do uruchomienia przez administratora SQL. RPC: `DiscoverDatabases` (Operator, niemodyfikujące); UI: `IWizardService.DiscoverDatabasesAsync`. Test na prawdziwym SQL Server: W-26.

Brak (następne części BL-40): ACL stagingu na Windows, zapytania kontrolne V4 (D5.11), łańcuch P-03/P-04 z `dbsrc_chain`, restore bazy (`RESTORE … WITH MOVE`). Test na prawdziwym SQL Server: W-23, W-24.

## Kreator S1-db → zadanie „database” (it. 73, BL-40 cz. 4 — ADR-0020, PROWIZORYCZNIE Qb31)
- S1: „Pliki i foldery / Baza SQL Server” (wybór widoczny, gdy Agent obsługuje bazy). Serwer, logowanie (konto usługi albo login SQL = nazwa poświadczenia w katalogu sekretów Agenta),
  opcjonalnie nazwa zatwierdzonego pliku `.cer` → „Pokaż bazy” (DiscoverDatabases, tylko odczyt) → wybór jednej bazy.
- Baza bez prawa kopii: „Dalej” pokazuje KB-3003 i od razu rozwija skrypt uprawnień dla administratora (D5.7). Zmiana serwera/logowania/certyfikatu czyści listę.
- Zapis: UpsertJob(validate_only) i InitRepository z polem `database`; staging Agenta z `KOBIAN_DB_STAGING` (domyślnie `<stan>/staging`), `KOBIAN_DB_SQL_STAGING` gdy SQL Server widzi go pod inną ścieżką.
- Zrzuty: `docs/ui/SCR-03-S1db-*.png`. Test na Windows z prawdziwym SQL Server: W-27.

## Zatwierdzenie certyfikatu serwera SQL (it. 74, BL-40 cz. 5 — ADR-0021, PROWIZORYCZNIE Qb32)
Pin z Qb30 wymaga pliku `.cer` w `<stan>/certs`; od it. 74 tworzy go Agent, nie administrator ręcznie.

1. **Pobranie** (`TdsCertificateFetcher`, Providers.Sources/MsSql/Tls): TCP → PRELOGIN (`ENCRYPTION = ON`) → handshake TLS w pakietach PRELOGIN (`TdsPreloginStream`, MS-TDS 2.2.6.5); `strict` = TDS 8, TLS od pierwszego bajtu z ALPN `tds/8.0`. Instancja nazwana bez portu → port TCP z SQL Browser (SSRP, UDP 1434, `CLNT_UCAST_INST`). Callback walidacji zapamiętuje certyfikat liścia i **zawsze odrzuca** połączenie — LOGIN7 nigdy nie jest wysyłany. Limit 15 s.
2. **Pokazanie**: odcisk SHA-256 (DER) w grupach po 4 znaki, podmiot, wystawca, ważność, „samopodpisany”, „zaufany w systemie” (łańcuch + nazwa hosta, bez CRL/OCSP).
3. **Zatwierdzenie** (`ServerCertificateService.ApproveAsync`): Agent pobiera certyfikat **ponownie**, porównuje odcisk stałoczasowo z tym, który zatwierdził Operator, i zapisuje DER do `<stan>/certs/<nazwa>.cer` (plik tymczasowy + `Move` bez nadpisania). Klient nigdy nie przesyła bajtów certyfikatu.

| Sytuacja | Kod |
|---|---|
| brak połączenia / limit czasu / SQL Browser bez portu TCP | KB-3001 |
| serwer bez TLS (`ENCRYPT_NOT_SUP`), usługa nie-TDS | KB-3006 |
| certyfikat zmienił się między pokazaniem a zatwierdzeniem | KB-3007 (nic nie zapisano) |
| nazwa zajęta przez inny certyfikat | KB-3008 (ten sam certyfikat pod tą samą nazwą = OK, idempotentnie) |
| zły serwer / nazwa pliku / odcisk | KB-0103 (przed połączeniem) |

Odnowienie certyfikatu na serwerze: zatwierdź nowy pod nową nazwą (proponowana nazwa zawiera 8 znaków odcisku) i zmień zadanie; stary plik zostaje do usunięcia przez administratora. Test na prawdziwym SQL Server (TDS 7.4 i TDS 8, instancja nazwana): W-28.


## Lista dozwolonych serwerów SQL (it. 78, RD-39, ADR-0023)
Agent łączy się wyłącznie z serwerami z `KOBIAN_SQL_ALLOWED_SERVERS` (zmienna środowiskowa usługi; wpisy `host`, `host\instancja`, `host:port` rozdzielone `;`; `*` wyłącza ochronę). Bez konfiguracji — tylko serwer na tym samym komputerze (`localhost`, `.`, `127.0.0.1`, `::1`). Serwer spoza listy: KB-3009 przy wyszukiwaniu baz, pobieraniu certyfikatu, zapisie zadania i każdym uruchomieniu — zanim zostanie nawiązane połączenie. Nazwy porównywane są dosłownie (bez DNS): wpisz serwer tak, jak w zadaniu.

## Odtwarzanie bazy (it. 83, SCR-08 — warstwa źródła)
`MsSqlRestoreService` odtwarza plik `.bak` (już w stagingu na hoście SQL) do **nowej** bazy: domyślnie `<źródło>_restore_RRRRMMDD` (AC-32.1). Kolejność: istnienie bazy docelowej (istnieje → KB-3501, bez REPLACE), uprawnienie CREATE ANY DATABASE (KB-3502), `RESTORE VERIFYONLY … WITH CHECKSUM`, `RESTORE FILELISTONLY` → plan MOVE do domyślnych katalogów serwera (KB-3504 gdy nieznane; FILESTREAM/full-text → KB-3503), `RESTORE DATABASE @db FROM DISK = @path WITH MOVE @lN TO @pN …, CHECKSUM, RECOVERY` (nazwy wyłącznie jako zmienne T-SQL), na końcu `TRUSTWORTHY OFF`. Nadpisanie bazy produkcyjnej (AC-32.2, step-up Administratora) — nieobsługiwane (Qb39). Brakuje jeszcze: pobrania `.bak` z repozytorium do stagingu w Agencie, RPC i ekranu SCR-08.

## Zapytania kontrolne ERP (it. 123, BL-40 — D5.11, SEC-15.3, ADR-0039, PROWIZORYCZNIE Qb103)

Przy kopii bazy Agent wykrywa profil ERP po schemacie (`ErpDetector.BuildDatabaseQuery`, metadane `sys.tables` wskazanej bazy, nazwa bazy jako parametr + `QUOTENAME` po stronie serwera) i — dla profili z `controlQueries` (np. `insert-gt`, `comarch-optima-config`) — wykonuje zapytania kontrolne **przed** i **po** `BACKUP` (`READ COMMITTED`, `LOCK_TIMEOUT 5000`, 30 s na zapytanie). Wyniki trafiają do klucza 14 snapshotu jako `erpProfile` i `controls` (JSON `[[zapytanie, klucz, przed, po|null], …]`). Zapisywane są wyłącznie **liczby całkowite i daty UTC**; wynik z kolumną tekstową, bez kolumny `n`, z NULL w `n`, z liczbą niecałkowitą, ze zdublowanym kluczem grupy albo z > 256 wierszami powoduje pominięcie całego zapytania. Konwencja wyniku: kolumna `n` (liczba), opcjonalnie `last` (data), pozostałe kolumny = klucz grupy.

Odtwarzanie bazy (`RestoreDatabase`) po `RESTORE` i `TRUSTWORTHY OFF` uruchamia te same zapytania **na nowej bazie** i porównuje z tolerancją z profilu (`range` — odtworzone ≥ przed i ≤ max(przed, po); `exact`; `approx` ±0,1%; daty — maksimum ≥ wzorzec). Niezgodność = uruchomienie `Warning` z **KB-3505** (baza pozostaje utworzona pod nową nazwą; w wyniku tylko identyfikatory zapytań, bez wartości).

**Uprawnienia:** wariant minimalny konta (D5.7: `db_backupoperator` + `VIEW SERVER STATE`) **nie ma** `SELECT` na tabelach ERP — zapytania kontrolne zostaną pominięte, a uruchomienie dostanie ostrzeżenie **KB-3010** (kopia jest ważna). Kontrola logiczna wymaga dodatkowo `db_datareader` (opcjonalnie, decyzja administratora SQL; generator skryptu uprawnień nie dodaje go domyślnie). Wykrywanie profilu przy braku widoczności metadanych kończy się „brak profilu” (bez błędu).
