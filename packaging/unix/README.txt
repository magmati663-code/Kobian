Kobian Backup — Linux i macOS
=============================

Instalacja (jako root, z rozpakowanej paczki):
    sudo ./install.sh

Co instaluje:
  Linux:  program w /opt/kobian, usługa systemd „kobian-agent”, polecenia /usr/local/bin/kobian, kobian-restore, kobian-ui,
          dane w /var/lib/kobian (katalog, klucze, sekrety — tylko root), gniazdo /run/kobian/agent.sock.
  macOS:  /Applications/Kobian.app (aplikacja + Agent + narzędzia), demon launchd „pl.kobian.agent”, polecenia w /usr/local/bin,
          dane w /Library/Application Support/Kobian, dziennik /Library/Logs/Kobian/agent.log, gniazdo /var/run/kobian/agent.sock.

Role (rozpoznawane po koncie, które łączy się z gniazdem — podaje je jądro systemu, nie program klienta):
  root, grupa kobian-admins      → Administrator (wszystko, w tym eksport/import zadań, zatwierdzanie odcisków serwerów)
  grupa kobian-operators         → Operator (zadania, kopie, odtwarzanie do folderów, w których sam może zapisywać)
  pozostali lokalni użytkownicy  → Odczyt (stan, historia)
  UWAGA: Operator może skopiować i odtworzyć dowolny folder systemu (Agent działa jako root) — nadawaj tę grupę jak „Backup Operators” w Windows.

Dodanie użytkownika do grupy (potem wyloguj się i zaloguj):
  Linux:  sudo usermod -aG kobian-operators jan
  macOS:  sudo dseditgroup -o edit -a jan -t user kobian-operators

Poświadczenia celów sieciowych (SFTP, FTPS, WebDAV, SMB): pliki JSON w katalogu sekretów (Linux: /var/lib/kobian/secrets, macOS:
/Library/Application Support/Kobian/secrets), właściciel root, prawa 0600 — zob. dokumentacja celów (docs/admin/cel-*.md).

Najprościej: aplikacja „Kobian” (kreator) — foldery wybierasz oknem, login i hasło do serwera wpisujesz w kreatorze (zapisze je usługa),
serwer SFTP/FTPS/WebDAV możesz dodać także jako ŹRÓDŁO („Dodaj folder z serwera…”).

Hasło do serwera z wiersza poleceń (bez ręcznego pisania plików):
  kobian credential set nas-kopie --user kopie --password-file ~/haslo-nas.txt     # → file:nas-kopie

Kilka źródeł, także z serwera:
  kobian init Biuro --source /home/jan/Dokumenty --source sftp://nas.firma.pl/dane --source-secret-ref file:nas-kopie \
      --source-host-key SHA256:<odcisk> --dest /mnt/usb/kopie --password-file ~/haslo.txt

Pierwsza kopia z wiersza poleceń:
  echo 'moje-dlugie-haslo-repo' > ~/haslo.txt && chmod 600 ~/haslo.txt
  kobian init Dom --source /home/jan/Dokumenty --dest /mnt/usb/kopie --password-file ~/haslo.txt
  kobian job run Dom --wait
  kobian restore --snapshot latest --job Dom --to /home/jan/odtworzone

Linux/macOS w tej wersji:
  - kopiowane: pliki, katalogi, dowiązania symboliczne (jako dowiązania, bez podążania), uprawnienia rwx, czasy modyfikacji;
  - pomijane z ostrzeżeniem: FIFO, gniazda, urządzenia;
  - NIE kopiowane: właściciel/grupa plików, ACL, atrybuty rozszerzone (xattr), flagi plików macOS; odtworzone pliki dostają właściciela folderu docelowego;
  - setuid/setgid nie są odtwarzane (bezpieczeństwo);
  - brak migawki spójności (VSS jest tylko w Windows) — pliki zmieniane w trakcie kopii są oznaczane ostrzeżeniem;
  - cel SMB tylko w Windows (w Linux/macOS zamontuj udział i użyj celu lokalnego albo SFTP/WebDAV);
  - kopie baz SQL Server: Agent musi widzieć katalog roboczy (KOBIAN_DB_STAGING) — zob. docs/admin/kopia-bazy-sql.md.

macOS: paczka budowana poza macOS jest podpisywana ad-hoc przy instalacji (codesign -s -). Dla dystrybucji poza własnymi komputerami
podpisz ją certyfikatem Developer ID i poddaj notaryzacji.

Odinstalowanie:  sudo ./uninstall.sh          (dane zostają)
                 sudo ./uninstall.sh --purge  (usuwa też katalog, klucze i sekrety Agenta; kopii na celach nie rusza)
