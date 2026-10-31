# Pomiary (tylko faktycznie uruchomione)

## 2026-10-12 — iteracja 13 — przepustowość etapów zapisu (NFR-24, wstępnie SP-17)

**Środowisko:** środowisko budowy Linux w chmurze (Ubuntu 24.04, Intel Xeon 2,8 GHz, 2 vCPU, 7 GB RAM), .NET 10.0.12, Release. **To NIE jest sprzęt referencyjny z planu** — wynik orientacyjny, nie zamyka NFR-24 ani SP-17.
**Dane:** 256 MiB syntetyczne w blokach 4 MiB (segmenty 4 KiB: ~25% zer, ~25% tekst powtarzalny, ~50% losowe) — współczynnik kompresji 50,2%. Korpus mieszany D10.9 (rzeczywiste rozkłady) — do powtórzenia.
**Narzędzie:** `tools/Kobian.Bench` (Stopwatch, rozgrzewka + 5 przebiegów, mediana).

| Etap | Mediana MB/s (min–max) | Cel planu |
|---|---|---|
| HMAC-SHA256 chunkId | 350,6 (335,0–369,4) | — |
| zstd poziom 3 (ZstdSharp.Port 0.8.8) | 415,4 (386,3–479,9) | — |
| AES-256-GCM seal | 4 102,8 (4 042,7–4 171,1) | — |
| Pełny etap, 1 wątek (zstd3 + HMAC + PackWriter/AEAD) | **171,5** (164,9–173,8) | NFR-24: ≥ 120 MB/s na rdzeń |
| Pełny etap, 2 wątki | 327,2 (251,8–343,6) | NFR-03: ≥ 150 MB/s end-to-end (z I/O — niezmierzone) |

Wniosek wstępny: na tym środowisku i tej mieszance danych etap CPU mieści się w NFR-24 z zapasem ~40%. Wąskim gardłem jest HMAC chunkId (liczony na danych surowych), nie zstd. Ryzyko: dane mniej kompresowalne (losowe 100%) obniżą wynik zstd — pomiar do wykonania na korpusie D10.9 i sprzęcie referencyjnym (SP-17 pozostaje otwarte).

## 2026-10-12 — iteracja 19 — dokładność ogranicznika pasma (NFR-33, część algorytmiczna)

**Metoda:** test jednostkowy na wirtualnym zegarze monotonicznym (krok 1 ms, porcje 64 KiB, stan ustalony po wyczerpaniu zapasu 2 s), okno 10 s. Mierzy wyłącznie dokładność algorytmu D4.9 — **bez** sieci, bez narzutu CPU i bez rzeczywistego planisty wątków.

| Limit | Wynik | Kryterium NFR-33 |
|---|---|---|
| 128 KiB/s, 1 MiB/s, 10 MiB/s, 100 MiB/s | średnia w oknie 10 s w przedziale ±10% (asercja testu, 4 przypadki zielone) | ±10% limitu dla 128 KiB/s…100 MB/s |
| zmiana profilu 10 → 2 MiB/s | ±10% w kolejnym oknie, bez resetu zawartości | zmiana profilu ≤ 60 s |

Niezmierzone: narzut CPU ≤ 1% rdzenia przy 100 MB/s; zachowanie na prawdziwym łączu (wymaga celu SFTP/SMB).

## 2026-10-13 — iteracja 27 — MODEL protokołu D3.14 pod wstrzykiwaniem awarii (SP-18, NFR-26) — to NIE jest pomiar implementacji

**Metoda:** `tools/model-d3-14-chaos.py` (Python, wszystko w pamięci, dane syntetyczne, ziarno deterministyczne). Model potoku: pack → V1 (rozmiar+SHA po stronie celu) → I-3 na danych dokładnych → indeks + odczyt kontrolny → snapshot → transakcja katalogu. 300 uruchomień na wariant = 200 × zabicie w 20 punktach potoku, 50 × ENOSPC, 50 × zerwane łącze (proporcje SP-18). Niezależny weryfikator nie używa kodu zapisu. Kontrola negatywna: wariant „borg” (błąd zapisu = ostrzeżenie, bez V1).

