-- D7.7: zadania i zależności.
CREATE TABLE job_jobs (
  job_id TEXT PRIMARY KEY, name TEXT NOT NULL UNIQUE, type TEXT NOT NULL
    CHECK (type IN ('files','database','replication','restoreTest','verify')),
  definition BLOB NOT NULL,
  definition_sha256 BLOB NOT NULL,
  enabled INTEGER NOT NULL DEFAULT 1, protected INTEGER NOT NULL DEFAULT 0,
  priority INTEGER NOT NULL DEFAULT 1 CHECK (priority IN (0,1,2)),
  time_zone TEXT NOT NULL, misfire TEXT NOT NULL CHECK (misfire IN ('runOnceIfMissed','skip','runIfMissedWithin')),
  misfire_within_sec INTEGER, created_at TEXT NOT NULL, updated_at TEXT NOT NULL, quarantined_actions INTEGER NOT NULL DEFAULT 0
) STRICT;
CREATE TABLE job_dependencies (
  job_id TEXT NOT NULL REFERENCES job_jobs ON DELETE CASCADE, depends_on TEXT NOT NULL REFERENCES job_jobs,
  condition TEXT NOT NULL CHECK (condition IN ('success','always')), group_no INTEGER NOT NULL DEFAULT 0,
  PRIMARY KEY (job_id, depends_on)
) STRICT, WITHOUT ROWID;
