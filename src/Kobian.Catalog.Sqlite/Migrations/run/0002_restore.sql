-- D8.8: dziennik wznowień restore (idempotentny na poziomie bloku; zapis co pack). Zgodny z R2.8.2.
CREATE TABLE restore_sessions (
  run_id TEXT PRIMARY KEY REFERENCES run_runs, snap_id TEXT NOT NULL, target TEXT NOT NULL,
  caller_sid TEXT NOT NULL, conflict TEXT NOT NULL, restore_acl INTEGER NOT NULL,
  plan_sha256 BLOB NOT NULL,               -- wznowienie tylko przy identycznym planie
  files_total INTEGER, bytes_total INTEGER, state TEXT NOT NULL
) STRICT;
CREATE TABLE restore_files (
  run_id TEXT NOT NULL REFERENCES restore_sessions, file_no INTEGER NOT NULL, dst TEXT NOT NULL,
  size INTEGER NOT NULL, chunks_total INTEGER NOT NULL, chunks_done INTEGER NOT NULL DEFAULT 0,
  state TEXT NOT NULL CHECK (state IN ('pending','partial','verified','renamed','quarantined','skipped')),
  PRIMARY KEY (run_id, file_no)
) STRICT, WITHOUT ROWID;
CREATE TABLE restore_done_packs (          -- pack przetworzony w całości (wszystkie potrzebne bloki zapisane i zsynchronizowane)
  run_id TEXT NOT NULL, pack_id BLOB NOT NULL, PRIMARY KEY (run_id, pack_id)
) STRICT, WITHOUT ROWID;
