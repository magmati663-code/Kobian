# kobian_tool.py — narzędzie pętli planu Kobian Backup (faza II, it. 22–50). Nie modyfikuj.
# Komendy: check | map | digest | aggregate | ids <rola> | decisions [--init] | alert <kod> | fix-status | insert <N> [--dry-run]
# W plikach wejściowych znacznik {DATA} zostaje zastąpiony dzisiejszą datą (RRRR-MM-DD).
import os, sys, re, math, hashlib, datetime
H = os.environ['HOME']; F = os.environ.get('KOBIAN_DIR', H + '/mnt/kobian backup')
P, HASH, DEC, ALERT = F + '/plan.md', F + '/.kobian-plan-hash', F + '/decyzje.md', F + '/.kobian-plan-alert'
TODAY = datetime.date.today().isoformat()
def prev_path(N): return F + f'/.kobian-plan-prev-{N % 3}.md'  # rotacja 3 kopii
IDX = '# Indeks decyzji — faza II'
REG = [('risk.md', '# Rejestr ryzyk i spike’ów — faza II',
        '| ID | Iteracja | Dziedzina | Typ (PM/SP) | Opis (1 zdanie) | Kryterium / mitygacja | Status |\n|---|---|---|---|---|---|---|'),
       ('epics.md', '# Rejestr epików — faza II',
        '| ID | Iteracja | Dziedzina | Epik | Kamień | Estymata (osobodni) | Zależności |\n|---|---|---|---|---|---|---|')]
KB = {1: [0], 2: [7], 3: [1], 4: [2], 5: [3], 6: [9, 0], 7: [4], 8: [5], 9: [6], 10: [8]}
HUMAN = {10, 37, 38, 39, 45, 51}  # pytania „decyzja człowieka” sprzed it. 22 (o innym brzmieniu statusu)
MAX_ADD = 45000  # maks. bajtów nowej treści na iterację (z rejestrami i wierszami)
STRE = r'^## Status: iteracja (\d+) \(faza II — rozbudowa, runda (\d+)\)$'
def read(p): return open(p, encoding='utf-8', newline='').read()
def sha(s): return hashlib.sha256(s.encode('utf-8')).hexdigest()
def ncols(l): return l.replace('\\|', '').count('|')
def status(s):
    m = [re.match(STRE, l) for l in s.split('\n') if l.startswith('## Status')]
    if any('ukończona (limit)' in l for l in s.split('\n') if l.startswith('## Status')): return 'LIMIT'
    m = [x for x in m if x]
    return int(m[0].group(1)) if len(m) == 1 else None
def logged(s, N): return any(re.match(rf'^\| \d{{4}}-\d\d-\d\d \| {N} \|', l) for l in s.split('\n'))
def structure(s):
    bad, S, i = [], s.split('\n'), 0
    if sum(1 for l in S if l.startswith('```')) % 2: bad.append('niezamknięty blok ```')
    while i < len(S):
        if S[i].startswith('|'):
            j = i
            while j < len(S) and S[j].startswith('|'): j += 1
            c = {ncols(l) for l in S[i:j]}
            if len(c) > 1: bad.append(f'tabela od linii {i+1}: różna liczba kolumn {sorted(c)}')
            i = j
        else: i += 1
    return bad
def drafts(s):
    bad, inf = [], False
    for n, l in enumerate(s.split('\n'), 1):
        if l.startswith('```'): inf = not inf; continue
        if not inf and (re.search(r'\?\s*(Nie|nie)\s*[—:,-]', l) or re.search(r'\b(TODO|TBD|FIXME|XXX)\b', l) or '???' in l):
            bad.append(f'linia {n}: zapis roboczy -> {l[:100]}')
    return bad
