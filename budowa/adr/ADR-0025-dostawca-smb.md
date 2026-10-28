# ADR-0025 — Dostawca SMB: port konektora, wspólne połączenia, poświadczenia z katalogu sekretów (BL-73, PROWIZORYCZNIE Qb42)

Data: 2026-10-14 (it. 92, rola 2 prowadzi; 2,4,11). Status: prowizoryczne (odwracalne). Realizuje D4.2 (natywny klient, UNC, bez litery dysku), D4.1/D4.6 (role poświadczeń), D4.8 (mapa błędów SMB/Win32), AC-04.3, AC-01.2.

## Decyzja
- Port w `Kobian.Application.Storage` (`ISmbConnector`, `ISmbConnection.LocalRoot`, `ISmbCredentialProvider`, `SmbShare`, `SmbConnectException(win32)`), adapter `WNetSmbConnector` w `Kobian.Platform.Windows` (WNetAddConnection2W z `lpLocalName = NULL`, `CONNECT_TEMPORARY`; hasło w buforze zerowanym; rozłączenie bez wymuszania).
- `SmbStorageProviderFactory` = walidacja `UncPath` → poświadczenie roli → połączenie → sesja dostawcy lokalnego na ścieżce UNC (te same zasady zapisu/odczytu/usuwania), połączenie zwalniane z sesją; równoległość 4 (4.7).
- `SharedSmbConnector`: jedno połączenie na udział dla równoległych sesji (licznik odwołań), drugie konto do tego samego serwera → od razu 1219/KB-2101 bez wywołania systemu.
- Cel „smb” rejestrowany w Agencie tylko w kompilacji Windows (`KOBIAN_WINDOWS`); poza Windows schemat nieznany (KB-0103 w walidacji).
- Poświadczenia: ten sam plik JSON co SFTP (`file:<nazwa>`, Qb16/ADR-0007), wymagane hasło; docelowo DPAPI (W-21).
- Kody Win32 spoza tabeli D4.8 (2202, 1331, 1793, 2242, 1385, 1203, 1222, 1311) przypisane do najbliższego wiersza — do potwierdzenia w SP-22.
- Nowe kody: KB-2101 (1219), KB-2103 (konto zablokowane/wygasłe).

## Alternatywy
- SMBLibrary (klient w C#, działałby też w Linuksie) — odrzucone w D4.2 (LGPL, 1 opiekun).
- Połączenie per sesja bez współdzielenia — prostsze, ale pierwsza zamknięta sesja zrywałaby pozostałe (deviceless connection jest wspólne dla sesji logowania).
- Kolejkowanie drugiego konta zamiast odmowy — to zadanie kolejki uruchomień (D7.4, zasób `smb:<serwer>:<konto>`), nie dostawcy.

## Konsekwencje / koszt zmiany zdania
- Mały: konektor i dostawca poświadczeń to jedyne punkty wymiany. Do zrobienia (BL-73 cz. 2): definicja zadania z celem „smb” i `secretRef` (dziś parser przyjmuje tylko „local”), kreator/CLI, zasób kolejki D7.4, ponowienia przejściowych błędów połączenia i breaker (D4.16), wykrycie SMB1/braku podpisywania (KB-2107/2111 po SP-22).
