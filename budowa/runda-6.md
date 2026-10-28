# Runda 6 budowy (iteracje 76–90) — podsumowanie

Data: 2026-10-14. Zakres bez zmian (S1=B, S2=B, S3=B, S4=C; Docker i git nie). Kurs: Qb33 (PROWIZORYCZNIE) — „odtwarzanie w UI + domknięcie ryzyk MSSQL”.

## Zadania (ID planu)
| It. | Rola | Wynik | ID planu |
|---|---|---|---|
| 76 | PO | plan rundy 6 (sekcja L); SCR-06 przeglądarka kopii (SnapshotBrowser, RPC BrowseTree) | BL-63, AC-07.1 |
| 77 | Architekt | SCR-07 odtwarzanie plików w UI (RestoreViewModel/View) | BL-64, Qb34 |
| 78 | Silnik | lista dozwolonych serwerów SQL (KOBIAN_SQL_ALLOWED_SERVERS, KB-3009); zrzuty całej aplikacji | RD-39, Qb35 |
| 79 | Transport | łańcuch audytu w katalogu (audit_log append-only), audyt API, `kobian audit verify` | BL-65/RD-42, D9.9, Qb36 |
| 80 | Bazy | RuntimeGuard (.NET ≥ 10.0.13, KB-7013), exact_paths w RestoreRequest | BL-62, Qb37 |
| 81 | UX | SCR-11 Ustawienia (motyw, język, usługa, dziennik audytu) | BL-66 |
| 82 | Harmonogramy | SCR-09 Cele (RPC ListDestinations) — wszystkie ekrany nawigacji zbudowane | BL-67, Qb38 |
| 83 | Odtwarzanie | MsSqlRestoreService (nowa baza, VERIFYONLY, FILELISTONLY → MOVE, CHECKSUM, TRUSTWORTHY OFF) | BL-68 cz. 1, Qb39 |
| 84 | Monitoring | RestoreDatabase w Agencie, `kobian restore-db` | BL-68 cz. 2 |
| 85 | Jakość | miejsce w stagingu (KB-3102), SCR-08 odtwarzanie bazy w UI | BL-68 cz. 3 |
| 86 | Bezpieczeństwo | fuzzing gRPC Agenta (31 metod × 120 ładunków), przepełnienie TimeSpan w retencji (Niskie, naprawione) | RD-44 |
| 87 | R&D | DedupTable/ChunkKey — pamięć dedup 4–5× mniejsza (zmierzone w chmurze) | RD-22 w1 |
| 88 | Wydajność | Kobian.Bench --e2e, strażnik DedupMemoryPerfTests, odświeżone zrzuty | D3.15 |
| 89 | Dokumentacja | wysyłka packu bez kopii, przewodnik admina odtwarzanie-i-ustawienia PL/EN | BL-69, RD-48 |
| 90 | Przegląd rundy | Round6FuzzTests (5 celów), naprawa ui-settings.json (Niskie), BL-70 `latest:<zadanie>` / `--job`, audyt, R&D | BL-71, BL-70, SEC-3.3 |

## Status DoD
Wszystkie ekrany nawigacji GUI istnieją (SCR-01/02/03/06/07/08/09/10/11) na prawdziwym Agencie przez gRPC; odtwarzanie plików i bazy MSSQL (do nowej bazy) z UI i CLI; łańcuch audytu z weryfikacją. Testy Windows w kolejce (W-29…W-32). Brak: SMB, usługa Windows/Worker, MSI, kotwice dzienne audytu, PlanRestore (BL-64 częściowe), nadpisanie bazy produkcyjnej (AC-32.2).

## NFR
Zmierzone w tej rundzie (chmura, informacyjnie — NIE sprzęt referencyjny): pamięć dedup 1 mln bloków 354 → 69 MiB, 3 mln 1144 → 298 MiB (it. 87); Bench --e2e: pełna 104 MiB/s, przyrost 1,1 s, restore 95 MiB/s, szczyt 273 MiB (it. 88); RD-48 bez mierzalnej zmiany szczytu (it. 89). Wszystkie NFR na sprzęcie referencyjnym: **niezmierzone**. ci.sh it. 90 (chmura): restore 17 s, build 51 s, testy 95 s.

## Spike’i
Bez zmian: SP-03 SFTP in-proc zielone (W-13); spike’i MSSQL zielone na serwerze zastępczym, na prawdziwym SQL Server — wymaga Windows (W-23…W-28, W-31).

## Przegląd bezpieczeństwa rundy (rola 11, it. 90) — szczegóły w bezpieczenstwo.md
- `dotnet list package --vulnerable --include-transitive`: 0 podatnych (SDK 10.0.402, SHA-512 zgodny).
- Skan sekretów zielony; zakazane API: brak użyć w src/.
- Fuzzing Round6FuzzTests ≈ 35 000 przypadków: SplitPath, PlanMoves (FILELISTONLY), SqlServerAllowList, ui-settings.json, VerifyAudit po ręcznej zmianie wiersza.
- Naprawione (Niskie): niepoprawne UTF-8 w ui-settings.json blokowało start GUI; BL-70 (latest = wszystkie zadania).
- Otwarte: Średnie — W-17/W-30 (tożsamość klienta pipe, Windows), RD-22 (brak twardego limitu RAM dedup); Niskie — RD-51 (Verify przy odbudowanym schemacie audit_log). Wysokich brak.

## Ryzyka i dług
- Build lokalny na urządzeniu nadal niemożliwy (`/sessions` 100%) — build w chmurze (pack.tgz → stage → it<N>.tgz → commit).
- Pokrycie AC: 29/59 Must z testem (+3 w rundzie).
- `rd-add` nadaje zduplikowane numery — poprawki ręczne.
- Qb3–Qb39 bez odpowiedzi właściciela — działają wartości prowizoryczne.
- Brak instalowalnej wersji (usługa + MSI) — największe ryzyko harmonogramu do M1.

## Propozycja kursu rundy 7 (Qb40)
Host usługi Windows/Worker (port + adapter, W-…) → SMB (BL-3x) → kotwice dzienne audytu (D9.9) + RD-51 → RD-22 warstwa 2 (Bloom) → PlanRestore w SCR-07 (BL-64) → MSI (WiX, S2=B).
