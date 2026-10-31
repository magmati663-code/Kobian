#!/bin/sh
# Kobian Backup — instalacja na Linux (systemd) i macOS (launchd). Uruchom jako root z rozpakowanej paczki:  sudo ./install.sh
# Aktualizacja: ten sam skrypt (zatrzymuje usługę, podmienia program, dane w /var/lib/kobian albo /Library/Application Support/Kobian zostają).
set -eu
HERE=$(cd "$(dirname "$0")" && pwd)
[ "$(id -u)" -eq 0 ] || { echo "Uruchom jako root: sudo $0" >&2; exit 1; }

case "$(uname -s)" in
Linux)
    command -v systemctl >/dev/null 2>&1 || { echo "Wymagany systemd (systemctl)." >&2; exit 1; }
    [ -x "$HERE/kobian/Kobian.Agent" ] || { echo "Brak plików programu w $HERE/kobian — rozpakuj całą paczkę." >&2; exit 1; }
    systemctl stop kobian-agent 2>/dev/null || true
    rm -rf /opt/kobian.new
    cp -a "$HERE/kobian" /opt/kobian.new
    chown -R root:root /opt/kobian.new
    chmod -R go-w /opt/kobian.new
    if [ -d /opt/kobian ]; then rm -rf /opt/kobian.old; mv /opt/kobian /opt/kobian.old; fi
    mv /opt/kobian.new /opt/kobian
    rm -rf /opt/kobian.old
    cp "$HERE/README.txt" /opt/kobian/README.txt
    for g in kobian-admins kobian-operators; do getent group "$g" >/dev/null || groupadd --system "$g"; done
    mkdir -p /usr/local/bin
    ln -sf /opt/kobian/kobian /usr/local/bin/kobian
    ln -sf /opt/kobian/kobian-restore /usr/local/bin/kobian-restore
    ln -sf /opt/kobian/Kobian.UI /usr/local/bin/kobian-ui
    install -m 0644 "$HERE/kobian-agent.service" /etc/systemd/system/kobian-agent.service
    if [ -d /usr/share/applications ]; then install -m 0644 "$HERE/kobian.desktop" /usr/share/applications/kobian.desktop; fi
    install -d -m 0700 /var/lib/kobian /var/lib/kobian/secrets
    systemctl daemon-reload
    systemctl enable --now kobian-agent
    sleep 2
    systemctl --no-pager --lines=0 status kobian-agent || true
    ;;
Darwin)
    [ -d "$HERE/Kobian.app" ] || { echo "Brak Kobian.app w $HERE — rozpakuj całą paczkę." >&2; exit 1; }
    launchctl bootout system/pl.kobian.agent 2>/dev/null || true
    rm -rf /Applications/Kobian.app.new
    cp -R "$HERE/Kobian.app" /Applications/Kobian.app.new
    chown -R root:wheel /Applications/Kobian.app.new
    chmod -R go-w /Applications/Kobian.app.new
    # Paczka z Linuksa nie jest podpisana: kwarantanna Gatekeepera zdjęta, podpis ad-hoc (wymagany dla arm64). Własny podpis Developer ID — przy dystrybucji.
    xattr -dr com.apple.quarantine /Applications/Kobian.app.new 2>/dev/null || true
    codesign --force --deep --sign - /Applications/Kobian.app.new
    rm -rf /Applications/Kobian.app.old
    if [ -d /Applications/Kobian.app ]; then mv /Applications/Kobian.app /Applications/Kobian.app.old; fi
    mv /Applications/Kobian.app.new /Applications/Kobian.app
    rm -rf /Applications/Kobian.app.old
    for g in kobian-admins kobian-operators; do dscl . -read "/Groups/$g" >/dev/null 2>&1 || dseditgroup -o create -r "Kobian Backup ($g)" "$g"; done
    mkdir -p /usr/local/bin
    ln -sf /Applications/Kobian.app/Contents/MacOS/kobian /usr/local/bin/kobian
    ln -sf /Applications/Kobian.app/Contents/MacOS/kobian-restore /usr/local/bin/kobian-restore
    install -d -m 0700 "/Library/Application Support/Kobian" "/Library/Application Support/Kobian/secrets"
    install -d -m 0755 /Library/Logs/Kobian
    install -m 0644 -o root -g wheel "$HERE/pl.kobian.agent.plist" /Library/LaunchDaemons/pl.kobian.agent.plist
    launchctl bootstrap system /Library/LaunchDaemons/pl.kobian.agent.plist
    sleep 2
    launchctl print system/pl.kobian.agent 2>/dev/null | grep -E "state =|pid =" || true
    ;;
*)
    echo "Nieobsługiwany system: $(uname -s)" >&2; exit 1 ;;
esac

echo
echo "Kobian zainstalowany. Uprawnienia użytkowników (wyloguj się i zaloguj ponownie po zmianie grup):"
if [ "$(uname -s)" = Darwin ]; then
    echo "  sudo dseditgroup -o edit -a <użytkownik> -t user kobian-operators   # kopie i odtwarzanie"
    echo "  sudo dseditgroup -o edit -a <użytkownik> -t user kobian-admins      # pełna administracja"
else
    echo "  sudo usermod -aG kobian-operators <użytkownik>   # kopie i odtwarzanie"
    echo "  sudo usermod -aG kobian-admins <użytkownik>      # pełna administracja"
fi
echo "Sprawdzenie: kobian status"
