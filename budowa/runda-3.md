# Runda 3 budowy (iteracje 31–45) — podsumowanie

Data: 2026-10-13. Zakres bez zmian (S1=B, S2=B, S3=B, S4=C; Docker i git nie). Cel rundy: pionowy przekrój backup → restore → CLI (Qb10) — zrealizowany w bibliotekach.

## Zadania (ID planu)
| It. | Rola | Wynik | ID planu |
|---|---|---|---|
| 31 | PO | plan rundy, KB-1401…1405 | BL-12 |
| 32 | Architekt | RepoPaths, IndexCodec, SnapshotCodec | BL-21, R2.3.5 |
| 33 | Silnik | BackupPipeline, VerifiedUploader, MetadataStore; AC-08.4 | BL-22, D3.14 |
| 34 | Transport | RepoReader, MemoryStorage z awariami, SP-18 in-proc | BL-30, SP-18 |
| 35 | Bazy/ERP | StagedFileSource, DbSnapshotMeta | BL-40 |
| 36 | UX | CLI kobian (CliApp, kody 0/1/2/3/4/64) | BL-47, AC-37.1/2 |
| 37 | Harmonogramy | IJobRunner, RunExecutor z ponowieniami | BL-42 |
| 38 | Odtwarzanie | RestoreExecutor, kobian-restore list/restore | BL-43, BL-44 |
| 39 | Monitoring | RunFinishedEvent (bez danych klienta) | BL-45 |
| 40 | Jakość | etap ac-raport | BL-8 |
| 41 | Bezpieczeństwo | KeyFiles (PBKDF2 + opakowanie MK), V-03/V-04 | BL-24 |
| 42 | R&D | etap E2E w ci.sh + EndToEndTests | BL-8, RD-18 |
| 43 | Wydajność | RepoHeader (V-09), KB-1104 | BL-24 |
| 44 | Dokumentacja | RepoProvisioner (init repo, unieważnianie klucza) | BL-24 |
| 45 | Przegląd rundy | fuzzing IndexCodec/SnapshotCodec (SEC-3.3), audyt, R&D | SEC-3.3 |

## Status DoD
Vertical slice backup → restore działa w bibliotekach i w teście E2E (pełny → przyrost → restore obu generacji bit w bit). Nadal brak: host Agenta (BL-15) i gRPC (BL-14) — CLI zwraca kod 3 dla poleceń wymagających Agenta; dostawcy SMB/SFTP (BL-32/33), harmonogram w usłudze, MSSQL na żywo, GUI, MSI. US spełniająca DoD end-to-end (z Agentem i UI): żadna.

## NFR
Zmierzone w tej rundzie: nic nowego jako NFR. Informacyjnie: czasy potoku ci.sh w chmurze (nie sprzęt referencyjny; NFR-20 niezmierzony). Wszystkie pozostałe NFR: **niezmierzone**.

## Spike’i
SP-18 in-proc: 0 dangling w teście (it. 34) — pełne go/no-go wymaga 300 uruchomień harnessu chaos. SP-17 pomiar wstępny z it. 13 (nie sprzęt referencyjny). Windows/serwery/właściciel: bez zmian.

## Przegląd bezpieczeństwa rundy (rola 11, it. 45)
- Audyt zależności: 0 podatnych (SDK 10.0.401, SHA-512 zgodny; uruchomione w it. 45).
- Skan sekretów i zakazane API: testy zielone. Unit 399/399, Arch 13/13, E2E 1.
- Fuzzing losowy kodeków indeksu i snapshotu: 60 000 mutacji, tylko KobianException.
- Otwarte Średnie: dedup bez limitu RAM (D3.15, RD-22); unieważnienie klucza nie zmienia MK (RD-23). Wysokich brak.

## Ryzyka i dług
- Build lokalny na urządzeniu niemożliwy (dysk VM pełny, restore na dysku Windows wisi) — build wyłącznie w chmurze przez pack.tgz → stage. Token blokady wymaga HOME poza pełnym /sessions (użyto `.toolchain/home`).
- Etap ac-raport: Must z testem 18/59 — pokrycie AC wymaga dopisania tagów w rundzie 4.
- Brak odpowiedzi właściciela na Qb3–Qb9, Qb11, Qb12 — działają wartości prowizoryczne.

## Propozycje korekty kursu (do decyzji-budowy.md)
Runda 4: BL-15 host Agenta + BL-14 gRPC (zdejmuje kod 3 z CLI), potem dostawcy Local/SFTP w potoku i RD-22.
