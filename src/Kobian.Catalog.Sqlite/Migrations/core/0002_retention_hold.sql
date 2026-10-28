-- BL-25 cz. 5 (budowa it. 69), SEC-17.4 / A17 / D9.3: wstrzymanie retencji po skoku zegara — stan całego hosta (jeden wiersz).
-- Obecność wiersza = retencja wstrzymana (żadne zapomnienie, także automatyczne po kopii) do potwierdzenia przez Administratora.
-- Zwolnienie usuwa wiersz; ślad audytu (ustawienie i zwolnienie) w core_events. Pierwsze wstrzymanie wygrywa (kolejne skoki nie nadpisują przyczyny).
CREATE TABLE core_retention_hold (
  id INTEGER PRIMARY KEY CHECK (id = 1),
  held_at TEXT NOT NULL,
  kb_code TEXT NOT NULL CHECK (kb_code GLOB 'KB-[0-9][0-9][0-9][0-9]'),
  -- obserwowany minus oczekiwany upływ czasu [s]: dodatni = skok w przód (AlertFacts.ClockDelta)
  delta_sec INTEGER NOT NULL,
  source TEXT NOT NULL CHECK (source IN ('scheduler','manual'))
) STRICT;
