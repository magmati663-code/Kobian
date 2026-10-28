# ADR-0018 — Adapter SqlClient i zadanie „database” (MSSQL) w Agencie (BL-40 cz. 2, it. 71)

Status: PROWIZORYCZNIE (Qb29). Realizuje R2.5.1, plan „Połączenie” (5.x, linia ~840), SEC-5.6, SEC-5.1, NFR-07, NFR-11, D5.10.

## Decyzja
- `SqlClientMsSqlSession : IMsSqlSession` na `Microsoft.Data.SqlClient` 7.1.1: jedno połączenie na zadanie, `Encrypt=Mandatory` (opcja `Strict` = TDS 8),
  `ApplicationName = "Kobian Backup"`, `InitialCatalog = master`, bez MARS, `PersistSecurityInfo=false`; zapytania z limitem 60 s, BACKUP/RESTORE `CommandTimeout = 0` + anulowanie tokenem.
- Pin certyfikatu = **ścieżka pliku .cer** (`ServerCertificate`). `TrustServerCertificate` tylko dla localhost i tylko z jawnym `allowLocalhostUntrusted` (zakaz w BannedApiTests poza fabryką).
- Hasło loginu SQL wyłącznie przez `SqlCredential` (nigdy w łańcuchu połączenia), czytane z katalogu sekretów Agenta (`file:<nazwa>`, wpis `backup`: `user` + `password`; ten sam format co SFTP — Qb16) przy każdym otwarciu połączenia; bufor zerowany.
- Błędy: `MsSqlServerException` z samymi numerami (bez tekstu serwera i bez wyjątku wewnętrznego); niepowodzenie TLS → numer zastępczy `-2146893019` → KB-3004 (klasa Security, wygrywa z innymi kodami).
- Serwer z definicji: tylko host/IPv4/[IPv6] z `\instancja` albo `,port`; prefiksy `tcp:/np:/lpc:/admin:` odrzucane (bez DAC i potoków), znaki `;'=` i spacje odrzucane (wstrzyknięcie do łańcucha połączenia).
- Zadanie `job_jobs.type = 'database'` (DDL planu) z sekcją `"database"` (silnik `mssql`); snapshot w trybie `Database` (klucz 14 `dbMeta`), baza dedup = poprzedni snapshot zadania; ostrzeżenia przygotowania (KB-3103/3301/3401) → uruchomienie `Warning` z pierwszym kodem.

## Alternatywy
- (a) Pin jako odcisk SHA-256 w definicji (bez pliku): prostsze przenoszenie, ale SqlClient 7.1 nie ma natywnej opcji odcisku — wymagałby `ServerCertificateCustomValidationCallback`-podobnego obejścia (niedostępne w SqlClient; byłoby własną walidacją TLS).
- (b) Hasło w łańcuchu połączenia z `PersistSecurityInfo=false`: prostsze, ale łańcuch trafia do pul i zrzutów pamięci jako `string`.
- (c) Typ zadania `mssql` zamiast `database`: niezgodny z CHECK w DDL `job_jobs`.
- (d) KB-3101 (kolizja P-01) jako `Skipped`: dziś `Failed` z KB-3101 (uczciwe, ponawiane przez RunExecutor tylko gdy przejściowe) — do decyzji z rolą 7.

## Konsekwencje
+ Ścieżka backup MSSQL → repo kompletna w Agencie (testy na SQL zastępczym; prawdziwy serwer — W-24).
− Pin plikowy wymaga zatwierdzenia certyfikatu w kreatorze (SCR-03 S1-db — jeszcze nie ma) i zapisu .cer w katalogu stanu.
− `SecureString` (wymóg `SqlCredential`) — nie szyfruje pamięci poza Windows; akceptowane (hasło i tak w pamięci procesu na czas połączenia).
Koszt zmiany zdania: mały — opcje połączenia w jednym rekordzie (`MsSqlConnectionOptions`), definicja wersjonowana (v1).
