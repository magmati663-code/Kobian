-- R2.3.9: repozytoria, packi, dziennik uploadu (AC-18.1).
CREATE TABLE repo_repositories (
  repo_id TEXT PRIMARY KEY, destination_id TEXT NOT NULL, format_version INTEGER NOT NULL,
  header_sha256 BLOB NOT NULL, last_reconciled_at TEXT, state TEXT NOT NULL CHECK (state IN ('ok','degraded','corrupt'))
) STRICT;
CREATE TABLE repo_packs (
  repo_id TEXT NOT NULL REFERENCES repo_repositories, pack_id BLOB NOT NULL, size INTEGER NOT NULL,
  sha256 BLOB NOT NULL, index_id BLOB, created_run TEXT, mark_delete_at TEXT,
  PRIMARY KEY (repo_id, pack_id)
) STRICT, WITHOUT ROWID;
CREATE TABLE repo_upload_journal (
  run_id TEXT NOT NULL, pack_id BLOB NOT NULL, state TEXT NOT NULL CHECK (state IN ('uploading','committed','verified')),
  bytes INTEGER NOT NULL, updated_at TEXT NOT NULL, PRIMARY KEY (run_id, pack_id)
) STRICT, WITHOUT ROWID;
