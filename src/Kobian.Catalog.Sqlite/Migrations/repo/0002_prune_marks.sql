-- BL-25 cz. 4 (budowa it. 68), D3.7/D3.13: faza 1 prune dwufazowego — obiekty nieżywe oznaczone z czasem oznaczenia.
-- object_key = 'snapshot:<hex>' | 'index:<hex>' | 'pack:<hex>' (hex małymi literami, 32 lub 64 znaki); usunięcie na celu dopiero po karencji (domyślnie 24 h).
CREATE TABLE repo_prune_marks (
  repo_id TEXT NOT NULL REFERENCES repo_repositories,
  object_key TEXT NOT NULL CHECK (
    (object_key GLOB 'snapshot:*' OR object_key GLOB 'index:*' OR object_key GLOB 'pack:*')
    AND substr(object_key, instr(object_key, ':') + 1) NOT GLOB '*[^0-9a-f]*'
    AND length(substr(object_key, instr(object_key, ':') + 1)) IN (32, 64)),
  marked_at TEXT NOT NULL,
  PRIMARY KEY (repo_id, object_key)
) STRICT, WITHOUT ROWID;
