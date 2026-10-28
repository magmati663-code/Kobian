# `check` — weryfikacja repozytorium (BL-26, KOSZT-14; 3.7 V2/V3)

Stan: it. 55 — biblioteka `Kobian.Engine.Repo.RepoChecker`, polecenie offline `kobian-restore check` oraz RPC Agenta `Check` (rola Operator) z CLI `kobian check`. Automatyczne V2 po każdym uruchomieniu, `--repair` (D8.10) oraz V3 według harmonogramu Agenta (sekcja niżej); wysyłka alertu A04 — rola 9 (dane: run.finished z kodem KB-15xx).

## Użycie przez Agenta

```
kobian check <zadanie> [--sample <0-100>] [--rotation <n>] [--json]
```

Agent bierze cel, klucz i hasło z definicji zadania (job_jobs), otwiera cel poświadczeniem Backup i zapisuje przebieg w `run_runs` (cause `manual`, przejścia z reason `check`, `kb_code` = najgorszy kod). Bez `--rotation` rotacja = tydzień ISO (rok·53 + tydzień), więc cotygodniowe `check --sample P` pokrywa 100% packów w ⌈100/P⌉ tygodniach. JSON: schemat `kobian.cli.check/1` (`result`, `code`, `level`, `snapshots`, `packs`, `packsRead`, `blocksVerified`, `issues`, `damagedSnapshots[]`, `findings[]` — maks. 100). Brak zadania → KB-0104; Agent bez usługi → KB-7012.

## Użycie (offline, bez Agenta)

```
kobian-restore check --repo <katalog> --key-file <plik.key> --password-file <plik> [--repo-header <kobian.repo>] [--sample <0-100>] [--rotation <n>]
```

| Opcja | Znaczenie |
|---|---|
| bez `--sample` | V2 — strukturalna (bez pobierania packów danych; drzewa są czytane, więc packi z drzewami są pobierane) |
| `--sample P` | dodatkowo V3 na próbce: packi posortowane po id dzielone na ⌈100/P⌉ kubełków; sprawdzany kubełek `rotation mod kubełki` |
| `--rotation n` | numer przebiegu (np. numer tygodnia) — kolejne wartości pokrywają 100% packów w ⌈100/P⌉ przebiegach (3.7: „rotacyjnie”) |

Kod wyjścia (6.8): `0` Succeeded, `1` Warning (tylko obiekty bez odwołań), `2` Failed (uszkodzenie albo brak klucza/hasła), `64` składnia.
Znaleziska (stderr): `KB-xxxx: <rodzaj>:<id-hex>`; stdout: `damaged snapshot <id>` dla każdego dotkniętego snapshotu i linia podsumowania.
Raport zawiera wyłącznie identyfikatory obiektów (losowe/HMAC) — bez nazw plików i treści (NFR-11). Lista znalezisk ograniczona do 1000 (DoS).

## Co sprawdza

| Poziom | Sprawdzenie | Kod |
|---|---|---|
| V2 | snapshot czytelny i uwierzytelniony (koperta AEAD) | kod z dekodera (np. KB-1103) |
| V2 | indeks z odwołania snapshotu istnieje, jest uwierzytelniony, SHA-256 = odwołanie | kod z dekodera / KB-1403 |
| V2 | drzewa od korzenia czytelne; każdy blok (dane, ADS, poddrzewo) jest w indeksach **tego** snapshotu | KB-1503 |
| V2 | każdy pack z indeksów istnieje na celu z rozmiarem z indeksu | KB-1501 / KB-1502 |
| V2 | indeksy i packi bez odwołań (np. po przerwanym zadaniu — D3.14) — **ostrzeżenie**; indeks skonsolidowany i indeksy przez niego zastąpione (czekające na prune) nie są sierotami (D3.16) | KB-1504 |
| V3 | pack: SHA-256, stopka (commitTag), AEAD każdej ramki, dekompresja z limitem, `chunkId = HMAC(K_id, raw)` | KB-1403 / KB-1103 / KB-13xx |

Snapshot „uszkodzony” = dotyczy go znalezisko albo używa packa z błędem (wejście dla `check --repair`, S6/D8.10).
`check` działa na sesji z poświadczeniem Backup i niczego nie zapisuje ani nie usuwa (test: repozytorium bajtowo identyczne po `check`).

## V2 automatycznie po każdym backupie (it. 56, 3.7)
- Agent (`CatalogJobService`) po zapisie snapshotu i wierszu `snap_snapshots` uruchamia V2 **tego snapshotu** (`RepoChecker.CheckSnapshotAsync`): bez listowania celu — Stat tylko packów, których snapshot używa; sieroty nie są raportowane.
- Wynik OK → `snap_snapshots.verify_level = 2`. Błąd → uruchomienie `Failed` z kodem KB-15xx (lub KB-1403/dekodera), bez ponowień, snapshot `suspect = 1`.
- Snapshot `suspect` (z V2 po backupie albo z `check` V2/V3 — `CatalogCheckService` oznacza snapshoty uszkodzone) wymusza, że następna kopia zadania jest **pełna** (3.7), a nie przyrost.

## Ograniczenia (PROWIZORYCZNIE)
- Drzewa współdzielone przez snapshoty są czytane osobno dla każdego snapshotu; pack trzymany w pamięci w całości (jak restore, limit 2 GiB) — wydajność V2/V3 niezmierzona.
- Brak plakietki V2 w UI i wysyłki alertu A04 (kod KB-15xx jest w wyniku uruchomienia; powiadomienia — rola 9); `check` nie blokuje równoległego backupu tego samego celu (obiekty w trakcie zapisu mogą chwilowo wyglądać na „bez odwołań” — KB-1504, ostrzeżenie).
- Pack z drzewem, którego brak, jest raportowany raz (KB-1501/1502) — drzewo nie jest wtedy czytane, więc bloki pod nim nie są sprawdzane.
- Kontrola indeksów konsolidowanych (D3.16, pole „supersedes”) — po wdrożeniu konsolidacji.

