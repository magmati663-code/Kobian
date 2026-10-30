#!/bin/bash
# CI: nieudane testy i wskazane pliki dziennika jako adnotacje GitHub (::error) — czytelne w podsumowaniu przebiegu i przez API, bez pobierania pełnych logów.
#   tools/ci-annotate.sh testy <plik-z-wyjściem-xunit>
#   tools/ci-annotate.sh plik <tytuł> <plik> [liczba-ostatnich-wierszy]
set -uo pipefail
esc() { sed -e $'s/\x1b\[[0-9;]*m//g' -e $'s/\r//g' -e 's/%/%25/g' | awk 'BEGIN{ORS="%0A"} {print}'; }
case "${1:-}" in
testy)
    [ -f "$2" ] || exit 0
    # blok = wiersz „[FAIL]” + 30 kolejnych (komunikat, oczekiwane/otrzymane, początek stosu); najwyżej 10 adnotacji. Bez rozszerzeń GNU (macOS: BSD awk/sed).
    clean=$(mktemp); sed -e $'s/\x1b\[[0-9;]*m//g' "$2" > "$clean"
    grep -n '\[FAIL\]' "$clean" | head -10 | cut -d: -f1 | while read -r n; do
        title=$(sed -n "${n}p" "$clean" | sed -e 's/^ *//' | cut -c1-200 | sed -e 's/%/%25/g' -e 's/:/%3A/g' -e 's/,/%2C/g')
        echo "::error title=${title}::$(sed -n "${n},$((n + 30))p" "$clean" | cut -c1-400 | esc)"
    done
    rm -f "$clean"
    ;;
plik)
    [ -f "$3" ] || { echo "::error title=$2::(brak pliku $3)"; exit 0; }
    echo "::error title=${2//:/%3A}::$(tail -n "${4:-60}" "$3" | cut -c1-400 | esc)"
    ;;
*) echo "użycie: $0 testy <plik> | plik <tytuł> <plik> [n]" >&2; exit 64 ;;
esac
