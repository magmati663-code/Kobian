# Ustawia status (ostatnia kolumna) wierszy backlogu: python3 tools/bl-status.py "BL-1=zrobione" "SP-05=w toku" ...
import sys, os
p = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'budowa', 'backlog.md')
L = open(p, encoding='utf-8').read().split('\n')
for arg in sys.argv[1:]:
    k, v = arg.split('=', 1); hit = 0
    for i, l in enumerate(L):
        if l.startswith('| ' + k + ' |'):
            c = l.rstrip().rstrip('|').split('|'); c[-1] = ' ' + v + ' '; L[i] = '|'.join(c) + '|'; hit += 1
    print(k, 'OK' if hit else 'BRAK')
open(p, 'w', encoding='utf-8').write('\n'.join(L))
