-- D2.11: tabele wspólne (właściciel: Architekt). PRAGMA ustawiane przy połączeniu (CatalogConnection).
CREATE TABLE core_host (
  host_id         TEXT PRIMARY KEY,
  installation_id TEXT NOT NULL,
  created_at      TEXT NOT NULL
) STRICT;
CREATE TABLE core_events (
  seq         INTEGER PRIMARY KEY AUTOINCREMENT,
  at          TEXT NOT NULL,
  type        TEXT NOT NULL,
  aggregate   TEXT NOT NULL,
  payload     BLOB NOT NULL,
  correlation TEXT
) STRICT;
CREATE INDEX core_events_aggregate ON core_events(aggregate, seq);
CREATE TABLE core_idempotency (
  request_id  TEXT PRIMARY KEY, caller_sid TEXT NOT NULL,
  method TEXT NOT NULL, response BLOB, created_at TEXT NOT NULL
) STRICT;
CREATE TABLE core_outbox (
  id INTEGER PRIMARY KEY AUTOINCREMENT, channel TEXT NOT NULL, payload BLOB NOT NULL,
  attempts INTEGER NOT NULL DEFAULT 0, next_attempt_at TEXT NOT NULL, state TEXT NOT NULL
    CHECK (state IN ('pending','sent','dead'))
) STRICT;
CREATE TABLE core_secrets_ref (
  secret_ref TEXT PRIMARY KEY, kind TEXT NOT NULL, created_at TEXT NOT NULL, rotated_at TEXT
) STRICT;
