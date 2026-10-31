# Zmiany statusu kandydatów R&D (dopisywane)

| Kandydat | Runda | Nowy status | Uzasadnienie |
|---|---|---|---|
| RD-1 | 1 | przyjęty | wdrożony w it. 3 (`tools/vectors/ref_v1.py`, wektory V-01/02/05/06); zgodny z Q17 |
| RD-5 | 1 | przyjęty | wdrożony w it. 12 (`SchedulerCore` + symulacja); znalazł błąd startu po cofnięciu zegara |
| RD-2 | 1 | odroczony | do rundy 2 — potrzebny przy potoku backupu end-to-end (Qb8) |
| RD-6 | 1 | odroczony | do rundy 2 — razem z drzewami snapshotów |
| RD-8, RD-9 | 1 | odroczony | wymagają audytu nowych narzędzi (Stryker.NET, SharpFuzz) |
| RD-4 | 1 | odroczony | zależny od decyzji Qb5 (lista haseł) |
| RD-2 | 2 | odroczony | do rundy 3 — wejdzie razem z potokiem (RD-21) |
| RD-19, RD-21 | 2 | przyjęty do rundy 3 | test SP-18 na kodzie po zbudowaniu potoku |
| RD-19, RD-21 | 3 | w toku | SP-18 in-proc zrobiony w it. 34; harness chaos na kodzie nadal otwarty |
| RD-18 | 3 | przyjęty | wdrożony w it. 42 (etap E2E, pełny→przyrost→restore); wariant na korpusie D10.9 otwarty |
| RD-19, RD-21 | 3 | w toku | SP-18 in-proc zrobiony w it. 34; harness chaos na kodzie nadal otwarty |
| RD-18 | 3 | przyjęty | wdrożony w it. 42 (etap E2E, pełny→przyrost→restore); wariant na korpusie D10.9 otwarty |
| RD-26 | 4 | przyjęty (część) | wdrożony w it. 54: wyzwalacze cron z katalogu w Agencie (CatalogScheduler); brak RPC/CLI i hot-reload → RD-27 |
| RD-27 | 4 | przyjęty | wdrożony w it. 55: RPC ListSchedules/SetSchedule, kobian schedule list/set/off, SchedulerCore.Reconfigure bez restartu (kod z przerwanego przebiegu, zweryfikowany ci.sh) |
| RD-22 | 4 | odroczony | do rundy 5 (po BL-60/adapterze kreatora); wymaga decyzji projektowej D3.15 warstwy 2–3 |
| RD-29 | 4 | kandydat | regresja wizualna UI — runda 5 razem z kolejnymi ekranami |
| RD-31, RD-32 | 4 | kandydat | zgłoszone w przeglądzie rundy 4 (it. 60); RD-32 częściowo wdrożone (IsSafeName w FileSftpCredentialProvider) |
| RD-48 | 6 | przyjęty | wdrożony w it. 89 (VerifiedUploader ReadOnlyMemory, FlushPackAsync bez ToArray); szczyt procesu w Bench bez mierzalnej zmiany (pomiary.md) |
| RD-39 | 6 | przyjęty | wdrożony w it. 78 (SqlServerAllowList, KOBIAN_SQL_ALLOWED_SERVERS, KB-3009); fuzzing konfiguracji w it. 90 |
| RD-42 | 6 | przyjęty | wdrożony w it. 79 (audit_log, VerifyAudit); kotwice dzienne otwarte (runda 7) |
| RD-44 | 6 | przyjęty | wdrożony w it. 86 (GrpcFuzzTests) |
| RD-22 | 6 | przyjęty (część) | warstwa 1 w it. 87 (DedupTable, zmierzone 4–5× mniej pamięci); warstwa 2 (Bloom) — runda 7 |
| RD-49 | 6 | kandydat | alokacje GC w Bench — runda 7 razem z RD-22 w2 |
| RD-50, RD-51 | 6 | kandydat | zgłoszone w przeglądzie rundy 6 (it. 90); RD-51 w kursie rundy 7 (Qb40) |
| RD-57 | 7 | przyjęty (część) | w1 w it. 96: baner trybu podglądu SCR-01 + TokenElevationInfo; RPC GetCallerInfo niepotrzebny (rola w GetStatus) — ADR-0029 |

| RD-22 | 7 | przyjęty | warstwa 2 w it. 102: tablica posortowana 32 B/blok (zmierzone), Bloom zaimplementowany i domyślnie wyłączony po pomiarze; warstwa 3 (katalog) — gdy potrzebna |
| RD-51 | 7 | przyjęty | wdrożony w it. 99 (kontrola schematu audytu, KB-6502) |
| RD-60 | 7 | kandydat | postęp odtwarzania — kurs rundy 8 (Qb54) |
| RD-61 | 8 | wdrożony (it. 112) | kotwica `seq:sha256` w raporcie dziennym przez webhook (Qb59); e-mail później |
| RD-62 | 7 | odroczony | generator instalatora — po pierwszej budowie MSI (W-42, Q37) |
| RD-63 | 7 | kandydat | właściciel plików odtworzonych = klient |
| RD-64 | 8 | wdrożony (it. 113) | BaselineSet; pomiar 144 → 38 B/blok trzymane (pomiary.md) |
| RD-65 | 7 | kandydat | launcher Workera — kurs rundy 8 (pierwszy) |
| RD-70 | 8 | wdrożony (it. 115) | znacznik w ListSnapshots, CLI i SCR-06/SCR-10 |
| RD-69 | 8 | wdrożony (it. 116) | CatalogMaintenance: restore_* i outbox po 30 dniach (Qb61) |
| RD-74 | 8 | wdrożony (it. 117) | secure_delete=ON; koszt usuwania +5…13% (pomiary.md) |
| RD-73 | 8 | w toku (it. 118) | silnik: wysyłki równoległe na osobnych sesjach + AIMD; Agent — it. 119 |
| RD-73 | 8 | wdrożony (it. 119) | Agent: N sesji wysyłki przez fabrykę/governor, limit AIMD z katalogu, zapis po uruchomieniu (ADR-0038/Qb63); Local +~17% pełna kopia (pomiary.md) |
| RD-75 | 8 | wdrożony (it. 120) | pula buforów o stałej pojemności + budżet 192 MiB (Qb74); duplikat numeru RD-72 z it. 119 poprawiony |
| RD-72 | 8 | odroczony (it. 120) | raport dzienny w UI — runda 9 lub później |
| RD-76 | 9 | wdrożony częściowo (it. 135) | podstawiony .kobian-partial (dowiązanie lub większy niż oczekiwany rozmiar) nie jest usuwany — wpis pomijany z KB RestoreTargetExists; pełna tożsamość pliku roboczego → RD-78 |
| RD-78 | 9 | kandydat (it. 135) | tożsamość pliku roboczego w dzienniku wznowień |
| RD-79 | 9 | kandydat (it. 135) | widok postępu SCR-07 w UI |
| RD-80 | 9 | kandydat (it. 135) | step-up dla wyciszania alertów |
| RD-79 | 10 | czesciowo wdrozony (it. 136) | port IRunProgressSource + adapter gRPC + RunProgressViewModel/View (ponawianie, stany); brak podpiecia w ekranie Restore - RD-81 |
| RD-81 | 10 | kandydat (it. 136) | Restore zwraca run_id od razu; podpiecie RunProgressView w SCR-07 |
