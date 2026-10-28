#!/usr/bin/env python3
"""Model filtra Bloom z D3.15 (10 bitów/wpis, k=7) — harness SP-19 (część: FP Bloom ≤ 1,5%) — rola 13.
UWAGA: to POMIAR MODELU w Pythonie (SHA-256 + podwójne haszowanie), nie kodu C# ani pamięci procesu (Private Bytes
niezmierzone). Dane syntetyczne deterministyczne, wszystko w pamięci. Uruchom: python3 -I tools/model-d3-15-bloom.py [N ...]"""
import hashlib, math, sys, time

def probes(cid, m, k):
    h = hashlib.sha256(cid).digest()
    a = int.from_bytes(h[:8], 'little'); b = int.from_bytes(h[8:16], 'little') | 1   # Kirsch–Mitzenmacher
    return [(a + i * b) % m for i in range(k)]

def run(n, bits_per=10, k=7, probes_n=None):
    m = n * bits_per; bf = bytearray((m + 7) // 8); probes_n = probes_n or n
    for i in range(n):
        for p in probes(b'in%d' % i, m, k): bf[p >> 3] |= 1 << (p & 7)
    fp = sum(all(bf[p >> 3] >> (p & 7) & 1 for p in probes(b'out%d' % i, m, k)) for i in range(probes_n))
    theory = (1 - math.exp(-k * n / m)) ** k
    return fp / probes_n, theory, len(bf)

if __name__ == '__main__':
    for n in [int(x) for x in sys.argv[1:]] or [100_000, 1_000_000]:
        t = time.time(); fp, th, size = run(n)
        print(f'n={n:>9} bity/wpis=10 k=7 filtr={size/1e6:7.2f} MB  FP zmierzone={fp*100:.3f}%  teoria={th*100:.3f}%  próg=1,5%  {"OK" if fp <= 0.015 else "PRZEKROCZONY"}  ({time.time()-t:.0f}s)')
