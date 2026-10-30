#!/bin/bash
# Pomiar wydajności kopii (Linux, Agent jako usługa na gnieździe systemowym): czas, szczyt pamięci Agenta, rozmiar repozytorium.
#   tools/perf/perf.sh <katalog-repo-po-budowie-Release> [MB-losowych]
# Dane deterministyczne (ziarno), ~1,2 GB: tekst (kompresuje się), losowe (nie), duplikaty, 20 000 małych plików.
# Wymaga: python3, bc, dotnet. Uruchamia własnego Agenta (KOBIAN_SERVICE=1) w /var/lib/kobian — tylko na maszynie testowej / w CI (root).
# Wynik: wiersze „klucz=wartość” (czytelne dla progów w CI) + tabela.
set -euo pipefail
W=${1:?podaj katalog repozytorium po budowie Release}
B=${KOBIAN_PERF_DIR:-/tmp/kperf}
RND=${2:-600}
[ "$(id -u)" -eq 0 ] || { echo "Uruchom jako root (gniazdo /run/kobian, dane /var/lib/kobian)." >&2; exit 1; }
rm -rf "${B:?}" /var/lib/kobian
mkdir -p "$B/src/txt" "$B/src/bin" "$B/src/small" "$B/repo" /var/lib/kobian /run/kobian
python3 - "$B/src" "$RND" <<'PY'
import os, random, sys
b, rnd_mb = sys.argv[1], int(sys.argv[2])
random.seed(1)
words = [''.join(random.choice('abcdefghij') for _ in range(random.randint(3, 9))) for _ in range(5000)]
for i in range(20):
    with open(f'{b}/txt/t{i}.txt', 'w') as f:
        for _ in range(100000):
            f.write(' '.join(random.choices(words, k=10)) + '\n')
n = max(1, rnd_mb // 100)
for i in range(n):
    with open(f'{b}/bin/r{i}.bin', 'wb') as f:
        r = random.Random(100 + i)
        for _ in range(100):
            f.write(r.randbytes(1 << 20))
first = open(f'{b}/bin/r0.bin', 'rb').read()
for i in range(3):
    open(f'{b}/bin/dup{i}.bin', 'wb').write(first)
r = random.Random(7)
for i in range(20000):
    open(f'{b}/small/s{i}.dat', 'wb').write(r.randbytes(r.randint(100, 8000)))
PY
echo "dane: $(du -sm "$B/src" | cut -f1) MB"
echo "pwtest-Haslo-12345" > "$B/pw"

AGENT="$W/src/Kobian.Agent/bin/Release/net10.0/Kobian.Agent.dll"
K="dotnet $W/src/Kobian.Cli/bin/Release/net10.0/kobian.dll"
KOBIAN_SERVICE=1 dotnet "$AGENT" > "$B/agent.log" 2>&1 &
AP=$!
trap 'kill $AP 2>/dev/null || true' EXIT
for _ in $(seq 120); do [ -S /run/kobian/agent.sock ] && break; sleep 0.5; done
idle=$(ps -o rss= -p "$AP")
$K init perf --source "$B/src" --dest "$B/repo" --password-file "$B/pw" > /dev/null
( while kill -0 "$AP" 2>/dev/null; do ps -o rss= -p "$AP"; sleep 0.2; done | sort -n | tail -1 > "$B/peak" ) &
MON=$!
run() { local s e; s=$(date +%s.%N); $K job run perf --wait > /dev/null; e=$(date +%s.%N); echo "$e - $s" | bc; }
full=$(run)
repo_mb=$(du -sm "$B/repo" | cut -f1)
inc1=$(run)
inc2=$(run)
sleep 3
after=$(ps -o rss= -p "$AP")
kill "$AP"; wait "$MON" 2>/dev/null || true
peak=$(cat "$B/peak")
printf 'idle_rss_kb=%s\nfull_s=%.2f\nrepo_mb=%s\nincremental_unchanged_s=%.2f\nincremental_unchanged2_s=%.2f\npeak_rss_kb=%s\nafter_rss_kb=%s\n' \
    "$idle" "$full" "$repo_mb" "$inc1" "$inc2" "$peak" "$after" | tee "$B/result.txt"
