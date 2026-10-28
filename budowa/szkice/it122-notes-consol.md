# Notatki — rola 3 (silnik): BL-25 dokończenie — D3.16 konsolidacja indeksów (L4, PM-17)

## Zakres
- **D3.16 / L4 / PM-17 — zrobione:** konsolidacja indeksów jako krok `kobian prune` (pod blokadą wyłączną), relacja „zastąpiony przez” rozwiązywana przez wszystkich czytelników, usuwanie zastąpionych indeksów przez prune dwufazowy po karencji (D3.12).
- **SEC-17.4 / KB-4201 (wstrzymanie retencji po skoku zegara) — POMINIĘTE, już istnieje:** `src/Kobian.Agent/Jobs/RetentionHoldService.cs`, `core_retention_hold`, KB-6117, RPC `ReleaseRetentionHold`, testy `tests/Kobian.Tests.Unit/Agent/RetentionHoldTests.cs` (it. 69 w `docs/retention.md`). Nic nie zmieniano.

## Projekt (zgodnie z D3.16, z doprecyzowaniami)
- **Format (R2.3.5):** nowy indeks = klucz 1 unia packów wejść, klucz 2 zastąpione `indexId`, **klucz 32** SHA-256 pliku każdego zastąpionego indeksu, **klucz 33** numery packów (pozycje w kluczu 1) każdego zastąpionego indeksu. Klucze ≥ 32 są pomijane przez starsze czytniki. `IndexObject` ma 2 nowe opcjonalne pola (`SupersededSha256`, `SupersededPacks`); kodek waliduje równoległość list, zakres numerów, limity (≤ 10 000 list, ≤ 1 mln numerów łącznie).
- **`IndexResolver` (nowy, Engine/Repo):** odwołanie snapshotu `(indexId, sha)` → oryginał; gdy brak/uszkodzony → mapa relacji (lista `index/`, każdy indeks czytany raz) → indeks skonsolidowany TYLKO gdy klucz 32 ma skrót równy skrótowi z odwołania (kotwica łańcucha zaufania + AEAD K_obj). Wynik to **widok** (tylko packi zastąpionego indeksu wg klucza 33). Konsolidator przenosi relację przechodnio → zawsze jeden krok. Kandydat deterministyczny: niezastąpiony > najmniejszy id. `ResolveAllAsync` czyta każdy indeks skonsolidowany raz na wywołanie.
- **Dlaczego widoki:** dzięki nim zakres dedup kolejnej kopii, zależności snapshotów i odzysk miejsca przez prune (Qb26) są IDENTYCZNE jak bez konsolidacji (test właściwości: zbiór żywych packów przed = po). Unia bez widoków blokowałaby odzysk miejsca po zapomnieniu starego łańcucha (konsolidacja „sklejałaby” łańcuchy/zadania).
- **Czytelnicy przepięci na resolver:** `RepoReader.LoadIndexesAsync` / `LoadIndexesWithFallbackAsync` (restore, przeglądanie, RepoDamageAssessor), `RepoChecker` (V2 pełne i jednego snapshotu; indeks skonsolidowany i zastąpione oryginały nie są sierotami KB-1504), `PrunePlanner.CollectAsync` (żywy = indeks skonsolidowany; oryginał zastąpiony = nieżywy), baza przyrostu `BaselineSet.LoadResolvedAsync` (nowe; używa go `CatalogJobService.PlanBaselineAsync`). Nowy snapshot dziedziczy odwołania rodzica BEZ ZMIAN. Zmiana zachowania: skrót indeksu bazowego jest teraz porównywany z odwołaniem rodzica (wcześniej przyjmowany z wczytanego obiektu) — niezgodność → kopia pełna (jak przy nieczytelnej bazie).
- Duplikaty wpisów w wielu indeksach: `RepoReader.AddIndex` już tolerował (TryAdd); prune — unie zbiorów; dedup — `AddConfirmed`.
- **`IndexConsolidator` (nowy):** wymaga dzierżawy WYŁĄCZNEJ (D3.13); wejście = indeksy aktywne (rozwiązane z odwołań żywych snapshotów — nie zapomnianych w katalogu; nieznane katalogowi żywe). Próg z planu: aktywnych > 32 → wszystkie; małych (< 1 MiB) > 16 → tylko małe. Podział na kilka indeksów po CAŁYCH wejściach (≤ 50 000 packów, ≤ 2 mln wpisów ≈ 112 MiB CBOR, ≤ 5 000 zastąpionych). Pack o tym samym id w dwóch wejściach musi być identyczny (SHA, rozmiar, wpisy) — inaczej KB-1604, nic nie zapisane. Wejście skonsolidowane: przenoszone tylko jego żywe wpisy i ich packi + wpis samego wejścia. Zapis: `VerifiedUploader` (V1-a, V1-b) + ponowne otwarcie i porównanie liczności. Nic nie usuwa.
- **Wpięcie:** `CatalogPruneService.PruneCoreAsync`: blokada wyłączna → **konsolidacja** → spis + plan (zastąpione od razu nieżywe → faza 1 oznacza) → faza 2 (po 24 h). `Consolidation` (init, domyślnie progi z planu; null = wyłączone). KB-1604 → przebieg `Warning`, prune trwa dalej. `PruneRunOutcome.Consolidation` (nowy opcjonalny parametr).
- **RPC/CLI (mała zmiana):** `PruneReply` + `indexes_consolidated = 14`, `indexes_written = 15`, `active_indexes = 16`; CLI `kobian prune`: tekst `; consolidated N indexes into M`, JSON `indexesConsolidated`, `indexesWritten`, `activeIndexes`; `PruneRunResult` +3 opcjonalne pola.

