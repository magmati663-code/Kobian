# Okresowa weryfikacja kopii (V3) — PROWIZORYCZNIE Qb101

Agent co pewien czas sam sprawdza, czy dane kopii na celu nadają się do odtworzenia: pobiera część plików danych repozytorium (packów), sprawdza ich sumę SHA-256, stopkę oraz uwierzytelnienie (AEAD) i identyfikator (HMAC) każdego bloku. Kolejne cykle sprawdzają kolejne części repozytorium, tak że po kilku cyklach sprawdzone jest całe repozytorium.

## Ustawienia domyślne
| Ustawienie | Domyślnie | Znaczenie |
|---|---|---|
| `enabled` | `true` | weryfikacja okresowa włączona |
| `intervalDays` | `7` | odstęp od ostatniego ukończonego cyklu (1–366 dni); pierwszy cykl — tydzień po pierwszej kopii |
| `samplePercent` | `5` | część packów sprawdzana w jednym cyklu (1–100%); całość w ⌈100 / %⌉ cyklach — domyślnie 20 tygodni |

Zmiana dla zadania — sekcja `verify` w definicji (np. w pliku eksportu `kobian-export v1`):

```json
"verify": {"enabled": true, "intervalDays": 7, "samplePercent": 10}
```

Wyłączenie w całym Agencie: zmienna środowiskowa usługi `KOBIAN_VERIFY_SCHEDULE=0` (ręczne `kobian check` działa dalej).

## Koszt
Cykl pobiera ok. `samplePercent`% danych repozytorium z celu. Dla celów zdalnych (SFTP, FTPS, SMB przez WAN) to rzeczywisty transfer — przy dużych repozytoriach i wolnym łączu zmniejsz `samplePercent` albo zwiększ `intervalDays`. Pobrane bajty widać w historii uruchomień (`kobian runs`, kolumna bajtów odczytu; rodzaj „check”).

## Kopia i weryfikacja nie przeszkadzają sobie
- Weryfikacja nie startuje, gdy trwa lub czeka w kolejce uruchomienie tego zadania, i nie biegnie równolegle z kopią na tym samym repozytorium — kopia, która przyjdzie w trakcie, czeka w kolejce na koniec weryfikacji.
- Na celu weryfikacja zakłada blokadę współdzieloną (jak kopia), więc konserwacja (prune) innego komputera nie usunie danych w trakcie.
- Gdy cel jest niedostępny albo usługa została zatrzymana, cykl zostaje powtórzony najwcześniej po godzinie — ta sama część repozytorium, bez pomijania.

## Wynik
| Wynik | Co się dzieje |
|---|---|
| poprawny | kopie, których wszystkie dane zostały już sprawdzone, dostają plakietkę **V3**; API stanu Agenta (GetStatus) podaje datę ostatniej weryfikacji, jej kod i termin następnej (wyświetlenie w `kobian status` i na pulpicie — w kolejnych wersjach) |
| **KB-1510** — uszkodzone dane | uruchomienie `Failed`, dotknięte kopie oznaczone jako podejrzane, **następna kopia zadania będzie pełna**, zdarzenie `run.finished` (powiadomienie / alert A04) |
| **KB-1511** — brak kopii na celu | kopia zapisana w katalogu nie istnieje na celu (np. podłączono inny dysk USB albo dane usunięto) — jak wyżej |
| KB-1501…1503 | brak packa, zły rozmiar, blok poza indeksem (V2 wykonywane w każdym cyklu) |
| KB-1504 (ostrzeżenie) | obiekty bez odwołań — zwykle po przerwanej kopii |

Weryfikacja niczego nie usuwa ani nie naprawia. Po błędzie: sprawdź nośnik/serwer, uruchom `kobian check <zadanie> --repair` (rola Administrator), aby poznać zakres strat, i odtwarzaj dotknięte pliki z innej kopii (replika, USB).
