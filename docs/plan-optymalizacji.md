# Plan: wydajność, oszczędność zasobów i niezawodność (20 rund)

> **Stan:** rundy 1–11, 13–19 zrealizowane (runda 5: drzewa 300 tys. plików, szczyt 777 → 434 MB) (wyniki: `docs/wyniki-optymalizacji.md`, ustawienia: `docs/admin/dostrajanie.md`). Runda 12 (pełny dysk, zmiana tożsamości serwera) zrealizowana częściowo — pełny dysk nie jest ponawiany na poziomie packa, zmiana klucza serwera blokuje wysyłkę hasła (istniejące testy KB-2104/KB-2109); pozostaje test z prawdziwym pełnym wolumenem. Runda 7 w praktyce objęta rundą 6 (zostaje ~0,6 s po stronie usługi i ~0,2 s startu CLI); runda 20 = ten dokument.

Zasada: **najpierw pomiar, potem zmiana, potem test, który pilnuje wyniku.** Żadna runda nie osłabia gwarancji bezpieczeństwa
ani formatu repozytorium (dane zapisane wcześniej odtwarzają się bit w bit). Zmiana bez liczby „przed/po” nie wchodzi.

## 0. Pomiary bazowe (Linux x64, 4 rdzenie, 16 GB, Agent jako usługa, 30.10.2026)

Dane testowe: 1,2 GB — 400 MB tekstu (dobrze się kompresuje), 900 MB losowych (nie kompresuje się, z czego 300 MB to duplikaty),
20 000 małych plików. Skrypt: `tools/perf/perf.sh <repo>` (już w repo; powtarzalność czasu ±8%, pamięci ±10%).

| Wielkość | Wynik | Uwaga |
|---|---|---|
| Pierwsza kopia pełna | **11,4–12,4 s** (~100 MB/s), repozytorium 732 MB | dedup duplikatów działa |
| Kopia przyrostowa bez zmian | **3,4–4,3 s** | czyta i haszuje wszystko od nowa (skrót po mtime wyłączony) |
| Pamięć Agenta w spoczynku | **78 MB** | po poprawce katalogu roboczego |
| Szczyt pamięci Agenta podczas kopii | **474–583 MB** (3 pomiary: 504, 583, 474) | limit NFR-05 = 500 MB — **przekraczany** |
| Pamięć po kopii (bez nowej pracy) | **271 MB** | runtime nie oddaje pamięci systemowi |
| Czas startu Agenta do gniazda | 0,5 s | |
| Paczka linux-x64 | 65 MB (148 MB po rozpakowaniu) | Skia 11 MB, BouncyCastle 5 MB, System.Xml 8 MB w Agencie |
| Dziennik błędów Agenta | do 1 MB + 1 kopia | ograniczony — OK |

Wnioski: (1) pamięć i jej zwalnianie, (2) przyrost mimo braku zmian kosztuje prawie tyle co czytanie wszystkiego,
(3) CPU marnuje się na próbę kompresji danych losowych/skompresowanych, (4) paczka zawiera biblioteki, których Agent nie używa,
(5) niezawodność sieciowa (SFTP/FTPS/WebDAV jako cel i źródło) ma testy tylko na „szczęśliwej ścieżce” w CI.

## Kolejność rund i uzasadnienie

Najpierw narzędzia pomiaru i bramki (rundy 1–2), bo bez nich dalsze zmiany są ślepe. Potem pamięć (3–5), przyrost (6–7),
CPU/kompresja (8–9), sieć i niezawodność (10–15), zasoby systemowe (16–17), rozmiar/start (18), twardnienie i długi test (19), podsumowanie (20).
Po każdej rundzie: build `-warnaserror`, wszystkie testy, CI zielone na 3 systemach, wpis w `docs/wyniki-optymalizacji.md` (tabela przed/po).

## Rundy

### Runda 1 — Narzędzia pomiaru
- (zrobione) `tools/perf/perf.sh` + generator danych (deterministyczny, ziarno); wyjście `klucz=wartość`: czas, szczyt RSS, rozmiar repo.
- Licznik metryk w Agencie (bez danych klienta): szczyt pamięci uruchomienia, czas faz (skan, kompresja, upload, commit) w `run_runs`/zdarzeniu.
- **Gotowe gdy:** jedno polecenie daje tabelę z sekcji 0; wynik powtarzalny ±10%.

