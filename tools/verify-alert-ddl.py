#!/usr/bin/env python3
"""Weryfikacja DDL alertów (BL-82, D9.7, R2.9.6) bez .NET: migracja alert 0001 w pamięci,
niezmienniki: jeden nierozwiązany alert na (code, scope), formaty kodu/zakresu/identyfikatora, ważność i stan z listy,
resolved ⇔ resolved_at, STRICT. Dane wyłącznie syntetyczne. Uruchom: python3 tools/verify-alert-ddl.py  (kod wyjścia 0 = OK)"""
import sqlite3, sys, pathlib
root = pathlib.Path(__file__).resolve().parent.parent / "src/Kobian.Catalog.Sqlite/Migrations/alert"
db = sqlite3.connect(":memory:")
db.executescript((root / "0001_init.sql").read_text(encoding="utf-8"))
fails = []
def ok(name, cond): print(("OK   " if cond else "FAIL ") + name); cond or fails.append(name)
def raises(sql, args=()):
    try: db.execute(sql, args); return False
    except sqlite3.IntegrityError: return True
I = "insert into alert_alerts(alert_id,code,scope,severity,state,opened_at,resolved_at) values(?,?,?,?,?,?,?)"
n = [0]
def aid(): n[0] += 1; return "%032x" % n[0]
T = "2026-10-17T10:00:00.000Z"
db.execute(I, (aid(), "A01", "job:j1", "critical", "notified", T, None))
ok("alert_alerts: alert przyjęty", db.execute("select count(*) from alert_alerts").fetchone()[0] == 1)
ok("alert_alerts: drugi nierozwiązany na ten sam klucz odrzucony", raises(I, (aid(), "A01", "job:j1", "critical", "open", T, None)))
ok("alert_alerts: inny zakres przyjęty", not raises(I, (aid(), "A01", "job:j2", "critical", "open", T, None)))
ok("alert_alerts: rozwiązany obok otwartego przyjęty", not raises(I, (aid(), "A01", "job:j1", "critical", "resolved", T, T)))
ok("alert_alerts: zły kod reguły odrzucony", raises(I, (aid(), "B01", "host", "critical", "open", T, None)))
ok("alert_alerts: ścieżka w zakresie odrzucona", raises(I, (aid(), "A02", "job:C:\\Users\\x", "warning", "open", T, None)))
ok("alert_alerts: spacja w zakresie odrzucona", raises(I, (aid(), "A02", "job:Kadry Tajne", "warning", "open", T, None)))
ok("alert_alerts: zakres > 128 znaków odrzucony", raises(I, (aid(), "A02", "job:" + "a" * 125, "warning", "open", T, None)))
ok("alert_alerts: identyfikator spoza hex odrzucony", raises(I, ("x" * 32, "A02", "host", "warning", "open", T, None)))
ok("alert_alerts: nieznana ważność odrzucona", raises(I, (aid(), "A02", "host", "info", "open", T, None)))
ok("alert_alerts: nieznany stan odrzucony", raises(I, (aid(), "A02", "host", "warning", "closed", T, None)))
ok("alert_alerts: resolved bez resolved_at odrzucony", raises(I, (aid(), "A03", "host", "critical", "resolved", T, None)))
ok("alert_alerts: resolved_at przy otwartym odrzucony", raises(I, (aid(), "A03", "host", "critical", "open", T, T)))
ok("alert_alerts: tekst w reminders odrzucony (STRICT)", raises("insert into alert_alerts(alert_id,code,scope,severity,state,opened_at,reminders) values(?,?,?,?,?,?,?)", (aid(), "A04", "host", "critical", "open", T, "x")))
ok("alert_alerts: ujemne reminders odrzucone", raises("insert into alert_alerts(alert_id,code,scope,severity,state,opened_at,reminders) values(?,?,?,?,?,?,?)", (aid(), "A04", "host", "critical", "open", T, -1)))
ok("alert_alerts: last_payload > 4 KiB odrzucony", raises("insert into alert_alerts(alert_id,code,scope,severity,state,opened_at,last_payload) values(?,?,?,?,?,?,?)", (aid(), "A05", "host", "critical", "open", T, b"x" * 4097)))
sys.exit(1 if fails else 0)