| protokół | tryb zapisu | uruchomień | snapshoty z brakującym blokiem | Succeeded mimo braków |
|---|---|---|---|---|
| D3.14 | StagedRename | 300 | 0 | 0 |
| D3.14 | DirectFinal | 300 | 0 | 0 |
| borg (kontrola) | StagedRename / DirectFinal | 300 / 300 | 24 / 24 | 24 / 24 |

Wniosek ograniczony do modelu: kolejność operacji D3.14 jest odporna na te klasy awarii, a harness wykrywa błąd klasy Borg. **SP-18 i NFR-26 pozostają otwarte** — wymagają testu na kodzie C# (kill w 20 punktach, VHD 1 GB, SFTP). Obserwacja: przy DirectFinal 4 uruchomienia zostawiają uciętą końcówkę obiektu snapshotu; czytnik musi ją odrzucać po sumie/AEAD, a rekoncyliacja (D8.2) sprzątać.

## 2026-10-13 — iteracja 28 — MODEL filtra Bloom z D3.15: fałszywe trafienia (SP-19, NFR-22 — część algorytmiczna) — to NIE jest pomiar implementacji

**Metoda:** `tools/model-d3-15-bloom.py` (Python 3, SHA-256 + podwójne haszowanie Kirscha–Mitzenmachera, 10 bitów/wpis, k = 7, wszystko w pamięci, dane syntetyczne deterministyczne; sondy spoza zbioru w liczbie równej n). Uruchomiono lokalnie w tym przebiegu (środowisko zaplanowane, bez .NET).

| Wpisów n | Rozmiar filtra | FP zmierzone | FP teoria | Próg SP-19 |
|---|---|---|---|---|
| 100 000 | 0,12 MB | 0,780% | 0,819% | ≤ 1,5% |
| 1 000 000 | 1,25 MB | 0,826% | 0,819% | ≤ 1,5% |
| 3 000 000 | 3,75 MB | 0,812% | 0,819% | ≤ 1,5% |

