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
