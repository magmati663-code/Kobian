-- RD-42 (budowa it. 79), D9.9 / D9.2: łańcuch audytu — append-only (wyzwalacze), hash = SHA-256(prevHash ‖ JCS(entry)), kotwice.
-- Nadawanie seq i liczenie hash — wyłącznie AuditLogService przez CatalogWriter (jeden pisarz). Wyzwalacze chronią przed kodem aplikacji,
-- nie przed SYSTEM z dostępem do pliku — od tego są kotwice (D9.9; kotwice w repo/e-mailu = później).
CREATE TABLE audit_log (
  seq INTEGER PRIMARY KEY CHECK (seq >= 1),
  at_utc TEXT NOT NULL,
  actor_sid TEXT NOT NULL,
  actor_name TEXT,
  role TEXT NOT NULL,
  channel TEXT NOT NULL CHECK (channel IN ('ui','cli','api','scheduler','installer','updater')),
  operation TEXT NOT NULL,
  target TEXT,
  params BLOB,
  outcome TEXT NOT NULL CHECK (outcome IN ('allowed','denied','failed')),
  kb_code TEXT CHECK (kb_code IS NULL OR kb_code GLOB 'KB-[0-9][0-9][0-9][0-9]'),
  prev_hash BLOB NOT NULL CHECK (length(prev_hash) = 32),
  hash BLOB NOT NULL CHECK (length(hash) = 32)
) STRICT;
CREATE TABLE audit_anchors (
  anchor_seq INTEGER PRIMARY KEY REFERENCES audit_log(seq),
  at_utc TEXT NOT NULL,
  hash BLOB NOT NULL CHECK (length(hash) = 32),
  email_outbox_id INTEGER,
  repo_object TEXT
) STRICT;
CREATE TRIGGER audit_no_update BEFORE UPDATE ON audit_log BEGIN SELECT RAISE(ABORT, 'audit append-only'); END;
CREATE TRIGGER audit_no_delete BEFORE DELETE ON audit_log BEGIN SELECT RAISE(ABORT, 'audit append-only'); END;
-- Ciągłość numeracji: nowy wpis = poprzedni seq + 1 (pierwszy = 1), bez luk przy ręcznym wstawianiu.
CREATE TRIGGER audit_seq_contiguous BEFORE INSERT ON audit_log
WHEN NEW.seq <> COALESCE((SELECT MAX(seq) FROM audit_log), 0) + 1
BEGIN SELECT RAISE(ABORT, 'audit seq gap'); END;
