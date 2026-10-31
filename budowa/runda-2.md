# Runda 2 budowy (iteracje 16–30) — podsumowanie

Data: 2026-10-13. Zakres bez zmian (S1=B, S2=B, S3=B, S4=C, S5: SDK i NuGet tak, Docker i git nie; usuwanie tylko w folderze projektu).

## Zadania (ID planu)
| It. | Rola | Wynik | ID planu |
|---|---|---|---|
| 16 | PO | 23 pliki Gherkin (64 scenariusze), ścieżka krytyczna (Qb8) | BL-6, D1.4, D1.7 |
| 17 | Architekt | katalog SQLite: utwardzenie, migracje, CatalogWriter | BL-15, D2.11, D2.14 |
| 18 | Silnik | kodek drzewa CBOR, koperta metadanych, V-07/V-08 | BL-21, D3.8, R2.3.5 |
| 19 | Transport | token bucket D4.9 | BL-30, NFR-33 (algorytm) |
| 20 | Bazy/ERP | staging D5.10, łańcuch D5.2, tolerancje D5.11 | BL-40 |
| 21 | UX | WizardViewModel, KbMessages PL/EN | BL-46, BL-4, AC-01.2, AC-28.1 |
| 22 | Harmonogramy | DDL D7.7 + weryfikator | BL-42 |
| 23 | Odtwarzanie | dziennik wznowień D8.8 (DDL) | BL-43 |
| 24 | Monitoring | reguły A01–A17 jako dane, AlertEvaluator | BL-45, D9.8 |
| 25 | Jakość | ci.sh z etapami i audytem | BL-8 |
| 26 | Bezpieczeństwo | limit zakresów reguły (SEC-3.3) | BL-45 |
| 27 | R&D | MODEL protokołu D3.14 (nie pomiar) | SP-18 (w toku), RD-19 |
| 28 | Wydajność | MODEL filtra Bloom D3.15 (nie pomiar) | SP-19 (w toku), RD-20 |
| 29 | Dokumentacja | docs/README.md — stan modułów, narzędzia | D10.4 |
| 30 | Przegląd rundy | ten dokument, audyt, R&D do planu | — |

## Status DoD
Nadal brak historii US spełniającej DoD end-to-end: brak potoku backupu (BL-22), wykonawcy restore (BL-43), CLI (BL-47) i hosta Agenta (BL-15). Runda 3 zaczyna od tego (sekcja H backlogu).

## NFR
Zmierzone w tej rundzie: NFR-33 — wyłącznie dokładność algorytmu na wirtualnym zegarze (±10%, `pomiary.md`). Modele (nie pomiary): D3.14 (0/600), D3.15 (FP ≈ 0,8%). Pozostałe NFR: **niezmierzone**.

## Spike’i
SP-18, SP-19 — w toku (modele gotowe; test na kodzie wymaga potoku). Pozostałe jak w rundzie 1 (Windows / serwery / właściciel).

## Przegląd bezpieczeństwa rundy (rola 11)
- Audyt zależności: `dotnet list package --vulnerable --include-transitive` — 0 podatnych (it. 25, 26, powtórzony 2026-10-13 w it. 30 w środowisku chmurowym z SDK 10.0.401, SHA-512 zgodny).
- Skan sekretów: test `SecretScanner` w zestawie Unit — zielony (it. 30, `tools/ci.sh --locked`: build 0 ostrzeżeń, testy OK, DDL OK, audyt OK).
- Zakazane API: test architektury BannedApiTests zielony.
- Threat model: nowe elementy rundy (katalog SQLite, reguły alertów) — STRIDE w `bezpieczenstwo.md` it. 17, 24, 26; bez znalezisk Wysokich w rundzie.

## Ryzyka i dług
- PM-1: ścieżka krytyczna ruszyła tylko w katalogu; potok i restore — priorytet rundy 3.
- Przebiegi zaplanowane bez SDK (Qb7) — iteracje 22, 23, 27, 28 bez kompilatora; od it. 30 SDK instalowany w chmurze sesji ręcznej.
- Dług: AC Must oznaczone 12/59; DNS rebinding webhooków; TOCTOU local (Windows); lista haseł (Qb5).
