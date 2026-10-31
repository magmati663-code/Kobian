#!/bin/bash
# Scenariusz „wiele małych plików” (runda 5): N plików po 50–1200 B w 3000 katalogach; pełna kopia i dwa przyrosty bez zmian.
# Mierzy czas i szczyt pamięci Agenta — liczba wpisów drzew i bloków rośnie, a pamięć ma rosnąć liniowo i niewiele (limit procesu 500 MB).
#   tools/perf/many.sh <katalog-repo-po-budowie-Release> [liczba-plików=300000]
set -euo pipefail
W=${1:?katalog repozytorium po budowie Release}
N=${2:-300000}
B=${KOBIAN_MANY_DIR:-/tmp/kmany}
[ "$(id -u)" -eq 0 ] || { echo "Uruchom jako root." >&2; exit 1; }
rm -rf "${B:?}" /var/lib/kobian
mkdir -p "$B/src" "$B/repo" /var/lib/kobian /run/kobian
python3 - "$B/src" "$N" <<'PY'
import os, random, sys
root, n = sys.argv[1], int(sys.argv[2])
r = random.Random(5)
for i in range(n):
    d = os.path.join(root, f"d{i % 3000:04d}")
    if i < 3000: os.makedirs(d, exist_ok=True)
    p = os.path.join(d, f"f{i}.dat")
    with open(p, "wb") as f: f.write(r.randbytes(r.randint(50, 1200)))
    os.utime(p, (1.6e9, 1.6e9))   # stare znaczniki czasu — skrót „plik bez zmian” działa już w pierwszym przyroście
PY
echo "many-Haslo-12345" > "$B/pw"
KOBIAN_SERVICE=1 dotnet "$W/src/Kobian.Agent/bin/Release/net10.0/Kobian.Agent.dll" > "$B/agent.log" 2>&1 &
AP=$!
trap 'kill $AP 2>/dev/null || true' EXIT
for _ in $(seq 120); do [ -S /run/kobian/agent.sock ] && break; sleep 0.5; done
K="dotnet $W/src/Kobian.Cli/bin/Release/net10.0/kobian.dll"
$K init many --source "$B/src" --dest "$B/repo" --password-file "$B/pw" > /dev/null
( while kill -0 "$AP" 2>/dev/null; do ps -o rss= -p "$AP"; sleep 0.1; done | sort -n | tail -1 > "$B/peak" ) &
MON=$!
run() { local s e; s=$(date +%s.%N); $K job run many --wait > /dev/null; e=$(date +%s.%N); echo "$e - $s" | bc; }
full=$(run); inc1=$(run); inc2=$(run)
kill "$AP"; wait "$MON" 2>/dev/null || true
printf 'files=%s\nfull_s=%.2f\nincremental_unchanged_s=%.2f\nincremental_unchanged2_s=%.2f\npeak_rss_kb=%s\nrepo_mb=%s\n' \
    "$N" "$full" "$inc1" "$inc2" "$(cat "$B/peak")" "$(du -sm "$B/repo" | cut -f1)" | tee "$B/result.txt"