### Runda 2 — Bramka regresji w CI
- Zadanie CI „perf” (Linux): mały zbiór (200 MB) → progi: czas, szczyt RSS, rozmiar repo, liczba packów przy przyroście bez zmian = 0.
- Progi z pomiarów ×1,5 (runnery są zmienne); zapis trendu jako artefakt.
- **Gotowe gdy:** celowe pogorszenie (np. podwojenie bufora) wywala CI.

### Runda 3 — Pamięć: szczyt kopii poniżej ~350 MB
- Rozbicie szczytu (dotnet-gcdump / liczniki): bufory packów (192 MiB budżetu), struktura dedup, kompresja, kolejki, cache indeksów.
- Obniżenie `DefaultPackBufferBudget` zależnie od liczby sesji; pule `ArrayPool` zamiast alokacji dużych tablic (LOH).
- **Gotowe gdy:** szczyt ≤ 350 MB na zbiorze bazowym, czas kopii nie gorszy niż +10%.

### Runda 4 — Pamięć: oddawanie po pracy
- Ustawienia GC dla usługi: `System.GC.Concurrent=false`, `GCConserveMemory`, `TieredPGO=0` (mniej JIT-a w tle), `GCHeapAffinitizeMask`/limit wg liczby rdzeni.
- Po zakończeniu uruchomienia: zwolnienie pul (`GC.Collect` kompaktujący LOH, tylko po dużym uruchomieniu), zmniejszenie pul buforów.
- **Gotowe gdy:** pamięć 60 s po kopii ≤ 120 MB.

### Runda 5 — Pamięć: duże repozytoria i wiele plików
- Test na 500 tys. plików i 20 mln bloków (generator): struktura dedup, drzewa katalogów strumieniowo, indeksy w częściach.
- Brak materializacji pełnej listy plików; limity i komunikaty (KB) zamiast OOM.
- **Gotowe gdy:** wzrost pamięci jest liniowy ≤ 32 B/blok, a nie skokowy; test w CI (mały wariant).

### Runda 6 — Przyrost: szybka ścieżka bez czytania niezmienionych plików
- Włączenie skrótu „rozmiar + mtime + i-węzeł/ID pliku jak w poprzedniej migawce ⇒ bloki z poprzedniej migawki” z ochroną przed „racy timestamp”
  (plik zmieniony w oknie ≤ 2 s przed poprzednią kopią jest czytany ponownie) i wymuszonym pełnym odczytem co N-tą kopię / przy zmianie ustawień.
- Przełącznik `contentVerify` (wymuś pełny odczyt) w definicji zadania i w CLI.
- **Gotowe gdy:** przyrost bez zmian ≤ 0,5 s na zbiorze bazowym; testy: zmiana treści bez zmiany mtime i rozmiaru wykryta przy pełnej weryfikacji;
  odtworzenie bit w bit.

### Runda 7 — Przyrost: koszt metadanych
- Lista plików strumieniowo z poprzednią migawką (merge posortowany) zamiast słownika; unikanie wielokrotnego `lstat` (Unix: jedno `statx` na wpis).
- Windows: jedno wyliczanie z pełnymi atrybutami (bez `Refresh` per plik tam, gdzie już świeże) — pomiar na runnerze Windows.
- **Gotowe gdy:** 20 tys. małych plików bez zmian ≤ 0,3 s na Linuksie; brak regresji na Windows w CI.

### Runda 8 — CPU: kompresja adaptacyjna
- Szybkie wykrywanie danych nieściśliwych (próbka 4 KiB: entropia / rozszerzenie: zip, jpg, mp4, 7z, docx…) ⇒ zapis bez kompresji (alg „none” już istnieje w formacie? – sprawdzić; jeśli nie, flaga ramki).
- Poziom kompresji zależny od celu: sieć wolna ⇒ wyższy poziom, dysk lokalny ⇒ niski.
- **Gotowe gdy:** 600 MB losowych bloków nie zużywa CPU na kompresję; rozmiar repo nie rośnie > 0,5% na danych mieszanych.