## Zmienione / nowe pliki
- Nowe: `src/Kobian.Engine/Repo/IndexResolver.cs`, `src/Kobian.Engine/Repo/IndexConsolidator.cs`, `tests/Kobian.Tests.Unit/Engine/IndexConsolidationTests.cs`.
- Zmienione: `src/Kobian.Format/Metadata/IndexCodec.cs`, `src/Kobian.Engine/Repo/{RepoReader,RepoChecker,PrunePlanner}.cs`, `src/Kobian.Engine/Backup/BaselineSet.cs`, `src/Kobian.Agent/Jobs/{CatalogPruneService,CatalogJobService}.cs`, `src/Kobian.Agent/Hosting/CatalogAgentBackend.cs`, `src/Kobian.Api.Contracts/Protos/kobian/v1/agent.proto`, `src/Kobian.Cli/Commands/{AgentClient,GrpcAgentClient,CliApp}.cs`, `tests/Kobian.Tests.Unit/Agent/CatalogPruneServiceTests.cs`, `tests/Kobian.Tests.Unit/Cli/CliAppTests.cs`.
- Generowane: `docs/kb-codes.yaml`, `docs/kb-codes.md`, `src/Kobian.Domain/KbCode.cs`, `src/Kobian.UI/Resources/KbMessages{,.en}.resx` (tools/kb.py add/gen/docs/resx), `docs/ac-coverage.md` (tools/ac-coverage.py).
- Dokumentacja: `docs/retention.md` (nowa sekcja „Konsolidacja indeksów”, poprawione „Ograniczenie”), `docs/agent-api.md` (PruneReply), `docs/check.md` (KB-1504 vs indeksy zastąpione).

## Pakiety
- Brak nowych pakietów.

## Nowe kody KB
- **KB-1604 IndexConsolidationIncomplete** (transient, warning): konsolidacja niedokończona (zapis/weryfikacja nieudane, niespójne wejście); repozytorium czytelne i kompletne; kolejny prune ponawia.
- KB-1601 (istniejący) zwracany przez konsolidację, gdy żywy snapshot/indeks nieczytelny (prune i tak się wstrzyma).

