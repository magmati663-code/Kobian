#!/bin/bash
# Progi dla tools/perf/many.sh (100 tys. plików w CI).
set -uo pipefail
f=${1:?plik result z many.sh}
declare -A v
while IFS== read -r k val; do [ -n "$k" ] && v[$k]=$val; done < "$f"
rc=0
gate() { local k=$1 max=$2 val=${v[$1]:-}; if [ -z "$val" ]; then echo "::error title=many::brak $k"; rc=1; return; fi
  if awk -v a="$val" -v b="$max" 'BEGIN{exit !(a+0 > b+0)}'; then echo "::error title=many $k::$k=$val przekracza próg $max"; rc=1; else echo "ok  $k=$val (próg $max)"; fi; }
gate peak_rss_kb 420000
gate full_s "${KOBIAN_PERF_FULL_MAX_S:-120}"
gate incremental_unchanged_s "${KOBIAN_PERF_INC_MAX_S:-40}"
gate incremental_unchanged2_s "${KOBIAN_PERF_INC_MAX_S:-40}"
exit $rc
