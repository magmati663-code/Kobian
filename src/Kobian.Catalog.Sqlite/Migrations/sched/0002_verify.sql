-- BL-26 (3.7 V3 z harmonogramu, D7.4): stan okresowej weryfikacji próbkowej per zadanie.
-- rotation = liczba UKOŃCZONYCH cykli (= następny kubełek próbki); last_attempt_at > last_completed_at = cykl nieukończony (awaria, cel niedostępny) → ponowienie po odstępie.
-- force_full = 1 po błędzie V3 (3.7: wymuszenie kolejnej kopii pełnej); zerowane po udanej kopii pełnej zadania.
CREATE TABLE sched_verify_jobs (
  job_id TEXT PRIMARY KEY REFERENCES job_jobs ON DELETE CASCADE,
  rotation INTEGER NOT NULL DEFAULT 0 CHECK (rotation >= 0),
  sample_percent INTEGER CHECK (sample_percent IS NULL OR sample_percent BETWEEN 1 AND 100),
  last_attempt_at TEXT,
  last_completed_at TEXT,
  last_run_id TEXT,
  last_status TEXT CHECK (last_status IS NULL OR last_status IN ('Succeeded','Warning','Failed')),
  last_kb_code TEXT CHECK (last_kb_code IS NULL OR (length(last_kb_code) = 7 AND last_kb_code GLOB 'KB-[0-9][0-9][0-9][0-9]')),
  force_full INTEGER NOT NULL DEFAULT 0 CHECK (force_full IN (0,1))
) STRICT;
-- Packi sprawdzone w całości przez V3 (SHA-256, stopka, AEAD i HMAC bloków); ok = 0 → ostatnia weryfikacja packa nieudana.
-- Snapshot dostaje verify_level 3, gdy wszystkie jego packi mają ok = 1 (bez nazw plików — tylko identyfikatory hex, NFR-11).
CREATE TABLE sched_verify_packs (
  repo_id TEXT NOT NULL CHECK (length(repo_id) BETWEEN 16 AND 64 AND repo_id NOT GLOB '*[^0-9a-f]*'),
  pack_id TEXT NOT NULL CHECK (length(pack_id) = 32 AND pack_id NOT GLOB '*[^0-9a-f]*'),
  verified_at TEXT NOT NULL,
  ok INTEGER NOT NULL CHECK (ok IN (0,1)),
  PRIMARY KEY (repo_id, pack_id)
) STRICT, WITHOUT ROWID;
