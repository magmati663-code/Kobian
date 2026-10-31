# Runda 7 budowy (iteracje 91–105) — podsumowanie

Data: 2026-10-17. Zakres bez zmian (S1=B, S2=B, S3=B, S4=C; Docker i git nie). Kurs: Qb40 (PROWIZORYCZNIE) — „instalowalna usługa”. Iteracje 98–105 wykonane w jednej sesji (polecenie właściciela: kolejne 20 iteracji po przebiegu zaplanowanym).

## Zadania (ID planu)
| It. | Rola | Wynik | ID planu |
|---|---|---|---|
| 91 | PO | plan rundy 7 (sekcja M); host usługi Windows: profil, ścieżki usługi, pojedyncza instancja KB-7007, ACL pipe | BL-72, ADR-0024/Qb41 |
| 92 | Architekt | dostawca SMB cz. 1: port ISmbConnector, UncPath, SmbErrorMap, WNetSmbConnector | BL-73, ADR-0025/Qb42 |
| 93 | Silnik | SMB cz. 2: cel smb/sftp w definicji zadania, CLI `init --dest-scheme`, blokada per serwer | BL-73, ADR-0026/Qb43 |
| 94 | Transport | SMB cz. 3: watchdog 30 s, kolejka D7.4 `smb:<serwer>`, kreator S2 | BL-73, ADR-0027/Qb44 |
| 95 | Bazy | tożsamość klienta pipe (role z tokenu, KB-7001, actor_sid) | BL-75 (BL-17), ADR-0028/Qb45 |
| 96 | UX | RunJob przez wspólną kolejkę, baner „Tryb podglądu” | BL-74, RD-57, ADR-0029/Qb46 |
| 97 | Harmonogramy | ręczne uruchomienie jako wiersz Queued; dławienie otwarć sesji D4.16 pkt 3 | BL-76, BL-77, ADR-0030/Qb47 |
| 98 | Odtwarzanie | PlanRestore (podgląd: pliki, konflikty, miejsce, prawo zapisu) w Engine/Agencie/CLI/UI, KB-5106 | BL-64, R2.8.1, ADR-0031/Qb48 |
| 99 | Monitoring | kotwice dzienne audytu 06:55, `audit verify --anchor`, kontrola schematu (KB-6502/6503) | BL-65, D9.9, RD-51, ADR-0032/Qb49 |
| 100 | Jakość | szkielet MSI (Package.wxs poza sln) + `Kobian.Agent --provision/--unprovision` | BL-48, D10.2, D2.12, ADR-0033/Qb50 |
| 101 | Bezpieczeństwo | AccessCheck tokenem klienta przy odtwarzaniu (SEC-6.3) | BL-64, SEC-6.3, Qb51 |
| 102 | R&D | dedup: tablica posortowana 32 B/blok + filtr Blooma (zmierzone) | RD-22 w2, D3.15, ADR-0034/Qb52 |
| 103 | Wydajność | protokół usługa ↔ Worker (rola vss), zaślepka VSS do SP-01 | BL-16, D2.8, ADR-0035/Qb53 |
| 104 | Dokumentacja | SCR-11 ostatnia kotwica; dokumentacja admina MSI/`--provision`, SEC-6.3, Worker | BL-66, D9.9 |
| 105 | Przegląd rundy | Round7FuzzTests (6 celów, ≈ 100 tys. przypadków), naprawa Niskie (KOBIAN_CATALOG = korzeń), audyt, R&D, kurs rundy 8 | SEC-3.3, BL-72 |

## Status DoD
Kurs Qb40 zrealizowany w całości po stronie Linuksa: usługa Windows (profil, ACL, tożsamość klienta), SMB, kotwice audytu + RD-51, RD-22 w2, PlanRestore, szkielet MSI; dodatkowo SEC-6.3 i protokół Worker. Weryfikacja na Windows w kolejce W-33…W-44 (12 nowych pozycji w rundzie). „Instalowalna wersja” nadal NIE istnieje fizycznie: MSI nie zbudowany (Q37 + Windows), VSS — zaślepka (SP-01).

