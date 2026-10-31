#!/usr/bin/env python3
"""Weryfikacja DDL harmonogramu (D7.7/R2.7.1) bez .NET: wykonuje migracje job/sched/run w pamięci
i sprawdza niezmienniki (unikalność wystąpienia -> KB-4901, CHECK-i, kaskady). Dane wyłącznie syntetyczne.
Uruchom: python3 tools/verify-sched-ddl.py  (kod wyjścia 0 = OK)"""
import sqlite3, sys, pathlib
root = pathlib.Path(__file__).resolve().parent.parent / "src/Kobian.Catalog.Sqlite/Migrations"
db = sqlite3.connect(":memory:"); db.execute("PRAGMA foreign_keys=ON")
for m in ("job", "sched", "run"):
    db.executescript((root / m / "0001_init.sql").read_text(encoding="utf-8"))
fails = []
def ok(name, cond): print(("OK   " if cond else "FAIL ") + name); cond or fails.append(name)
def raises(sql, args=()):
    try: db.execute(sql, args); return False
    except sqlite3.IntegrityError: return True
J = "insert into job_jobs(job_id,name,type,definition,definition_sha256,time_zone,misfire,created_at,updated_at) values(?,?,?,x'7b7d',x'00',?,?,'t','t')"
db.execute(J, ("j1", "a", "files", "Europe/Warsaw", "skip"))
ok("job_jobs: duplikat nazwy odrzucony", raises(J, ("j2", "a", "files", "UTC", "skip")))
ok("job_jobs: zły type odrzucony", raises(J, ("j3", "b", "bogus", "UTC", "skip")))
ok("job_jobs: zły misfire odrzucony", raises(J, ("j4", "c", "files", "UTC", "bogus")))
db.execute("insert into sched_triggers values('t1','j1','cron',x'7b7d',1)")
ok("sched_triggers: zły kind odrzucony", raises("insert into sched_triggers values('t2','j1','bogus',x'7b7d',1)"))
R = "insert into run_runs(run_id,job_id,trigger_id,scheduled_for_utc,cause,state) values(?,?,?,?,?,'Queued')"
db.execute(R, ("r1", "j1", "t1", "2026-10-13T01:00:00Z", "schedule"))
ok("run_occurrence: drugi run tego samego wystąpienia (KB-4901)", raises(R, ("r2", "j1", "t1", "2026-10-13T01:00:00Z", "misfire")))
ok("run_occurrence: nie dotyczy cause=manual", not raises(R, ("r3", "j1", "t1", "2026-10-13T01:00:00Z", "manual")))
ok("run_occurrence: inne wystąpienie dozwolone", not raises(R, ("r4", "j1", "t1", "2026-10-14T01:00:00Z", "schedule")))
ok("run_runs: zła przyczyna odrzucona", raises(R, ("r5", "j1", "t1", "x", "bogus")))
ok("run_runs: nieistniejące zadanie odrzucone (FK)", raises(R, ("r6", "nope", "t1", "y", "schedule")))
db.execute("insert into sched_next_fire values('t1','2026-10-13T01:00:00Z','2026-10-12T00:00:00Z',0)")
db.execute("insert into sched_event_budget values('t1',3.0,1,null)")
db.execute(J, ("j9", "z", "files", "UTC", "skip"))
db.execute("insert into job_dependencies values('j1','j9','success',0)")  # job_id=j1 -> kaskada; depends_on bez CASCADE
ok("job_dependencies: zła condition odrzucona", raises("insert into job_dependencies values('j9','j1','bogus',0)"))
ok("job_dependencies: usunięcie zadania będącego depends_on zablokowane", raises("delete from job_jobs where job_id='j9'"))
ok("usunięcie zadania z uruchomieniami zablokowane (FK bez CASCADE)", raises("delete from job_jobs where job_id='j1'"))
db.execute("delete from run_runs"); db.execute("delete from job_jobs where job_id='j1'")
left = [db.execute(f"select count(*) from {t}").fetchone()[0] for t in ("sched_triggers", "sched_next_fire", "sched_event_budget", "job_dependencies")]
ok("kaskada: wyzwalacze, next_fire, budżet, zależności usunięte", left == [0, 0, 0, 0])
plan = " ".join(r[3] for r in db.execute("explain query plan select * from sched_next_fire where fire_at_utc<='z' order by fire_at_utc"))
ok("sched_next_fire_at używany przez zapytanie o najbliższe wystąpienia", "sched_next_fire_at" in plan)
# BL-26 (3.7 V3 z harmonogramu): sched 0002 — stan weryfikacji per zadanie i packi sprawdzone V3.
db.executescript((root / "sched" / "0002_verify.sql").read_text(encoding="utf-8"))
db.execute(J, ("v1", "v", "files", "UTC", "skip"))
V = "insert into sched_verify_jobs(job_id,rotation,sample_percent,last_status,last_kb_code,force_full) values(?,?,?,?,?,?)"
db.execute(V, ("v1", 0, 5, "Succeeded", None, 0))
ok("sched_verify_jobs: drugi wiersz zadania odrzucony (PK)", raises(V, ("v1", 1, 5, None, None, 0)))
db.execute(J, ("v2", "v2", "files", "UTC", "skip"))
ok("sched_verify_jobs: ujemna rotacja odrzucona", raises(V, ("v2", -1, 5, None, None, 0)))
ok("sched_verify_jobs: próbka 0% odrzucona", raises(V, ("v2", 0, 0, None, None, 0)))
ok("sched_verify_jobs: próbka 101% odrzucona", raises(V, ("v2", 0, 101, None, None, 0)))
ok("sched_verify_jobs: nieznany status odrzucony", raises(V, ("v2", 0, 5, "Running", None, 0)))
ok("sched_verify_jobs: kod spoza formatu KB odrzucony", raises(V, ("v2", 0, 5, "Failed", "KB-15x0", 0)))
ok("sched_verify_jobs: force_full spoza 0/1 odrzucone", raises(V, ("v2", 0, 5, None, None, 2)))
ok("sched_verify_jobs: zadanie spoza katalogu odrzucone (FK)", raises(V, ("nope", 0, 5, None, None, 0)))
P = "insert into sched_verify_packs values(?,?,?,?)"
h32 = "ab" * 16
db.execute(P, (h32, "cd" * 16, "2026-10-17T00:00:00.000Z", 1))
ok("sched_verify_packs: duplikat (repo, pack) odrzucony", raises(P, (h32, "cd" * 16, "t", 0)))
ok("sched_verify_packs: wielkie litery hex odrzucone", raises(P, (h32, "CD" * 16, "t", 1)))
ok("sched_verify_packs: ścieżka zamiast id odrzucona", raises(P, (h32, "../" + "a" * 29, "t", 1)))
ok("sched_verify_packs: ok spoza 0/1 odrzucone", raises(P, (h32, "ef" * 16, "t", 2)))
ok("sched_verify_packs: zły repo_id odrzucony", raises(P, ("xyz", "ef" * 16, "t", 1)))
db.execute("delete from job_jobs where job_id='v1'")
ok("sched_verify_jobs: kaskada przy usunięciu zadania", db.execute("select count(*) from sched_verify_jobs").fetchone()[0] == 0)
print("WYNIK:", "OK" if not fails else f"{len(fails)} niepowodzeń"); sys.exit(1 if fails else 0)