## Testy (uruchomione)
- `Kobian.Tests.Unit.Engine.IndexConsolidationTests` — 15 przypadków, wszystkie zielone (≈ 7–11 s):
  - właściwość (4 ziarna, 2 zadania w jednym repo, losowe zapomnienia): zbiór żywych packów bez zmian po konsolidacji; żywy tylko nowy indeks; check V2+V3 100 % bez sierot przed i po usunięciu; restore bit w bit wszystkich żywych; po prune `.idx` = 1 ≤ próg; przyrost bez zmian → `PacksNew = 0` (dedup widzi bloki); druga konsolidacja (przechodniość) + prune;
  - progi z planu (32 / 16 małych < 1 MiB), brak konsolidacji przy 1 aktywnym;
  - blokada: współdzielona dzierżawa → InvalidOperationException; trwający backup (współdzielona) → KB-1202 dla konsolidacji;
  - awaria przy zapisie nowego indeksu: NoSpaceOnCreate/SilentTruncate (StagedRename), DisconnectMidWrite/ProcessKill/SilentTruncate (DirectFinal) → stare indeksy nietknięte, check bez błędów (tylko KB-1504 dla niepełnego pliku), restore OK, kolejny przebieg konsolidacji + prune dokańcza (1 plik .idx);
  - awaria przed usunięciem, w połowie usuwania (połowa oryginałów usunięta, przyrost w stanie pośrednim bez nowych packów), dokończenie przez prune; uszkodzony nowy indeks przy obecnych oryginałach → nieżywy tylko on, oryginały żywe, restore OK;
  - kotwica skrótu: odwołanie ze złym SHA nie jest rozwiązywane przez indeks skonsolidowany (KB-1403 ReferenceIntegrityFailed);
  - kodek kluczy 32/33 (round-trip, brak kluczy w zwykłym indeksie, odrzucenie niespójności, numer packa spoza zakresu → KB-1304);
  - pomiar PM-17.
- `CatalogPruneServiceTests.Prune_consolidates_indexes_and_deletes_superseded_ones_after_grace` (nowy; Agent na celu lokalnym: 5 przyrostów → prune konsoliduje 5→1 i oznacza 5 → po 25 h usuwa 5; RPC `PruneReply` pola; check V2+V3 100 %; restore bit w bit; kolejny przyrost + check) — zielony; cała klasa 6/6.
- `CliAppTests` (rozszerzony o pola JSON i tekst) — zielony.
- Regresja: cały `Kobian.Tests.Unit` (`-notrait Category=Windows`): **1410/1410 zielone**; `Kobian.Tests.Architecture` 16/16. Build Release `-warnaserror` testów jednostkowych i architektury: 0 błędów, 0 ostrzeżeń.

## Pomiar (faktycznie zmierzony, MemoryStorage, maszyna współdzielona z innymi agentami)
- 400 indeksów × 3 packi × 40 bloków, jeden snapshot z 400 odwołaniami; mediana z 5 wczytań `RepoReader.LoadIndexesAsync`:
  - **odczyty obiektów indeksu: 400 → 2** (nieudana próba oryginału + mapa z 1 indeksem; obiekt z pamięci podręcznej);
  - czas (bez RTT): przebiegi 58 / 129 / 138 ms przed vs 70 / 84 / 114 ms po — w pamięci porównywalne (dominuje dekodowanie, szum maszyny). Zysk na FTP/SFTP skaluje się z liczbą żądań × RTT — **nie mierzono na prawdziwym celu zdalnym** (NFR-25: do zrobienia).

## Przegląd bezpieczeństwa
Lista kontrolna: brak sekretów w kodzie/testach (klucze losowe w teście); brak danych klienta w wyjątkach/komunikatach (tylko „rodzaj:hex”); brak Process.Start, MD5/SHA-1, Random do kluczy (id indeksu i runId z RandomNumberGenerator; `Random` tylko do danych testowych); wejście z celu niezaufane: limity kodeka (klucze 32/33 ≤ 10 000 list, ≤ 1 mln numerów, numer < liczby packów, długości id/skrótów), obiekt ≤ 256 MiB jak dotąd; porównania skrótów w czasie stałym; konsolidacja nie usuwa (D4.6 — rola Backup i tak by nie mogła), usuwa wyłącznie prune z sesją Maintenance i blokadą wyłączną.
STRIDE nowych elementów:
- **S/T** (podstawienie relacji „zastąpiony przez”): indeks skonsolidowany uwierzytelniony AEAD K_obj, a odwołanie rozwiązuje tylko przy zgodności skrótu z kluczem 11 snapshotu → bez klucza repozytorium nie da się przekierować odwołania. Osoba z kluczem i tak może zapisać dowolne obiekty (poza modelem).
- **D** (DoS: wiele/śmieciowe `.idx`): budowa mapy czyta każdy plik indeksu (jak istniejący fallback D3.10) — koszt O(liczba plików); nieczytelne pomijane. Niskie.
- **D** (usunięcie indeksu skonsolidowanego przez osobę z dostępem zapisu): po usunięciu oryginałów → brak indeksu jak dotąd; `LoadIndexesWithFallbackAsync` + `check --repair` (RepoIndexRecovery z trailerów) dalej działa. Bez zmian względem stanu obecnego.
- **R**: przebieg prune z konsolidacją ma wiersz run_runs; wynik w PruneReply. **I**: brak nowych danych w logach. **E**: brak nowych uprawnień RPC (Prune — jak dotąd).
Znaleziska: Wysokie — brak; Średnie — brak; Niskie: (1) spis prune czyta teraz każdy indeks dwa razy (mapa + żywe) — koszt utrzymaniowy na celu zdalnym (do optymalizacji: pamięć podręczna obiektów w spisie); (2) w oknie karencji (oryginały + nowy indeks) czytelnik ładuje oryginały — przyspieszenie dopiero po usunięciu oryginałów (świadomie: brak zmian zachowania do czasu prune).

