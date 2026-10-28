#!/usr/bin/env python3
"""Weryfikacja DDL oznaczeń prune (BL-25 cz. 4, D3.7) bez .NET: migracje repo 0001+0002 w pamięci,
niezmienniki: klucz obiektu (rodzaj:hex 32/64 małymi literami), PK (repo_id, object_key), FK do repo_repositories, STRICT.
Dane wyłącznie syntetyczne. Uruchom: python3 tools/verify-prune-ddl.py  (kod wyjścia 0 = OK)"""
import sqlite3, sys, pathlib
root = pathlib.Path(__file__).resolve().parent.parent / "src/Kobian.Catalog.Sqlite/Migrations"
db = sqlite3.connect(":memory:"); db.execute("PRAGMA foreign_keys=ON")
for f in ("0001_init", "0002_prune_marks"):
    db.executescript((root / "repo" / f"{f}.sql").read_text(encoding="utf-8"))
fails = []
def ok(name, cond): print(("OK   " if cond else "FAIL ") + name); cond or fails.append(name)
def raises(sql, args=()):
    try: db.execute(sql, args); return False
    except sqlite3.IntegrityError: return True
db.execute("insert into repo_repositories values('r1','d1',1,x'00',NULL,'ok')")
M = "insert into repo_prune_marks values(?,?,?)"
h32, h64 = "ab" * 16, "cd" * 32
for k in ("snapshot:" + h32, "index:" + h32, "pack:" + h64):
    db.execute(M, ("r1", k, "2026-10-14T00:00:00.000Z"))
ok("repo_prune_marks: trzy rodzaje obiektów przyjęte", db.execute("select count(*) from repo_prune_marks").fetchone()[0] == 3)
ok("repo_prune_marks: duplikat (repo, obiekt) odrzucony", raises(M, ("r1", "pack:" + h64, "t")))
ok("repo_prune_marks: nieznany rodzaj odrzucony", raises(M, ("r1", "tree:" + h32, "t")))
ok("repo_prune_marks: wielkie litery hex odrzucone", raises(M, ("r1", "pack:" + h64.upper(), "t")))
ok("repo_prune_marks: ścieżka zamiast id odrzucona", raises(M, ("r1", "pack:../../etc/" + h32[:20], "t")))
ok("repo_prune_marks: zła długość odrzucona", raises(M, ("r1", "index:" + h32[:30], "t")))
ok("repo_prune_marks: repozytorium spoza katalogu odrzucone (FK)", raises(M, ("r9", "pack:" + h32, "t")))
ok("repo_prune_marks: NULL marked_at odrzucony", raises(M, ("r1", "index:" + "ef" * 16, None)))
sys.exit(1 if fails else 0)
