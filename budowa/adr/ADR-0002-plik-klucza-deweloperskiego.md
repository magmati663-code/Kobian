# ADR-0002 — Tymczasowy plik klucza deweloperskiego dla kobian-restore (do BL-24)

Data: 2026-10-13 (iteracja 38). Status: przyjęty tymczasowo — WYMAGA potwierdzenia właściciela (Qb11).
Dotyczy: D3.11 (KDF hasła i opakowanie klucza głównego), D8.3 (kobian-restore), BL-24, BL-44.

## Kontekst
Potok backupu i restore działają end-to-end (it. 33–38), ale BL-24 (PBKDF2/Argon2 + opakowanie MK + pliki `keys/<id>.key`, wektory V-03/V-04) nie jest zaimplementowany. Bez sposobu podania klucza nie da się uruchomić `kobian-restore` ani testu offline.

## Decyzja
`DevKeyFile` (Kobian.Crypto): JSON `{"format":"kobian-devkey-0","repoId":hex16,"masterKey":hex32}` — klucz główny JAWNIE. Używany wyłącznie w testach na danych syntetycznych i w `kobian-restore` do czasu BL-24. Bufory zerowane po wyprowadzeniu kluczy. Format `-0` zostanie odrzucony przez wydanie 1.0 (test do dodania razem z BL-24).

## Alternatywy
1. Zrealizować BL-24 od razu (PBKDF2 z planu MVP) — właściwe rozwiązanie; większy zakres niż jedna iteracja roli 8. **Rekomendowane jako następne zadanie roli 3.**
2. Klucz z zmiennej środowiskowej — gorsze (wycieki do logów procesów).

## Konsekwencje
Ryzyko: plik klucza w postaci jawnej = pełny dostęp do repo. Akceptowalne tylko dla danych syntetycznych; nigdy dla danych klienta. Odstępstwo od D3.11 do czasu BL-24.


**Status (it. 48): ZASTĄPIONY/USUNIĘTY.** `DevKeyFile` usunięty z kodu (kopia w `_to_delete/`); `KeyFileLoader` przyjmuje wyłącznie opakowany klucz + plik hasła + `kobian.repo`. Test: klucz `kobian-devkey-0` → KB-1002.
