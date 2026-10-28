-- BL-82 (budowa it. alerty), D9.7 / R2.9.6: trwały stan alertów — encja z kluczem deduplikacji (code, scope) i maszyną stanów.
-- DDL wg R2.9.6 + CHECK-i formatów (kod A01–A99, ważność i stan z zamkniętej listy, zakres = nieprzejrzysty identyfikator bez ścieżek — SEC-9.6).
-- PROWIZORYCZNIE Qb93: dodatkowa kolumna acknowledged_at (maszyna stanów D9.7 liczy 72 h od potwierdzenia; plan ma tylko acknowledged_by).
-- next_reminder nie jest przechowywany — wynika z last_notified_at + reminderHours reguły (jedno źródło prawdy, bez rozjazdu przy zmianie progu).
-- alert_channels z R2.9.6 — później (kanały konfigurowane dziś zmiennymi środowiska: Qb58, Qb64).
-- Wiersze resolved zostają jako historia; jeden nierozwiązany alert na klucz (indeks częściowy).
CREATE TABLE alert_alerts (
  alert_id TEXT PRIMARY KEY CHECK (length(alert_id) = 32 AND alert_id NOT GLOB '*[^0-9a-f]*'),
  code TEXT NOT NULL CHECK (code GLOB 'A[0-9][0-9]'),
  scope TEXT NOT NULL CHECK (length(scope) BETWEEN 1 AND 128 AND scope NOT GLOB '*[^A-Za-z0-9_.:-]*'),
  severity TEXT NOT NULL CHECK (severity IN ('warning','critical','security')),
  state TEXT NOT NULL CHECK (state IN ('open','notified','acknowledged','silenced','resolved')),
  opened_at TEXT NOT NULL,
  last_notified_at TEXT,
  reminders INTEGER NOT NULL DEFAULT 0 CHECK (reminders >= 0),
  escalated INTEGER NOT NULL DEFAULT 0 CHECK (escalated IN (0,1)),
  acknowledged_by TEXT CHECK (acknowledged_by IS NULL OR length(acknowledged_by) BETWEEN 1 AND 200),
  acknowledged_at TEXT,
  silenced_until TEXT,
  resolved_at TEXT,
  last_payload BLOB CHECK (last_payload IS NULL OR length(last_payload) <= 4096),
  CHECK ((state = 'resolved') = (resolved_at IS NOT NULL))
) STRICT;
CREATE UNIQUE INDEX alert_open_dedup ON alert_alerts(code, scope) WHERE state <> 'resolved';
CREATE INDEX alert_by_state ON alert_alerts(state, opened_at);
