# Wyniki optymalizacji (przed / po)

Pomiary: `tools/perf/perf.sh` na 1,2 GB (Linux x64, 4 rdzenie). Plan: `docs/plan-optymalizacji.md`.

| Runda | Zmiana | Przed | Po |
|---|---|---|---|
| 1–2 | Skrypt pomiarowy + bramka w CI (`tools/perf/check.sh`, zadanie `perf`) | brak | progi: szczyt ≤ 420 MB, pamięć prywatna po kopii ≤ 130 MB, rozmiar repo, czasy |
| 3 | GC bez wątku współbieżnego + `GCConserveMemory=7` (Agent) | pełna kopia 11,4–12,4 s; szczyt 474–583 MB | pełna kopia 10,3–11,2 s; szczyt **324 MB** |
| 4 | Sprzątanie `GCCollectionMode.Aggressive` po przebiegu z dużą stertą (`MemoryTrim`) | 271 MB RSS po kopii (193 MB po zmianie GC) | 116 MB RSS, z czego **28 MB prywatne** (reszta to mapowane biblioteki, odzyskiwalne przez system) |
| 6 | Skrót „plik bez zmian” w przyroście (rozmiar + mtime + okno racy 2 s, bloki z poprzedniej migawki; pełny odczyt treści co 7 dni, `KOBIAN_CONTENT_CHECK_DAYS`) | przyrost bez zmian 3,4–4,3 s (czyta 1,2 GB) | **1,1–1,3 s**, 0 plików otwartych, 0 nowych packów |
| 8 | Próba nieściśliwości (4 × 4 KiB, zstd 1) — bloki losowe/spakowane bez kompresji poziomu 3 | pełna kopia ~11 s (kompresja próbuje 900 MB losowych) | ~9,4 s; rozmiar repozytorium bez zmian (732 MB) |
| 9a | Mniej alokacji na blok: brak kopii bloku (`ToArray`), bufory robocze przebiegu zamiast 4 tablic LOH na blok | — | mniej pracy GC; pamięć po przebiegu niezmieniona |
| 9b | Równoległe przygotowanie bloków dużych plików (HMAC chunkId + kompresja na 1–3 wątkach, kolejność i wynik jak sekwencyjnie; `KOBIAN_BLOCK_PARALLELISM`) | ~9,4 s | **~7,5–8,1 s**; szczyt pamięci 340 MB (+15 MB), 27 MB prywatne po kopii |