def cmd_check():
    try: s = read(P)
    except UnicodeDecodeError: sys.exit('UTRATA TREŚCI: plan.md nie jest poprawnym UTF-8 (uszkodzony/ucięty) — NIE zapisuj; powiadom (kopie: .kobian-plan-prev-0/1/2.md)')
    N = status(s)
    print('rozmiar', len(s.encode()), 'B; dzisiaj', datetime.date.today().isoformat())
    if os.path.exists(HASH) and read(HASH).strip() != sha(s):
        last = read(HASH + '.ref') if os.path.exists(HASH + '.ref') else ''
        if last:
            lost = [h for h in last.split('\n') if h.startswith('# ') and h not in s.split('\n')]
            size0 = int(last.split('\n', 1)[0])
            if len(s.encode()) < 0.95 * size0 or lost:
                sys.exit(f'UTRATA TREŚCI: plan mniejszy niż {size0} B o >5% lub brak nagłówków {lost[:3]} — NIE zapisuj; powiadom (kopie: .kobian-plan-prev-0/1/2.md)')
        print('EDYCJA WŁAŚCICIELA: plan zmieniono poza pętlą od ostatniego zapisu (zmiany są wiążące; odnotuj w dzienniku)')
    if N == 'LIMIT': print('LIMIT: faza II ukończona'); return
    if N is None: sys.exit('BŁĄD STATUSU: brak dokładnie jednej poprawnej linii statusu — powiadom i zakończ')
    R, role = math.ceil(N/10), (N-1) % 10 + 1
    print(f'N={N} R={R} rola={role}')
    if logged(s, N): print(f'DUBEL: dziennik ma już iterację {N} — wykonaj fix-status i zakończ')
def cmd_fix_status():
    s = read(P); N = status(s)
    if not isinstance(N, int) or not logged(s, N): sys.exit('fix-status: brak potrzeby')
    new = ('## Status: iteracja 50 — faza II ukończona (limit)' if N == 50
           else f'## Status: iteracja {N+1} (faza II — rozbudowa, runda {math.ceil((N+1)/10)})')
    s2 = re.sub(STRE, new, s, count=1, flags=re.M); open(P, 'w', encoding='utf-8', newline='').write(s2)
    remember(s2); print('status poprawiony:', new)
def remember(s):
    open(HASH, 'w').write(sha(s))
    open(HASH + '.ref', 'w', encoding='utf-8').write(str(len(s.encode())) + '\n' + '\n'.join(l for l in s.split('\n') if l.startswith('# ')))
def cmd_aggregate():
    s = read(P); cur = ''
    for l in s.split('\n'):
        if l.startswith('# '): cur = l[2:60]
        if re.match(r'^\| (T-\d+\.\d+\.\d+|EP-\d+\.\d+|NFR-\d+|SP-\d+|PM-\d+|KOSZT-\d+) ', l): print(cur, '::', l)
def cmd_alert(code):
    st = read(ALERT).split('\n') if os.path.exists(ALERT) else []
    if f'{code} {TODAY}' in st: print('SKIP: ten sam błąd zgłoszony dziś'); return
    open(ALERT, 'a', encoding='utf-8').write(f'{code} {TODAY}\n'); print('SEND')
def section(s, title):
    L = s.split('\n'); a = L.index(title); b = next((i for i in range(a+1, len(L)) if L[i].startswith('# ')), len(L))
    return L[a:b]
def cmd_digest():
    s = read(P); L = s.split('\n')
    logs = [l for l in L if re.match(r'^\| \d{4}-\d\d-\d\d \| ', l)]
    print('== Ostatnie wpisy dziennika =='); print(*logs[-6:], sep='\n')
    print('== Otwarte pytania =='); print(*[l for l in section(s, '# Otwarte pytania') if re.match(r'^\| Q\d+ ', l) and 'rozwiązane w it.' not in l], sep='\n')
    print('== Ostatnie korekty =='); print(*[l for l in section(s, '# Korekty') if l.startswith('- **K')][-12:], sep='\n')
    last = {}
    for l in section(s, '# Zależności — audyt'):
        if l.startswith('| ') and not l.startswith('| Nazwa') and not l.startswith('|---'):
            last[re.sub(r'\s*—\s*re-audyt.*|\s*\(.*', '', l.split('|')[1]).strip()] = l
    print('== Zależności (ostatni wiersz na pozycję) =='); print(*last.values(), sep='\n')
def cmd_ids(role):
    s = read(P)
    def mx(pat, fl=0):
        v = [int(x) for x in re.findall(pat, s, fl)]; return max(v) if v else '—'
    print(f'D{role}.x:', mx(rf'\bD{role}\.(\d+)\b'), '| Q:', mx(r'^\| Q(\d+) \|', re.M), '| SP:', mx(r'\bSP-(\d+)\b'),
          '| PM:', mx(r'\bPM-(\d+)\b'), '| NFR:', mx(r'\bNFR-(\d+)\b'), '| US:', mx(r'\bUS-(\d+)\b'), '| SC:', mx(r'\bSC-(\d+)\b'),
          '| AS:', mx(r'\bAS-(\d+)\b'), '| KOSZT:', mx(r'\bKOSZT-(\d+)\b'), f'| SEC-*:', mx(r'\bSEC-\d+\.(\d+)\b'), f'| EP-{role}.x:', mx(rf'\bEP-{role}\.(\d+)\b'))
    for k in KB[role]: print(f'KB-{k}xxx najwyższy:', mx(rf'\bKB-({k}\d{{3}})\b(?!\s*[–-])'))
