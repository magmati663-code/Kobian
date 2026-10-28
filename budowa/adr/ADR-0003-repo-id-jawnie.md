# ADR-0003 — repoId podawany jawnie do kobian-restore z plikiem klucza hasła (PROWIZORYCZNIE Qb12)

Data: 2026-10-13 (iteracja 41). Status: prowizoryczny. Dotyczy: D3.11, BL-24, BL-21 (kobian.repo), BL-44.

## Kontekst
Plik `keys/*.key` (D3.11) nie zawiera `repoId`, a HKDF wymaga go jako soli. Źródłem w docelowym formacie jest `kobian.repo` (body.repoId, MAC V-09) — jego czytnik nie jest jeszcze zaimplementowany.

## Decyzja
`kobian-restore` przyjmuje `--key-file <keys/x.key> --repo-id <hex32> --password-file <plik>`. Hasło tylko z pliku (pierwsza linia, UTF-8, bez normalizacji). Po wdrożeniu `kobian.repo` `--repo-id` staje się zbędny (odczyt + weryfikacja MAC po odblokowaniu).

## Alternatywy
Interaktywny prompt hasła bez echa (do dodania z GUI/CLI w BL-47); `repoId` w pliku klucza (zmienia format D3.11 — odrzucone).

## Koszt zmiany zdania
Niski: zmiana tylko w `KeyFileLoader` i `OfflineRestore`; format plików kluczy bez zmian.


**Status (it. 48): ZASTĄPIONY/USUNIĘTY.** `--repo-id` usunięty z `kobian-restore` i `KeyFileLoader`; `repoId` wyłącznie z uwierzytelnionego nagłówka `kobian.repo` (MAC, V-09).
