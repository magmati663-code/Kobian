# Odpowiedzi startowe

- START MIMO NIEDOKOŃCZONEGO PLANU: tak — decyzja właściciela (2026-10-12): etap planowania zakończony po iteracji 24 (status 25 pozostaje); rundy 4 i 5 nie powstały.
- BACKLOG: w planie brak zadań T-… i epików EP-… (runda 4 nie powstała). Product Owner w iteracji 1 tworzy zadania BL-n ze specyfikacji: R2.x (D1.4–D10.9), spike'i SP-01…SP-24, AC, NFR-01…NFR-34, KOSZT-*, kody KB, korekty K*. Estymaty z KOSZT-* i R3.x; kamienie M0/M1/M2/1.0 z roadmapy 10.8 i D1.4.
- Ankieta startowa (S1–S10) nadal obowiązuje — brak odpowiedzi = nie piszemy kodu.
- S1: B — pliki + bazy MSSQL/ERP wg D1.3 (właściciel, 2026-10-12)
- S2: B — MSI per-machine z usługą Windows (właściciel)
- S3: B — GUI Avalonia + CLI równolegle (właściciel)
- S4: C — tylko Linux w piaskownicy; moduły Windows weryfikuje właściciel później (właściciel)
- S5: (a) tak, (b) tak, (c) nie podano → NIE (bez Dockera), (d) nie podano → NIE (bez git i bez usuwania plików; pliki do usunięcia → _to_delete/) (właściciel: „a b”)
- S6: runda = 15 iteracji — USTAWIONE (narzędzie i prompt zaktualizowane, 2026-10-12; iteracja 15 = przegląd rundy: bezpieczeństwo + R&D); tempo co 1 h i limit ≤ 800 linii kodu + testy — domyślne.
- S7: A — standard planu (właściciel)
- S8: tak — UI PL/EN; Windows 10 22H2 / 11 / Server 2016–2025 (właściciel)
- S9: tak — open-core Apache-2.0; nazwa wg Q39 (nadal otwarte, używaj „Kobian Backup”) (właściciel)
- S10: domyślnie, powiadomienie na koniec rundy (+ wysoka podatność / potrzebna decyzja / błąd przebiegu) (właściciel)
- S5(d) korekta (właściciel, 2026-10-12): usuwanie plików DOZWOLONE, ale wyłącznie w obrębie folderu roboczego „kobian backup” (katalogi tymczasowe, artefakty budowy, własne pliki projektu). Nic poza tym folderem. Git: nadal nie podano → bez git. Docker (S5c): nadal nie.