Profil pełnej kopii (1,2 GB, `KOBIAN_PROFILE=1`, sumy wątku głównego): odczyt 0,7 s · hasze 1,9 s · kompresja 3,2 s · szyfrowanie do packa 0,5 s · oczekiwanie na wysyłkę 1,5 s.
Uwaga (pułapka): `ArrayPool.Shared` w kompresorze trzymał bufory 8 MiB poza zasięgiem sprzątania — pamięć prywatna po kopii rosła z 27 do 110 MB; bufor należy do kompresora.
| 10–11 | Ponawianie wysyłki packa na świeżej sesji po zerwaniu połączenia (4 próby, 2/6/18 s ±25%); rozpoznanie utraconego potwierdzenia po rozmiarze i skrócie | zerwanie w trakcie wysyłki = porażka całego przebiegu, ponowienie od zera | przebieg kończy się sukcesem; błędy trwałe, niezgodność skrótu i pełny dysk nie są ponawiane |
| 13 | Automatyczna kopia katalogu (co dobę, `VACUUM INTO` + kontrola) i samoczynne odtworzenie przy starcie z uszkodzonym plikiem | uszkodzony katalog = tryb awaryjny, usługa nie startuje | usługa startuje z kopii (uszkodzony plik zachowany jako `catalog.db.corrupt-<czas>`) |
| 14 | Nadzór zawieszeń: brak postępu przez 30 min (`KOBIAN_STALL_MINUTES`, 0 = wyłączony) → KB-4106 (przejściowy), nawet gdy praca tkwi w nieprzerywalnym wywołaniu | uruchomienie mogło wisieć bez końca, trzymając blokady | przerwane, ponawiane przez wykonawcę; postępująca kopia nigdy nie jest przerywana |
| 16 | Usługa pracuje z priorytetem „poniżej normalnego” (Windows: klasa priorytetu; Linux/macOS: nice 10; `KOBIAN_PRIORITY=normal\|belownormal\|idle`) | kopia konkuruje z pracą użytkownika na równych prawach | bez rywalizacji czas bez zmian (pomiar: 8,3 s vs 9,0 s — szum); przy rywalizacji pierwszeństwo mają programy interaktywne |
| 17 | Limit przepustowości wysyłki (`KOBIAN_MAX_UPLOAD_KBPS`, wspólny dla wszystkich sesji i zadań, kawałki 16–256 KiB) | brak | 732 MB przy limicie 50 MB/s: 17,6 s (≈ 41 MB/s łącznie z odczytem i kompresją) |
| 15 | Test: CLI i aplikacja (ten sam obiekt klienta) wracają do pracy po restarcie usługi, a przy wyłączonej usłudze dostają czytelny błąd (KB-9001), nie zawieszenie | — (zachowanie było poprawne, teraz zablokowane testem) | bez zmian w produkcie |
| 18 | `TieredPGO=false` w Agencie, CLI i narzędziu odtwarzania; pomiar startu: `kobian status` zimny 0,20 s (cel ≤ 0,25 s osiągnięty bez ReadyToRun, który zwiększyłby paczkę o ~60%) | start CLI 222 ms, pełna kopia 7,4–8,3 s, przyrost 1,1–1,4 s | start CLI 201 ms, pełna kopia 7,2 s, przyrost 0,95–1,04 s |
| 19 | `tools/perf/soak.sh` + nocny `soak.yml`: kopie z losowymi zmianami danych, `kill -9` Agenta w trakcie kopii, odtworzenie z porównaniem bajt w bajt, `check`, nadzór pamięci | brak testu długotrwałego | 2 serie × 50 kopii, 16 zabić procesu: wszystkie odtworzenia zgodne, `check` czysty, pamięć 116 → 115 MB (bez wzrostu) |
| 5 | Duże drzewa (300 tys. plików, `tools/perf/many.sh`): weryfikacja V2 z odczytem zakresowym ramek zamiast całych packów (`RepoReader.UseRangeReads`), cache packów kontroli 24 MB (było 256 MB), klucze bloków jako struktura 32 B zamiast tekstu hex; ten sam odczyt dla drzew poprzedniej migawki | szczyt pamięci **777 MB** (limit 500 MB), pełna kopia 19 s | szczyt **434 MB**, pełna kopia 17–18 s, przyrost 4,6 s (100 tys. plików: 338 MB, bramka w CI) |

## Pomiary końcowe (1,2 GB, Linux x64, 4 rdzenie)

| Wielkość | Przed (plan, sekcja 0) | Po |
|---|---|---|
| Pierwsza pełna kopia | 11,4–12,4 s | **7,2–8,3 s** |
| Przyrost bez zmian | 3,4–4,3 s | **0,95–1,4 s** (0 otwartych plików) |
| Szczyt pamięci Agenta | 474–583 MB (limit 500 MB przekraczany) | **~330 MB** (1,2 GB); 434 MB przy 300 tys. plików (było 777 MB) |
| Pamięć prywatna po kopii | 271 MB RSS | **~27 MB** prywatnej (116 MB RSS z mapowanymi bibliotekami) |
| Rozmiar repozytorium | 732 MB | 732 MB (bez zmian) |
| Wznawianie po zerwaniu połączenia | porażka przebiegu | ponowienie wysyłki packa (4 próby) |
| Uszkodzony katalog | usługa nie startuje | start z kopii dobowej |
| Zawieszone uruchomienie | wisi bez końca | przerwane po 30 min bez postępu (KB-4106) |
