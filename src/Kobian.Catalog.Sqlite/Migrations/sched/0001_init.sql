-- D7.7: wyzwalacze, najbliższe wystąpienia, budżet zdarzeń.
CREATE TABLE sched_triggers (
  trigger_id TEXT PRIMARY KEY, job_id TEXT NOT NULL REFERENCES job_jobs ON DELETE CASCADE,
  kind TEXT NOT NULL CHECK (kind IN ('cron','interval','startup','session','device','network','idle','dependency','manual')),
  spec BLOB NOT NULL,
  enabled INTEGER NOT NULL DEFAULT 1
) STRICT;
CREATE TABLE sched_next_fire (
  trigger_id TEXT PRIMARY KEY REFERENCES sched_triggers ON DELETE CASCADE,
  fire_at_utc TEXT NOT NULL, computed_at_utc TEXT NOT NULL, wake_requested INTEGER NOT NULL DEFAULT 0
) STRICT;
CREATE INDEX sched_next_fire_at ON sched_next_fire(fire_at_utc);
CREATE TABLE sched_event_budget (
  trigger_id TEXT PRIMARY KEY REFERENCES sched_triggers ON DELETE CASCADE,
  tokens REAL NOT NULL, refreshed_at_mono INTEGER NOT NULL, last_event_utc TEXT
) STRICT;
