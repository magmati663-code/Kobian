# Runda 5 budowy (iteracje 61–75) — podsumowanie

Data: 2026-10-14. Zakres bez zmian (S1=B, S2=B, S3=B, S4=C; Docker i git nie). Kurs: Qb21 (PROWIZORYCZNIE) — „UI na prawdziwym Agencie”, potem retencja/prune i MSSQL.

## Zadania (ID planu)
| It. | Rola | Wynik | ID planu |
|---|---|---|---|
| 61 | PO | plan rundy 5 (sekcja K); GetStatus bez wartości zastępczych, RPC ListRuns, `kobian runs` | BL-60 |
| 62 | Architekt | RPC ProbeDestination, UpsertJob (validate_only) | BL-60, D4.11 |
| 63 | Silnik | RPC InitRepository + klucz odzyskiwania, GrpcWizardService — kreator na prawdziwym Agencie | BL-61, Qb20/Qb24 |
| 64 | Transport | SCR-10 z ListRuns (nieudane z KB) | BL-61 |
| 65 | Bazy | RetentionPlanner (Keep, V-10), `kobian forget`, retencja z S5 | BL-25, AC-10.1 |
| 66 | UX | PreviewRetention, S5 z podglądem | BL-25 |
| 67 | Harmonogramy | PrunePlanner (dry-run), test właściwości po prune | BL-25, AC-10.3, PM-18 |
| 68 | Odtwarzanie | blokady-dzierżawy D3.13, prune faza 1/2, RPC Prune | BL-25, D3.13 |
| 69 | Monitoring | retencja automatyczna, wstrzymanie po skoku zegara, baner SCR-01 | BL-25, SEC-17.4 |
| 70 | Jakość | MsSqlBackupService (pre-checki, BACKUP CHECKSUM, VERIFYONLY) na porcie | BL-40, AC-02.1–02.3 |
| 71 | Bezpieczeństwo | runtime 10.0.13 (krytyczne CVE ASP.NET Core), adapter SqlClient 7.1.1, zadanie `database` | BL-40, BL-62 |
| 72 | R&D | discovery MSSQL (RPC DiscoverDatabases) | BL-40 |
| 73 | Wydajność | kreator S1-db, zapis zadania `database` | BL-40, BL-61 |
| 74 | Dokumentacja | zatwierdzanie certyfikatu TLS serwera SQL (PRELOGIN/TDS 8/SSRP), przewodnik admina PL/EN | BL-40, Qb32 |
| 75 | Przegląd rundy | fuzzing parserów rundy 5 (6 celów), utwardzenie ramkowania PRELOGIN (puste pakiety), audyt, R&D | SEC-3.3 |

## Status DoD
Kreator SCR-03 tworzy zadanie plikowe i bazodanowe na prawdziwym Agencie (testy przez pipe). Retencja GFS + prune dwufazowy z blokadami działa i ma test właściwości (żywe snapshoty przechodzą V2/V3 i restore). MSSQL: pełny przebieg na serwerze zastępczym; na prawdziwym SQL Server — wymaga Windows/serwera (W-23…W-28). Brak: SCR-06 odtwarzanie w UI, SMB, usługa Windows/MSI, RD-22 (plan it. 68 nie został wykonany — przesunięty na rzecz BL-25/BL-40).

## NFR
Zmierzone w tej rundzie: nic jako NFR (NFR-25 Perf retencji — test wydajności planera w chmurze, informacyjnie). Wszystkie NFR: **niezmierzone** na sprzęcie referencyjnym. Informacyjnie ci.sh it. 75 (chmura): restore 17 s, build 47 s, testy 51 s.

## Spike’i
SP-03 SFTP: bez zmian (in-proc zielone; W-13). Spike’i MSSQL: na serwerze zastępczym zielone, na prawdziwym SQL — wymaga Windows (W-23…W-28).

## Przegląd bezpieczeństwa rundy (rola 11, it. 75) — szczegóły w bezpieczenstwo.md
- `dotnet list package --vulnerable --include-transitive`: 0 podatnych (2026-10-14, SDK 10.0.402, runtime 10.0.13, SHA-512 zgodny).
- Skan sekretów (SecretScanTests) zielony; zakazane API (ScpClient, BinaryFormatter, TrustServerCertificate=true, bypass walidacji, MD5/SHA-1, Process.Start): brak użyć w src/ (grep).
- Fuzzing (Round5FuzzTests): definicja `database`, SqlEndpoint, opcja ENCRYPTION PRELOGIN, SSRP SVR_RESP, ramkowanie TdsPreloginStream, odpowiedź PRELOGIN — razem ≈ 90 000 mutacji; tylko oczekiwane wyjątki, bez nazw serwera/ścieżek w komunikatach.
- Naprawione (Niskie): zalew pustymi pakietami TDS (sam nagłówek) trzymał pętlę odczytu do limitu 15 s — limit 16 kolejnych pustych pakietów i 16 pakietów odpowiedzi PRELOGIN.
- Otwarte Średnie: RD-39 (NTLM konta usługi do dowolnego hosta przez Operatora), W-17 (tożsamość serwera pipe), RD-22 (dedup bez limitu RAM). Niskie: RD-42 (brak audytu zatwierdzenia zaufania). Wysokich brak.

## Ryzyka i dług
- Build lokalny na urządzeniu nadal niemożliwy (`/sessions` 100%) — build w chmurze (pack.tgz → stage → it<N>.tgz → commit).
- Pokrycie AC: 26/59 Must z testem (+3 w rundzie).
- `rd-add` nadaje zduplikowane numery (liczy wiersze) — poprawki ręczne co iterację.
- Qb3–Qb32 bez odpowiedzi właściciela — działają wartości prowizoryczne.

## Propozycja kursu rundy 6 (Qb33)
SCR-06 odtwarzanie w UI (pliki + `database.bak`) → RD-39 lista dozwolonych serwerów SQL → RD-42 audyt zmian zaufania → BL-62 kontrola runtime przy starcie Agenta → RD-22 → SMB (BL-3x) → usługa Windows/Worker → MSI.
