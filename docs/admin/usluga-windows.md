# Agent Kobian jako usługa Windows (BL-72, it. 91)

> Stan: kod hosta usługi gotowy i testowany w Linuksie (ścieżki, pojedyncza instancja, plan ACL); zachowanie pod SCM, Event Log i ACL pipe — **wymaga Windows** (kolejka W-33/W-34). Produkcyjna instalacja = MSI (później); poniżej instalacja deweloperska.

## Profil usługi (D2.5, D2.12 — `AgentServiceProfile`)

| Ustawienie | Wartość |
|---|---|
| Nazwa / wyświetlana | `KobianAgent` / „Kobian Backup Agent” |
| Konto | wirtualne `NT SERVICE\KobianAgent` (bez Administrators/Backup Operators, nie LocalSystem) |
| Uruchamianie | Automatyczne (opóźnione) |
| Uprawnienia tokenu | `SeBackupPrivilege`, `SeRestorePrivilege`, `SeChangeNotifyPrivilege`, `SeShutdownPrivilege` (bez `SeImpersonatePrivilege`) |
| Odzyskiwanie | restart po 1 min, po 1 min, potem co 5 min; licznik zerowany po 1 dobie (NFR-09) |
| SID usługi | Unrestricted (SP-13 do potwierdzenia) |
| VSS | `HKLM\SYSTEM\CurrentControlSet\Services\VSS\VssAccessControl` → `NT SERVICE\KobianAgent` = 2 |

## Ścieżki (PROWIZORYCZNIE Qb41)

| Tryb | `catalog.db` | Stan | Sekrety |
|---|---|---|---|
| Usługa (SCM) | `KOBIAN_CATALOG` albo domyślnie `%ProgramData%\Kobian\catalog.db` | `KOBIAN_STATE_DIR` albo katalog `catalog.db` | `KOBIAN_SECRETS_DIR` albo `<stan>\secrets` |
| Konsola (dev) | `KOBIAN_CATALOG` wymagane (względne = względem katalogu bieżącego) | jw. | jw. |

W trybie usługi każda ścieżka musi być bezwzględna. Zawsze odrzucane: ścieżki UNC i urządzeń (`\\serwer\…`, `\\?\…`) oraz znaki sterujące — katalog SQLite na udziale sieciowym grozi uszkodzeniem. Błąd konfiguracji = kod wyjścia **64** (wpis Event Log, jeśli źródło `KobianAgent` jest zarejestrowane).

## Pojedyncza instancja (D2.15 pkt 3, AC-37.3)

Przed otwarciem katalogu Agent zajmuje plik `catalog.db.lock` (wyłącznie; zawiera PID, nie jest usuwany) i — na Windows — mutex `Global\Kobian.Agent.Instance`. Druga instancja kończy się kodem wyjścia **75** i **KB-7007**, bez zmiany `catalog.db`.

## Pipe API w trybie usługi (D2.9, D2.15 pkt 1 — `AgentPipeAcl`)

`CurrentUserOnly = false` z własnym ACL: **Deny** `NT AUTHORITY\NETWORK`; konto usługi — odczyt/zapis + tworzenie instancji; `SYSTEM`, `Administrators`, `Kobian Operators` i `Kobian Admins` (jeśli grupy istnieją; `Kobian Admins` — PROWIZORYCZNIE Qb45) — wyłącznie odczyt/zapis. Pozostali użytkownicy — brak dostępu. Tryb konsoli pozostaje przy `CurrentUserOnly = true` (stała rola Operator).

### Role wywołującego (BL-17, D2.9)

Usługa odczytuje SID i grupy klienta z tokenu połączenia (`RunAsClient`, poziom Identification — klient nie daje usłudze prawa działania w swoim imieniu). Wynik jest zapamiętywany na czas połączenia.

| Token klienta | Rola |
|---|---|
| `SYSTEM`, Administratorzy **z podniesieniem UAC**, `Kobian Admins` | Administrator |
| `Kobian Operators` (także administrator bez podniesienia, jeśli jest w grupie) | Operator |
| inny dopuszczony przez ACL | Odczyt |
| token anonimowy, logowanie sieciowe (`NETWORK`), brak tokenu lub błąd odczytu | odmowa `KB-7001` |

