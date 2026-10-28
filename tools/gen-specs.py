#!/usr/bin/env python3
"""BL-6 / D1.7: generuje specs/US-xx.feature (Gherkin) z tabel R2.1.1 i R2.1.2 planu. Tagi: @US-xx @AC-xx.n @<kat.>.
Uruchom w folderze projektu: python3 tools/gen-specs.py. Pliki generowane — nie edytuj ręcznie (zmiany w plan.md)."""
import os, re, collections
ROOT = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..')
plan = open(os.path.join(ROOT, 'plan.md'), encoding='utf-8').read().split('\n')
# Kanoniczna tabela zakresu D1.4 (R2.1.1) — ma pierwszeństwo nad 1.4 (K11.1)
d14 = next(i for i, l in enumerate(plan) if l.startswith('### Decyzja D1.4'))
us = {}
for l in plan[d14:]:
    m = re.match(r'^\| (US-\d+[ab]?) \| ([^|]*) \| ([^|]*) \| \**([A-Z0-9-]+)\** \|', l)
    if m and m.group(1) not in us:
        us[m.group(1)] = (m.group(3).strip(), m.group(4))
acs = collections.OrderedDict()
for l in plan:
    m = re.match(r'^\| (AC-(\d+)\.(\d+)) \| (.*)$', l)
    if m and m.group(1) not in acs:
        cells = [c.strip() for c in m.group(4).rstrip().rstrip('|').split('|')]
        if len(cells) >= 3:
            acs[m.group(1)] = ('US-%02d' % int(m.group(2)), cells[0], cells[1], cells[2])
by = collections.defaultdict(list)
for ac, (u, g, w, t) in acs.items(): by[u].append((ac, g, w, t))
os.makedirs(os.path.join(ROOT, 'specs'), exist_ok=True)
n = 0
def clean(s): return s.replace('`', '').replace('\n', ' ').strip()
for u, items in sorted(by.items()):
    key = u if u in us else (u + 'a' if u + 'a' in us else u)
    title, cat = us.get(key, ('(brak w tabeli zakresu)', '?'))
    tag = {'M': '@M', 'S-1.0': '@S10'}.get(cat, '@' + cat.replace('-', ''))
    lines = [f'# language: pl', f'# Generowane z plan.md (R2.1.1, R2.1.2) przez tools/gen-specs.py — nie edytuj ręcznie.',
             f'@{u} {tag}', f'Funkcja: {u} — {clean(title)}', '']
    for ac, g, w, t in items:
        lines += [f'  @{ac}', f'  Scenariusz: {ac}', f'    Zakładając {clean(g)}', f'    Gdy {clean(w)}', f'    Wtedy {clean(t)}', '']
    open(os.path.join(ROOT, 'specs', f'{u}.feature'), 'w', encoding='utf-8').write('\n'.join(lines))
    n += 1
print(f'{n} plików .feature, {len(acs)} scenariuszy')