Wniosek ograniczony do modelu: parametry D3.15 dają FP ≈ 0,8% (plan zakładał ≈ 1%) niezależnie od n; dla 10 mln wpisów filtr ma 12,5 MB (obliczenie, nie pomiar). Rozmiar tabeli dokładnej przy progu 2 mln: 2 000 000 / 0,7 × 40 B ≈ 114 MB (obliczenie, zgodne z szacunkiem planu ≈ 115 MB). **Niezmierzone:** Private Bytes p99 ≤ 500 MB (NFR-22/NFR-05), zachowanie przy 10 mln plików, katalog 200 000, plik 2 TiB, koszt zapytań `repo_chunk_index` — SP-19 pozostaje otwarte (wymaga implementacji C# i korpusu D10.9). Implementacja C# musi użyć innej funkcji haszującej niż Python; FP do ponownego zmierzenia testem.


## 2026-10-13 — iteracja 34 — SP-18 na KODZIE potoku, in-proc (NFR-26, część) — test, nie benchmark
**Metoda:** `CrashConsistencyTests` — rzeczywisty `BackupPipeline` na magazynie w pamięci (`TestDoubles/MemoryStorage`); awaria w każdym kolejnym zapisie obiektu (packi, indeks, snapshot) × 4 rodzaje (ENOSPC przy otwarciu, zerwanie w trakcie zapisu, zabicie procesu, cicha utrata ostatniego bajtu przez cel) × 2 tryby (StagedRename, DirectFinal); po każdej awarii niezależny weryfikator czyta wszystkie snapshoty z celu i sprawdza, że każdy blok jest w istniejącym packu o rozmiarze i SHA-256 z indeksu; potem drugi przebieg na „naprawionym” celu.
**Wynik (uruchomione):** 0 czytelnych snapshotów z brakującym blokiem; każdy przebieg z awarią = Failed lub brak wyniku (zabicie); każdy ponowny przebieg ≠ Failed i spójny. Przy DirectFinal i przy cichym obcięciu na celu zostają nieczytelne (odrzucane przez AEAD) częściowe obiekty snapshotu — Backup ich nie usuwa (D4.1); sprzątanie = rekoncyliacja D8.2 / prune.
**Nie pokrywa:** prawdziwy ENOSPC na VHD, zerwany SFTP, zabicie procesu na poziomie OS (SP-18 pełny — wymaga Windows/serwera).

## 2026-10-14 — iteracja 65 — NFR-25 `Keep` dla 5 000 snapshotów (test Perf)
**Metoda:** `RetentionPlannerTests.Keep_for_5000_snapshots_is_fast` (Category=Perf, poza ci.sh) uruchomiony ręcznie; polityka 10/30/12/24/5 + within 2 d + lock 7 d, strefa Europe/Warsaw.
**Wynik (uruchomione):** cały test (generacja danych + rozgrzewka JIT + pomiar) 0,229 s; próg pomiaru 1 s spełniony.
**Środowisko:** kontener w chmurze, SDK 10.0.401 — NIE sprzęt referencyjny NFR-20.

## 2026-10-14 — it. 87 (RD-22): pamięć struktur dedup (Kobian.Bench --dedup)
Środowisko: chmura budowy (Ubuntu 24.04.5, .NET 10.0.13) — NIE sprzęt referencyjny; pomiar GC.GetTotalMemory po pełnym GC, losowe identyfikatory 32 B.
| Bloków | Stara (Dictionary<string,bool> + HashSet<string>) | DedupTable (ChunkKey → flagi) | Wyszukiwanie |
|---|---|---|---|
| 1 000 000 | 353,8 MiB (371 B/blok) | 69,2 MiB (73 B/blok) | 6,9 mln/s |
| 3 000 000 | 1 144,4 MiB (400 B/blok) | 297,5 MiB (104 B/blok) | 7,0 mln/s |
Wniosek: warstwa 1 D3.15 mieści ~5 mln bloków w budżecie NFR-05 (500 MB) zamiast ~1,3 mln; dla 10 mln (PM-16) nadal potrzebne warstwy 2–3 (Bloom + repo_chunk_index). NFR-22 formalnie: niezmierzony na sprzęcie referencyjnym.

## 2026-10-14 — it. 88 (rola 13): pełny cykl na dostawcy Local (Kobian.Bench --e2e 300) — informacyjnie
Środowisko: chmura budowy (Ubuntu 24.04.5, 2 rdzenie, .NET 10.0.13) — NIE sprzęt referencyjny NFR-20; korpus syntetyczny 381 plików / 301 MiB (85% małych, 13% średnich, 2% dużych; połowa danych niekompresowalna).
| Etap | Wynik (uruchomione) |
|---|---|
| Kopia pełna | 2,9 s, 104 MiB/s; repozytorium 189 MiB |
| Przyrost (20 zmienionych plików ≈ 5%) | 1,1 s, nowe dane 10,4 MiB |
| Restore całości | 3,2 s, 95 MiB/s, 381 plików |
| Szczytowy zestaw roboczy procesu | 273 MiB (wraz z generacją korpusu i buforem packu) |
| DedupTable 1 mln bloków (test Perf `DedupMemoryPerfTests`) | 73 B/blok (próg strażnika 128 B) |
Obserwacja: `FlushPackAsync` kopiuje cały pack (`MemoryStream.ToArray()`) — przy 64 MiB packach podwaja szczyt pamięci (RD-48).

## 2026-10-14 — it. 89 (rola 14/13): RD-48 przed/po (Kobian.Bench --e2e 200, po 2 przebiegi) — informacyjnie
Środowisko: chmura budowy, .NET 10.0.13 — NIE sprzęt referencyjny.
| Wariant | Szczytowy zestaw roboczy | Kopia pełna |
|---|---|---|
| przed (ToArray) | 239 / 251 MiB | 59,1 / 59,0 MiB/s |
| po (GetBuffer) | 274 / 256 MiB | 61,6 / 63,5 MiB/s |
Wniosek: w tym scenariuszu różnica szczytu procesu mieści się w szumie (GC, bufor restore, generacja korpusu) — ZMIERZONO brak mierzalnej zmiany PeakWorkingSet; zysk RD-48 to jedna alokacja rozmiaru packu mniej na każdy flush (nie mierzono alokacji osobno). Do potwierdzenia przy packach 64 MiB i pomiarze `GC.GetTotalAllocatedBytes`.

## 2026-10-17 — it. 102 (RD-22 w2): tablica posortowana i filtr Blooma (Kobian.Bench --dedup) — uruchomione
Środowisko: chmura budowy (Ubuntu 24.04.5, 2 rdzenie, .NET 10.0.13) — NIE sprzęt referencyjny; losowe identyfikatory 32 B; N bloków bazowych + N/10 nowych; pamięć GC.GetTotalMemory po pełnym GC (bez wpisów nowych bloków).
| Bloków bazowych | Wariant | Pamięć | Trafienia (baza) | Nowe bloki | Zamrożenie |
|---|---|---|---|---|---|
| 1 000 000 | słownik flag (it. 87) | 69,2 MiB (73 B/blok) | 15,0 mln/s | 14,9 mln/s | — |
| 1 000 000 | tablica posortowana | 30,5 MiB (32 B/blok) | 1,8 mln/s | 1,0 mln/s | 0,31 s |
| 1 000 000 | tablica + Bloom 10 bit | 31,7 MiB (33 B/blok) | 1,5 mln/s | 2,9 mln/s | 0,25 s |
| 3 000 000 | słownik flag (it. 87) | 297,5 MiB (104 B/blok) | 9,5 mln/s | 10,1 mln/s | — |
| 3 000 000 | tablica posortowana | 91,6 MiB (32 B/blok) | 1,4 mln/s | 0,9 mln/s | 0,64 s |
| 3 000 000 | tablica + Bloom 10 bit | 95,1 MiB (33 B/blok) | 0,9 mln/s | 2,0 mln/s | 0,91 s |
FP filtra Blooma zmierzony: 0,84% (1 mln) i 0,80% (3 mln) przy 10 bit/wpis, k = 7 (wzór: ≈ 0,8%).
Wniosek: tablica posortowana daje 2,3–3,3× mniej pamięci kosztem ~6–10× wolniejszego wyszukiwania; ~1 mln wyszukiwań/s to wciąż o rzędy wielkości więcej niż potrzeba potoku (blok ≥ 1 plik/fragment; przy 100 MiB/s i blokach 64 KiB ≈ 1 600 wyszukiwań/s). Filtr Blooma w pamięci przyspiesza tylko bloki nowe (2–3×) i spowalnia trafienia — w kopiach przyrostowych dominują trafienia, więc **domyślnie wyłączony**; zostaje jako warstwa przed zapytaniem do katalogu (warstwa 3 D3.15), gdzie chybienie oszczędza zapytanie SQL. Warstwa 1 mieści teraz ≈ 15 mln bloków w 500 MB (NFR-05) [z pomiaru 32 B/blok; nie uwzględnia obiektów indeksów bazowych trzymanych przez wywołującego].
Dodatkowo (informacyjnie, jeden przebieg, nieporównywalny z it. 88 — inne obciążenie maszyny): Bench --e2e 300 po zmianie — pełna 6,1 s (49,4 MiB/s), przyrost 2,8 s, restore 7,5 s, szczyt procesu 345 MiB; wszystkie etapy Succeeded.

## 2026-10-17 — it. 113 (BL-83, RD-64): baza przyrostu — lista obiektów indeksów vs ładowanie strumieniowe (Kobian.Bench --baseline N list|stream) — uruchomione
Środowisko: chmura budowy (Linux, 2 rdzenie, .NET 10.0.13) — NIE sprzęt referencyjny; N losowych bloków 32 B w 64 indeksach syntetycznych (≈ 1 000 bloków/pack); każdy wariant w osobnym procesie, jeden przebieg. „Trzymane” = GC.GetTotalMemory po pełnym GC z bazą gotową do potoku (przed zamrożeniem tabeli); „szczyt” = PeakWorkingSet64 procesu.
| Bloków bazowych | Wariant | Trzymane w trakcie uruchomienia | Ładowanie | Szczyt procesu |
|---|---|---|---|---|
| 1 000 000 | lista BaselineIndex (przed) | 137,6 MiB (144 B/blok) | 0,58 s | 183 MiB |
| 1 000 000 | BaselineSet strumieniowo (po) | 36,7 MiB (38 B/blok) | 0,36 s | 136 MiB |
| 3 000 000 | lista BaselineIndex (przed) | 412,6 MiB (144 B/blok) | 1,85 s | 460 MiB |
| 3 000 000 | BaselineSet strumieniowo (po) | 110,0 MiB (38 B/blok) | 1,33 s | 323 MiB |
Wniosek: pamięć trzymana przez całe uruchomienie spada ≈ 3,8× (144 → 38 B/blok; 38 B zamiast 32 B, bo lista budowy ma rezerwę ×1,5 do zamrożenia — po zamrożeniu tablica dokładna, 32 B/blok, it. 102). Szczyt procesu spada mniej (−26…−30%), bo w trakcie ładowania jeden indeks naraz i tak żyje w pamięci, a GC nie oddaje od razu stron. Uzupełnia zastrzeżenie z it. 102 („nie uwzględnia obiektów indeksów bazowych”): w Agencie (CatalogJobService) baza jest teraz strumieniowa. Liczby nie są pomiarem NFR-05 na sprzęcie referencyjnym.

## 2026-10-17 — it. 117 (RD-74): koszt PRAGMA secure_delete w katalogu (Kobian.Bench --secure-delete N) — uruchomione
Środowisko: chmura budowy (Ubuntu 24.04.5, 2 rdzenie, .NET 10.0.13) — NIE sprzęt referencyjny (dysk chmury, nie SSD klienta); WAL + synchronous=FULL jak w Agencie; wiersze ~300 B w core_outbox, usuwane partiami po 200 (jak CatalogMaintenance) + checkpoint TRUNCATE; rozgrzewka + 5 przebiegów, mediana.
| Wierszy | secure_delete OFF | secure_delete ON | Różnica |
|---|---|---|---|
| 20 000 | 0,092 s | 0,097 s | +5% |
| 100 000 | 0,390 s | 0,442 s | +13% |
Wniosek: koszt dotyczy tylko usuwania (sprzątanie raz na dobę, RD-69) i jest pomijalny wobec korzyści; włączone na każdym połączeniu zapisującym. Test SecureDeleteTests pokazuje, że bez pragmy usunięta treść zostaje w pliku katalogu, a z nią — nie.

## 2026-10-17 — it. 119 (rola 14/13): RD-73 cz. 2 — Bench --e2e 300 z 1 i 2 sesjami wysyłki (Local) — informacyjnie

Środowisko: chmura, Ubuntu 24.04, 2 rdzenie logiczne, .NET 10.0.13; korpus 381 plików / 301 MiB; pack 16 MiB (Local). Po 2 przebiegi na wariant, naprzemiennie (1, 2, 1, 2).

| Wariant | Kopia pełna | Przyrost | Restore | Szczyt zestawu roboczego |
|---|---|---|---|---|
| 1 sesja | 5,2 s / 58,4 MiB/s; 5,0 s / 59,9 MiB/s | 2,7 s; 2,7 s | 45,3; 40,0 MiB/s | 236; 245 MiB |
| 2 sesje | 4,4 s / 69,0 MiB/s; 4,3 s / 70,2 MiB/s | 2,7 s; 2,6 s | 42,4; 45,2 MiB/s | 367; 426 MiB |

Wnioski (tylko z tych liczb): pełna kopia ~+17% przepustowości na dysku lokalnym przy 2 rdzeniach; przyrost bez zmiany (mało nowych packów); szczyt pamięci +130–180 MiB — więcej niż 1 dodatkowy bufor packa (prawdopodobnie bufory MemoryStream z zapasem pojemności i opóźnione GC — niezmierzone osobno). NFR-05 niezmierzony (to nie usługa i nie Windows) — W-48.
