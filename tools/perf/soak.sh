#!/bin/bash
# Test długotrwały (runda 19): Agent jako usługa, wiele kopii z losowymi zmianami danych, losowe zabicie procesu w trakcie kopii (kill -9),
# okresowe odtwarzanie z porównaniem bajt w bajt (diff -r) i kontrola repozytorium (check), pomiar pamięci Agenta po każdej iteracji.
#   tools/perf/soak.sh <katalog-repo-po-budowie-Release> [iteracje=40] [ziarno=1]
# Kończy się błędem, gdy: kopia/odtwarzanie/check zawiedzie, odtworzone dane różnią się od źródła, albo pamięć rośnie (> 60 MB wzrostu szczytu między początkiem a końcem).
set -uo pipefail
W=${1:?katalog repozytorium po budowie Release}
N=${2:-40}
SEED=${3:-1}
B=${KOBIAN_SOAK_DIR:-/tmp/ksoak}
[ "$(id -u)" -eq 0 ] || { echo "Uruchom jako root." >&2; exit 1; }
rm -rf "${B:?}" /var/lib/kobian
mkdir -p "$B/src" "$B/repo" "$B/restore" /var/lib/kobian /run/kobian
echo "soak-Haslo-12345" > "$B/pw"
AGENT="$W/src/Kobian.Agent/bin/Release/net10.0/Kobian.Agent.dll"
K="dotnet $W/src/Kobian.Cli/bin/Release/net10.0/kobian.dll"
RANDOM=$SEED
AP=0
fail=0
start_agent() {
  KOBIAN_SERVICE=1 KOBIAN_CONTENT_CHECK_DAYS=1 dotnet "$AGENT" >> "$B/agent.log" 2>&1 &
  AP=$!
  # gotowość = usługa odpowiada (kod wyjścia 1 = stan „ostrzeżenie”, np. przerwana kopia; brak usługi = KB-7010 w wyniku)
  for _ in $(seq 120); do [ -S /run/kobian/agent.sock ] && [[ "$($K status 2>&1)" != *KB-7010* ]] && return 0; sleep 0.5; done
  echo "Agent nie wystartował"; return 1
}
stop_agent() { kill "$AP" 2>/dev/null; wait "$AP" 2>/dev/null; }
trap 'kill $AP 2>/dev/null || true' EXIT

mutate() { python3 - "$B/src" "$SEED" "$1" <<'PY'
import os, random, sys, time
root, seed, it = sys.argv[1], int(sys.argv[2]), int(sys.argv[3])
r = random.Random(seed * 1000 + it)
def files():
    out = []
    for d, _, fs in os.walk(root):
        for f in fs: out.append(os.path.join(d, f))
    return sorted(out)
old = time.time() - 40 * 86400
if it == 1:
    for i in range(30):
        d = os.path.join(root, f"d{i % 5}", f"s{i % 3}"); os.makedirs(d, exist_ok=True)
        p = os.path.join(d, f"plik{i}.bin")
        open(p, "wb").write(r.randbytes(r.choice([0, 1, 100, 5000, 300000, 2_000_000, 5_000_000])))
        os.utime(p, (old, old))
else:
    for _ in range(r.randint(1, 6)):
        fs = files(); act = r.choice(["add", "add", "modify", "modify", "delete", "text", "rename"])
        if act == "add" or not fs:
            d = os.path.join(root, f"d{r.randint(0, 6)}", f"s{r.randint(0, 3)}"); os.makedirs(d, exist_ok=True)
            p = os.path.join(d, f"n{it}_{r.randint(0, 9999)}.bin"); open(p, "wb").write(r.randbytes(r.choice([1, 777, 40000, 1_000_000, 4_300_000, 9_000_000])))
            if r.random() < 0.5: os.utime(p, (old, old))
        elif act == "modify":
            p = r.choice(fs); size = os.path.getsize(p); data = bytearray(open(p, "rb").read())
            if data: data[r.randrange(len(data))] ^= 0xFF
            data += r.randbytes(r.choice([0, 10, 5000]))
            open(p, "wb").write(bytes(data))
            if r.random() < 0.5: os.utime(p, (old, old))   # mtime „stary”, ale rozmiar lub treść inne albo mtime inny niż przy poprzedniej kopii
        elif act == "delete": os.remove(r.choice(fs))
        elif act == "text":
            p = os.path.join(root, f"tekst{r.randint(0, 5)}.txt"); open(p, "a").write(("Zażółć gęślą jaźń " * r.randint(1, 2000)) + "\n")
        else:
            p = r.choice(fs); os.rename(p, p + ".r")
PY
}

start_agent || exit 1
$K init soak --source "$B/src" --dest "$B/repo" --password-file "$B/pw" > /dev/null || { echo "init nie powiódł się"; exit 1; }
rss=()
kills=0
for it in $(seq 1 "$N"); do
  mutate "$it"
  if [ $((RANDOM % 5)) -eq 0 ] && [ "$it" -gt 2 ]; then
    # zabicie procesu Agenta w losowym momencie kopii
    $K job run soak --wait > /dev/null 2>&1 &
    CLI=$!
    sleep "0.$((RANDOM % 9 + 1))"
    kill -9 "$AP" 2>/dev/null; wait "$AP" 2>/dev/null; wait "$CLI" 2>/dev/null
    kills=$((kills + 1))
    start_agent || { echo "po zabiciu Agent nie wystartował"; exit 1; }
    echo "iteracja $it: zabito Agenta w trakcie kopii (#$kills)"
  fi
  if ! $K job run soak --wait > "$B/run.out" 2>&1; then echo "iteracja $it: KOPIA NIE POWIODŁA SIĘ"; cat "$B/run.out"; fail=1; break; fi
  rss+=("$(ps -o rss= -p "$AP")")
  if [ $((it % 4)) -eq 0 ] || [ "$it" -eq "$N" ]; then
    rm -rf "${B:?}/restore"; mkdir -p "$B/restore"
    if ! $K restore --snapshot latest --job soak --to "$B/restore" > "$B/restore.out" 2>&1; then echo "iteracja $it: ODTWARZANIE NIE POWIODŁO SIĘ"; cat "$B/restore.out"; fail=1; break; fi
    # odtworzone drzewo leży pod nazwą ostatniego segmentu źródła
    if ! diff -r "$B/src" "$B/restore/src" > "$B/diff.out" 2>&1 && ! diff -r "$B/src" "$B/restore" > "$B/diff.out" 2>&1; then echo "iteracja $it: RÓŻNICE źródło ↔ odtworzenie"; head -20 "$B/diff.out"; fail=1; break; fi
  fi
  if [ $((it % 10)) -eq 0 ]; then
    if ! $K check soak --sample 100 > "$B/check.out" 2>&1; then echo "iteracja $it: CHECK NIE POWIÓDŁ SIĘ"; cat "$B/check.out"; fail=1; break; fi
  fi
done
stop_agent
if [ "$fail" -eq 0 ]; then
  first=$(printf '%s\n' "${rss[@]:0:5}" | sort -n | tail -1)
  last=$(printf '%s\n' "${rss[@]: -5}" | sort -n | tail -1)
  echo "pamięć Agenta (szczyt z 5 pierwszych → 5 ostatnich iteracji): ${first} KB → ${last} KB"
  if [ $((last - first)) -gt 61440 ]; then echo "WZROST PAMIĘCI > 60 MB"; fail=1; fi
fi
echo "iteracje=$N zabicia=$kills wynik=$([ $fail -eq 0 ] && echo OK || echo BŁĄD)"
exit $fail
