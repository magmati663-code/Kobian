# Runda 8 budowy (iteracje 106–120) — podsumowanie

Data: 2026-10-17. Zakres bez zmian (S1=B, S2=B, S3=B, S4=C; Docker i git nie). Kurs: Qb54 (PROWIZORYCZNIE) — „Worker, wznowienia odtwarzania, naprawa i powiadomienia”. Iteracja 120 wykonana w sesji ręcznej z agentami (polecenie właściciela 2026-10-17: „zrób jak najwięcej, 5 iteracji”) — oprócz przeglądu rundy objęła BL-45 (e-mail) i BL-34 (FTP/FTPS).

## Zadania (ID planu)
| It. | Rola | Wynik | ID planu |
|---|---|---|---|
| 106 | PO | plan rundy 8 (sekcja N); launcher Workera (rola z listy, podpis KB-7015, ArgumentList, job object) | BL-79 |
| 107 | Architekt | dziennik wznowień odtwarzania `.kobian-partial` + CatalogRestoreJournal | BL-80, D8.8 |
| 108 | Silnik | postęp odtwarzania (RD-60) + PlanRestore świadomy dziennika (RD-68) | BL-80 |
| 109 | Transport | check --repair: RepoDamageAssessor, migracja damage | BL-81, D8.10 |
| 110 | Bazy | RepoIndexRecovery (packi-sieroty z trailerów), fallback indeksów KB-1505 | BL-81 |
| 111 | UX | webhook D9.10 + outbox | BL-82, ADR-0037/Qb58 |
| 112 | Harmonogramy | raport dzienny report.ready.v1 z kotwicą audytu | BL-82, RD-61, Qb59 |
| 113 | Odtwarzanie | baza przyrostu strumieniowo (BaselineSet) — zmierzone 144 → 38 B/blok | BL-83 |
| 114 | Monitoring | AIMD D4.16 pkt 2 + repo_dest_parallelism | BL-84, Qb60 |
| 115 | Jakość | znacznik uszkodzonej kopii w CLI i UI | RD-70 |
| 116 | Bezpieczeństwo | CatalogMaintenance (30 dni, RODO) | RD-69, Qb61 |
| 117 | R&D | PRAGMA secure_delete (zmierzone +5…13% czasu usuwania) | RD-74 |
| 118 | Wydajność | równoległe wysyłki packów w silniku | RD-73, Qb62 |
| 119 | Dokumentacja | równoległe wysyłki w Agencie | RD-73, ADR-0038/Qb63 |
| 120 | Przegląd rundy | Round8FuzzTests (5 Niskich naprawionych), RD-75 pula/budżet buforów packów (Qb74); BL-45 e-mail SMTP (MailKit 4.18.1, Qb64–67); BL-34 dostawca FTP/FTPS (FluentFTP 55.0.0, pin SPKI, Qb68–73) | SEC-3.3, RD-75, BL-45, BL-34 |

## Status DoD
Kurs Qb54 zrealizowany po stronie Linuksa. Weryfikacja na Windows/serwerach: W-45…W-50 (kolejka). Instalowalna wersja nadal nie istnieje fizycznie (MSI nie zbudowany — Windows; VSS — zaślepka do SP-01).

## NFR
Zmierzone w tej rundzie (chmura, informacyjnie — NIE sprzęt referencyjny): BaselineSet 144 → 38 B/blok (it. 113), secure_delete +5…13% (it. 117), równoległe wysyłki pełna +17% (it. 119); it. 120 Bench `--e2e 800` (2 rdzenie, maszyna współdzielona — orientacyjnie): 3 sesje/pack 16 MiB szczyt 630/550 → 385/415 MiB, przepustowość 152/147 → 174/172 MiB/s; pack 64 MiB szczyt 1319/955/1271 → 585/811 MiB. Wszystkie NFR na sprzęcie referencyjnym: **niezmierzone**. ci.sh it. 120 (chmura): Unit 1339, UI 277, Arch 16, E2E 3, DDL OK, audyt czysty.

## Spike’i
SP-01 (VSS), SP-04 (FTPS vsftpd require_ssl_reuse) — wymagają Windows/serwerów (W-49). Pozostałe bez zmian.

## Ryzyka i długi
- Średnie: FluentFTP nie waliduje certyfikatu kanału danych (SP-04).
- Niskie otwarte: F7 (podstawiony `.kobian-partial` usuwany), sekrety w plikach bez kontroli ACL (RD-71), brak alertu dla dead e-mail, okno wyścigu RNTO na FTP (Qb71).
- Dług: duplikat RD-72 → RD-75 (poprawione w it. 120), statusy BL w backlogu kumulatywne (czytać od końca).

## Kurs rundy 9 (ustalenie właściciela 2026-10-17)
Kobian Central odłożony. Najpierw braki do 1.0 budowalne w Linuksie: pinning tożsamości D4.11 (dest_identity) i FTPS w kreatorze, eksport/import konfiguracji (US-29/US-37), alerty A01–A17 w Agencie przez outbox (webhook + e-mail), V3 z harmonogramu, konsolidacja indeksów D3.16, planer odtwarzania po packach, zapytania kontrolne MSSQL D5.11, EN dla pola action KB, repozytoria złote NFR-14, kobian-restore na innych celach (US-33). Równolegle kolejka Windows.
