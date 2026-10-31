# Runda 1 budowy (iteracje 1–15) — podsumowanie

Data: 2026-10-12. Zakres wg ankiety: S1=B (pliki + MSSQL/ERP), S2=B (MSI + usługa), S3=B (Avalonia + CLI), S4=C (Linux; Windows później), S5: SDK i NuGet tak, Docker i git nie (usuwanie plików tylko w folderze projektu). Środowisko budowy: Linux w chmurze sesji z .NET SDK 10.0.401 (SHA-512 zweryfikowany); kod synchronizowany do folderu projektu archiwami ze skrótem (`tools/pack-sync.sh` / `tools/apply-sync.sh`).

## Zamknięte i rozpoczęte zadania (ID planu)
| It. | Rola | Wynik | ID planu |
|---|---|---|---|
| 1 | PO | backlog (BL, KOSZT, SP, US, NFR, AC) | D1.4, R2.1 |
| 2 | Architekt | SDK, solucja 17 projektów, test architektury, konwencje, CI lokalne | D2.7, K22.1, KOSZT-1 |
| 3 | Silnik | hierarchia kluczy, pack writer/reader, kody KB, wektory V-01/02/05/06 zgodne z niezależną implementacją | D3.9, D3.10, R2.3.8 |
| 4 | Transport | kontrakt magazynu, dostawca local, mapa błędów | D4.6–D4.8 |
| 5 | Bazy/ERP | T-SQL discovery/pre-check, szablon BACKUP COPY_ONLY, skrypt uprawnień, profile ERP | R2.5, D5.7–D5.9, AC-02.x, AC-30.1 |
| 6 | UX | maszyna stanów kreatora, walidacja S1/S4/S5, klucz odzyskiwania | D6.7, K14.1, AC-28.1 |
| 7 | Harmonogramy | maszyna stanów uruchomień, cron + DST, misfire, blokady | D7.8, D7.10, E1–E3, E6, E10, AC-03.1/03.3 |
| 8 | Odtwarzanie | MapPath R1–R6/R9, planer odczytów zakresowych | D8.9, R2.8.1, AC-08.7 |
| 9 | Monitoring | CloudEvents, alerty, łańcuch audytu, webhook, JCS | D9.6, D9.7, D9.9, D9.10 |
| 10 | Jakość | generator korpusu, raport AC→test, kolejka Windows | D10.9, D1.7 |
| 11 | Bezpieczeństwo | zakazane API, skaner sekretów, fuzzing — 1 znalezisko naprawione | SEC-3.3, NFR-11 |
| 12 | R&D | pętla schedulera + symulacja (RD-5) — 1 znalezisko naprawione | D7.9 |
| 13 | Wydajność | kompresja zstd z ochroną przed bombą, pomiar etapu | D3.3, NFR-24, SP-17 |
| 14 | Dokumentacja | README, specyfikacja zaimplementowanego formatu, kb-codes.md, licencje | D1.8, D10.4 |
| 15 | Przegląd rundy | ten dokument, audyt, R&D do planu | — |

Stan backlogu: BL — 5 zrobione, 20 częściowe, 21 zablokowane, 2 wymaga Windows, 1 odroczone; SP — 1 w toku (SP-17), 8 wymaga Windows, 11 gotowe (wymagają serwerów lub ludzi), 3 zablokowane (właściciel), 1 odroczone; US — żadna historia nie spełnia DoD end-to-end (brak potoku backup/restore i hosta usługi).

## Status DoD
Żadne zadanie BL dotyczące funkcji produktu nie jest „zrobione” w rozumieniu DoD (brak E2E, testy Windows niewykonane). „Zrobione”: BL-1 (SDK), BL-2 (szkielet), BL-3 (test architektury), BL-7 (skaner sekretów), BL-11 (skrypt kolejki Windows).

## NFR
Zmierzone w tej rundzie: **NFR-24 — wstępnie 171,5 MB/s na rdzeń** (środowisko budowy 2 vCPU, dane syntetyczne 50% kompresowalne; nie sprzęt referencyjny — `budowa/pomiary.md`). Pozostałe 33 NFR: **niezmierzone**.

## Spike’i
SP-17 — wynik wstępny „obiecujący”, decyzja go/no-go wymaga sprzętu referencyjnego i korpusu D10.9. SP-01, 02, 11, 13, 15, 16, 20, 22 — wymaga Windows. SP-08, 09, 12 — wymagają właściciela (bazy demo, wywiady, konfiguracje Cobian). Pozostałe — wymagają serwerów testowych (Docker niedozwolony — S5c).

## Ryzyka (PM) — obserwacje z budowy
- PM-1 (poślizg 1.0): fundamenty gotowe, ale ścieżka krytyczna (Agent + katalog + potok) jeszcze nie ruszyła; runda 2 musi ją rozpocząć.
- Brak Dockera i Windows → integracje (SFTP, SMB, SQL Server, VSS, usługa) nieweryfikowalne; ryzyko odkrycia problemów późno (wzmacnia PM-21…27).
- Zależność od środowiska chmurowego do kompilacji — przebiegi zaplanowane na komputerze bez SDK mogą tylko dokumentować (do decyzji Qb7).

## Dług techniczny
- `docs/ac-coverage.md`: 11/56 AC Must oznaczonych testami (oznaczenie ≠ spełnienie).
- Lista popularnych haseł tymczasowa (Qb5); R8 restore i TOCTOU dostawcy local (Windows); DNS rebinding webhooków.
- Kody KB nowe w budowie (KB-0001, 1103, 1309, 2303) — do akceptacji PO.
- Brak `.resx` i generatora komunikatów; brak plików `specs/*.feature` (BL-6).
