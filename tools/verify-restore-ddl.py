#!/usr/bin/env python3
"""Weryfikacja DDL dziennika restore (D8.8/R2.8.2) bez .NET: migracje job/sched/run (0001+0002) w pamięci,
niezmienniki: CHECK stanu pliku, FK, PK idempotencji packa, STRICT. Dane wyłącznie syntetyczne.
Uruchom: python3 tools/verify-restore-ddl.py  (kod wyjścia 0 = OK)"""
import sqlite3, sys, pathlib
root = pathlib.Path(__file__).resolve().parent.parent / "src/Kobian.Catalog.Sqlite/Migrations"
db = sqlite3.connect(":memory:"); db.execute("PRAGMA foreign_keys=ON")
for m, f in (("job", "0001_init"), ("sched", "0001_init"), ("run", "0001_init"), ("run", "0002_restore")):
    db.executescript((root / m / f"{f}.sql").read_text(encoding="utf-8"))
fails = []
def ok(name, cond): print(("OK   " if cond else "FAIL ") + name); cond or fails.append(name)
def raises(sql, args=()):
    try: db.execute(sql, args); return False
    except sqlite3.IntegrityError: return True
db.execute("insert into job_jobs(job_id,name,type,definition,definition_sha256,time_zone,misfire,created_at,updated_at) values('j1','a','files',x'7b7d',x'00','UTC','skip','t','t')")
db.execute("insert into run_runs(run_id,job_id,cause,state) values('r1','j1','manual','Running')")
S = "insert into restore_sessions values(?,?,?,?,?,?,?,?,?,?)"
db.execute(S, ("r1", "s1", "C:/t", "S-1-5-x", "rename", 0, b"\x00" * 32, 2, 100, "Running"))
ok("restore_sessions: duplikat run_id odrzucony", raises(S, ("r1", "s1", "C:/t", "S", "rename", 0, b"\x00", 1, 1, "Running")))
ok("restore_sessions: run_id spoza run_runs odrzucony (FK)", raises(S, ("nope", "s1", "C:/t", "S", "rename", 0, b"\x00", 1, 1, "Running")))
ok("restore_sessions: NULL plan_sha256 odrzucony", raises(S, ("r1x", "s1", "t", "S", "c", 0, None, 1, 1, "x")))
ok("restore_sessions: zły typ (STRICT) odrzucony", raises(S, ("r9", "s1", "t", "S", "c", "abc", b"\x00", 1, 1, "x")))
Fl = "insert into restore_files(run_id,file_no,dst,size,chunks_total,state) values(?,?,?,?,?,?)"
db.execute(Fl, ("r1", 1, "a.txt", 10, 1, "pending"))
for i, st in enumerate(("partial", "verified", "renamed", "quarantined", "skipped")):
    db.execute(Fl, ("r1", 10 + i, "f" + st, 1, 1, st))
ok("restore_files: wszystkie 6 stanów dozwolone", db.execute("select count(*) from restore_files").fetchone()[0] == 6)
ok("restore_files: zły stan odrzucony", raises(Fl, ("r1", 99, "x", 1, 1, "bogus")))
ok("restore_files: duplikat (run_id,file_no) odrzucony", raises(Fl, ("r1", 1, "b", 1, 1, "pending")))
ok("restore_files: sesja nieistniejąca odrzucona (FK)", raises(Fl, ("zz", 1, "b", 1, 1, "pending")))
ok("restore_files: chunks_done domyślnie 0", db.execute("select chunks_done from restore_files where file_no=1").fetchone()[0] == 0)
P = "insert into restore_done_packs values(?,?)"
db.execute(P, ("r1", b"\x01" * 16))
ok("restore_done_packs: powtórny zapis packa odrzucony (PK; wznowienie robi INSERT OR IGNORE)", raises(P, ("r1", b"\x01" * 16)))
db.execute("insert or ignore into restore_done_packs values('r1',x'01010101010101010101010101010101')")
ok("restore_done_packs: INSERT OR IGNORE idempotentny", db.execute("select count(*) from restore_done_packs").fetchone()[0] == 1)
ok("usunięcie sesji z plikami zablokowane (FK bez CASCADE)", raises("delete from restore_sessions where run_id='r1'"))
ok("usunięcie run_runs z sesją restore zablokowane (FK)", raises("delete from run_runs where run_id='r1'"))
print("WYNIK:", "OK" if not fails else f"{len(fails)} niepowodzeń"); sys.exit(1 if fails else 0)
