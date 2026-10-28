#!/bin/sh
# Kobian Backup — odinstalowanie (Linux/macOS). Dane (katalog, klucze, sekrety) zostają, chyba że podasz --purge.
# Kopie na celach (dyski, serwery) NIE są usuwane w żadnym trybie.
set -eu
[ "$(id -u)" -eq 0 ] || { echo "Uruchom jako root: sudo $0 [--purge]" >&2; exit 1; }
PURGE=0; [ "${1:-}" = "--purge" ] && PURGE=1
case "$(uname -s)" in
Linux)
    systemctl disable --now kobian-agent 2>/dev/null || true
    rm -f /etc/systemd/system/kobian-agent.service /usr/share/applications/kobian.desktop
    systemctl daemon-reload || true
    rm -f /usr/local/bin/kobian /usr/local/bin/kobian-restore /usr/local/bin/kobian-ui
    rm -rf /opt/kobian
    if [ $PURGE -eq 1 ]; then rm -rf /var/lib/kobian; fi
    ;;
Darwin)
    launchctl bootout system/pl.kobian.agent 2>/dev/null || true
    rm -f /Library/LaunchDaemons/pl.kobian.agent.plist /usr/local/bin/kobian /usr/local/bin/kobian-restore
    rm -rf /Applications/Kobian.app
    if [ $PURGE -eq 1 ]; then rm -rf "/Library/Application Support/Kobian" /Library/Logs/Kobian; fi
    ;;
esac
echo "Kobian odinstalowany.$([ $PURGE -eq 1 ] && echo ' Dane usunięte.' || echo ' Dane zostały (użyj --purge, aby je usunąć).')"
echo "Grupy kobian-admins i kobian-operators pozostawiono (usuń ręcznie, jeśli niepotrzebne)."
