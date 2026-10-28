#!/usr/bin/env bash
# Uruchamiane w folderze projektu właściciela: weryfikuje skrót archiwum i rozpakowuje je (nadpisując pliki kodu).
# Użycie: tools/apply-sync.sh budowa/sync/kobian-sync-<h12>.tgz
set -euo pipefail
cd "$(dirname "$0")/.."
f="$1"
want=$(basename "$f" .tgz); want=${want#kobian-sync-}
got=$(sha256sum "$f" | cut -c1-12)
[[ "$got" == "$want" ]] || { echo "SKRÓT NIEZGODNY: $got != $want"; exit 1; }
tar --overwrite -xzf "$f"
for old in budowa/sync/kobian-sync-*.tgz; do [[ "$old" == "$f" ]] || rm -f "$old"; done
echo "ZASTOSOWANO $f"
