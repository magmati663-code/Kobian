# ADR-0001 — Specyfikacja korpusu testowego w JSON zamiast YAML

- Status: przyjęte w budowie (iteracja 10); do akceptacji właściciela (odstępstwo od D10.9 w szczególe formatu).
- Kontekst: D10.9 przewiduje `corpus/*.yaml`. Parser YAML dla .NET nie jest na liście „Zależności — audyt”; nowy pakiet wymaga audytu.
- Decyzja: `tests/corpus/*.json` (System.Text.Json, `UnmappedMemberHandling.Disallow`). Semantyka (ziarno, rozkład rozmiarów, odsetki nazw PL/emoji/długich, duplikaty) bez zmian.
- Konsekwencje: brak nowej zależności; komentarze niemożliwe w specyfikacji (opis w README korpusu). Elementy NTFS (ADS, sparse, junction, ACL) — tryb Windows w kolejnym przyroście.
- Odniesienia: D10.9, zasady bezpieczeństwa promptu (pkt 3).
