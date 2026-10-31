# Kolejka testów do uruchomienia na Windows

| ID | Iteracja | Test | Polecenie | Oczekiwany wynik | Status |
|---|---|---|---|---|---|
| W-1 | 4 | Pełny zestaw testów jednostkowych na NTFS (LocalStorageTests: symlink/junction, File.Move bez nadpisania, mapowanie ERROR_DISK_FULL/ERROR_FILE_EXISTS) | w folderze projektu: `dotnet build Kobian.sln -c Release -warnaserror` potem `dotnet tests\Kobian.Tests.Unit\bin\Release\net10.0\Kobian.Tests.Unit.dll` (test `Link_inside_repository_is_refused` wymaga trybu dewelopera lub uprawnień do tworzenia symlinków) | wszystkie testy zielone; 0 ostrzeżeń | do uruchomienia |
| W-2 | 4 | Junction (`mklink /J`) zamiast symlinku w korzeniu repo lokalnego | `mklink /J C:\tmp\repo\data C:\tmp\inny` i zapis przez dostawcę local | `KB-2303`, brak plików w `C:\tmp\inny` | do uruchomienia (test automatyczny — BL następny) |
| W-3 | 5 | Zapytania R2.5.2–R2.5.3 i szablon BACKUP na SQL Server Express 2019/2022 oraz 2014 SP3/2016 SP3 (SP-07) | po implementacji wykonawcy SqlClient (BL-40): test integracyjny `Category=Windows` na instancji testowej z bazą syntetyczną | wyniki D-01…P-03 bez błędów składni; `is_copy_only = 1`, `differential_base_guid` bez zmian (AC-02.1); skrypt uprawnień (wariant minimalny) wystarcza do BACKUP | oczekuje na BL-40 |
| W-4 | 7 | Strefy IANA na Windows z ICU (`InvariantGlobalization=false`): `CronScheduleTests` (Europe/Warsaw, E1/E2) | jak W-1 (cały zestaw jednostkowy) | testy E1/E2/AC-03.1 zielone na Windows 10 22H2 i Server 2016 (ICU systemowe od Win10 1903; na Server 2016 może wymagać app-local ICU — sprawdzić) | do uruchomienia |
| W-5 | 8 | D8.9 R7/R8: adapter `IRestoreTargetProbe` (GetVolumeInformation, FILE_FLAG_OPEN_REPARSE_POINT + GetFinalPathNameByHandle) | po implementacji adaptera w Platform.Windows: testy `Category=Windows` z junction w ścieżce docelowej i wolumenem FAT32 (VHD) | zapis przez junction → KB-5104; plik > 4 GiB na FAT32 → KB-5103 | oczekuje na adapter |

**Skrót dla właściciela (od it. 10):** całą kolejkę automatycznych testów (W-1, W-4) uruchamia `powershell -ExecutionPolicy Bypass -File tools\windows\run-queue.ps1` w folderze projektu; wynik trafia do `budowa\wyniki-windows-<data>.txt`. Wymaga .NET SDK 10.0.401.
| W-6 | 20 | ACL katalogu stagingu D5.10 (`IStagingAcl`): KobianAgent pełne, NT SERVICE\MSSQL$<inst> Read+Write, bez dziedziczenia; BACKUP przez konto SQL do tego katalogu | po implementacji adaptera: test `Category=Windows` na SQL Express z kontem wirtualnym | `icacls` pokazuje wyłącznie 2 wpisy + SYSTEM/Administrators wg D5.10; inne konto nie odczyta pliku .bak | oczekuje na adapter |

## W-9 (it. 38) — kobian-restore na Windows
Polecenie: `dotnet run --project src\Kobian.Restore.Standalone -- restore --repo <repo testowe> --key-file <dev.key> --to C:\Temp\kr-test` na repo wygenerowanym testem `RestoreExecutorTests` (dane syntetyczne). Oczekiwane: kod 0, pliki identyczne (porównanie `Get-FileHash`), nazwy zarezerwowane Windows (CON, NUL) raportowane — wymaga podpięcia `WindowsPathMapper` w adapterze (dziś mapowanie przenośne).

## W-10 (it. 46) — Agent gRPC na named pipe
Polecenie: `$env:KOBIAN_CATALOG="C:\Temp\kobian-test\catalog.db"; dotnet run --framework net10.0-windows10.0.17763.0 --project src\Kobian.Agent` w jednym oknie, w drugim `dotnet run --project src\Kobian.Cli -- status`. Oczekiwane: kod 0, „Kobian Backup <wersja>: Ok”; po zatrzymaniu Agenta `status` → kod 3 w ≤ 5 s; z innego konta użytkownika (pipe CurrentUserOnly) → kod 3 (odmowa). Weryfikuje: Kestrel named pipe + klient NamedPipeClientStream (ścieżka Windows nie jest wykonywana w testach Linux, które używają gniazda Unix). Powiązane: SP-02, BL-17.

## W-11 (it. 47) — RunJob w Agencie na NTFS
Polecenie: w katalogu testowym utwórz klucz (kobian-restore/RepoProvisioner albo plik deweloperski), wpisz wiersz job_jobs z definicją v1 (docs/agent-job-definition.md) i uruchom `kobian job run <nazwa>` przy działającym Agencie (patrz W-10). Oczekiwane: kod 0, wiersz snap_snapshots, drugie uruchomienie = przyrost (mode=1); ścieżki Windows (C:\…) przechodzą walidację JobDefinition (IsPathFullyQualified).


## W-12 (it. 49) — `kobian init` przez named pipe na NTFS
Polecenie: przy działającym Agencie (W-10; `KOBIAN_CATALOG`, opcjonalnie `KOBIAN_STATE_DIR`) utwórz plik z hasłem (np. `C:\Temp\kt\pw.txt`) i uruchom `kobian init Pliki --source C:\Temp\kt\src --dest C:\Temp\kt\repo --password-file C:\Temp\kt\pw.txt`, potem `kobian job run Pliki --wait`, `kobian snapshots`. Oczekiwane: kody 0; w `C:\Temp\kt\repo` pliki `kobian.repo` i `keys\<id>.key`; hasła nie ma w żadnym wyjściu; powtórny init na tym samym `--dest` → kod 2 z KB-1003; z konta bez dostępu do pipe → kod 3.

- W-13 (it. 50, nie wymaga Windows, wymaga sshd): po dodaniu adaptera SSH.NET uruchomić test integracyjny SFTP na realnym OpenSSH (Linux/WSL, `sshd -p 2222`, katalog tymczasowy): backup→restore + AC-04.1/04.2 (pinning). Oczekiwanie: zielone; do czasu adaptera brak polecenia.

