# Runda 4 budowy (iteracje 46–60) — podsumowanie

Data: 2026-10-14. Zakres bez zmian (S1=B, S2=B, S3=B, S4=C; Docker i git nie). Cel rundy (backlog J): Agent jako host (BL-15) + gRPC po pipe (BL-14), potem dostawcy, harmonogram i odtwarzanie w Agencie. Od it. 57 — polecenie właściciela na żywo: priorytet UI i design.

## Zadania (ID planu)
| It. | Rola | Wynik | ID planu |
|---|---|---|---|
| 46 | PO | agent.proto, AgentHost (pipe/gniazdo Unix, ADR-0004), CatalogAgentBackend, GrpcAgentClient w CLI | BL-14, BL-15, US-37 |
| 47 | Architekt | CatalogJobService (RunJob z katalogu), JobDefinition v1 (ADR-0005) | BL-15, BL-42, BL-22 |
| 48 | Silnik | RepoInitService; usunięte DevKeyFile i --repo-id (ADR-0002/0003 zamknięte) | BL-24 |
| 49 | Transport | RPC InitJob + `kobian init` | BL-24, BL-14 |
| 50 | Bazy (prowadzi), transport | port ISftpTransport, sesja, FakeSftpServer | BL-33, SP-03 (część) |
| 51 | UX | adapter SSH.NET 2026.0.0, pin SHA256 hosta (ADR-0006) | BL-33 |
| 52 | Harmonogramy | FileSftpCredentialProvider (ADR-0007), AgentProviders local+sftp | BL-33, BL-15 |
| 53 | Odtwarzanie | CatalogRestoreService + RPC Restore, KB-5002 | BL-43 |
| 54 | Monitoring | CatalogScheduler (wyzwalacze cron w Agencie) | BL-42, RD-26 |
| 55 | Jakość | RPC/CLI schedule + przeładowanie; `check` V2/V3 (RepoChecker), KB-1501…1504 | RD-27, BL-26, AC-09.1 |
| 56 | Bezpieczeństwo | V2 automatycznie po backupie, suspect → następna kopia pełna | BL-26, PM-15 |
| 57 | R&D + UX | Avalonia 12.1.4, tokeny, powłoka, pulpit SCR-01, Kobian.Tests.UI | BL-46 |
| 58 | Wydajność + UX | SCR-02 zadania, SCR-10 historia (ADR-0009) | BL-61 |
| 59 | Dokumentacja + UX | SCR-03 kreator (ADR-0010) | BL-61 |
| 60 | Przegląd rundy | fuzzing parserów rundy 4 (20k mutacji × 4), poprawka FileSftpCredentialProvider (dowiązania na każdej platformie, nazwy urządzeń Windows), audyt, R&D | SEC-3.3, D8.9 |

## Status DoD
Agent działa jako host: init → run (ręcznie i z harmonogramu) → V2 → snapshots → restore → check przez CLI po pipe/gnieździe, w testach na Linuksie. SFTP: adapter gotowy, test na prawdziwym serwerze w kolejce Windows/serwer (W-13; Docker niedozwolony). UI: SCR-01/02/03/10 na porcie usług, adapter gRPC dla pulpitu/zadań/historii; kreator bez adaptera do Agenta (Qb20, czeka na BL-60). Brak: retencja GFS (BL-25), MSSQL na żywo, SMB, usługa Windows/MSI. US spełniająca DoD end-to-end z UI i na Windows: żadna (wymaga Windows).

## NFR
Zmierzone w tej rundzie: nic jako NFR. Informacyjnie (chmura, nie sprzęt referencyjny): ci.sh it. 60 — restore 17 s, build 35 s, testy 31 s, E2E 1 s, audyt 13 s (pierwszy przebieg). Wszystkie NFR: **niezmierzone**.

## Spike’i
SP-03 (SFTP): in-proc zielone (FakeSftpServer), na prawdziwym serwerze — wymaga serwera/Windows (W-13). SP-18: bez zmian (in-proc 0 dangling; 300 uruchomień chaos otwarte, RD-19/RD-21).

## Przegląd bezpieczeństwa rundy (rola 11, it. 60) — szczegóły w bezpieczenstwo.md
- `dotnet list package --vulnerable --include-transitive`: 0 podatnych w 23 projektach (uruchomione 2026-10-14, SDK 10.0.401, SHA-512 zgodny).
- Skan sekretów (SecretScanTests) zielony; zakazane API (ScpClient, BinaryFormatter, walidacja certyfikatów, Process.Start, MD5/SHA-1): brak użyć (grep src/).
- Fuzzing: JobDefinition, RepoHeader (Peek+Verify), plik klucza (recovery, MarkRevoked), CronSchedule — 4 × 20 000 mutacji, tylko KobianException z właściwym kodem, bez ścieżek w komunikatach.
- Znaleziono i naprawiono (Średnie): FileSftpCredentialProvider sprawdzał dowiązania tylko poza Windows i przyjmował nazwy urządzeń Windows (CON, NUL, COM1) oraz kropkę końcową.
- Otwarte Średnie: tożsamość serwera pipe (W-17), schowek/WDA dla klucza odzyskiwania (W-18), dedup bez limitu RAM (RD-22), unieważnienie klucza bez rotacji MK (RD-23). Wysokich brak.

## Ryzyka i dług
- Build lokalny na urządzeniu nadal niemożliwy (`/sessions` 100%); build w chmurze przez pack.tgz → stage → it<N>.tgz → commit.
- Pokrycie AC: 23/59 Must z testem (bez zmian od it. 55) — runda 4 przesunęła się na UI.
- Kontrakt gRPC (BL-60) blokuje adapter kreatora i dokładność pulpitu (Qb18–Qb20).
- Brak odpowiedzi właściciela na Qb3–Qb20 — działają wartości prowizoryczne.

## Propozycje korekty kursu (Qb21 w decyzje-budowy.md)
Runda 5: BL-60 (rozszerzenie kontraktu) → adapter kreatora (UI działa end-to-end) → retencja GFS (BL-25) → RD-22 → MSSQL (BL-40…). Priorytet UI zachowany w formie „UI na prawdziwym Agencie”.
