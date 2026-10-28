#!/usr/bin/env python3
"""
Raport pokrycia „AC → test” (D1.7, BL-6). Czyta plan.md (R2.1.1 tabela zakresu, R2.1.2 AC) i testy (atrybuty
[Trait("AC","AC-xx.n")]). Wynik: docs/ac-coverage.md. Uruchamiany w folderze projektu (python3 tools/ac-coverage.py).
AC historii z kategorią M = Must (DoD: Must bez testu = nieukończone).
"""
import os, re, sys, glob, collections
ROOT = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..')
plan_path = os.path.join(ROOT, 'plan.md')
if not os.path.exists(plan_path):
    sys.exit('brak plan.md (uruchom w folderze projektu)')
plan = open(plan_path, encoding='utf-8').read().split('\n')

# Kanoniczna tabela zakresu D1.4 (R2.1.1) — ma pierwszeństwo nad 1.4 (K11.1)
d14 = next(i for i, l in enumerate(plan) if l.startswith('### Decyzja D1.4'))
us_cat = {}
for l in plan[d14:]:
    m = re.match(r'^\| (US-\d+[ab]?) \| [^|]* \| [^|]* \| \**([A-Z0-9-]+)\** \|', l)
    if m and m.group(1) not in us_cat:
        us_cat[m.group(1)] = m.group(2)

acs = collections.OrderedDict()
for l in plan:
    m = re.match(r'^\| (AC-(\d+)\.(\d+)) \|', l)
    if m and m.group(1) not in acs:
        acs[m.group(1)] = 'US-%02d' % int(m.group(2))

tested = collections.defaultdict(set)
for f in glob.glob(os.path.join(ROOT, 'tests', '**', '*.cs'), recursive=True):
    txt = open(f, encoding='utf-8').read()
    for m in re.finditer(r'Trait\("AC",\s*"(AC-[\d.]+)"\)', txt):
        tested[m.group(1)].add(os.path.relpath(f, ROOT).replace('\\', '/'))

def cat(us):
    for k in (us, us + 'a'):
        if k in us_cat: return us_cat[k]
    return '?'

rows, must_total, must_cov = [], 0, 0
for ac, us in acs.items():
    c = cat(us)
    cov = bool(tested.get(ac))
    if c == 'M':
        must_total += 1
        must_cov += cov
    rows.append(f'| {ac} | {us} | {c} | {"tak" if cov else "—"} | {", ".join(sorted(tested.get(ac, []))) or ""} |')

out = ['# Pokrycie kryteriów akceptacji testami (generowane: tools/ac-coverage.py)', '',
       f'AC Must: {must_cov}/{must_total} z co najmniej jednym testem oznaczonym `Trait("AC", …)`. '
       'Uwaga: test oznaczony ≠ AC spełnione — wiele AC wymaga E2E/Windows (kolejka budowa/do-uruchomienia-na-windows.md).', '',
       '| AC | US | Kat. | Test | Pliki |', '|---|---|---|---|---|'] + rows
open(os.path.join(ROOT, 'docs', 'ac-coverage.md'), 'w', encoding='utf-8').write('\n'.join(out) + '\n')
print(f'AC: {len(acs)}; Must z testem: {must_cov}/{must_total}')
