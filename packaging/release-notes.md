Pierwsze wydanie przedpremierowe Kobian Backup — Windows, Linux i macOS. Zbudowane automatycznie przez GitHub Actions z oznaczonego commitu.

## Paczki
| Plik | System | Instalacja |
|---|---|---|
| `kobian-<wersja>-win-x64.zip` | Windows 10/11, Server 2019+ (x64) | rozpakuj, PowerShell jako administrator: `.\install.ps1` |
| `kobian-<wersja>-linux-x64.tar.gz` | Linux x64 z systemd | `tar xzf …; sudo ./install.sh` |
| `kobian-<wersja>-linux-arm64.tar.gz` | Linux arm64 (np. Raspberry Pi 4/5 z 64-bit OS) | jw. |
| `kobian-<wersja>-osx-arm64.tar.gz` | macOS 12+ Apple Silicon | `tar xzf …; sudo ./install.sh` |
| `kobian-<wersja>-osx-x64.tar.gz` | macOS 12+ Intel | jw. |

Paczki są samodzielne (zawierają środowisko .NET 10). Sumy SHA-256 w plikach `.sha256`.

## Co jest w środku
- Agent (usługa), wiersz poleceń `kobian`, aplikacja okienkowa, narzędzie awaryjne `kobian-restore`.
- Cele kopii: dysk lokalny/USB, SMB (Windows), SFTP, FTPS, **WebDAV (HTTPS)**.
- Kopie baz SQL Server (COPY_ONLY + CHECKSUM), odtwarzanie do nowej bazy.
- Linux/macOS: usługa systemd/launchd, role z grup `kobian-admins` / `kobian-operators`, zachowanie uprawnień plików.

## Stan testów — przeczytaj przed instalacją
| Platforma | Stan |
|---|---|
| Linux x64 | ✅ instalacja, usługa (uruchomiona jak jednostka systemd), role, kopia/odtwarzanie, aplikacja okienkowa |
| Linux arm64 | ✅ pod emulatorem QEMU: Agent, CLI, kopia/odtwarzanie |
| SQL Server 2022 (Linux) | ✅ kopia, sprawdzenie, odtworzenie, odtworzenie po utracie bazy |
| WebDAV | ✅ na serwerze WsgiDAV z HTTPS |
| prawdziwy systemd | ⚠️ niezweryfikowany |
| **Windows** | ⚠️ **zbudowane na Linuksie, nie uruchamiane na Windows** (usługa, VSS, ACL, SMB) |
| **macOS** | ⚠️ **nie uruchamiane** (brak macOS w środowisku budowy); podpis ad-hoc przy instalacji |

To wersja do testów — nie zastępuj nią jeszcze jedynej kopii ważnych danych.
