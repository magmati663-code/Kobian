-- BL-35 (budowa it. 121), D4.11 / SEC-14.5 / AC-12.2 (CE-25): tożsamości serwerów celów (klucz hosta SSH, SPKI TLS) z maszyną stanów zatwierdzania.
-- DDL wg R2.4.7 „dest_identities” — PROWIZORYCZNIE Qb77: w module „repo” z prefiksem repo_dest_* (jak repo_dest_parallelism, it. 114),
-- bo tabela dest_targets z planu jeszcze nie istnieje (cel = skrót z definicji zadania, JobDestination.Id) — brak klucza obcego do dest_targets.
-- Stany: pending (czeka na Administratora, połączenie odmówione), pinned (zatwierdzony), rejected (odrzucony), retired (zastąpiony).
-- approved_by_sid/approved_at = KTO i KIEDY podjął decyzję (także odrzucenie/wycofanie); 'system:ca-chain' = SPKI zapisany informacyjnie przy ważnym łańcuchu CA
-- (nie jest pinem przekazywanym do połączenia), 'system:job-definition' = pin przeniesiony z pola hostKey definicji zadania (kompatybilność wstecz).
CREATE TABLE repo_dest_identities (
  destination_id TEXT NOT NULL CHECK (destination_id GLOB 'dest-[0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f]'),
  fingerprint TEXT NOT NULL CHECK (fingerprint GLOB 'SHA256:?*' AND substr(fingerprint, 8) NOT GLOB '*[^A-Za-z0-9+/]*' AND length(fingerprint) <= 64),
  kind TEXT NOT NULL CHECK (kind IN ('ssh-hostkey','tls-spki')),
  state TEXT NOT NULL CHECK (state IN ('pending','pinned','rejected','retired')),
  endpoint TEXT NOT NULL CHECK (length(endpoint) BETWEEN 1 AND 300),
  display TEXT CHECK (display IS NULL OR length(display) <= 200),
  first_seen TEXT NOT NULL,
  last_seen TEXT NOT NULL,
  approved_by_sid TEXT CHECK (approved_by_sid IS NULL OR length(approved_by_sid) BETWEEN 1 AND 200),
  approved_at TEXT,
  CHECK (state = 'pending' OR (approved_by_sid IS NOT NULL AND approved_at IS NOT NULL)),
  CHECK (state <> 'pending' OR (approved_by_sid IS NULL AND approved_at IS NULL)),
  PRIMARY KEY (destination_id, fingerprint)
) STRICT, WITHOUT ROWID;
-- SEC-14.5: cel „wysokiego ryzyka” — pin wymagany także przy ważnym łańcuchu CA (opcja Administratora). Brak wiersza = require_pin 0.
CREATE TABLE repo_dest_identity_policy (
  destination_id TEXT PRIMARY KEY CHECK (destination_id GLOB 'dest-[0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f]'),
  require_pin INTEGER NOT NULL CHECK (require_pin IN (0,1)),
  updated_at TEXT NOT NULL,
  updated_by_sid TEXT NOT NULL CHECK (length(updated_by_sid) BETWEEN 1 AND 200)
) STRICT, WITHOUT ROWID;
