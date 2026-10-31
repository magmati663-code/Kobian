-- R2.3.9: snapshoty i indeks nazw.
CREATE TABLE snap_snapshots (
  snap_id TEXT PRIMARY KEY, repo_id TEXT NOT NULL REFERENCES repo_repositories, job_id TEXT NOT NULL,
  run_id TEXT NOT NULL, host_id TEXT NOT NULL, time_utc TEXT NOT NULL,
  mode INTEGER NOT NULL CHECK (mode BETWEEN 0 AND 3), base_full TEXT, parent TEXT, root_tree BLOB NOT NULL,
  files INTEGER, bytes_raw INTEGER, bytes_new INTEGER, verify_level INTEGER NOT NULL DEFAULT 1,
  suspect INTEGER NOT NULL DEFAULT 0, forgotten_at TEXT
) STRICT;
CREATE INDEX snap_by_job_time ON snap_snapshots(job_id, time_utc DESC);
CREATE TABLE snap_name_index (
  snap_id TEXT NOT NULL, path_id INTEGER NOT NULL, change INTEGER NOT NULL CHECK (change IN (0,1,2)),
  size INTEGER, mtime INTEGER, PRIMARY KEY (snap_id, path_id)
) STRICT, WITHOUT ROWID;
CREATE TABLE snap_paths (path_id INTEGER PRIMARY KEY, parent_id INTEGER, name TEXT NOT NULL) STRICT;
