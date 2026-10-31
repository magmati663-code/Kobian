# budowa_tool.py — pomocnik pętli budowy Kobian Backup. Nie modyfikuj.
# Komendy: init | state | lock | unlock | set-mode <START|BUILD> | done "<podsumowanie>" "<role csv>" | rd-add "<pomysł>" W Z P K "<rola>" | rd-table | plan-append <plik>
import os, sys, json, math, time, random, datetime, shutil
H = os.environ['HOME']; F = os.environ.get('KOBIAN_DIR', H + '/mnt/kobian backup'); B = F + '/budowa'
ST, LOG, RD, PLAN, LOCK = B + '/stan.json', B + '/log.md', B + '/rd-kandydaci.md', F + '/plan.md', F + '/.budowa-run'
TOK = H + '/tmp/budowa_token'; TODAY = datetime.date.today().isoformat(); ROUND = 15
ROLES = {1: 'Product Owner / analityk', 2: 'Architekt systemowy', 3: 'Inżynier silnika backupu', 4: 'Inżynier transportu',
         5: 'Specjalista baz danych i ERP', 6: 'UX/UI', 7: 'Harmonogramy i automatyzacja', 8: 'Odtwarzanie i DR',
         9: 'Monitoring i powiadomienia', 10: 'Jakość, CI, instalator i wydania', 11: 'Bezpieczeństwo',
         12: 'R&D', 13: 'Wydajność i niezawodność', 14: 'Dokumentacja, lokalizacja i zgodność',
         15: 'Przegląd rundy: bezpieczeństwo + R&D (prowadzi rola 11)'}
def rd(p): return open(p, encoding='utf-8', newline='').read()
def wr(p, s): open(p, 'w', encoding='utf-8', newline='').write(s)
def load(): return json.loads(rd(ST))
def kc(k): return 1 if k <= 2 else 2 if k <= 5 else 3 if k <= 10 else 5
def weight(w, z, p, k): return round(10 * ((w + z) / 2) * p / kc(k), 1)
def cmd_init():
    os.makedirs(B, exist_ok=True)
    if not os.path.exists(ST): wr(ST, json.dumps({'iter': 0, 'mode': 'START'}, ensure_ascii=False))
    hdr = {LOG: '# Dziennik budowy\n\n| Data | Iteracja | Runda | Prowadzący | Role | Podsumowanie |\n|---|---|---|---|---|---|\n',
           RD: '# Kandydaci R&D (budowa)\n\n| ID | Iteracja | Rola | Pomysł | W | Z | P | K | Waga | Status |\n|---|---|---|---|---|---|---|---|---|---|\n',
           B + '/bezpieczenstwo.md': '# Rejestr przeglądów bezpieczeństwa (budowa)\n\n',
           B + '/do-uruchomienia-na-windows.md': '# Kolejka testów do uruchomienia na Windows\n\n',
           B + '/decyzje-budowy.md': '# Decyzje właściciela — budowa\n\nWpisz odpowiedź po „DECYZJA:” i zapisz plik. „odłóż” zostawia pytanie otwarte.\n\n',
           B + '/odpowiedzi-startowe.md': '# Odpowiedzi startowe\n\n'}
    for p, h in hdr.items():
        if not os.path.exists(p): wr(p, h)
    print('INIT OK', B)
def cmd_state():
    d = load(); n = d['iter'] + 1; lead = (n - 1) % ROUND + 1
    st = next((l for l in rd(PLAN).split('\n') if l.startswith('## Status')), '?')
    print(json.dumps({'iter_next': n, 'mode': d['mode'], 'round': math.ceil(n / ROUND), 'lead_role': lead, 'lead_name': ROLES[lead],
                      'round_end': n % ROUND == 0, 'plan_status': st, 'plan_done': 'ukończona (limit)' in st}, ensure_ascii=False, indent=1))
def cmd_lock():
    cur = rd(LOCK).strip() if os.path.exists(LOCK) else 'free'
    if cur.startswith('busy '):
        try:
            if time.time() - int(cur.split()[1].split('-')[0]) < 7200: print('BUSY'); return
        except ValueError: pass
    t = f'{int(time.time())}-{random.randint(0, 99999)}'; os.makedirs(H + '/tmp', exist_ok=True)
    wr(LOCK, f'busy {t}'); wr(TOK, t); time.sleep(2)
    print('OK ' + t if rd(LOCK).strip() == f'busy {t}' else 'BUSY')