## W-13 — adapter SSH.NET na prawdziwym serwerze OpenSSH (it. 51) [nie tylko Windows: wymaga serwera SSH; Docker niedozwolony → serwer właściciela/VM]
Polecenie: skonfiguruj chroot SFTP (`ForceCommand internal-sftp -P remove,rmdir,posix-rename@openssh.com`), odcisk `ssh-keygen -lf /etc/ssh/ssh_host_ed25519_key.pub`, uruchom backup→restore z celem `sftp` i `hostkey=SHA256:…`. Oczekiwane: sukces; zły odcisk → KB-2104; LSTAT nie podąża za dowiązaniem (Get); RenameNoReplace nie nadpisuje; CreateNew daje błąd przy istniejącym pliku (mapowanie SshException Failure → obecnie Transient — sprawdzić).

## W-14 (it. 52) — katalog sekretów SFTP: ACL na Windows
Polecenie: utwórz `%ProgramData%\Kobian\secrets` z ACL tylko dla konta Agenta (`icacls <dir> /inheritance:r /grant "NT SERVICE\KobianAgent:(OI)(CI)F"`), plik `nas.json` wg ADR-0007, uruchom Agenta z `KOBIAN_SECRETS_DIR` i backup na cel `sftp` (SecretRef `file:nas`, opcja `hostkey`). Oczekiwane: połączenie działa; inne konto nie odczyta pliku. Kod nie sprawdza ACL na Windows (BL-17) — to test ręczny.

## W-15 (it. 53) — restore w Agencie: Windows
Polecenie: uruchom Agenta, wykonaj `kobian init` + `kobian job run <nazwa> --wait`, potem `kobian restore --snapshot latest --to C:\Temp\restore-test`. Oczekiwane: kod wyjścia 0, pliki bajt w bajt (`fc /b`), drugie uruchomienie na tym samym celu → kod 1 i KB-5105 bez nadpisania; ścieżka względna → KB-5001. Nazwy niedozwolone w Windows (R1–R9, `WindowsPathMapper`) nadal nie podpięte do RestoreExecutor.

## W-16 (it. 54) — wyzwalacze czasowe: Windows
Polecenie: uruchom Agenta (`KOBIAN_CATALOG=…`), wstaw zadanie i wyzwalacz `{"cron":"*/5 * * * *"}` ze strefą `Europe/Warsaw` (identyfikatory IANA wymagają ICU na Windows 10 22H2/Server 2016 — sprawdź `TimeZoneInfo.FindSystemTimeZoneById("Europe/Warsaw")`), odczekaj do kolejnej piątej minuty. Oczekiwane: wiersz `run_runs` z `cause='schedule'`, stan `Succeeded`; po `net stop`/`net start` usługi bez zmiany harmonogramu brak duplikatu tego samego `scheduled_for_utc`. Wybudzanie z uśpienia i zmiana czasu systemowego (KB-4201) — test ręczny.

## W-17 (it. 57) — UI Kobian na Windows: start, pulpit, połączenie z Agentem
- Polecenie: `dotnet run --project src/Kobian.UI -c Release` przy działającym Agencie (`dotnet run --project src/Kobian.Agent`), potem bez Agenta.
- Oczekiwane: okno 1180×780, pulpit z zadaniami z katalogu (stan zgodny z `kobian status`, AC-37.1); bez Agenta — „Usługa Kobian nie działa” KB-9001 i „Spróbuj ponownie” w ≤ 5 s; skala 200% — nawigacja zwinięta do ikon; NVDA/Narrator czyta stan („Zagrożone”…) i nazwy pozycji nawigacji.
- Do zrobienia po stronie kodu: weryfikacja tożsamości serwera pipe (PID = SCM, D2.9/KB-7004) przed pierwszym wywołaniem.

## W-18 (it. 59) — kreator SCR-03 na Windows: klucz odzyskiwania i dostępność
- Polecenie: `dotnet test`-owy przebieg `Kobian.Tests.UI` na Windows + ręcznie: uruchom UI z adapterem kreatora (po BL-60), przejdź S1–S6; na ekranie klucza zrób zrzut ekranu (Win+Shift+S) i nagranie (Game Bar).
- Oczekiwane: okno klucza wykluczone z przechwytywania (`SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)`, D6.9) — na zrzucie czarny prostokąt; skopiowany klucz znika ze schowka po 60 s (AC-28.1); skala 200% i 1366×768 — brak obcięć i poziomego przewijania (AC-UX.3); Narrator czyta nazwy pól („Hasło”, „Grupa 3”) i komunikaty walidacji (LiveSetting).
- Do zrobienia po stronie kodu: wybór folderu przez `StorageProvider.OpenFolderPickerAsync` (S1/S2), osobne okno SCR-12 dla klucza.

## W-19 (it. 60) — katalog sekretów Agenta: dowiązania i nazwy urządzeń na Windows
Polecenie (PowerShell jako właściciel katalogu): `dotnet tests\Kobian.Tests.Unit\bin\Release\net10.0\Kobian.Tests.Unit.dll -class Kobian.Tests.Unit.Agent.FileSftpCredentialProviderTests`, następnie ręcznie: w `KOBIAN_SECRETS_DIR` utwórz `mklink ln.json real.json` (symlink) oraz `mklink /J` katalog-junction i wywołaj `kobian init ... --dest-scheme sftp` z `file:ln`.
Oczekiwane: wszystkie testy zielone; Agent odmawia (AuthRejected, „must not be a link”), bez treści sekretu w komunikacie i logu; `file:CON` odrzucone jako nieprawidłowa referencja.

## W-20 (it. 62) — sonda tożsamości SFTP na prawdziwym serwerze (BL-60, D4.11)
Polecenie: Agent z celem testowym OpenSSH (VM/serwer testowy, NIE produkcyjny); `ProbeDestination` z `scheme=sftp, host=<test>, root=/kopie` bez `host_key`.
Oczekiwane: status UNKNOWN_IDENTITY, odcisk = `ssh-keygen -lf /etc/ssh/ssh_host_ed25519_key.pub` (SHA256), w logu serwera BRAK próby uwierzytelnienia (tylko kex); z poprawnym `host_key` i `secret_ref` → OK; z innym `host_key` → FAILED KB-2104 + odcisk.

## W-21 (it. 63) — ochrona pliku hasła repozytorium w stanie Agenta (Qb24, ADR-0013)
Polecenie: usługa na koncie wirtualnym, kreator z UI na koncie użytkownika (grupa Kobian Operators) → InitRepository. `icacls "%ProgramData%\Kobian\secrets\repo-*.pw"`.
Oczekiwane: dostęp wyłącznie SYSTEM + konto usługi (bez Users/Administrators w odczycie poza przejęciem własności); docelowo treść chroniona DPAPI (LocalMachine + entropia) — do implementacji przed M1.

