#!/usr/bin/env python3
"""Weryfikacja DDL tożsamości serwerów celów (BL-35, D4.11, SEC-14.5) bez .NET: migracje repo 0001..0004 w pamięci,
niezmienniki CHECK lustrzane do maszyny stanów (ServerIdentityMachine): identyfikator celu, kształt odcisku (bez listy/dopełnienia),
rodzaj, stan, decyzja (kto/kiedy) dla stanów innych niż pending, brak decyzji dla pending, polityka require_pin 0/1, STRICT.
Dane wyłącznie syntetyczne. Uruchom: python3 tools/verify-ident-ddl.py  (kod wyjścia 0 = OK)"""
import sqlite3, sys, pathlib
root = pathlib.Path(__file__).resolve().parent.parent / "src/Kobian.Catalog.Sqlite/Migrations/repo"
db = sqlite3.connect(":memory:")
for f in sorted(root.glob("000[1-4]_*.sql")):
    db.executescript(f.read_text(encoding="utf-8"))
fails = []
def ok(name, cond): print(("OK   " if cond else "FAIL ") + name); cond or fails.append(name)
def raises(sql, args=()):
    try: db.execute(sql, args); return False
    except sqlite3.IntegrityError: return True
D = "dest-0123456789abcdef"
FP = "SHA256:" + "Ab+/9" * 8 + "xyz"
T = "2026-10-17T08:00:00.000Z"
I = ("insert into repo_dest_identities(destination_id,fingerprint,kind,state,endpoint,display,first_seen,last_seen,approved_by_sid,approved_at) "
     "values(?,?,?,?,?,?,?,?,?,?)")
def row(d=D, fp=FP, kind="ssh-hostkey", state="pending", ep="sftp://h:22", disp=None, by=None, at=None): return (d, fp, kind, state, ep, disp, T, T, by, at)
db.execute(I, row())
ok("identities: pending bez decyzji przyjęty", db.execute("select count(*) from repo_dest_identities").fetchone()[0] == 1)
ok("identities: duplikat (cel, odcisk) odrzucony", raises(I, row()))
ok("identities: zły identyfikator celu", raises(I, row(d="dest-0123456789ABCDEF", fp=FP + "1")))
ok("identities: identyfikator z traversal", raises(I, row(d="../dest-0123456789abcdef", fp=FP + "2")))
ok("identities: lista pinów w jednym wierszu", raises(I, row(fp="SHA256:abc,SHA256:def")))
ok("identities: dopełnienie '='", raises(I, row(fp="SHA256:abc=")))
ok("identities: inny algorytm", raises(I, row(fp="MD5:aa:bb")))
ok("identities: pusty odcisk po prefiksie", raises(I, row(fp="SHA256:")))
ok("identities: odcisk > 64 znaki", raises(I, row(fp="SHA256:" + "A" * 58)))
ok("identities: nieznany rodzaj", raises(I, row(fp=FP + "3", kind="ssh-dss")))
ok("identities: nieznany stan", raises(I, row(fp=FP + "4", state="trusted", by="S-1", at=T)))
ok("identities: pinned bez decyzji", raises(I, row(fp=FP + "5", state="pinned")))
ok("identities: pending z decyzją", raises(I, row(fp=FP + "6", by="S-1", at=T)))
ok("identities: pusty endpoint", raises(I, row(fp=FP + "7", ep="")))
ok("identities: display > 200", raises(I, row(fp=FP + "8", disp="x" * 201)))
db.execute(I, row(fp=FP + "9", kind="tls-spki", state="pinned", by="system:ca-chain", at=T))
ok("identities: pinned z decyzją przyjęty", db.execute("select count(*) from repo_dest_identities").fetchone()[0] == 2)
P = "insert into repo_dest_identity_policy(destination_id,require_pin,updated_at,updated_by_sid) values(?,?,?,?)"
db.execute(P, (D, 1, T, "S-1-5-21-1"))
ok("policy: require_pin 2 odrzucony", raises(P, ("dest-00112233aabbccdd", 2, T, "S-1")))
ok("policy: tekst w require_pin (STRICT)", raises(P, ("dest-00112233aabbccdd", "yes", T, "S-1")))
ok("policy: pusty SID", raises(P, ("dest-00112233aabbccdd", 0, T, "")))
ok("policy: jeden wiersz na cel", raises(P, (D, 0, T, "S-1")))
sys.exit(1 if fails else 0)