### Runda 9 — CPU: haszowanie i szyfrowanie równolegle
- Pipeline: odczyt → hasz/szyfr/kompresja w `Channel` z liczbą pracowników = min(rdzenie−1, 4); ograniczenie przez bufory, nie przez kolejkę nieskończoną.
- Użycie sprzętowego AES-GCM / SHA-NI (sprawdzenie, czy ścieżki używają `System.Security.Cryptography`, a nie BouncyCastle w gorącej pętli).
- **Gotowe gdy:** przepustowość pełnej kopii ≥ +30% na 4 rdzeniach przy tej samej pamięci; test determinizmu (ten sam wynik logiczny).

### Runda 10 — Sieć: ponawianie i limity czasu
- Jedna polityka: wykładniczy backoff z losowym rozrzutem, limit prób i czasu na operację, rozróżnienie błędów trwałych/chwilowych (już jest) — przegląd wszystkich dostawców (SFTP, FTP(S), WebDAV, SMB).
- Test z serwerem, który zrywa połączenia w losowych miejscach (proxy przerywające, w CI na Linuksie).
- **Gotowe gdy:** kopia 200 MB przez SFTP/FTPS/WebDAV z 5 zerwaniami kończy się sukcesem i poprawnym odtworzeniem.

### Runda 11 — Wznawianie przerwanej kopii
- Przerwanie (zerwanie sieci, restart usługi, awaria zasilania): niedokończone packi sprzątane, ukończone packi ponownie użyte (dedup po stronie celu), brak „sierot” (sprawdza `check`).
- Test chaosu: zabicie procesu Agenta w losowym momencie 50 razy → każda następna kopia poprawna, `check` czysty.
- **Gotowe gdy:** po 50 awariach brak uszkodzeń; druga próba wysyła tylko brakujące dane.

### Runda 12 — Niezawodność celu: pełny dysk, brak uprawnień, zmiana serwera
- Pełny cel w trakcie: czytelny kod, brak uszkodzonej migawki, alert; wznowienie po zwolnieniu miejsca.
- Zmiana klucza serwera / certyfikatu: alarm (bez ciągłych prób logowania — ochrona konta przed blokadą).
- **Gotowe gdy:** testy na `tmpfs` o małym rozmiarze i na serwerze ze zmienionym kluczem.

### Runda 13 — Niezawodność katalogu (SQLite) i stanu
- WAL + okresowy checkpoint, `PRAGMA optimize`, `integrity_check` przy starcie już jest — dodać kopię zapasową katalogu i automatyczną odbudowę z repozytorium (katalog to cache, repozytorium jest prawdą).
- Obsługa uszkodzonego katalogu: tryb awaryjny z jasną instrukcją, odbudowa jednym poleceniem.
- **Gotowe gdy:** zniszczenie `catalog.db` + restart ⇒ odbudowa i działające zadania; test.

### Runda 14 — Nadzór usługi i samonaprawa
- Watchdog: usługa zgłasza „żyję” (sd_notify / SCM), wykrywanie zawieszonego uruchomienia (brak postępu > N min) ⇒ anulowanie i alert zamiast wiszenia w nieskończoność.
- Restart po awarii z narastającym opóźnieniem (systemd/launchd/SCM — konfiguracja już jest; test, że działa).
- **Gotowe gdy:** sztuczne zawieszenie (test) kończy uruchomienie ze stanem „Failed + kod”, a harmonogram działa dalej.

### Runda 15 — Niezawodność kreatora i klienta
- Wszystkie wywołania UI z limitem czasu i anulowaniem, komunikaty KB zamiast wyjątków; ponowne połączenie po restarcie usługi bez restartu aplikacji.
- Test smoke w CI rozszerzony: restart usługi w trakcie sesji kreatora, brak ruchu przy wyłączonej usłudze.
- **Gotowe gdy:** zabicie i uruchomienie Agenta w trakcie kreatora nie psuje stanu formularza.