## W-22 (it. 68) — prune wykonawczy na NTFS/SMB z poświadczeniem Maintenance (ADR-0016)
Polecenie: zadanie z celem lokalnym NTFS i (osobno) udziałem SMB; 3 kopie, `kobian forget` usuwające 2 najstarsze (polityka keepLast=1), `kobian prune Pliki` (oczekiwane: OK, `marked > 0`, nic nie usunięte, `locks\` bez pliku wyłącznej), przestawienie zegara VM o +25 h, ponownie `kobian prune Pliki`, potem `kobian check Pliki --sample 100` i `kobian restore --snapshot latest --to C:\t`.
Oczekiwane: druga próba usuwa snapshoty/indeksy/packi (deleted > 0), check OK, restore bit w bit; `kobian prune` w trakcie trwającej kopii → kod 1, KB-1202; pliki w `locks\` mają ACL dziedziczone z repozytorium (bez Everyone).

## W-23 (it. 70) — MsSqlBackupService na prawdziwym SQL Server (BL-40, AC-02.1–02.3)
Warunek: adapter SqlClient (BL-40 cz. 2). Polecenie: SQL Express 2022 na VM, syntetyczna baza `KobianTest` (SIMPLE, ~50 MB), login z `db_backupoperator` + `VIEW SERVER STATE` (wariant minimalny), potem dodatkowo `CREATE DATABASE` w master; `MsSqlBackupService.PrepareAsync` ze stagingiem `C:\ProgramData\Kobian\staging`; w trakcie drugiego przebiegu uruchom `DBCC CHECKDB` w SSMS.
Oczekiwane: msdb `is_copy_only = 1`, `has_backup_checksums = 1`; `differential_base_guid` bez zmian; `dbMeta.verify` = `checksum`, a z CREATE DATABASE = `verifyonly`; z DBCC: oczekiwanie, potem KB-3101 po `ConflictWait`; login bez uprawnień → KB-3003; po DisposeAsync katalog `staging\<runId>` nie istnieje.

## W-24 (it. 71) — adapter SqlClient + zadanie „database” na prawdziwym SQL Server (BL-40 cz. 2, SEC-5.6, ADR-0018)
Polecenie: SQL Express 2022 na VM (certyfikat self-signed), baza syntetyczna `KobianTest`; zadanie `database` (definicja z docs/agent-job-definition.md) z `auth: integrated` (konto usługi z PermissionScript) i osobno `auth: sql` (`file:erp-sql`); (1) bez `certificate` z nazwą hosta → oczekiwane KB-3004 (łańcuch niezaufany); (2) z wyeksportowanym .cer → kopia OK; (3) z .cer innego serwera → KB-3004; (4) `localhost` + `allowLocalhostUntrusted` → OK; (5) SQL 2016 SP3 z TLS 1.2 → OK z ostrzeżeniem KB-3401 (SP-07); (6) `kobian run` przerwany Ctrl+C w trakcie BACKUP → `sys.dm_exec_requests` bez sesji „Kobian Backup” po ≤ 5 s, staging pusty.
Oczekiwane: `sys.dm_exec_sessions.program_name = 'Kobian Backup'`, `encrypt_option = TRUE` w `sys.dm_exec_connections`; logi Agenta bez nazwy bazy/serwera/hasła; snapshot `mode = 3`, `kobian-restore restore` odtwarza `database.bak` bit w bit (SHA-256 = plik ze stagingu przed usunięciem — zapis kontrolny w teście); hasło nieobecne w zrzucie `procdump -ma` poza czasem otwierania połączenia (best effort).

## W-25 (it. 71) — minimalny runtime .NET 10.0.13 na maszynie docelowej
Polecenie: na VM z runtime 10.0.12 uruchom Agenta (framework-dependent) i instalator (gdy powstanie).
Oczekiwane: instalator/bootstrapper wymaga lub dołącza runtime ≥ 10.0.13 (Microsoft.NETCore.App i Microsoft.AspNetCore.App); Agent uruchomiony na 10.0.12 — do decyzji: odmowa startu z KB albo ostrzeżenie w logu (dziś brak kontroli w produkcie, tylko w testach).

## W-26 (it. 72) — discovery MSSQL (DiscoverDatabases) na prawdziwym SQL Server (BL-40 cz. 3, ADR-0019)
Środowisko: SQL Server Express/Developer 2019+ z 3 bazami użytkownika (jedna z TDE, jedna w modelu FULL bez kopii logów, jedna bez uprawnień dla konta usługi), Agent jako usługa `NT SERVICE\KobianAgent`.
Polecenie: uruchom Agenta, w PowerShell klient gRPC (lub kreator, gdy powstanie S1-db) → `DiscoverDatabases { server: "localhost\\SQLEXPRESS" }`; potem z `auth:"sql"`, `secret_ref:"file:erp-sql"`; potem z `certificate:"srv.cer"` (plik w `<stan>\certs`) i z celowo innym certyfikatem.
Oczekiwane: lista 3 baz z `can_backup` zgodnym ze stanem; KB-3301 przy bazie TDE; KB-3105 przy FULL z LOG_BACKUP; skrypt uprawnień zawiera tylko bazę bez uprawnień; zły pin → `code = KB-3004`; `sys.dm_exec_sessions.program_name = 'Kobian Backup'`; w Profilerze/XEvents tylko SELECT/sp_executesql (żadnych CREATE/GRANT/BACKUP).

## W-27 (it. 73) — kreator S1-db → zadanie „database” → pierwsza kopia na prawdziwym SQL Server (BL-40 cz. 4, ADR-0020)
Środowisko jak W-26 (SQL Express 2019+, 3 bazy, Agent jako usługa `NT SERVICE\KobianAgent`), bez `KOBIAN_DB_STAGING` (domyślny `%ProgramData%\Kobian\staging`), UI Kobian.UI na tym samym hoście.
Polecenie: Nowe zadanie → „Baza SQL Server” → serwer `localhost\SQLEXPRESS` → „Pokaż bazy” → wybierz bazę bez uprawnień i „Dalej” (oczekuj KB-3003 + skrypt) → wykonaj skrypt w SSMS → „Pokaż bazy” → wybierz bazę → cel lokalny `E:\KopieKobian` → harmonogram → hasło → klucz odzyskiwania → zapis z „Uruchom teraz”.
Oczekiwane: `job_jobs.type = 'database'`, sekcja `database` z `staging` = `C:\ProgramData\Kobian\staging`; pierwsza kopia Succeeded (snapshot trybu Database z `database.bak`); staging pusty po kopii (AC-02.1); ACL stagingu: tylko SYSTEM, Administratorzy, konto usługi Agenta i konto usługi SQL (D5.10); w XEvents brak poleceń modyfikujących poza BACKUP … COPY_ONLY.

## W-28 (it. 74) — pobranie i zatwierdzenie certyfikatu prawdziwego SQL Server (BL-40 cz. 5, ADR-0021)
Środowisko: SQL Server 2019 Express (`localhost\SQLEXPRESS`, SQL Browser włączony, certyfikat samopodpisany przypisany w Configuration Manager) + SQL Server 2022 (instancja domyślna, „Force Strict Encryption” = tak); Agent jako usługa.
Polecenie: kreator → „Baza SQL Server” → `localhost\SQLEXPRESS` → „Pobierz certyfikat serwera”; na serwerze `$c = Get-Item Cert:\LocalMachine\My\<odcisk SHA-1>; [BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash($c.RawData)) -replace '-'` → porównaj → „Zatwierdź odcisk” → „Pokaż bazy”. Powtórz dla SQL 2022 z `strict` (TDS 8) oraz dla `host,port`. Następnie zmień certyfikat na serwerze i uruchom zadanie.
Oczekiwane: odcisk w UI = wynik PowerShell; plik `%ProgramData%\Kobian\certs\<nazwa>.cer` (DER) zgodny; w XEvents/`sys.dm_exec_sessions` brak sesji logowania z pobrania certyfikatu (tylko PRELOGIN); discovery z pinem działa; po zmianie certyfikatu kopia kończy się KB-3004; serwer z wyłączonym TLS → KB-3006; TLS 1.3 (Windows Server 2022+) — pobranie działa.

## W-29 (it. 77, BL-64) — odtwarzanie z UI do folderu wybranego oknem systemowym
- Polecenie: uruchom Agenta jako usługę (konto wirtualne), w Kobian.UI wybierz kopię → „Odtwórz ten folder…” → „Wybierz…” → `D:\Odtworzone` (istniejący), „Odtwórz”; powtórz do folderu, do którego konto usługi NIE ma prawa zapisu.
- Oczekiwane: (1) pliki w `D:\Odtworzone\<ścieżka z kopii>`, wynik „Odtworzono”; (2) brak prawa → wynik „błąd” z KB-5001, żadnych plików; (3) ponowne odtworzenie → „z uwagami” KB-5105, pliki nienadpisane.

## W-30 (it. 79, RD-42) — SID wywołującego w audycie
- Polecenie: Agent jako usługa; z konta użytkownika A wywołaj `kobian schedule set X --cron "0 21 * * *"`, potem `sqlite3 catalog.db "select actor_sid, operation from audit_log"`.
- Oczekiwane (po implementacji impersonacji potoku): actor_sid = SID użytkownika A, nie „local-pipe”; `kobian audit verify` → ok.

## W-31 (it. 83, BL-68) — RESTORE do nowej bazy na prawdziwym SQL Server
- Polecenie: na SQL Server 2019/2022 Developer wykonaj kopię testowej bazy `KobianTest` przez zadanie `database`, skopiuj `.bak` do stagingu i uruchom `MsSqlRestoreService` (test integracyjny `Category=Windows`, gdy powstanie) dla celu `KobianTest_restore_<data>`.
- Oczekiwane: nowa baza ONLINE z plikami `<cel>.mdf`/`<cel>_log.ldf` w domyślnych katalogach, `TRUSTWORTHY = 0`, `KobianTest` nietknięta; drugie uruchomienie → KB-3501; konto bez dbcreator → KB-3502.

## W-32 (it. 86, RD-44) — fuzzing gRPC przez named pipe
- Polecenie: `dotnet tests\Kobian.Tests.Unit\bin\Release\net10.0-windows…\Kobian.Tests.Unit.dll -class Kobian.Tests.Unit.Security.GrpcFuzzTests` po dodaniu kanału named pipe w teście (dziś pomijany na Windows).
- Oczekiwane: jak na Linuksie — tylko dozwolone statusy, host odpowiada po serii.

## W-33 (it. 91, BL-72) — Agent jako usługa Windows (SCM, konto wirtualne, pojedyncza instancja, Event Log)
- Polecenie (Administrator): `dotnet publish src\Kobian.Agent -f net10.0-windows10.0.17763.0 -c Release -o C:\Kobian`; `New-EventLog -LogName Application -Source KobianAgent`; `mkdir C:\ProgramData\Kobian` (ACL wg docs/admin/usluga-windows.md); polecenia `sc.exe` z `AgentServiceProfile.ScCommands("C:\Kobian\Kobian.Agent.exe")` (docs/admin/usluga-windows.md); `sc.exe start KobianAgent`; potem w konsoli Administratora `$env:KOBIAN_CATALOG='C:\ProgramData\Kobian\catalog.db'; C:\Kobian\Kobian.Agent.exe; $LASTEXITCODE`; `Stop-Process -Name Kobian.Agent -Force` i obserwacja SCM; `sc.exe qprivs KobianAgent`, `sc.exe qfailure KobianAgent`.
- Oczekiwane: usługa RUNNING jako `NT SERVICE\KobianAgent`, `catalog.db` w C:\ProgramData\Kobian; druga instancja z konsoli kończy się w ≤ 2 s kodem 75, stderr i Event Log (Application, źródło KobianAgent, ID 7007) z KB-7007, suma `catalog.db` bez zmian; po zabiciu procesu SCM restartuje usługę w ≤ 60 s (NFR-09); `qprivs` = 4 uprawnienia z D2.12; `qfailure` = 60000/60000/300000, reset 86400. SEC-22.4: utworzenie mutexu `Global\Kobian.Agent.Instance` z konta zwykłego użytkownika przed startem usługi → dziś (domyślne ACL) zapisać zachowanie; docelowo KB-7007 + alarm Critical.

## W-34 (it. 91, BL-72) — ACL pipe Agenta w trybie usługi (D2.9, D2.15 pkt 1)
- Polecenie: usługa z W-33 działa; (1) z konsoli Administratora (podniesionej) `kobian status`; (2) ze zwykłego konta użytkownika (spoza „Kobian Operators”) `kobian status`; (3) po `net localgroup "Kobian Operators" /add` + dodaniu użytkownika — ponownie (2) po wylogowaniu; (4) z drugiej maszyny: `[System.IO.Pipes.NamedPipeClientStream]::new('<host>','Kobian.Agent.v1','InOut').Connect(3000)` jako administrator domeny; (5) `accesschk.exe -v \pipe\Kobian.Agent.v1` (Sysinternals).
- Oczekiwane: (1) i (3) działają (rola Operator — PROWIZORYCZNIE Qb41); (2) odmowa dostępu do pipe (CLI: Agent niedostępny, kod 3); (4) odmowa (Deny NETWORK); (5) Deny NETWORK pierwszy, klienci bez CreateNewInstance, brak Users/Everyone.

## W-35 (it. 92, BL-73) — natywny cel SMB (WNetAddConnection2 bez litery dysku)
- Środowisko: Windows Server 2022 z udziałem `\\srv\kopie` (SMB 3.1.1, podpisywanie wymagane), konta `FIRMA\kobian-bk` (Create files/folders, BEZ Delete) i `FIRMA\kobian-mt`; Agent jako usługa (W-33); plik sekretu `%ProgramData%\Kobian\secrets\nas.json` wg docs/admin/cel-smb.md.
- Polecenie: test integracyjny `Category=Windows` (do dopisania przy BL-73 cz. 2) albo `kobian` → ProbeDestination `smb` `\\srv\kopie\repo1` `file:nas`; w trakcie: `net use` w sesji interaktywnej, `Get-SmbConnection` jako Administrator; po zakończeniu ponownie `Get-SmbConnection`. Powtórz z błędnym hasłem, z kontem zablokowanym, z nieistniejącym udziałem, z serwerem Samba bez podpisywania, z serwerem tylko-SMB1, z adresem IP przy zablokowanym NTLM.
- Oczekiwane: probe OK; brak wpisu w `net use` użytkownika (AC-04.3); po zakończeniu brak połączenia w `Get-SmbConnection`; błędne hasło → KB-2102 po dokładnie jednej próbie (dziennik Security serwera: 1 zdarzenie 4625); zablokowane → KB-2103; brak udziału → KB-2106; Samba bez podpisywania / SMB1 / IP przy NTLM off — ZAPISZ kody Win32 (SP-22, dziś mapowane ogólnie); usunięcie pliku poświadczeniem backup → KB-2105.

## W-36 (it. 94, BL-73 cz. 3) — watchdog łączenia SMB i kolejka D7.4 na prawdziwym Windows (D4.13, E6)
- Środowisko: jak W-35 (Agent jako usługa, udział `\\srv\kopie`, wpisy sekretów `nas-bk.json` i `nas-inny.json` z RÓŻNYMI kontami).
- Polecenie: (1) ProbeDestination `smb` `\\nieistniejacy-host-xyz\kopie` `file:nas-bk` oraz `\\10.255.255.1\kopie` (adres niedostępny — blackhole); zmierz czas odpowiedzi; (2) dwa zadania z harmonogramem na tę samą minutę do `\\srv\kopie\a` (`file:nas-bk`) i `\\srv\kopie\b` (`file:nas-inny`); po zakończeniu `kobian status` / run_runs; (3) jak (2), ale oba z `file:nas-bk`; w trakcie `Get-SmbConnection`.
- Oczekiwane: (1) odpowiedź ≤ ~30 s z KB-2001 (lub szybciej KB-2106 dla nieistniejącej nazwy), usługa dalej odpowiada; po powrocie wywołania brak pozostawionego połączenia w `Get-SmbConnection`; (2) oba Succeeded, drugie wystartowało po zakończeniu pierwszego, brak KB-2101; (3) oba równolegle, jedno połączenie SMB.

## W-37 (it. 95, BL-17) — role z tokenu klienta pipe (SP-02, D2.9; uzupełnia W-17/W-30)
- Środowisko: Agent jako usługa (W-33), grupy lokalne `Kobian Operators` i `Kobian Admins`; konta: A (Administrators), B (Kobian Operators), C (Kobian Admins, bez Administrators), D (zwykły użytkownik).
- Polecenie: z każdego konta `kobian status`, `kobian schedule set X --cron "0 21 * * *"`, `kobian retention release` (Administrator); A dwukrotnie — konsola zwykła i „Uruchom jako administrator”; z drugiej maszyny `\\host\pipe\Kobian.Agent.v1` (np. PowerShell NamedPipeClientStream). Potem `sqlite3 catalog.db "select actor_sid, role, operation, outcome, kb_code from audit_log"`.
- Oczekiwane: A bez podniesienia → odmowa połączenia przez ACL (lub Odczyt, jeśli A jest też w Operators); A podniesiony → Administrator; B → Operator (release: KB-7011); C → Administrator; D → brak połączenia (ACL); zdalnie → odmowa (Deny NETWORK). actor_sid = SID konta, role zgodne; żadnej odpowiedzi KB-7001 dla A/B/C (KB-7001 = błąd adaptera RunAsClient → plan B SP-02). Sprawdzić też, że `WindowsIdentity.ImpersonationLevel` = Identification (a nie None).

## W-38 (it. 96, RD-57/BL-74) — baner „Tryb podglądu” i UAC; ręczne uruchomienie w kolejce
- Środowisko: Agent jako usługa (W-33), konta jak w W-37; dwa zadania SMB do jednego serwera z różnymi wpisami poświadczeń (W-36).
- Polecenie: (1) konto A (Administrators, w ACL jako Odczyt, np. przez grupę Users w ACL testowym) uruchamia `Kobian.UI.exe` bez podniesienia; (2) konto D/Odczyt — to samo; (3) w trakcie zaplanowanego zadania SMB (konto k1) `kobian run <zadanie-z-kontem-k2> --wait`.
- Oczekiwane: (1) pulpit z banerem „Tryb podglądu…” i zdaniem „…Uruchom jako administrator”, akcje wyszarzone; po „Uruchom jako administrator” banera brak; (2) baner z „…grupy „Kobian Operators””; (3) polecenie czeka (bez KB-2101), startuje po zakończeniu pierwszego i kończy Succeeded; w `run_runs` brak wiersza w trakcie czekania (BL-76). Jeśli ACL odrzuca A — ekran KB-9001 (RD-58).

## W-39 (it. 97, BL-76/BL-77) — „W kolejce” w UI i dławienie otwarć SMB
- Środowisko: jak W-38 (usługa, dwa zadania SMB do jednego serwera z różnymi wpisami poświadczeń); dodatkowo wpis poświadczenia z błędnym hasłem do konta testowego z progiem blokady konta 5 na NAS/AD.
- Polecenie: (1) w trakcie zaplanowanego zadania SMB (konto k1) `kobian run <zadanie-k2>` (bez --wait), potem `kobian runs --limit 5` i ekran Zadania/Historia w UI; (2) 4 × `kobian run <zadanie-złe-hasło> --wait` w odstępach 1 min; (3) po 11 min ponownie.
- Oczekiwane: (1) wiersz `Queued` (cause manual) w `kobian runs`, pigułka „W kolejce” na SCR-02 i „w kolejce” w SCR-10, po zwolnieniu ten sam run_id kończy Succeeded (zastępuje oczekiwanie „brak wiersza” z W-38 pkt 3); (2) próby 1–3 → KB-2102, próba 4 → KB-2114 bez logowania na serwerze (dziennik zabezpieczeń NAS/AD: 3 nieudane logowania, konto NIE zablokowane); (3) znów KB-2102 (próba dociera do serwera).

## W-40 (it. 98, BL-64) — PlanRestore w usłudze: prawo zapisu, miejsce, konflikty
- Środowisko: Agent jako usługa (W-33), konto B (Kobian Operators); folder `D:\Odtw\ro` z ACL: B tylko odczyt, konto usługi zapis; folder `D:\Odtw\ok` (B zapis); pendrive FAT32 z < 50 MB wolnego jako `E:\`.
- Polecenie: jako B `kobian restore --snapshot latest --job <zadanie> --to D:\Odtw\ro --dry-run --json`; to samo dla `D:\Odtw\ok` (dwukrotnie: przed i po odtworzeniu) i `E:\`; w UI SCR-07 te same foldery.
- Oczekiwane: `ro` — DZIŚ plan bez blokady (próbka na koncie usługi; ZNANA LUKA SEC-6.3 → po wdrożeniu AccessCheck tokenem klienta: KB-5001); `ok` — przed: kod 0, po: kod 1 z `conflicts` = liczba plików; `E:\` — `freeBytes` ≈ wolne miejsce pendrive'a i KB-5106 przy większej kopii; w folderach brak plików `.kobian-write-probe-*`.

## W-41 (it. 99, D9.9) — kotwica dzienna audytu w usłudze
- Środowisko: Agent jako usługa (W-33), strefa „Central European Standard Time”.
- Polecenie: (1) zatrzymaj usługę przed 06:55, uruchom po 07:00 → `kobian audit verify --json`; (2) zostaw usługę na noc → rano `kobian audit verify`; (3) przepisz `last_anchor` do notatki, wykonaj kilka operacji (np. `kobian schedule set …`), potem `kobian audit verify --anchor <zapisana>`; (4) jako Administrator zatrzymaj usługę, w `sqlite3 catalog.db` zdejmij wyzwalacz `audit_no_delete` i usuń ostatni wpis, odtwórz wyzwalacz identycznym SQL z migracji, uruchom usługę, `kobian audit verify --anchor <zapisana>`.
- Oczekiwane: (1) jedna nowa kotwica przy starcie (`anchorsChecked` +1), (2) dokładnie jedna nowa kotwica z `last_anchor_at_utc` ≈ 04:55Z/05:55Z (czas letni/zimowy), (3) kod 0, `anchorsChecked` = kotwice + 1; (4) kod 2, KB-6503 (bez `--anchor` też KB-6503, jeśli usunięty wpis był zakotwiczony w katalogu).

## W-42 (it. 100, D10.2/D2.12) — budowa i instalacja MSI (szkielet)
- Środowisko: VM Windows 11/Server 2022 (czysta), .NET SDK 10.0.402, WiX (WixToolset.Sdk 7.0.0 — po decyzji Q37), PowerShell jako Administrator.
- Polecenie: (1) polecenia budowy z komentarza `installer/Kobian.Installer.wixproj`; (2) `msiexec /i Kobian.msi /qn /l*v inst.log`; (3) `sc qc KobianAgent`, `sc qprivs KobianAgent`, `sc qfailure KobianAgent`, `sc qsidtype KobianAgent`, `Get-LocalGroup Kobian*`, `icacls C:\ProgramData\Kobian`, `reg query HKLM\SYSTEM\CurrentControlSet\Services\VSS\VssAccessControl`, `secedit /export /cfg c:\t\s.inf` (SeBackupPrivilege/SeRestorePrivilege), `Get-Service KobianAgent`; (4) `kobian status` jako Administrator; (5) `msiexec /x Kobian.msi /qn /l*v un.log`, ponownie (3).
- Oczekiwane: (1) MSI bez błędów schematu (inaczej popraw Package.wxs i zapisz rozbieżność); (2) kod 0, w inst.log „ok group-operators … ok lsa-rights”; (3) DELAYED_AUTO_START, konto NT SERVICE\KobianAgent, uprawnienia dokładnie 4 z D2.12, restart 60000/60000/300000 reset 86400, SID UNRESTRICTED; grupy istnieją; ACL: tylko SYSTEM, Administrators, NT SERVICE\KobianAgent (F), bez dziedziczenia; wartość VSS = 0x2; prawa LSA dla konta usługi; usługa Running; (4) odpowiedź Agenta; (5) usługa usunięta, wartość VSS i prawa LSA usunięte, `C:\ProgramData\Kobian` i grupy POZOSTAJĄ.

## W-43 (it. 101, SEC-6.3/AC-07.2) — AccessCheck tokenem klienta przy odtwarzaniu (zastępuje oczekiwanie „DZIŚ bez blokady” z W-40)
- Środowisko: jak W-40 (usługa, konto B w „Kobian Operators”, `D:\Odtw\ro` — B tylko odczyt, usługa zapis; `D:\Odtw\ok` — B zapis); konto A podniesione (Administrator); folder `D:\Odtw\nr` bez READ_CONTROL dla konta usługi.
- Polecenie: jako B `kobian restore --snapshot latest --job <z> --to D:\Odtw\ro --dry-run --json`, potem bez `--dry-run`; to samo dla `ok` i `nr`; jako A dla `ro`; w UI SCR-07 jako B dla `ro`.
- Oczekiwane: B/`ro` — `code` KB-5001 (plan i odtworzenie), w folderze nic nie powstaje, w audycie `Agent/Restore` failed KB-5001; B/`ok` — plan bez blokady, odtworzenie Succeeded; B/`nr` — KB-5001 (fail-closed); A/`ro` — odtworzenie Succeeded (Administrator bez kontroli); UI — komunikat KB-5001 w panelu planu, przycisk wyłączony.

## W-44 (it. 103, D2.8) — Kobian.Worker --role vss (protokół, bez VSS)
- Środowisko: Windows 10/11, `dotnet publish src\Kobian.Worker -c Release -r win-x64`.
- Polecenie (PowerShell): (1) `Kobian.Worker.exe` bez argumentów; (2) `$null | .\Kobian.Worker.exe --role vss; $LASTEXITCODE`; (3) wyślij na stdin ramkę `{"id":1,"op":"vss.create","volumes":["C:\\"]}` (4 B długości LE + JSON) i odczytaj odpowiedź; (4) wyślij `{"id":1,"op":"x"}`.
- Oczekiwane: (1) kod 64 i „usage”; (2) kod 0 (EOF); (3) odpowiedź `{"id":1,"ok":false,"code":"KB-1406"}` (zaślepka do SP-01), proces czeka na kolejne ramki; (4) kod 3 bez odpowiedzi. Po SP-01: (3) zwraca `devices` z `\\?\GLOBALROOT\Device\HarddiskVolumeShadowCopyN\`, a zabicie procesu usuwa migawkę (`vssadmin list shadows`).

## W-45 (it. 106, D2.8/SEC-2.7/NFR-19) — launcher Workera: podpis, obiekt zadania, polityka
- Środowisko: VM Windows 11, kompilacja podpisana certyfikatem testowym (zaufanym na tej maszynie), druga kopia Kobian.Worker.exe niepodpisana i trzecia z jednym zmienionym bajtem; Process Explorer; polityka AppLocker blokująca nieznanego wydawcę (osobny snapshot VM).
- Polecenie: mały program testowy (albo test xunit z Category=Windows) uruchamiający `WorkerLauncher` z `AuthenticodeVerifier` i `WindowsProcessJob` dla każdej z 3 kopii; dla podpisanej: `WorkerVssSnapshotService.CreateAsync(["C:\\"])`, w trakcie — zabicie procesu testowego z Menedżera zadań; Process Explorer → zakładka Job procesu Worker.
- Oczekiwane: podpisana — start, odpowiedź KB-1406 (SP-01), w zakładce Job: Kill on job close, Process memory 150 MB, Active processes 1; po zabiciu rodzica Worker znika (0 osieroconych); niepodpisana i zmieniona — KB-7015 bez startu procesu; przy AppLocker — KB-7008 (Win32 1260) zamiast KB-7016; zmienne środowiskowe Workera (Process Explorer → Environment) bez KOBIAN_*.

## W-46 (it. 107, D8.8) — wznowienie odtwarzania po zatrzymaniu usługi
- Środowisko: Agent jako usługa (W-33), zadanie z kopią ~5 GB w wielu plikach (np. 2 000 plików), folder `D:\Odtw\w`.
- Polecenie: `kobian restore --snapshot latest --job <z> --to D:\Odtw\w`; po ~30% `Stop-Service KobianAgent -Force` (albo zabicie procesu); `Start-Service KobianAgent`; ponownie to samo polecenie; potem `Get-ChildItem D:\Odtw\w -Recurse -Filter *.kobian-partial`.
- Oczekiwane: druga próba kończy się `ok` (bez `warning KB-5105`), czas wyraźnie krótszy niż pełne odtworzenie, brak plików `*.kobian-partial`, sumy plików zgodne (np. `Get-FileHash` próbki z kopią źródłową); w katalogu `restore_sessions`: pierwsza sesja Superseded, druga Done.

## W-47 (it. 111, D9.10) — webhook po zakończonym uruchomieniu
- Środowisko: Agent jako usługa (W-33); odbiorca HTTPS na publicznym adresie (np. własny serwer testowy z ważnym certyfikatem) logujący nagłówki i treść.
- Kroki: zapisz 64 znaki hex do `%ProgramData%\Kobian\secrets\webhook.secret`; ustaw `KOBIAN_WEBHOOK_URL` dla usługi (`reg add HKLM\SYSTEM\CurrentControlSet\Services\KobianAgent /v Environment /t REG_MULTI_SZ /d KOBIAN_WEBHOOK_URL=https://...`); restart usługi; `kobian run <zadanie> --wait`.
- Oczekiwane: w ≤ 30 s odbiorca dostaje POST z `Kobian-Webhook-Signature` zgodnym z HMAC sekretu; po wyłączeniu odbiorcy kolejne uruchomienie daje ponowienie po ~1 min (ten sam Kobian-Webhook-Id); URL `https://192.168.x.x/...` → usługa nie startuje (KB-6302 w dzienniku zdarzeń).

## W-48 (it. 119, D4.16/NFR-05) — równoległe wysyłki packów: pamięć usługi i limit sesji NAS
- Środowisko: Agent jako usługa (W-33), zadanie plikowe ~20 GB na udział SMB (NAS z limitem sesji, jeśli dostępny) i drugie na dysk lokalny; Monitor wydajności: `Process(Kobian.Agent)\Private Bytes` co 1 s.
- Polecenie: `kobian run <zadanie> --wait` dla obu zadań; potem to samo z `KOBIAN_PARALLEL_UPLOADS=0` (zmienna usługi, restart); na koniec `sqlite3 catalog.db "SELECT * FROM repo_dest_parallelism"`.
- Oczekiwane: p99 Private Bytes ≤ 500 MB w obu trybach (NFR-05; inaczej zgłoś korektę Qb63 → wariant b); czas kopii z równoległością ≤ czas sekwencyjny; NAS z limitem sesji: brak Failed, w `repo_dest_parallelism` wiersz z limitem < 4 i datą; po 7 dniach wiersz ignorowany.

## W-49 (it. 120, BL-34/SP-04) — FTPS na prawdziwych serwerach
vsftpd 3.x (`ssl_enable=YES`, `require_ssl_reuse=YES`, `force_local_data_ssl=YES`): `kobian init T --dest /kopie --dest-scheme ftps --host <vsftpd> --host-key <SPKI> --secret-ref file:vs --password-file pw.txt`, potem 1 000 uploadów 8 MiB. Oczekiwane: 0 błędów TLS (SP-04 go). Dalej: FileZilla Server, IIS FTP, ProFTPD (Implicit 990) — zapis, odczyt, wznowienie, RNTO na istniejący plik (oczekiwane KB-2301 na IIS/FileZilla), konto bez DELE, `max_per_ip=2` przy 6 zadaniach (KB-2112), MITM tylko na porcie danych (znalezisko Średnie). Szczegóły: notatki it. 120 w `budowa/szkice/it120-notes-ftp.md`.

## W-50 (it. 120, BL-45) — e-mail przez prawdziwy serwer SMTP
Skonfiguruj `KOBIAN_SMTP_HOST/PORT/FROM/TO` + `<sekrety>\smtp.credentials` (konto testowe), uruchom zadanie zakończone błędem. Oczekiwane: e-mail PL z kodem KB, bez ścieżek; przy złym certyfikacie — brak AUTH, ponowienia, potem wpis audytu `notify.email` z KB-6206.

## W-48 (uzupełnienie it. 120, RD-75) — Private Bytes `Kobian.Bench --e2e 800 3 16` i `--e2e 800 3 64`; oczekiwane: szczyt ≤ NFR-05 przy budżecie 192 MiB (Qb74).

## W-51 (it. 123, BL-40, D5.11) — zapytania kontrolne ERP na prawdziwym SQL Server
Na instancji z bazą testową o schemacie Subiekt GT (tabele z `profiles/insert-gt.json`, dane syntetyczne) i kontem Kobian z `db_backupoperator` + `db_datareader`: uruchom zadanie „database”. Oczekiwane: `kobian snapshots` pokazuje kopię bez ostrzeżeń; metadane zawierają `erpProfile=insert-gt` i `controls` (tylko liczby/daty). Następnie `kobian restore-db <snapshot>` do nowej bazy: wynik Succeeded bez KB-3505. Powtórz z kontem bez `db_datareader` — oczekiwane Warning KB-3010 przy kopii, kopia ważna; sprawdź ręcznie, że zapytania w SQL Server Profiler to wyłącznie `SELECT COUNT/MAX` w kontekście `USE [baza]`, `LOCK_TIMEOUT 5000`.

## W-52 (it. 126, BL-44) — kobian-restore na celu zdalnym
Na maszynie z dostępem do serwera SFTP i FTPS (dane syntetyczne): wykonaj kopię zadaniem Agenta, skopiuj `kobian.repo` i plik klucza lokalnie, utwórz `<secrets>/<nazwa>.json` (rola backup), uruchom `kobian-restore restore --dest-scheme sftp --host … --hostkey SHA256:… --secrets-dir … --secret-ref file:<nazwa> --repo-header kobian.repo --repo <ścieżka> --key-file … --password-file … --to <katalog>`; to samo z `--dest-scheme ftps --spki …`. Oczekiwane: kod 0, pliki bit w bit; zły odcisk → KB-2104/KB-2109, kod 2.

## W-53 (it. 129, BL-45) — e-mail przez Microsoft 365 (OAuth2) — nie wymaga Windows, wymaga dzierżawy testowej
Na dzierżawie testowej M365: aplikacja z `SMTP.SendAsApp`, principal skrzynki w Exchange Online; ustaw `KOBIAN_SMTP_AUTH=oauth2` (+TENANT, CLIENT_ID, HOST=smtp.office365.com), `smtp.oauth` z sekretem; wyślij alert testowy. Oczekiwane: dostarczono; zły sekret → wpis dead z KB-6208, brak sekretu/tokenu w logach i audycie.

## W-54 (it. 130, BL-45) — potwierdzanie/wyciszanie alertów w usłudze Windows
Na Windows z zainstalowaną usługą: wywołaj awarię zadania (A01), `kobian alerts` (kod 1, widoczny id), jako Operator `kobian alerts ack <id>` → `ok acknowledged`; jako Operator `kobian alerts silence <id> 30` → odmowa (kod 3, wpis „denied” w audycie); jako Administrator na nowym alercie → `ok silenced`. Oczekiwane: SID wywołującego w `audit_log` (actor_sid), `kobian audit verify` bez przerwanego łańcucha.

## W-55 (it. 131, BL-45) — alerty na pulpicie SCR-01 z prawdziwym Agentem
Na Windows z usługą i UI: wywołaj awarię zadania (A01); w UI jako Operator na pulpicie pojawia się wiersz „Zadanie zakończyło się błędem” z przyciskiem „Potwierdź” (bez „Wycisz” — to Administrator); po kliknięciu stan staje się „Potwierdzony”, a przyciski znikają; jako Administrator „Wycisz na 4 h” → stan „Wyciszony”; jako Viewer (grupa Readers) brak przycisków. Oczekiwane: wpisy w audycie z SID, brak zmiany stanu ochrony zadań (6.3).

- W-56 (it. 133): na Windows z Agentem uruchomić UI → pulpit SCR-01 z alertem; ComboBox „Czas wyciszenia” obok „Wycisz” pokazuje 4 godz.; wybór „7 dni” + „Wycisz” → `kobian alerts list` pokazuje stan silenced; dla alertu bezpieczeństwa lista kończy się na 24 godz.

## W-57 (it. 134, BL-64) — WatchRun po named pipe z rolami
Na Windows z usługą: `kobian job run <zadanie>` (bez --wait), z JSON/`kobian runs` weź run_id, uruchom `kobian watch <run-id>` jako użytkownik z grupy Readers — oczekiwane: linie `Running files N …` rosnące, na końcu `Succeeded`, kod wyjścia 0. Jako użytkownik spoza grup Kobian: kod 3 i brak danych (strumień odrzucony przez bramkę ról). `kobian watch brak` → `KB-0107`, kod 2. Przerwanie Ctrl+C nie zatrzymuje biegu w Agencie.

## W-58 (it. 136, RD-79) — widok postępu SCR-07 na prawdziwej usłudze
Widok `RunProgressView` nie jest jeszcze pokazywany w żadnym ekranie (RD-81). Test ręczny po podpięciu: uruchom długą kopię (`kobian job run <zadanie>`), w UI otwórz widok z jej `run_id` — oczekiwane: „W toku”, rosnące „Pliki: N”, na końcu plakietka „zakończono”. Zatrzymaj usługę (`Stop-Service KobianAgent`) w trakcie — oczekiwane: „ponawiam próbę 1 z 5…” co 1/2/4/8/15 s, po starcie usługi (`Start-Service`) widok wznawia i kończy poprawnie; po 5 nieudanych próbach „Nie można dalej odczytywać postępu” + KB-7010. Użytkownik spoza grup Kobian: KB-7011 bez ponawiania. Przycisk „Przestań obserwować” nie przerywa kopii (sprawdź `kobian runs`). Zrzuty referencyjne: `docs/ui/SCR-07-progress-*.png` (Skia headless w Linuksie — porównaj wygląd na Windows, zwłaszcza kontrast plakietek).

## W-59 (it. 137, RD-81) — restore w tle na prawdziwej usłudze
Brak jeszcze klienta (CLI/UI) używającego `detach` — test ręczny po podpięciu SCR-07 (RD-81 cz. 2): z UI „Odtwórz” dużej kopii do pustego folderu — oczekiwane: widok postępu pojawia się natychmiast (bez czekania na koniec), „Pliki: N” rośnie, na końcu „zakończono”; wiersz w `kobian runs` z reason restore. Zrestartuj usługę w trakcie: bieg ma stan Interrupted/Failed, ponowny restore do tego samego celu wznawia (D8.8).