## `check --repair` — ocena zakresu strat (it. 109, D8.10, S6; PROWIZORYCZNIE Qb57)
- `kobian check <zadanie> [--sample P] --repair [--json]` (RPC `Check` z `repair = true`) — **wymaga roli Administrator** (D8.10: akcje tylko za zgodą Admin), inaczej KB-7011.
- Po zwykłym check (V2, opcjonalnie V3) dla każdego dotkniętego snapshotu `RepoDamageAssessor` przechodzi drzewa: plik z blokiem w brakującym/uszkodzonym packu albo poza indeksami = uszkodzony; drzewo w uszkodzonym packu = cały folder uszkodzony; brak snapshotu, indeksów lub korzenia = **nieodtwarzalny**.
- Wynik: `damage[]` (snapshot, `intact|partial|unrecoverable`, liczby plików/folderów) — bez nazw plików w odpowiedzi (NFR-11); w katalogu `snap_snapshots.damage`, `damaged_files`, `suspect = 1` (następna kopia zadania pełna).
- **Repozytorium nie jest zmieniane i nic nie jest usuwane** (SEC-18.5, append-only). Od it. 110: przed oceną `--repair` odbudowuje indeks z trailerów packów-sierot (`RepoIndexRecovery`: stopka, commitTag i trailer uwierzytelnione kluczami; nowy obiekt `index/<nowy>.idx`, nic nie nadpisane; pack obcy/uszkodzony = „unreadable”, pomijany); odtwarzanie, przeglądanie i podgląd używają `LoadIndexesWithFallbackAsync` — gdy indeks snapshotu zniknął/zmieniono go, bloki są szukane we wszystkich czytelnych indeksach (każdy blok i tak weryfikowany AEAD + HMAC), a wynik odtworzenia to ostrzeżenie KB-1505. Później: tag „damaged” w repozytorium, kopia bloków z repliki (D7.5), lista plików do eksportu.

## Znacznik uszkodzonej kopii (it. 115, RD-70)
Wynik `check --repair` jest widoczny dalej: `kobian snapshots` dopisuje `DAMAGED partial (N files)` albo `DAMAGED unrecoverable` (w `--json` pola `damage` i `damagedFiles`), a w aplikacji kopia ma pigułkę „uszkodzona: N plików” albo „nie do odtworzenia” (Historia i Przeglądanie kopii). Kopii nieodtwarzalnej nie da się wybrać do odtworzenia z aplikacji; częściowo uszkodzoną można — pliki uszkodzone zostaną zgłoszone w wyniku odtwarzania.

## V3 z harmonogramu Agenta (BL-26, 3.7, D7.4, D3.13)
- Polityka per zadanie w definicji: `"verify":{"enabled":true,"intervalDays":7,"samplePercent":5}` (sekcja opcjonalna; brak = te wartości domyślne — `docs/agent-job-definition.md`).
- `CatalogVerifyScheduler` (obracany w pętli `CatalogScheduler`, ocena terminów co 1 min): termin = ostatni **ukończony** cykl (a bez niego najstarsza niezapomniana kopia zadania) + `intervalDays`. Nieukończona próba (cel niedostępny, KB-1202, awaria usługi → `Interrupted`) — ponowienie najwcześniej po 1 h.
- Zasoby kolejki: `repo:<cel>` **wyłączna** (kopie biorą ją współdzieloną) + `smb:<serwer>` z kontem; zadanie z uruchomieniem Queued/Running jest pomijane (priorytet Low). Na celu dzierżawa **współdzielona** (D3.13) — prune innego hosta nie usunie packów w trakcie.
- Próbka **stabilna**: kubełek packa = pierwsze 8 bajtów id mod ⌈100/P⌉ (nowe packi nie przesuwają starych), sprawdzany kubełek = `sched_verify_jobs.rotation mod kubełki`; rotacja rośnie tylko po ukończonym cyklu → każdy pack obecny przez ⌈100/P⌉ ukończonych cykli jest sprawdzony co najmniej raz (domyślnie 20 tygodni). Ręczne `kobian check` zachowuje próbkę pozycyjną (bez zmian).
- Wiersz `run_runs`: cause `schedule`, przejścia z powodem `verify` (w historii rodzaj „check”), `files` = packi pobrane, `bytes_read` = bajty pobrane (koszt transferu).
- Wynik (jedna transakcja): `sched_verify_packs` (packi ok/błąd), `verify_level = 3` dla nieuszkodzonych snapshotów, których **wszystkie** packi przeszły V3 (w tym lub wcześniejszych cyklach; także po ręcznym `check --sample`), `suspect = 1` dla dotkniętych, `force_full = 1` (następna kopia zadania pełna, bez bazy dedup; zdjęte po udanej kopii bez bazy), zdarzenie `pl.kobian.run.finished.v1` w outboxie.
- Kody: uszkodzenie → pierwszy kod KB-15xx raportu albo **KB-1510** (np. błąd AEAD/HMAC bloku); kopia z katalogu nieobecna na celu (pusty/obcy nośnik) → **KB-1511**; same sieroty → KB-1504 (Warning).
- `GetStatus`: `last_verify_unix_seconds`, `last_verify_code`, `next_verify_unix_seconds` (zaległość = termin w przeszłości).
- Wyłączenie w Agencie: `KOBIAN_VERIFY_SCHEDULE=0`; w zadaniu: `"verify":{"enabled":false}`. Nic nie jest usuwane (rola Backup).
