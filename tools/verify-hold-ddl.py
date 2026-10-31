#!/usr/bin/env python3
"""Weryfikacja DDL wstrzymania retencji (BL-25 cz. 5, SEC-17.4) bez .NET: migracje core 0001+0002 w pamięci,
niezmienniki: jeden wiersz (id=1), format kodu KB-dddd, źródło z zamkniętej listy, NOT NULL, STRICT.
Dane wyłącznie syntetyczne. Uruchom: python3 tools/verify-hold-ddl.py  (kod wyjścia 0 = OK)"""
import sqlite3, sys, pathlib
root = pathlib.Path(__file__).resolve().parent.parent / "src/Kobian.Catalog.Sqlite/Migrations/core"
db = sqlite3.connect(":memory:")
for f in ("0001_init", "0002_retention_hold"):
    db.executescript((root / f"{f}.sql").read_text(encoding="utf-8"))
fails = []
def ok(name, cond): print(("OK   " if cond else "FAIL ") + name); cond or fails.append(name)
def raises(sql, args=()):
    try: db.execute(sql, args); return False
    except sqlite3.IntegrityError: return True
I = "insert into core_retention_hold(id,held_at,kb_code,delta_sec,source) values(?,?,?,?,?)"
db.execute(I, (1, "2026-10-14T00:00:00.000Z", "KB-6117", 93600, "scheduler"))
ok("core_retention_hold: wstrzymanie przyjęte", db.execute("select count(*) from core_retention_hold").fetchone()[0] == 1)
ok("core_retention_hold: drugi wiersz odrzucony (id=1)", raises(I, (2, "t", "KB-6117", 1, "scheduler")))
ok("core_retention_hold: duplikat id odrzucony", raises(I, (1, "t", "KB-6117", 1, "scheduler")))
db.execute("delete from core_retention_hold")
ok("core_retention_hold: zły format kodu odrzucony", raises(I, (1, "t", "KB-61x7", 1, "scheduler")))
ok("core_retention_hold: tekst zamiast kodu odrzucony", raises(I, (1, "t", "clock jump C:\\\\x", 1, "scheduler")))
ok("core_retention_hold: nieznane źródło odrzucone", raises(I, (1, "t", "KB-6117", 1, "ui")))
ok("core_retention_hold: NULL delta odrzucona", raises(I, (1, "t", "KB-6117", None, "scheduler")))
ok("core_retention_hold: tekst w delta odrzucony (STRICT)", raises(I, (1, "t", "KB-6117", "abc", "scheduler")))
ok("core_retention_hold: INSERT OR IGNORE nie nadpisuje", (db.execute(I, (1, "a", "KB-6117", 5, "scheduler")), db.execute("insert or ignore into core_retention_hold values(1,'b','KB-6117',9,'manual')"), db.execute("select delta_sec from core_retention_hold").fetchone()[0] == 5)[-1])
sys.exit(1 if fails else 0)
