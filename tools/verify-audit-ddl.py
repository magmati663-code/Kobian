#!/usr/bin/env python3
"""Weryfikacja DDL łańcucha audytu (RD-42, D9.9) bez .NET: migracje core 0001..0003 w pamięci.
Niezmienniki: append-only (UPDATE/DELETE odrzucone), seq ciągły od 1, kanał/wynik z zamkniętej listy, hash 32 B, STRICT.
Dane syntetyczne. Uruchom: python3 tools/verify-audit-ddl.py (kod 0 = OK)"""
import sqlite3, sys, pathlib
root = pathlib.Path(__file__).resolve().parent.parent / "src/Kobian.Catalog.Sqlite/Migrations/core"
db = sqlite3.connect(":memory:")
for f in ("0001_init", "0002_retention_hold", "0003_audit"):
    db.executescript((root / f"{f}.sql").read_text(encoding="utf-8"))
fails = []
def ok(name, cond): print(("OK   " if cond else "FAIL ") + name); cond or fails.append(name)
def raises(sql, args=()):
    try: db.execute(sql, args); return False
    except (sqlite3.IntegrityError, sqlite3.DatabaseError): return True
H = b"\x00" * 32
I = "insert into audit_log(seq,at_utc,actor_sid,role,channel,operation,target,params,outcome,kb_code,prev_hash,hash) values(?,?,?,?,?,?,?,?,?,?,?,?)"
def row(seq, **k):
    d = dict(at="2026-10-14T00:00:00.000Z", sid="local", role="Operator", ch="api", op="x", tg=None, pa=None, out="allowed", kb=None, ph=H, h=H)
    d.update(k); return (seq, d["at"], d["sid"], d["role"], d["ch"], d["op"], d["tg"], d["pa"], d["out"], d["kb"], d["ph"], d["h"])
db.execute(I, row(1))
ok("audit_log: pierwszy wpis seq=1 przyjęty", db.execute("select count(*) from audit_log").fetchone()[0] == 1)
ok("audit_log: luka w seq odrzucona", raises(I, row(3)))
ok("audit_log: seq 0 odrzucony", raises(I, row(0)))
db.execute(I, row(2, kb="KB-7011", out="denied"))
ok("audit_log: UPDATE odrzucony", raises("update audit_log set outcome='allowed' where seq=2"))
ok("audit_log: DELETE odrzucony", raises("delete from audit_log where seq=2"))
ok("audit_log: nieznany kanał odrzucony", raises(I, row(3, ch="web")))
ok("audit_log: nieznany wynik odrzucony", raises(I, row(3, out="ok")))
ok("audit_log: zły format KB odrzucony", raises(I, row(3, kb="7011")))
ok("audit_log: hash krótszy niż 32 B odrzucony", raises(I, row(3, h=b"\x01")))
ok("audit_log: tekst zamiast hash odrzucony (STRICT)", raises(I, row(3, h="x" * 32)))
ok("audit_anchors: kotwica do istniejącego wpisu przyjęta", (db.execute("insert into audit_anchors(anchor_seq,at_utc,hash) values(2,'t',?)", (H,)), True)[1])
sys.exit(1 if fails else 0)
