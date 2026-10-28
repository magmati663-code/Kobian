# ADR-0017 — Retencja automatyczna po kopii i wstrzymanie po skoku zegara (BL-25 cz. 5, it. 69)

Status: PROWIZORYCZNIE (Qb28). Realizuje Qb25(c) (warunek: prune + wykrywanie skoku zegara), SEC-17.4, A17 (D9.8), D9.3.

## Decyzja
- Po udanej kopii (V2 OK) zadania z polityką: logiczne zapomnienie (`CatalogRetentionService.ApplyAfterBackupAsync`), przyczyna `dependency`, w obrębie blokady zadania. Prune pozostaje ręczny.
- Wstrzymanie retencji = stan hosta w `core_retention_hold` (migracja core 0002, jeden wiersz). Ustawia je pętla schedulera, gdy skok zegara (ścienny vs monotoniczny, D7.9) spełnia warunek reguły A17 z danych (progi nie są powielane w kodzie). Pierwsze wstrzymanie wygrywa; audyt w `core_events`.
- Zwolnienie: RPC `ReleaseRetentionHold` tylko dla roli Administrator; CLI wymaga `--confirm`.

## Alternatywy
- (a) Retencja tylko ręczna (stan z it. 65) — bez odzyskiwania miejsca bez udziału człowieka.
- (b) Wstrzymanie per zadanie zamiast hosta — skok zegara dotyczy całego hosta; per zadanie komplikuje zwolnienie.
- (c) Automatyczne zwolnienie po N poprawnych dobach — wygodniejsze, ale napastnik z kontrolą zegara mógłby je „przeczekać”.
- (d) Wykrywanie skoku także offline (porównanie z czasem modyfikacji na celu) — kolejna iteracja (RD).

## Konsekwencje
+ SEC-17.4 i A17 egzekwowane testami (próg z danych, scheduler, retencja, API, CLI, UI).
+ Kopie nie są blokowane wstrzymaniem — tylko zapominanie.
− Skok zegara przy wyłączonym Agencie (zmiana w BIOS) nie jest wykrywany pętlą; łagodzi to liczenie wieku od max(zegar, najnowsza kopia) i reguły kubełkowe GFS (luka czasu nie zwiększa liczby zapomnianych ponad politykę), ale keepWithin/lock mogą zostać skrócone.
− Tryb deweloperski (rola stała Operator) nie może zwolnić wstrzymania przez API — do czasu ról z SID (BL-17).
Koszt zmiany zdania: mały (flaga `AutoRetention`, `KOBIAN_AUTO_RETENTION=0`).
