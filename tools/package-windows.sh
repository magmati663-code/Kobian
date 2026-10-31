#!/bin/bash
# Paczka Kobian dla Windows x64 (zip, samodzielna) — budowana na Linuksie lub Windows (Git Bash). Wynik: dist/kobian-<wersja>-win-x64.zip (+ .sha256).
# Instalacja: packaging/windows/install.ps1 (usługa KobianAgent). Do czasu MSI.
set -euo pipefail
ROOT=$(cd "$(dirname "$0")/.." && pwd)
VERSION=${KOBIAN_VERSION:-1.0.0}
WIN=net10.0-windows10.0.17763.0
name="kobian-$VERSION-win-x64"; STAGE="$ROOT/dist/stage-win"; OUT="$STAGE/$name/Kobian"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 AVALONIA_TELEMETRY_OPTOUT=1
rm -rf "$STAGE"; mkdir -p "$OUT"
publish() { dotnet publish "$ROOT/$1" -f "$2" -c Release -r win-x64 --self-contained -p:ContinuousIntegrationBuild=true \
    -p:DebugType=None -p:DebugSymbols=false -p:Version="$VERSION" -o "$OUT" --nologo -v quiet; }
publish src/Kobian.Agent/Kobian.Agent.csproj "$WIN"
publish src/Kobian.Cli/Kobian.Cli.csproj net10.0
publish src/Kobian.UI/Kobian.UI.csproj net10.0
publish src/Kobian.Restore.Standalone/Kobian.Restore.Standalone.csproj "$WIN"
publish src/Kobian.Worker/Kobian.Worker.csproj "$WIN"
for exe in Kobian.Agent.exe kobian.exe Kobian.UI.exe kobian-restore.exe Kobian.Worker.exe; do [ -f "$OUT/$exe" ] || { echo "Brak $exe" >&2; exit 1; }; done
# Windows PowerShell 5.1 czyta skrypt bez BOM jako ANSI (polskie znaki i cudzysłowy „” psują się) — w paczce zawsze UTF-8 z BOM.
{ printf '\xEF\xBB\xBF'; sed '1s/^\xEF\xBB\xBF//' "$ROOT/packaging/windows/install.ps1"; } > "$STAGE/$name/install.ps1"
cp "$ROOT/packaging/windows/README.txt" "$STAGE/$name/"
(cd "$STAGE" && rm -f "$ROOT/dist/$name.zip" && zip -qr9 "$ROOT/dist/$name.zip" "$name")
(cd "$ROOT/dist" && sha256sum "$name.zip" > "$name.zip.sha256")
rm -rf "$STAGE"
echo "   $(du -h "$ROOT/dist/$name.zip" | cut -f1)  $ROOT/dist/$name.zip"
