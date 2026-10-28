#!/usr/bin/env bash
# Pakuje źródła (bez bin/obj) do archiwum synchronizacji z folderem projektu właściciela.
# Wypisuje: <sha256> <ścieżka>. Nazwa zawiera skrót — unika nieaktualnych kopii przy przesyłaniu.
set -euo pipefail
cd "$(dirname "$0")/.."
dir="${1:-/mnt/user-data/outputs}"
tmp=$(mktemp)
tar --exclude='*/bin' --exclude='*/obj' --exclude='./.vs' -czf "$tmp" \
  Kobian.sln global.json NuGet.config Directory.Build.props Directory.Packages.props THIRD-PARTY-NOTICES.md \
  src tests docs tools $( [[ -d specs ]] && echo specs )
h=$(sha256sum "$tmp" | cut -c1-64)
out="$dir/kobian-sync-${h:0:12}.tgz"
mv "$tmp" "$out"; chmod 644 "$out"
echo "$h $out"