def human_open(plan):
    out = []
    for l in plan.split('\n'):
        m = re.match(r'^\| Q(\d+) \| (.*?) \| (.*?) \|', l)
        if m and 'rozwiązane w it.' not in l and (int(m.group(1)) in HUMAN or 'decyzja człowieka' in m.group(3)): out.append((int(m.group(1)), m.group(2)))
    return out
def dec_line(q, text): return f'Q{q}: {re.sub(r"[`*]", "", text)[:200]} → DECYZJA: '
def cmd_decisions(init=False):
    p = DEC
    if not os.path.exists(p):
        if not init: print('BRAK decyzje.md — uruchom: decisions --init'); return
        hdr = ('# Decyzje właściciela — Kobian Backup\n\nWpisz odpowiedź po „DECYZJA:” w linii pytania i zapisz plik. '
               'Pętla odczyta ją przy następnym przebiegu. Słowo „odłóż” zostawia pytanie otwarte. Nie zmieniaj numerów Q.\n\n')
        open(p, 'w', encoding='utf-8').write(hdr + '\n'.join(dec_line(q, t) for q, t in human_open(read(P))) + '\n')
        print('UTWORZONO decyzje.md');
    elif init: print('decyzje.md już istnieje')
    s = read(p).lstrip('\ufeff').replace('\r', ''); plan = read(P)
    for l in s.split('\n'):
        m = re.match(r'^\s*[-*]?\s*Q(\d+)\b.*?DECYZJA\s*:\s*(.*)$', l)
        if not m: continue
        q, d = int(m.group(1)), m.group(2).strip()
        row = [x for x in plan.split('\n') if x.startswith(f'| Q{q} |')]
        st = 'NIE ISTNIEJE' if not row else ('JUŻ ROZWIĄZANE — WIĄŻĄCE' if 'rozwiązane w it.' in row[0] else 'OTWARTE')
        if d and not re.fullmatch(r'(?i)(odłóż|odloz|nie wiem|później|pozniej|-|—)', d): print(f'Q{q} [{st}] DECYZJA: {d}')
        elif d: print(f'Q{q} [{st}] odłożone przez właściciela — pozostaje otwarte')
