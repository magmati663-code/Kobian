-- BL-84 (budowa it. 114), D4.16 pkt 2: wyuczony limit równoległości celu (AIMD) — wygasa po 7 dniach (sprawdzane przy odczycie).
-- Wpis tylko, gdy limit < rekomendowanego; powrót do rekomendowanego = usunięcie wpisu.
CREATE TABLE repo_dest_parallelism (
  destination_id TEXT PRIMARY KEY CHECK (length(destination_id) BETWEEN 1 AND 128),
  parallelism INTEGER NOT NULL CHECK (parallelism BETWEEN 1 AND 64),
  learned_at TEXT NOT NULL
) STRICT, WITHOUT ROWID;
