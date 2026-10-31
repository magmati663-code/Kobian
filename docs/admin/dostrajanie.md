# Dostrajanie wydajności i niezawodności

Ustawienia usługi Agenta zmieniasz zmiennymi środowiskowymi usługi. Domyślne wartości są dobre dla większości komputerów — zmieniaj tylko z powodu.

| Zmienna | Domyślnie | Działanie |
|---|---|---|
| `KOBIAN_PRIORITY` | `belownormal` | Priorytet procesu usługi: `normal`, `belownormal`, `idle`. Kopia ustępuje programom interaktywnym; przy wolnym komputerze nic się nie zmienia. |
| `KOBIAN_MAX_UPLOAD_KBPS` | brak limitu | Limit wysyłki w kB/s dla wszystkich zadań łącznie (np. `1024` ≈ 1 MB/s). Limit dotyczy wysyłania danych na cel, nie odczytu z dysku. |
| `KOBIAN_CONTENT_CHECK_DAYS` | `7` | Kopia przyrostowa pomija czytanie plików, które mają ten sam rozmiar i czas modyfikacji co w poprzedniej kopii. Co tyle dni czyta całą treść (wykrywa zmianę, która zachowała rozmiar i czas). `0` = zawsze czytaj wszystko. Pliki zmienione w ostatnich 2 sekundach przed poprzednią kopią są zawsze czytane. Nie dotyczy serwerów zdalnych jako źródeł ani baz danych. |
| `KOBIAN_STALL_MINUTES` | `30` | Uruchomienie bez żadnego postępu przez tyle minut jest przerywane (KB-4106) i ponawiane zgodnie z harmonogramem. `0` = bez nadzoru. Długa kopia, która postępuje, nigdy nie jest przerywana. |
| `KOBIAN_BLOCK_PARALLELISM` | rdzenie − 1 (1–3) | Ile bloków dużego pliku przygotowuje się równolegle (kompresja, skróty). `1` = sekwencyjnie. Każdy blok w locie to bufor 4 MiB. |
| `KOBIAN_PROFILE` | wyłączone | `1` = w dzienniku usługi czasy faz kopii (odczyt, skróty, kompresja, szyfrowanie, oczekiwanie na wysyłkę). |
| `KOBIAN_MEM_LOG` | wyłączone | `1` = w dzienniku usługi pamięć przed i po sprzątaniu po kopii. |

## Jak ustawić

- **Linux (systemd):** `sudo systemctl edit kobian-agent`, dodaj `[Service]` i `Environment=KOBIAN_MAX_UPLOAD_KBPS=2048`, potem `sudo systemctl restart kobian-agent`.
- **macOS (launchd):** w `/Library/LaunchDaemons/pl.kobian.agent.plist` dodaj klucz w `EnvironmentVariables`, potem `sudo launchctl bootout system/pl.kobian.agent` i `sudo launchctl bootstrap system /Library/LaunchDaemons/pl.kobian.agent.plist`.
- **Windows:** zmienna systemowa przez `setx /M KOBIAN_MAX_UPLOAD_KBPS 2048`, potem `Restart-Service KobianAgent`.

## Co usługa robi sama

- **Pamięć:** usługa używa ok. 330 MB szczytowo przy dużej kopii i wraca do ok. 30 MB prywatnej pamięci po jej zakończeniu.
- **Zerwane połączenie w trakcie wysyłki** (SFTP, FTP(S), WebDAV, SMB): wysyłka paczki jest ponawiana na nowym połączeniu (do 4 prób, po 2, 6 i 18 s). Błędy trwałe (odmowa dostępu, zła tożsamość serwera, niezgodność sumy kontrolnej) i pełny dysk nie są ponawiane. Cele bez operacji zmiany nazwy po zerwaniu zostawiają częściowy obiekt — tam przebieg kończy się błędem i jest ponawiany przez harmonogram.
- **Uszkodzony katalog usługi** (`catalog.db`): raz na dobę powstaje kopia `catalog.db.auto.bak`. Jeśli plik katalogu jest uszkodzony, usługa przy starcie zachowuje go jako `catalog.db.corrupt-<czas>` (do analizy) i startuje z kopii. Dane kopii zapasowych są w repozytorium — tracisz tylko wpisy katalogu z ostatniej doby, a kolejna kopia planuje się od ostatniej znanej migawki.
- **Pomiar:** `tools/perf/perf.sh` (czasy, pamięć, rozmiar repozytorium) i `tools/perf/soak.sh` (test długotrwały z zabijaniem procesu); wyniki i metodologia: `docs/wyniki-optymalizacji.md`.
