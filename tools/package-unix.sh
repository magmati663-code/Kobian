#!/bin/bash
# Paczki Kobian dla Linux i macOS (samodzielne — bez instalowania .NET na docelowym komputerze).
#   tools/package-unix.sh                       # wszystkie: linux-x64 linux-arm64 osx-x64 osx-arm64
#   tools/package-unix.sh linux-x64 osx-arm64   # wybrane
# Wynik: dist/kobian-<wersja>-<rid>.tar.gz (+ .sha256). Wymaga .NET SDK 10 i dostępu do NuGet (pakiety środowiska uruchomieniowego dla RID).
set -euo pipefail
ROOT=$(cd "$(dirname "$0")/.." && pwd)
VERSION=${KOBIAN_VERSION:-$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$ROOT/Directory.Build.props" | head -1)}
VERSION=${VERSION:-1.0.0}
RIDS=("$@"); [ ${#RIDS[@]} -eq 0 ] && RIDS=(linux-x64 linux-arm64 osx-x64 osx-arm64)
DIST="$ROOT/dist"; STAGE="$DIST/stage"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 AVALONIA_TELEMETRY_OPTOUT=1
mkdir -p "$DIST"

publish() { # projekt framework rid wyjście
    dotnet publish "$ROOT/$1" -f "$2" -c Release -r "$3" --self-contained -p:ContinuousIntegrationBuild=true \
        -p:DebugType=None -p:DebugSymbols=false -p:Version="$VERSION" -o "$4" --nologo -v quiet
}

for rid in "${RIDS[@]}"; do
    case "$rid" in linux-x64|linux-arm64|osx-x64|osx-arm64) ;; *) echo "Nieznany RID: $rid" >&2; exit 64 ;; esac
    name="kobian-$VERSION-$rid"; pkg="$STAGE/$name"; rm -rf "$pkg"; mkdir -p "$pkg"
    if [[ $rid == osx-* ]]; then bin="$pkg/Kobian.app/Contents/MacOS"; else bin="$pkg/kobian"; fi
    echo "== $rid → $bin"
    # Wszystkie programy w jednym folderze: wspólne środowisko .NET i biblioteki (jedna wersja każdego pakietu — centralne zarządzanie wersjami).
    publish src/Kobian.Agent/Kobian.Agent.csproj net10.0 "$rid" "$bin"
    publish src/Kobian.Cli/Kobian.Cli.csproj net10.0 "$rid" "$bin"
    publish src/Kobian.UI/Kobian.UI.csproj net10.0 "$rid" "$bin"
    publish src/Kobian.Restore.Standalone/Kobian.Restore.Standalone.csproj net10.0 "$rid" "$bin"
    for exe in Kobian.Agent kobian Kobian.UI kobian-restore; do [ -f "$bin/$exe" ] || { echo "Brak $exe po publikacji ($rid)" >&2; exit 1; }; chmod 0755 "$bin/$exe"; done
    cp "$ROOT/packaging/unix/install.sh" "$ROOT/packaging/unix/uninstall.sh" "$ROOT/packaging/unix/README.txt" "$pkg/"
    if [[ $rid == osx-* ]]; then
        mkdir -p "$pkg/Kobian.app/Contents/Resources"
        sed "s/@VERSION@/$VERSION/g" "$ROOT/packaging/unix/Info.plist" > "$pkg/Kobian.app/Contents/Info.plist"
        cp "$ROOT/packaging/unix/pl.kobian.agent.plist" "$pkg/"
    else
        cp "$ROOT/packaging/unix/kobian-agent.service" "$ROOT/packaging/unix/kobian.desktop" "$pkg/"
    fi
    tar -C "$STAGE" --owner=0 --group=0 -czf "$DIST/$name.tar.gz" "$name"
    (cd "$DIST" && sha256sum "$name.tar.gz" > "$name.tar.gz.sha256")
    echo "   $(du -h "$DIST/$name.tar.gz" | cut -f1)  $DIST/$name.tar.gz"
done
rm -rf "$STAGE"
