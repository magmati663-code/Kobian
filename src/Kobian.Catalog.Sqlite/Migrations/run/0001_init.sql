-- D7.7: uruchomienia i historia przejść; unikalność wystąpienia (run_occurrence) — KB-4901 przy naruszeniu.
CREATE TABLE run_runs (
  run_id TEXT PRIMARY KEY, job_id TEXT NOT NULL REFERENCES job_jobs, trigger_id TEXT,
  scheduled_for_utc TEXT,
  cause TEXT NOT NULL CHECK (cause IN ('schedule','misfire','event','dependency','manual','retry','resume')),
  state TEXT NOT NULL, attempt INTEGER NOT NULL DEFAULT 0, parent_run_id TEXT,
  queued_at TEXT, started_at TEXT, finished_at TEXT, kb_code TEXT, snapshot_id TEXT,
  bytes_read INTEGER, bytes_uploaded INTEGER, files INTEGER, warnings INTEGER
) STRICT;
CREATE INDEX run_by_job ON run_runs(job_id, scheduled_for_utc DESC);
CREATE INDEX run_active ON run_runs(state) WHERE state IN ('Queued','Running','Paused');
CREATE UNIQUE INDEX run_occurrence ON run_runs(trigger_id, scheduled_for_utc) WHERE cause IN ('schedule','misfire');
CREATE TABLE run_transitions (
  run_id TEXT NOT NULL REFERENCES run_runs, seq INTEGER NOT NULL, from_state TEXT, to_state TEXT NOT NULL,
  at_utc TEXT NOT NULL, reason TEXT, kb_code TEXT, PRIMARY KEY (run_id, seq)
) STRICT, WITHOUT ROWID;