def cmd_insert(N, dry):
    D = f'{H}/tmp/it{N}/'; r = lambda f: read(D + f).strip().replace('{DATA}', TODAY) if os.path.exists(D + f) else ''
    before = read(P); L = before.split('\n')
    if logged(before, N): sys.exit(f'DUBEL: dziennik ma już iterację {N}')
    st = f'## Status: iteracja {N} (faza II — rozbudowa, runda {math.ceil(N/10)})'
    if L.count(st) != 1: sys.exit('STATUS: brak linii: ' + st)
    if not r('main.md') or not r('log.txt'): sys.exit('WEJŚCIE: brak main.md lub log.txt')
    log = r('log.txt')
    if '\n' in log or not re.match(rf'^\| \d{{4}}-\d\d-\d\d \| {N} \| .+ \| .+ \|$', log): sys.exit('LOG: zły format')
    L[L.index(st)] = ('## Status: iteracja 50 — faza II ukończona (limit)' if N == 50
                      else f'## Status: iteracja {N+1} (faza II — rozbudowa, runda {math.ceil((N+1)/10)})')
    L.insert(max(i for i, l in enumerate(L) if re.match(r'^\| \d{4}-\d\d-\d\d \| ', l)) + 1, log)
    for line in filter(None, r('resolved.txt').splitlines()):
        q, ref = [x.strip() for x in line.split('|', 1)]
        idx = [i for i, l in enumerate(L) if l.startswith(f'| {q} |')]
        if len(idx) != 1 or 'rozwiązane w it.' in L[idx[0]]: sys.exit(f'RESOLVED: {q} nie istnieje albo już rozwiązane')
        if ('decyzja człowieka' in L[idx[0]] or int(q[1:]) in HUMAN) and 'decyzje.md' not in ref: sys.exit(f'RESOLVED: {q} wymaga decyzji człowieka (tylko przez decyzje.md)')
        L[idx[0]] = L[idx[0]][:-2] + f' [rozwiązane w it. {N} — {ref}] |'
    def after_last_row(h1, h2, rows):
        a, b = L.index(h1), L.index(h2); last = max(i for i in range(a, b) if L[i].startswith('|'))
        L[last+1:last+1] = rows.split('\n')
    if r('q.md'):
        qi = max(i for i, l in enumerate(L) if re.match(r'^\| Q\d+ \|', l)); L[qi+1:qi+1] = r('q.md').split('\n')
    if r('deps.md'): after_last_row('# Zależności — audyt', '# Otwarte pytania', r('deps.md'))
    if r('index.md'): after_last_row(IDX, '# Zależności — audyt', r('index.md'))
    for f, hdr, th in REG:
        if not r(f): continue
        if hdr not in L: k = L.index(IDX); L[k:k] = [hdr, '', *th.split('\n'), '', '---', '']
        after_last_row(hdr, IDX, r(f))
    s = '\n'.join(L)
    first = next(h for h in [REG[0][1], REG[1][1], IDX] if ('\n' + h + '\n') in s)
    pos = s.index('\n' + first + '\n') + 1
    s = s[:pos] + r('main.md') + '\n\n---\n\n' + s[pos:]
    if r('k.md'): s = s.rstrip('\n') + '\n' + r('k.md') + '\n'
    new = s.split('\n'); newset = set(new)
    removed = [l for l in before.split('\n') if l not in newset]
    okrem = all(l.startswith('## Status:') or (l.startswith('| Q') and any(x.startswith(l[:-2]) and f'rozwiązane w it. {N}' in x for x in new)) for l in removed)
    added = '\n'.join(r(f) for f in ['main.md', 'index.md', 'deps.md', 'q.md', 'k.md', 'risk.md', 'epics.md', 'log.txt'])
    sb, db = structure(s), drafts(added) + structure(r('main.md'))
    grow = len(s.encode()) - len(before.encode())
    if grow > MAX_ADD: db.append(f'przyrost {grow} B > limit {MAX_ADD} B — skróć treść')
    if '$(date' in added: db.append('dosłowny tekst $(date …) — użyj znacznika {DATA}')
    print('zmienione/usunięte linie:', len(removed)); [print('  <', l[:120]) for l in removed]
    print('struktura pliku:', sb or 'OK'); print('nowa treść:', db or 'OK'); print('przyrost bajtów:', grow, '| rozmiar:', len(s.encode()))
    if not okrem or sb or db: sys.exit('WERYFIKACJA NIEUDANA — nic nie zapisano; popraw pliki w ' + D)
    if dry: print('DRY-RUN OK — nic nie zapisano'); return
    if read(P) != before: sys.exit('PLIK ZMIENIONY W TRAKCIE — uruchom insert ponownie')
    open(prev_path(N), 'w', encoding='utf-8', newline='').write(before)
    open(P, 'w', encoding='utf-8', newline='').write(s)
    if read(P) != s: open(P, 'w', encoding='utf-8', newline='').write(before); sys.exit('ZAPIS NIEUDANY — przywrócono treść')
    remember(s); print('ZAPISANO.', *[l for l in new if l.startswith('## Status')])
    adds = [dec_line(int(m.group(1)), m.group(2)) for m in re.finditer(r'^\| Q(\d+) \| (.*?) \| (.*?) \|', r('q.md'), re.M) if 'decyzja człowieka' in m.group(3)]
    if adds and os.path.exists(DEC):
        open(DEC, 'a', encoding='utf-8').write('\n'.join(adds) + '\n'); print('dopisano do decyzje.md:', len(adds))
if __name__ == '__main__':
    a = sys.argv[1:] or ['check']
    {'check': cmd_check, 'map': lambda: [print(n, l) for n, l in enumerate(read(P).split('\n'), 1) if l.startswith('# ')],
     'digest': cmd_digest, 'aggregate': cmd_aggregate, 'ids': lambda: cmd_ids(int(a[1])), 'alert': lambda: cmd_alert(a[1]),
     'decisions': lambda: cmd_decisions('--init' in a), 'fix-status': cmd_fix_status,
     'insert': lambda: cmd_insert(int(a[1]), '--dry-run' in a)}[a[0]]()
