#!/bin/bash
# CI: nieudane testy i wskazane pliki dziennika jako adnotacje GitHub (::error) — czytelne w podsumowaniu przebiegu i przez API, bez pobierania pełnych logów.
#   tools/ci-annotate.sh testy <plik-z-wyjściem-xunit>
#   tools/ci-annotate.sh plik <tytuł> <plik> [liczba-ostatnich-wierszy]
set -uo pipefail
esc() { sed -e 's/\x1b\[[0-9;]*m//g' -e 's/%/%25/g' | awk 'BEGIN{ORS="%0A"} {print}' | sed -e 's/\r/%0D/g'; }
case "${1:-}" in
testy)
    [ -f "$2" ] || exit 0
    # blok = wiersz „[FAIL]” + do 30 kolejnych wierszy (komunikat, oczekiwane/otrzymane, początek stosu); najwyżej 10 adnotacji
    awk '/\[FAIL\]/{n++; if(n>10) exit; blk=1; cnt=0; print "@@@"} blk{print; cnt++; if(cnt>30) blk=0}' "$2" | sed -e 's/\x1b\[[0-9;]*m//g' |
        awk 'BEGIN{RS="@@@\n"} NF{gsub(/%/,"%25"); gsub(/\r/,""); gsub(/\n/,"%0A"); split($0,a,"%0A"); t=substr(a[1],1,200); sub(/^ +/,"",t); gsub(/:/,"%3A",t); gsub(/,/,"%2C",t); print "::error title=" t "::" $0}'
    ;;
plik)
    [ -f "$3" ] || { echo "::error title=$2::(brak pliku $3)"; exit 0; }
    echo "::error title=${2//:/%3A}::$(tail -n "${4:-60}" "$3" | cut -c1-400 | esc)"
    ;;
*) echo "użycie: $0 testy <plik> | plik <tytuł> <plik> [n]" >&2; exit 64 ;;
esac