def cmd_unlock():
    if os.path.exists(TOK) and rd(LOCK).strip() == 'busy ' + rd(TOK).strip(): wr(LOCK, 'free'); print('FREE')
    else: print('NIE MOJA BLOKADA — nie zwalniam')
def own_lock():
    if not (os.path.exists(TOK) and rd(LOCK).strip() == 'busy ' + rd(TOK).strip()): sys.exit('BRAK WŁASNEJ BLOKADY — koniec bez zapisu')
def cmd_set_mode(m):
    own_lock(); d = load(); d['mode'] = m; wr(ST, json.dumps(d, ensure_ascii=False)); print('MODE', m)
def cmd_done(summary, roles):
    own_lock(); d = load(); n = d['iter'] + 1; lead = (n - 1) % ROUND + 1
    if '\n' in summary or '|' in summary or not summary.strip(): sys.exit('podsumowanie: jedna linia bez znaku |')
    open(LOG, 'a', encoding='utf-8').write(f'| {TODAY} | {n} | {math.ceil(n / ROUND)} | {lead} {ROLES[lead]} | {roles} | {summary} |\n')
    d['iter'] = n; wr(ST, json.dumps(d, ensure_ascii=False)); print('ZAPISANO iteracja', n)
def cmd_rd_add(idea, w, z, p, k, role):
    own_lock(); w, z, p, k = int(w), int(z), float(p), float(k)
    if not (1 <= w <= 5 and 1 <= z <= 5 and 0.5 <= p <= 1 and k > 0) or '|' in idea or '\n' in idea: sys.exit('RD: W,Z 1–5; P 0.5–1; K>0 osobodni; pomysł bez |')
    rows = [l for l in rd(RD).split('\n') if l.startswith('| RD-')]; n = len(rows) + 1; it = load()['iter'] + 1
    open(RD, 'a', encoding='utf-8').write(f'| RD-{n} | {it} | {role} | {idea} | {w} | {z} | {p} | {k:g} | {weight(w, z, p, k)} | kandydat |\n'); print(f'RD-{n} waga {weight(w, z, p, k)}')
def cmd_rd_table():
    rows = []
    for l in rd(RD).split('\n'):
        if l.startswith('| RD-'):
            c = [x.strip() for x in l.strip('|').split('|')]; rows.append((weight(int(c[4]), int(c[5]), float(c[6]), float(c[7])), c))
    rows.sort(key=lambda r: -r[0]); rnd = math.ceil(max(1, load()['iter']) / ROUND)
    print(f'# R&D — budowa, runda {rnd} (propozycje i wagi)\n\nWaga = 10 × (W + Z) / 2 × P / Kc; W wartość, Z zgodność z planem lub redukcja ryzyka (1–5), P pewność (0,5–1), Kc: ≤ 2 osobodni = 1, ≤ 5 = 2, ≤ 10 = 3, więcej = 5.\n')
    print('| ID | Iteracja | Rola | Pomysł | W | Z | P | K (osobodni) | Waga | Status |\n|---|---|---|---|---|---|---|---|---|---|')
    for w, c in rows: print('| ' + ' | '.join(c[:8] + [str(w), c[9]]) + ' |')
def cmd_plan_append(path):
    own_lock(); t = rd(path).strip(); old = rd(PLAN)
    if not t.startswith('# R&D — budowa, runda') or '$(date' in t or 'TODO' in t: sys.exit('plan-append: tylko sekcja „# R&D — budowa, runda K”')
    wr(F + '/.budowa-plan-prev.md', old); new = old.rstrip('\n') + '\n\n---\n\n' + t + '\n'; wr(PLAN, new)
    if not rd(PLAN).startswith(old.rstrip('\n')): wr(PLAN, old); sys.exit('ZAPIS NIEUDANY — przywrócono plan.md')
    print('DOPISANO do plan.md, +', len(new.encode()) - len(old.encode()), 'B')
if __name__ == '__main__':
    a = sys.argv[1:]
    {'init': cmd_init, 'state': cmd_state, 'lock': cmd_lock, 'unlock': cmd_unlock, 'set-mode': lambda: cmd_set_mode(a[1]),
     'done': lambda: cmd_done(a[1], a[2]), 'rd-add': lambda: cmd_rd_add(*a[1:7]), 'rd-table': cmd_rd_table,
     'plan-append': lambda: cmd_plan_append(a[1])}[a[0]]()
