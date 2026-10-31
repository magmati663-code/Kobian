# ADR-0022 — SCR-07: filtr odtwarzania z przeglądarki i folder docelowy (PROWIZORYCZNIE Qb34)

Data: 2026-10-14 (it. 77, rola 2). Status: prowizoryczne (odwracalne).

## Kontekst
SCR-07 (R2.6.1) ma wywoływać `PlanRestore` + `StartRestore`; w kontrakcie jest dziś tylko `Restore(snapshot, path_filter, target)` (BL-43) z filtrem w składni `FileSystemName.MatchesSimpleExpression` i bez planu. Wybór w SCR-06 to kopia + ścieżka (folder/plik).

## Decyzja
1. Filtr z wyboru: cała kopia = brak filtra; folder `a/b` = `a/b/*`; plik = dokładna ścieżka względna. Ścieżka z kopii jest zachowana w folderze docelowym (UI pokazuje podgląd „Pliki trafią do: …”).
2. Folder docelowy wpisywany albo wybierany oknem systemowym; walidacja w UI = ścieżka bezwzględna; istnienie i dowiązania sprawdza Agent (KB-5001/KB-5104).
3. Bez `PlanRestore` (planu z liczbą plików/bajtów) do czasu rozszerzenia kontraktu — ekran nie pokazuje szacunku.
4. Restore z UI czeka na wynik (termin 6 h), bez postępu na żywo (WatchRun = SCR-05, później).

## Konsekwencje / ryzyka
- Nazwy plików zawierające `*` lub `?` (możliwe w kopiach z Linuksa) działają w filtrze jak wzorzec — mogą dobrać sąsiednie pliki z TEGO SAMEGO folderu wzorca. Nic nie jest nadpisywane (KB-5105), więc skutkiem są tylko nadmiarowe pliki. Ryzyko Niskie.
- Alternatywa: pole `exact_paths` w RestoreRequest (dokładne dopasowanie, bez wzorców) — koszt mały (dodanie pola, D2.10). Rekomendacja: dodać przed 1.0.
- Koszt zmiany zdania: mały (adapter + jedno pole proto).