## NFR
Zmierzone w tej rundzie (chmura, informacyjnie — NIE sprzęt referencyjny): pamięć dedup bloków bazowych 32 B/blok (było 73–104), wyszukiwanie 0,9–1,8 mln/s, FP Blooma 0,80–0,84% (it. 102, pomiary.md); Bench --e2e 300 po zmianie: pełna 49,4 MiB/s, przyrost 2,8 s, restore 40,1 MiB/s, szczyt 345 MiB — jeden przebieg, nieporównywalny z it. 88. Wszystkie NFR na sprzęcie referencyjnym: **niezmierzone**. ci.sh it. 105 (chmura): restore 5 s, build 9 s (przyrostowo), testy 130 s; Unit 1107, UI 271, Arch 15, E2E 3.

## Spike’i
SP-01 (VSS w Worker) — nierozpoczęty: protokół i host gotowe, wybór CsWin32/AlphaVSS wymaga audytu pakietu i Windows (W-44). SP-02 (tożsamość pipe) — logika zielona w Linuksie, adapter: W-17/W-37. SP-13/SP-16 — wymagają Windows.

## Przegląd bezpieczeństwa rundy (rola 11, it. 105) — szczegóły w bezpieczenstwo.md
- `dotnet list package --vulnerable --include-transitive`: 0 podatnych (24 projekty; SDK 10.0.402, SHA-512 zgodny).
- Skan sekretów (test NFR-11) zielony; zakazane API (test architektury) — brak użyć.
- Fuzzing Round7FuzzTests ≈ 100 000 przypadków (UncPath, AgentPaths, ExactPathSelection, AuditAnchor.TryParse, WorkerJson, AgentInstallPlan.DataDir) + GrpcFuzzTests z ziarnami 105 i 7777 (33 metody).
- Naprawione (Niskie): `KOBIAN_CATALOG` wskazujący korzeń/katalog dawał względny katalog stanu „.” (sekrety w katalogu bieżącym usługi = System32) — teraz błąd konfiguracji (kod 64).
- Zamknięte w rundzie: Średnie z it. 92 (blokada globalna SMB), Średnie z it. 98 (SEC-6.3 — warunkowo, W-43), Niskie RD-51.
- Otwarte: Średnie — kotwice audytu tylko w katalogu (kopia zewnętrzna ręczna; Qb49/BL-45); Niskie — adapter instalatora niezweryfikowany na Windows (W-42), launcher Workera bez job object/podpisu (RD-65), TOCTOU folderu docelowego (Qb51), klucz breakera per cel (RD-59). Wysokich brak.
- Threat model (STRIDE) — nowe elementy rundy z tabelami w bezpieczenstwo.md: usługa/pipe z rolami z tokenu (it. 95), SMB (it. 92–94), PlanRestore + AccessCheck (it. 98, 101), kotwice (it. 99), CA instalatora (it. 100), kanał Worker (it. 103).

## Ryzyka i dług
- Build lokalny na urządzeniu nadal niemożliwy (`/sessions` 100%, token blokady trzeba trzymać w `.toolchain/home`) — build w chmurze.
- 40+ pytań Qb bez odpowiedzi właściciela (Qb3–Qb53) — działają wartości prowizoryczne; Q37 (WiX OSMF) blokuje pierwszą budowę MSI.
- Brak Windows w pętli: 44 pozycje kolejki W-… czekają na uruchomienie przez właściciela.
- Pokrycie AC: bez większych zmian w tej rundzie (rundy 7 skupione na usłudze/bezpieczeństwie).
- `rd-add` nadal nadaje zduplikowane numery — poprawki ręczne (sed na ostatnim wierszu).

## Propozycja kursu rundy 8 (Qb54)
Launcher Workera (job object, brak dziedziczenia uchwytów, weryfikacja podpisu, KB-7008; RD-65) → dziennik wznowień odtwarzania `restore_*` (D8.8) + postęp (RD-60) → `check --repair` (BL-43/BL-26) → powiadomienia e-mail/webhook + kotwica w raporcie (BL-45, RD-61; audyt MailKit) → strumieniowe ładowanie indeksów bazowych (RD-64) → AIMD D4.16 pkt 2.
