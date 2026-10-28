# Runda 9 budowy (iteracje 121–135) — podsumowanie

Data: 2026-10-17…18. Zakres bez zmian (S1=B, S2=B, S3=B, S4=C; Docker i git nie). Kurs rundy 9 (ustalenie właściciela 2026-10-17): braki do 1.0 budowalne w Linuksie; Kobian Central odłożony.

## Zadania (ID planu)
| It. | Rola | Wynik | ID planu |
|---|---|---|---|
| 121 | PO | D4.11 tożsamość serwera (ServerIdentityGate, RPC, CLI), FTPS w kreatorze i SCR-09, US-29 eksport/import | BL-33/34, US-29 |
| 122 | Architekt | alerty A01–A17 (outbox webhook + e-mail), V3 z harmonogramu, konsolidacja indeksów D3.16 | BL-26, BL-25, BL-45 |
| 123 | Silnik | zapytania kontrolne ERP D5.11 (KB-3010/3505) | BL-40, BL-68 |
| 124 | Transport | pole action.en dla 139 kodów KB, tools/kb.py | BL-14 (i18n) |
| 125–128 | Bazy/UX/Harmonogramy/DR | repozytoria złote #1–#3 (basic, keyed, pruned); kobian-restore na celach SFTP/FTPS | BL-9, BL-28, BL-44, NFR-14 |
| 129–133 | Monitoring/Jakość/Bezp./R&D/Wyd. | e-mail OAuth2 M365, Ack/Silence alertów, pulpit alertów SCR-01, `kobian status` z licznikiem, wybór czasu wyciszenia | BL-45, BL-82 |
| 134 | Dokumentacja | RPC WatchRun + `kobian watch`; bramka ról dla metod strumieniowych (Wysokie naprawione) | BL-64 |
| 135 | Przegląd rundy | audyt zależności, skan sekretów/banowanych API, RD-76 (podstawiony `.kobian-partial` nie jest usuwany), runda-9.md, rd-table → plan.md | RD-76, SEC |

## Status DoD
Wszystko po stronie Linuksa zrealizowane i zielone w chmurze (SDK 10.0.402, sha512 OK): ci.sh --locked — Unit 1561, UI 350, Arch 16, E2E 3, DDL OK, audyt czysty (ac-raport w chmurze zawsze pada bez plan.md; ac-raport uruchamiany na urządzeniu — ostatnio 33/59 Must, w tym przebiegu nie przeliczany). Lokalny build na urządzeniu nadal niemożliwy (brak miejsca w /tmp VM — NuGet „No space left on device”). Weryfikacja na Windows/serwerach: kolejka W-* (do W-57). Instalowalna wersja nadal nie istnieje fizycznie (MSI nie zbudowany).

## NFR
W tej rundzie (it. 121–135) niezmierzone poza wynikami z wcześniejszych rund (pomiary.md); w it. 135 nie uruchamiano benchmarków.

## Spike’i
SP-01 (VSS), SP-04 (FTPS require_ssl_reuse), SP-22 (SMB) — wymagają Windows/serwerów. Bez zmian.

## Bezpieczeństwo (przegląd całościowy rundy, it. 135)
- Audyt zależności: `dotnet list package --vulnerable --include-transitive` — 0 podatnych w 23 projektach (chmura, 2026-10-18).
- Skan sekretów (wzorce klucze prywatne/AWS/Slack/GitHub w src, tests, docs, tools, specs, installer): jedyne trafienia to wektory testowe skanera w SecurityTests.cs (zamierzone).
- Zakazane API (ScpClient, BinaryFormatter, wyłączanie walidacji certyfikatu, MD5/SHA1, UseShellExecute) w src: brak.
- Znaleziska rundy: Wysokie (it. 134, interceptor strumieniowy) — naprawione przed wdrożeniem. Niskie otwarte: F7 częściowo (RD-76 → pełna tożsamość pliku roboczego = RD-78), brak step-up dla wyciszania (Qb82/Qb106, RD-80), sekrety w plikach bez kontroli ACL (RD-71 z rundy 8).

## Ryzyka i długi
- Ścieżka build tylko przez chmurę (pack → stage → ci.sh); paczka MUSI zawierać `installer/` (inaczej InstallerTests pada).
- Duplikaty numerów RD przy `rd-add` (numeracja wg liczby wierszy) — poprawiane ręcznie (it. 135: RD-78…80).
- Niskie: statusy BL kumulatywne w backlogu (czytać od końca).

## Kurs rundy 10 (PROWIZORYCZNIE, bez czekania na właściciela)
Widok postępu SCR-07 w UI (RD-79), step-up Qb82 (RD-80), pełna tożsamość pliku roboczego (RD-78), SMB w kobian-restore, retencja GFS i pozostałe punkty ścieżki krytycznej; moduły Windows (MSI, VSS) czekają na kolejkę W-*.