## Decyzje PROWIZORYCZNE (do nadania numerów Qb)
- **Qb-NEW-consol-sha** (`IndexCodec`): skróty i packi zastąpionych indeksów w kluczach rozszerzenia 32/33 zamiast zmiany klucza 2. Alternatywa: pary/trójki w kluczu 2 (zmiana formatu v1, wektory). Koszt zmiany: mały przed wydaniem (kodek + resolver), po wydaniu — migracja formatu.
- **Qb-NEW-consol-views** (`IndexResolver`): odwołanie przez indeks skonsolidowany = widok packów zastąpionego indeksu (nie cała unia), by nie zmieniać zakresu dedup ani odzysku miejsca (Qb26). Alternatywa: unia (prostsza, ale „skleja” łańcuchy i zadania, blokując odzysk). Koszt zmiany: mały.
- **Qb-NEW-consol-lock** (`IndexConsolidator`): blokada WYŁĄCZNA zamiast współdzielonej z D3.16, bo wejście zależy od zbioru żywych snapshotów (zapomnianych pomijamy — inaczej ich packi stałyby się żywe na zawsze). Alternatywa (D3.16 dosłownie): współdzielona + konsolidacja wszystkich obecnych indeksów, bez pomijania. Koszt zmiany: mały.
- **Qb-NEW-consol-small** (progi): reguła „suma małych > 16” zinterpretowana jako LICZBA indeksów < 1 MiB > 16, a wtedy scalane tylko małe. Alternatywa: suma rozmiarów / scalanie wszystkich. Koszt: stała w `IndexConsolidationOptions`.
- **Qb-NEW-consol-where** (wpięcie): konsolidacja jako krok `kobian prune` (prune jest ręczny); brak osobnego harmonogramu. Alternatywa: osobne zadanie utrzymaniowe w schedulerze (po retencji automatycznej). Koszt: mały.
- Brak oznaczania „zastąpiony” w katalogu (plan: „oznacza stare jako zastąpione w katalogu”) — relacja żyje wyłącznie w repozytorium (odtwarzalne bez katalogu, D8.2); oznaczenie do usunięcia robi istniejący `repo_prune_marks`.
(Znaczniki `// PROWIZORYCZNIE Qb-NEW-consol-*` są w kodzie: `IndexCodec.cs` (sha), `IndexResolver.cs` (views), `IndexConsolidator.cs` (lock, small), `CatalogPruneService.cs` (where). Po ich dodaniu ponownie: build 0/0, IndexConsolidationTests + CatalogPruneServiceTests + KbRegistryTests 23/23.)

## Testy do kolejki Windows
- Brak nowych wymagań Windows. Do zrobienia (nie Windows, wymaga celu zdalnego): pomiar NFR-25/PM-17 na FTP/SFTP z RTT (365 snapshotów, czas wczytania ≤ 60 s na LAN).

## Niedokończone / później
- Pomiar na prawdziwym celu zdalnym; optymalizacja podwójnego czytania indeksów w spisie prune; UI (pulpit) nie pokazuje jeszcze liczby aktywnych indeksów; `check` nie zgłasza osobnym kodem relacji wskazujących nieistniejące indeksy (D3.16 „check raportuje indeksy osierocone” — sieroty KB-1504 są raportowane jak dotąd).
- Starszy Agent (sprzed tej zmiany) na repo po konsolidacji i prune: `LoadIndexesAsync` nie rozwiąże usuniętych oryginałów, ale `LoadIndexesWithFallbackAsync` (restore) znajdzie bloki we wszystkich indeksach (indeks skonsolidowany ma wszystkie packi) — z ostrzeżeniem KB-1505.
