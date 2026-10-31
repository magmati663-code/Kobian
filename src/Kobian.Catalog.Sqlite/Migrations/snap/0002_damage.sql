-- it. 109 (D8.10, check --repair): klasyfikacja szkód snapshotu w katalogu — bez zmiany obiektów w repozytorium (append-only, tag „damaged” w repo = później).
-- damage NULL = nieoceniony/pełny; 'partial' = część plików nieodtwarzalna (damaged_files); 'unrecoverable' = brak snapshotu/indeksów/korzenia.
ALTER TABLE snap_snapshots ADD COLUMN damage TEXT CHECK (damage IS NULL OR damage IN ('partial','unrecoverable'));
ALTER TABLE snap_snapshots ADD COLUMN damaged_files INTEGER CHECK (damaged_files IS NULL OR damaged_files >= 0);
