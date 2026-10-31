# ADR-0015 — Prune: żywość przez indeksy snapshotów, plan jako czysta funkcja, dwufazowość (BL-25 cz. 3, it. 67)

Status: PROWIZORYCZNIE (Qb26). Realizuje D3.7 / D3.12 „Prune (MVP)”; ryzyko PM-18 (krytyczne), CE-18 / AC-10.3.

## Kontekst
Snapshot (klucz 11) wymienia indeksy z SHA-256; przyrost (`CatalogJobService.PlanBaselineAsync`) przepisuje do nowego snapshotu WSZYSTKIE indeksy rodzica i deduplikuje względem nich. D3.12 mówi „żywe packi = referencjonowane przez snapshoty z Keep” i „nowy index bez nieżywych packów” — ale przepisanie indeksu zmienia jego SHA, a snapshoty są niezmienne (D3.16: relacja „zastąpiony przez” w katalogu).

## Decyzja (prowizoryczna)
1. Snapshot żywy = obecny na celu i niezapomniany w katalogu (`forgotten_at`) dla CAŁEGO repozytorium; snapshot nieznany katalogowi = żywy.
2. Indeks/pack żywy = wymieniony w którymkolwiek indeksie z klucza 11 żywego snapshotu (osiągalność przez indeksy, nie przez drzewa).
3. Plan = czysta funkcja `PrunePlanner.Plan(spis, oznaczenia, now, karencja)`; spis tylko do odczytu (sesja Backup). Bezpieczniki: KB-1601 (nieczytelny obiekt żywego snapshotu), KB-1602 (nie zostałaby żadna kopia).
4. Dwufazowość: usuwalne tylko oznaczone ≥ 24 h temu i nadal nieżywe; oznaczenie z przyszłości nie skraca karencji; znów żywe → zdjęcie oznaczenia.
5. W it. 67 wyłącznie podgląd (RPC `PlanPrune`, `kobian prune --dry-run`). Faza 1 (tabela oznaczeń w katalogu) i faza 2 (usuwanie w sesji Maintenance pod blokadą wyłączną D3.13, najpierw snapshoty, potem indeksy, na końcu packi) — kolejne iteracje.

## Alternatywy
- B: żywość przez drzewa (bloki faktycznie używane) + przepisanie indeksów + relacja „zastąpiony przez” (D3.16) + wykluczenie oznaczonych packów z deduplikacji w BackupPipeline. Większy odzysk miejsca w łańcuchach przyrostów; wyższe ryzyko PM-18 i koszt (≈ 5–8 osobodni).
- C: okresowa kopia pełna (np. co N przyrostów) jako parametr zadania — tani sposób, by wariant A odzyskiwał miejsce.

## Konsekwencje
+ Dedup kolejnej kopii nigdy nie wskaże usuniętego packa; pełne `check` po prune zielone bez przepisywania indeksów; zgodne z append-only.
+ Test właściwości (5 ziaren, dwa zadania w repo): po usunięciu planu każdy żywy snapshot przechodzi V2/V3 i odtwarza się bit w bit; kolejny przyrost spójny. Mutacja planera (pominięcie jednego packa z żywego indeksu) — 7/9 testów czerwonych.
− Przyrosty „na zawsze” nie odzyskują miejsca (cały łańcuch żywy, dopóki żyje jego koniec) — potrzebne C albo B.
Koszt zmiany zdania: średni (B wymaga zmian w formacie katalogu i potoku, nie w formacie repo).
