#!/usr/bin/env bash
# Lokalny potok „PR” (BL-8, D10.5/D10.6 w wersji bez zewnętrznej platformy — S4=C).
# Użycie: tools/ci.sh [--locked]   (wymaga dotnet 10.0.401 w PATH lub w DOTNET_ROOT)
# Etapy: restore → build -warnaserror → testy (bez Category=Windows/Perf) → e2e → weryfikatory DDL (Python) → audyt zależności.
# Na końcu tabela czasów etapów (NFR-20: pomiar WYŁĄCZNIE informacyjny — środowisko nie jest referencyjne)
# oraz kod wyjścia 0 tylko, gdy wszystkie etapy przeszły (audyt: podatny pakiet = porażka).
set -uo pipefail
cd "$(dirname "$0")/.."
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 TESTINGPLATFORM_TELEMETRY_OPTOUT=1 AVALONIA_TELEMETRY_OPTOUT=1
if [[ "${1:-}" == "--locked" ]]; then LOCK=(-p:ContinuousIntegrationBuild=true); else LOCK=(); fi
fail=0; declare -a STAGES=()
stage() { # stage <nazwa> <polecenie...> — mierzy czas, zapamiętuje wynik, nie przerywa potoku
  local name="$1"; shift; local t0=$SECONDS rc=0
  echo "== $name =="; "$@" || rc=$?
  STAGES+=("$name|$((SECONDS - t0))|$([[ $rc -eq 0 ]] && echo OK || echo "BŁĄD($rc)")")
  [[ $rc -eq 0 ]] || fail=1; return 0
}
run_tests() {
  local rc=0
  for t in tests/Kobian.Tests.*/; do
    local name; name=$(basename "$t"); [[ "$name" == *.Support ]] && continue
    local dll="$t/bin/Release/net10.0/$name.dll"
    [[ -f "$dll" ]] || { echo "brak $dll"; rc=1; continue; }
    # testy z cechą Category=Windows są pomijane w Linuksie (kolejka: budowa/do-uruchomienia-na-windows.md)
    dotnet "$dll" -notrait "Category=Windows" -notrait "Category=Perf" || rc=1
  done
  return $rc
}
verify_ddl() {
  local rc=0
  for s in tools/verify-sched-ddl.py tools/verify-restore-ddl.py tools/verify-prune-ddl.py tools/verify-hold-ddl.py tools/verify-audit-ddl.py tools/verify-ident-ddl.py tools/verify-alert-ddl.py; do python3 -I "$s" || rc=1; done
  return $rc
}
ac_report() { # raport AC→test (BL-8, it. 40): odświeża docs/ac-coverage.md; Must bez testu jest informacją, nie porażką
  python3 -I tools/ac-coverage.py
}
run_e2e() { # etap E2E (it. 42): backup → zmiany → przyrost → restore obu generacji; tylko testy Category=E2E (musi być ≥ 1)
  local out; out=$(dotnet tests/Kobian.Tests.Unit/bin/Release/net10.0/Kobian.Tests.Unit.dll -trait "Category=E2E" 2>&1); local rc=$?
  echo "$out" | tail -3
  echo "$out" | grep -qE "Total: [1-9]" || { echo "E2E: brak testów Category=E2E"; return 1; }
  return $rc
}
audit() {
  local out; out=$(dotnet list Kobian.sln package --vulnerable --include-transitive 2>&1)
  echo "$out" | grep -E "has the following vulnerable|no vulnerable" || true
  ! echo "$out" | grep -q "has the following vulnerable"
}
stage restore dotnet restore Kobian.sln "${LOCK[@]}"
stage build   dotnet build Kobian.sln --no-restore -warnaserror -c Release "${LOCK[@]}"
stage testy   run_tests
stage e2e     run_e2e
stage ddl     verify_ddl
stage ac-raport ac_report
stage audyt   audit
echo; echo "| Etap | Czas [s] | Wynik |"; echo "|---|---|---|"
for s in "${STAGES[@]}"; do IFS='|' read -r n t r <<<"$s"; echo "| $n | $t | $r |"; done
echo "(czasy: środowisko budowy, nie sprzęt referencyjny — NFR-20 pozostaje niezmierzony)"
exit $fail