Administrator bez podniesienia UAC ma grupę Administratorzy tylko „do odmowy” — dla zmian wymagających roli Administrator uruchom konsolę „jako administrator”. Dziennik audytu zapisuje SID wywołującego (kolumna `actor_sid`). Weryfikacja na Windows: W-17, W-37.

## Instalacja deweloperska (Administrator, PowerShell)

Argumenty `sc.exe` generuje `AgentServiceProfile.ScCommands("<ścieżka>\Kobian.Agent.exe")` (lista argumentów, bez powłoki). Ręcznie:

```
sc.exe create KobianAgent binPath= "C:\Program Files\Kobian\Kobian.Agent.exe" start= delayed-auto obj= "NT SERVICE\KobianAgent" DisplayName= "Kobian Backup Agent"
sc.exe privs KobianAgent SeBackupPrivilege/SeRestorePrivilege/SeChangeNotifyPrivilege/SeShutdownPrivilege
sc.exe sidtype KobianAgent unrestricted
sc.exe failure KobianAgent reset= 86400 actions= restart/60000/restart/60000/restart/300000
sc.exe failureflag KobianAgent 1
New-EventLog -LogName Application -Source KobianAgent
```

Katalog `%ProgramData%\Kobian` z ACL: SYSTEM, Administrators, `NT SERVICE\KobianAgent` — pełne (R2.2.1). Uprawnienia LSA i wpis VSS — MSI (później) lub ręcznie (W-33).

## Instalacja MSI i konfiguracja systemu (it. 100–103 — szkielet, PROWIZORYCZNIE Qb50/Qb53)

Instalator MSI (`installer/Package.wxs`, budowa na Windows po decyzji Q37 — W-42) instaluje usługę z profilem jak wyżej (natywne tabele MSI zamiast `sc.exe`) i po jej utworzeniu uruchamia **`Kobian.Agent.exe --provision`** jako LocalSystem. Ten tryb nie startuje usługi i nie dotyka katalogu; wykonuje kolejno (pierwszy błąd przerywa instalację, wynik w logu MSI: `ok <krok>` / `failed <krok> (win32 <kod>)`):

| Krok | Co robi |
|---|---|
| `group-operators`, `group-admins` | tworzy grupy lokalne „Kobian Operators” i „Kobian Admins” (istniejące zostają bez zmian członków) |
| `data-dir`, `data-secrets`, `data-keys`, `data-certs`, `data-staging` | `%ProgramData%\Kobian` i podkatalogi: właściciel Administrators, ACL **bez dziedziczenia** — SYSTEM, Administrators, `NT SERVICE\KobianAgent` pełny dostęp; grupy Kobian i użytkownicy — brak (dostęp tylko przez API); katalog będący dowiązaniem → odmowa |
| `vss-access` | `HKLM\SYSTEM\CurrentControlSet\Services\VSS\VssAccessControl\NT SERVICE\KobianAgent = 2` |
| `lsa-rights` | prawa SeBackupPrivilege i SeRestorePrivilege dla konta usługi |

Odinstalowanie uruchamia `--unprovision` (usuwa wpis VSS i prawa LSA, błędy nie blokują odinstalowania). **Dane (`%ProgramData%\Kobian`: katalog, klucze, sekrety) i grupy zostają** — usuń je ręcznie, jeśli naprawdę chcesz (repozytoria kopii na celach nie są dotykane).

Folder docelowy odtwarzania (od it. 101): Operator odtwarza tylko do folderu, w którym jego konto ma prawo dodawania plików i folderów (AccessCheck tokenem klienta) — inaczej KB-5001; Administrator bez tej kontroli.

Proces pomocniczy `Kobian.Worker.exe --role vss` (D2.8) — requester migawek VSS w osobnym procesie; do zakończenia spike'a SP-01 zwraca KB-1406 (migawka niedostępna). Zablokowanie startu przez WDAC/AppLocker/ASR będzie zgłaszane jako KB-7008.
