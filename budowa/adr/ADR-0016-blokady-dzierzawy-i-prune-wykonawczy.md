# ADR-0016 — Blokady-dzierżawy D3.13 i prune wykonawczy (faza 1 + 2) (BL-25 cz. 4, it. 68)

Status: PROWIZORYCZNIE (Qb27). Realizuje D3.13, D3.7; ryzyko PM-18 (krytyczne), SEC-13.6; wymaganie z przeglądu it. 67 („faza 2 tylko pod blokadą wyłączną”).

## Decyzja (prowizoryczna)
1. Plik blokady = płaski JSON wg D3.13 + pole `magic: "kobian-lock"` (rozdział domeny MAC od `kobian.repo` — ten sam K_mac, inny kształt i magic; bez nowej konstrukcji kryptograficznej). `hostId` = skrót nazwy komputera (nie nazwa — NFR-11), `installationId` losowy na proces. Plik musi mieć nazwę równą `lockId` (przeniesienie = odrzucenie).
2. Odświeżanie zawsze nowym plikiem (cele append-only); w sesji Maintenance poprzedni plik jest usuwany. Blokada z `renewedAt` w przyszłości = żywa (błąd zegara wydłuża ochronę).
3. Backup: blokada współdzielona na osobnej sesji Backup, odświeżana w tle; nie da się jej usunąć (D4.1), więc po kopii wygasa po 30 min. Prune przy żywej blokadzie = przebieg `Skipped` KB-1202 (nie czeka).
4. Prune: sesja Maintenance → wyłączna (brak żywych → zapis → odczekanie 2 × 5 min → ponowny przegląd) → faza 1 (oznaczenia w `repo_prune_marks`) → faza 2 (ponowny spis i plan; snapshoty → indeksy → packi; stop przy pierwszej niezgodności KB-1603) → sprzątnięcie przeterminowanych plików blokad.
5. Fałszywe/nieczytelne pliki blokad: ignorowane i raportowane (KB-1201, przebieg Warning); > 4096 plików = repo zajęte (KB-1202).

## Alternatywy
- 3B: plik „zwolnienia” (release marker) zapisywany przez backup po zakończeniu — prune liczy blokadę jako zwolnioną; skraca okno 30 min. Wymaga drugiego pliku z MAC i testu wyścigu.
- 3C: backup z poświadczeniem Maintenance tylko dla `locks/` — łamie D4.1 (sesja Backup nie usuwa); odrzucone.
- 4B: prune czeka (ponawia) zamiast Skipped — dłuższe wywołanie RPC; do rozważenia w harmonogramie prune.

## Konsekwencje
+ Wyścig prune ↔ backup zamknięty z obu stron (test „wyłączna rezygnuje, gdy współdzielona pojawi się w trakcie odczekania”); karencja 24 h zostaje drugą linią obrony.
+ Pełny cykl na prawdziwym repo lokalnym: oznaczenie → nic przed 24 h → usunięcie po 24 h → check V2+V3 100 % i restore bit w bit, kolejny przyrost spójny.
− Przy kopiach co godzinę prune ma okno ~30 min/h; przy częstszych może być stale Skipped (→ 3B).
− Blokada odświeżana w tle wymaga drugiej sesji do celu na czas kopii (dla SFTP: drugie połączenie).
Koszt zmiany zdania: mały (3B dokłada plik; format blokady bez zmian).
