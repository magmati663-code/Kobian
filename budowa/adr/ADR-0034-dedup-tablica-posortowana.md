# ADR-0034 — Dedup: bloki bazowe w tablicy posortowanej, Bloom domyślnie wyłączony (RD-22 w2, odstępstwo od D3.15) — PROWIZORYCZNIE Qb52

- Data: 2026-10-17, iteracja 102 (prowadzi rola 12; 3, 13, 11). Status: przyjęte prowizorycznie (Qb52).
- Kontekst: D3.15 — warstwa 1 „tabela z otwartym adresowaniem (32 B klucz + 8 B wartość, wypełnienie 0,7 ≈ 115 MB/2 mln)”, powyżej 2 mln — Bloom + `repo_chunk_index` w katalogu. Warstwa 1 w kodzie (it. 87): słownik ChunkKey → flagi, zmierzone 73–104 B/blok.
- Decyzja: bloki z indeksów bazowych → posortowana tablica `ChunkKey[]` (32 B/blok, wyszukiwanie binarne), zamrażana przy pierwszym odwołaniu; dla nich nie śledzimy „Referenced” (potwierdzone z definicji — I-3 dotyczy tylko bloków nowych, które zostają w słowniku flag). `ChunkBloomFilter` (10 bit/wpis, k = 7) zaimplementowany i zmierzony, ale domyślnie wyłączony (pomiary.md it. 102: przyspiesza tylko chybienia). Warstwa 3 (katalog) — bez zmian, później.
- Odstępstwo od D3.15: struktura warstwy 1 (tablica posortowana zamiast tablicy mieszającej) — mniejsza pamięć (32 vs ~57 B/blok z planu) za cenę wolniejszego, ale wystarczającego wyszukiwania; próg 2 mln przestaje być potrzebny dla budżetu NFR-05 do ≈ 15 mln bloków.
- Konsekwencje: szczyt pamięci przy zamrożeniu = 2 × 32 B/blok przez chwilę; obiekty indeksów bazowych (IndexObject) nadal w pamięci wywołującego — osobna optymalizacja (strumieniowe ładowanie).
- Alternatywy: tablica mieszająca z otwartym adresowaniem wg planu (≈ 46–57 B/blok, szybsza); Bloom domyślnie włączony; od razu `repo_chunk_index`.
- Koszt zmiany zdania: mały (jedna klasa za tym samym API).
