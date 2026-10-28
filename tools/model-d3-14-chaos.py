#!/usr/bin/env python3
"""Model protokołu commit-po-weryfikacji (D3.14) pod wstrzykiwaniem awarii — harness R&D dla SP-18 / NFR-26 (RD-19).
UWAGA: to MODEL protokołu w Pythonie, nie testowanie kodu C# ani prawdziwego celu. Nie zamyka SP-18 ani NFR-26;
sprawdza, czy sama kolejność operacji D3.14 (I-1..I-3, manifest ostatni) jest odporna, i czy harness wykrywa błąd klasy Borg
(kontrola negatywna). Dane syntetyczne, wszystko w pamięci. Uruchom: python3 -I tools/model-d3-14-chaos.py [--seed N]"""
import hashlib, random, sys, json

class Kill(Exception): pass            # zabicie procesu w punkcie potoku
class RepoWrite(Exception): pass       # ENOSPC / zerwane łącze po wyczerpaniu ponowień

def H(b): return hashlib.sha256(b).hexdigest()

class Store:
    """Cel: słownik ścieżka->bajty z pojemnością i wstrzykiwaniem awarii. Stan przeżywa zabicie procesu."""
    def __init__(self, cap, rename_atomic=True):
        self.f, self.cap, self.rename_atomic = {}, cap, rename_atomic
        self.fault = None   # ('enospc'|'drop', nr operacji zapisu)
        self.wr = 0
    def used(self): return sum(len(v) for v in self.f.values())
    def write(self, path, data):
        self.wr += 1
        if self.fault and self.fault[1] == self.wr:
            if self.fault[0] == 'drop':          # zerwanie w trakcie: zapisana połowa
                self.f[path] = data[:len(data) // 2]
            else:                                  # ENOSPC: część mieści się
                self.f[path] = data[:max(0, len(data) // 3)]
            raise RepoWrite(self.fault[0])
        if self.used() - len(self.f.get(path, b'')) + len(data) > self.cap:
            self.f[path] = data[:max(0, self.cap - self.used())]
            raise RepoWrite('enospc')
        self.f[path] = data
    def rename(self, a, b):
        if self.rename_atomic: self.f[b] = self.f.pop(a)
        else: self.f[b] = self.f[a]; self.f.pop(a)   # model DirectFinal obsługuje piszący (patrz put)
    def read(self, p): return self.f.get(p)

def put(st, path, data, staged):
    """Zapis obiektu: StagedRename (.part -> rename) lub DirectFinal (zapis wprost)."""
    if staged:
        st.write(path + '.part', data); st.rename(path + '.part', path)
    else:
        st.write(path, data)

def make_chunks(rng, n): return [(H(rng.randbytes(32)), rng.randbytes(rng.randint(200, 900))) for _ in range(n)]

def pack_bytes(chunks): return json.dumps([[c, d.hex()] for c, d in chunks]).encode()
def idx_bytes(entries): return json.dumps(entries).encode()
def snap_bytes(chunk_ids):
    body = json.dumps(chunk_ids).encode(); return json.dumps({'body': body.hex(), 'sha': H(body)}).encode()

class Ctx:
    def __init__(self, kills_at): self.pt, self.kills_at = 0, kills_at
    def point(self):
        self.pt += 1
        if self.kills_at == self.pt: raise Kill()

def backup(st, chunks, reused, ctx, protocol, staged, catalog):
    """chunks: nowe bloki; reused: chunkId z poprzedniego snapshotu. protocol: 'd314' albo 'borg' (kontrola negatywna)."""
    repo_write_errors, packs = 0, []
    for i in range(0, len(chunks), 4):
        grp = chunks[i:i + 4]; pid = H(pack_bytes(grp))[:16]; data = pack_bytes(grp)
        ctx.point()
        try:
            put(st, f'data/{pid}', data, staged)
            ctx.point()
            if protocol == 'd314':               # I-2: V1 = rozmiar i SHA po stronie celu
                got = st.read(f'data/{pid}')
                if got is None or len(got) != len(data) or H(got) != H(data): raise RepoWrite('v1')
            packs.append((pid, [c for c, _ in grp]))
        except RepoWrite:
            repo_write_errors += 1
            if protocol == 'd314': return 'Failed'     # klasa Repo-Write: bez snapshotu (I-1)
            # 'borg': błąd traktowany jak ostrzeżenie pliku — kontynuacja
    ctx.point()
    entries = {c: pid for pid, cs in packs for c in cs}
    if protocol == 'd314':
        # I-3 na danych dokładnych: każdy chunkId drzewa jest w nowym indeksie lub w indeksach zatwierdzonych
        known = {c for ix in catalog['indexes'] for c in ix}
        for c in [c for c, _ in chunks] + reused:
            if c not in entries and c not in known: return 'Failed'   # KB-1403
    iid = H(idx_bytes(entries))[:16]
    try:
        put(st, f'index/{iid}.idx', idx_bytes(entries), staged)
        ctx.point()
        if protocol == 'd314' and H(st.read(f'index/{iid}.idx') or b'') != H(idx_bytes(entries)): return 'Failed'
        ids = [c for c, _ in chunks] + reused
        sid = H(snap_bytes(ids))[:16]
        ctx.point()
        put(st, f'snapshots/{sid}.snap', snap_bytes(ids), staged)
    except RepoWrite:
        return 'Failed' if protocol == 'd314' else 'Succeeded'
    ctx.point()
    catalog['indexes'].append(list(entries)); catalog['snaps'].append(sid)       # transakcja katalogu — ostatnia
    return 'Succeeded'

def verify(st):
    """Niezależny weryfikator: nie używa kodu zapisu. Zwraca (dangling_snapshots, uszkodzone_obiekty_snapshotu)."""
    chunk_ok = set()
    for p, v in st.f.items():
        if p.startswith('data/') and not p.endswith('.part'):
            try: chunk_ok |= {c for c, d in json.loads(v) if H(bytes.fromhex(d)) is not None}
            except Exception: pass                    # obcięty pack nie dostarcza bloków
    idx = set()
    for p, v in st.f.items():
        if p.startswith('index/') and p.endswith('.idx'):
            try: idx |= set(json.loads(v))
            except Exception: pass
    dangling = bad = 0
    for p, v in st.f.items():
        if p.startswith('snapshots/') and p.endswith('.snap'):
            try:
                o = json.loads(v); body = bytes.fromhex(o['body'])
                if H(body) != o['sha']: raise ValueError
                ids = json.loads(body)
            except Exception: bad += 1; continue        # czytnik odrzuci (suma/AEAD) — nie jest snapshotem
            if any(c not in chunk_ok or c not in idx for c in ids): dangling += 1
    return dangling, bad

def run_case(seed, protocol, staged, kind, arg):
    rng = random.Random(seed)
    st = Store(cap=10**9, rename_atomic=True); catalog = {'indexes': [], 'snaps': []}
    base = make_chunks(rng, 12)
    # uruchomienie 0 (bez awarii): baza dla dedup
    backup(st, base, [], Ctx(None), 'd314', True, catalog)
    new = make_chunks(rng, 16); reused = [c for c, _ in base[:6]]
    if kind == 'kill': ctx = Ctx(arg)
    else: ctx = Ctx(None)
    if kind == 'enospc': st.cap = st.used() + arg          # arg = bajty zapasu
    if kind == 'drop': st.fault = ('drop', st.wr + arg)
    try: res = backup(st, new, reused, ctx, protocol, staged, catalog)
    except Kill: res = 'Killed'
    d, bad = verify(st)
    return res, d, bad

def main():
    seed0 = int(sys.argv[sys.argv.index('--seed') + 1]) if '--seed' in sys.argv else 1
    out = []
    for protocol in ('d314', 'borg'):
        for staged in (True, False):
            tot = {'runs': 0, 'dangling': 0, 'bad_snap_obj': 0, 'succeeded_but_dangling': 0}
            s = seed0
            cases = [('kill', p) for p in range(1, 21) for _ in range(10)] \
                  + [('enospc', rng_arg) for rng_arg in [random.Random(seed0 + i).randint(500, 9000) for i in range(50)]] \
                  + [('drop', random.Random(seed0 + 100 + i).randint(1, 9)) for i in range(50)]
            for kind, arg in cases:
                s += 1; res, d, bad = run_case(s, protocol, staged, kind, arg)
                tot['runs'] += 1; tot['dangling'] += d; tot['bad_snap_obj'] += bad
                if res == 'Succeeded' and d: tot['succeeded_but_dangling'] += 1
            out.append((protocol, 'StagedRename' if staged else 'DirectFinal', tot))
    print('| protokół | tryb zapisu | uruchomień | snapshoty z brakującym blokiem | Succeeded mimo braków | uszkodzone obiekty snapshotu (odrzucane przez czytnik) |')
    print('|---|---|---|---|---|---|')
    for p, m, t in out:
        print(f"| {p} | {m} | {t['runs']} | {t['dangling']} | {t['succeeded_but_dangling']} | {t['bad_snap_obj']} |")
    ok = all(t['dangling'] == 0 for p, m, t in out if p == 'd314') and any(t['dangling'] > 0 for p, m, t in out if p == 'borg')
    print('\nWYNIK MODELU:', 'OK (d314: 0 dangling; kontrola negatywna borg wykryta)' if ok else 'NIEPOWODZENIE')
    sys.exit(0 if ok else 1)
main()
