#!/bin/bash
# Bramka regresji wydajności: porównuje wynik tools/perf/perf.sh z progami. Progi = pomiar ×1,3–2 (runnery CI są wolniejsze i zmienne).
#   tools/perf/check.sh <result.txt>
# Zmiana progu wymaga wpisu „przed/po” w docs/wyniki-optymalizacji.md.
set -uo pipefail
f=${1:?plik result.txt z perf.sh}
declare -A v
while IFS== read -r k val; do [ -n "$k" ] && v[$k]=$val; done < "$f"
rc=0
# klucz maks [komentarz]
gate() { local k=$1 max=$2 val=${v[$1]:-}; if [ -z "$val" ]; then echo "::error title=perf::brak $k w $f"; rc=1; return; fi
  if awk -v a="$val" -v b="$max" 'BEGIN{exit !(a+0 > b+0)}'; then echo "::error title=perf $k::$k=$val przekracza próg $max"; rc=1; else echo "ok  $k=$val (próg $max)"; fi; }
gate peak_rss_kb 420000        # NFR-05: ≤ 500 MB; pomiar lokalny ~324 MB
gate after_anon_kb 70000       # pamięć prywatna po kopii; pomiar ~28 MB (po MemoryTrim Aggressive)
gate repo_mb "${KOBIAN_PERF_REPO_MAX_MB:-400}"   # rozmiar repozytorium (dedup + kompresja) dla zbioru CI
gate full_s "${KOBIAN_PERF_FULL_MAX_S:-30}"      # pełna kopia zbioru CI
gate incremental_unchanged_s "${KOBIAN_PERF_INC_MAX_S:-6}"
exit $rc