### Runda 16 — Zasoby systemowe: niski priorytet
- Linux: `nice`/`ionice` idle dla pracy kopii, `CPUWeight`/`IOWeight` w jednostce systemd; macOS: `taskpolicy`/QoS background; Windows: tryb `PROCESS_MODE_BACKGROUND_BEGIN` (CPU i I/O).
- Ustawienie `lowImpact` dla źródeł plików (jest dla SQL) — domyślnie włączone dla zadań uruchamianych z harmonogramu, wyłączone przy „Uruchom teraz”.
- **Gotowe gdy:** równoległe zadanie CPU dostaje ≥ 90% swojej mocy podczas kopii (test pomiarowy).

### Runda 17 — Zasoby sieci i energii
- Limit przepustowości (kB/s) w definicji zadania i w kreatorze; harmonogram okien czasowych.
- Wykrywanie zasilania z baterii / sieci taryfowej (Windows: `PowerStatus`, `NetworkCostType`; macOS/Linux: tam gdzie dostępne) ⇒ opcja „wstrzymaj” z jasnym komunikatem.
- **Gotowe gdy:** limit 1 MB/s utrzymany ±10% w teście; wstrzymanie i wznowienie bez błędu.

### Runda 18 — Rozmiar paczki i start
- ReadyToRun dla Agenta i CLI (szybszy start, mniej JIT); przycinanie (trim) tam, gdzie bezpieczne (CLI bez refleksji gRPC? — sprawdzić); osobne katalogi: Agent bez Avalonia/Skia (-15 MB), UI osobno.
- Pomiar: czas `kobian status` (zimny start), czas startu Agenta, rozmiar paczek.
- **Gotowe gdy:** `kobian status` ≤ 250 ms zimny; paczka serwerowa (bez UI) ≤ 35 MB; testy dymne z paczek przechodzą.

### Runda 19 — Test długotrwały (soak) i fuzzing
- Nocne zadanie CI: 6 h symulacji (przyspieszony czas): kopie co minutę, losowe zmiany danych, losowe awarie sieci i procesu, retencja i prune; sprawdzenie `check` i odtworzenia co godzinę; wykres pamięci w czasie (brak wycieków).
- Fuzzing parserów wejściowych (definicje zadań, odpowiedzi WebDAV/FTP, listingi) — krótkie przebiegi w CI.
- **Gotowe gdy:** 6 h bez wzrostu pamięci > 5% i bez uszkodzeń.

### Runda 20 — Podsumowanie i zamknięcie
- Tabela „przed/po” w `docs/wyniki-optymalizacji.md`, aktualizacja dokumentacji administratora (limity, ustawienia GC, niski priorytet, limit pasma).
- Przegląd regresji bezpieczeństwa (modele zagrożeń bez zmian), przegląd kompatybilności wstecznej formatu i definicji zadań.
- Wydanie `v1.0.0-beta.2` (do utworzenia przez właściciela — tworzenie wydań z tej sesji jest zablokowane).

## Czego NIE robię (świadomie)
- Nie zmieniam formatu repozytorium ani szyfrowania, jeśli nie da się tego zrobić wstecznie zgodnie.
- Nie wyłączam weryfikacji po zapisie (odczyt kontrolny packów) dla przyspieszenia — to gwarancja niezawodności.
- Nie wprowadzam trybów „szybko, ale mniej bezpiecznie”.

## Ryzyka
| Ryzyko | Ograniczenie |
|---|---|
| Skrót po mtime pomija zmienioną treść | okno „racy”, okresowy pełny odczyt, przełącznik `contentVerify`, testy zmiany bez zmiany metadanych |
| Mniejsze bufory spowalniają sieć | pomiar na SFTP/FTPS/WebDAV przed zmianą progu, bramka czasu w CI |
| Wykrywanie nieściśliwych danych myli się | próbka + fallback: zły wybór kosztuje miejsce, nigdy poprawność |
| Niski priorytet wydłuża kopię w nocy | tylko dla uruchomień z harmonogramu, limit czasu i alert o przekroczeniu okna |
| Runnery CI są zmienne (progi) | progi ×1,5, mediana z 3 przebiegów, trend zamiast pojedynczej liczby |
