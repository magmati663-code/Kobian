# ADR-0024 — Host usługi Windows Agenta: ścieżki, pojedyncza instancja, ACL pipe (BL-72, PROWIZORYCZNIE Qb41)

Data: 2026-10-14 (it. 91, rola 1 prowadzi; 1,2,10,11). Status: prowizoryczne (odwracalne). Realizuje R2.2.1/D2.5 (usługa), D2.12 (profil uprawnień — dane), D2.15 pkt 1 i 3, AC-37.3.

## Decyzja
- `Microsoft.Extensions.Hosting.WindowsServices` 10.0.13 (przyjęty w audycie planu; wiersz w zaleznosci.md) tylko dla `net10.0-windows`; `AddWindowsService` gdy proces działa pod SCM (`WindowsServiceHelpers.IsWindowsService`), Event Log tylko Warning+.
- `AgentPaths`: pod SCM brak `KOBIAN_CATALOG` → `%ProgramData%\Kobian\catalog.db`; wszystkie ścieżki bezwzględne; UNC/urządzenia/znaki sterujące odrzucane zawsze (kod 64). Konsola bez zmian (KOBIAN_CATALOG wymagane).
- `SingleInstanceGuard`: `catalog.db.lock` z FileShare.None (+ mutex `Global\Kobian.Agent.Instance` na Windows) PRZED CatalogBootstrap → druga instancja: kod 75 + KB-7007, katalog nietknięty (test procesowy).
- `AgentPipeAcl`: w trybie usługi `CurrentUserOnly=false` + PipeSecurity: Deny NETWORK, konto usługi RW+CreateNewInstance, SYSTEM/Administrators/„Kobian Operators” tylko RW; rola nadal FixedRoleResolver(Operator) do BL-17.
- `AgentServiceProfile`: jedno źródło wartości dla MSI i testów W-33 (konto wirtualne, delayed-auto, uprawnienia D2.12, odzyskiwanie 1/1/5 min, SID unrestricted, VssAccessControl=2).

## Alternatywy
- Konfiguracja w pliku `%ProgramData%\Kobian\agent.json` zamiast zmiennych środowiskowych usługi (wygodniejsze w MSI; koszt: parser z limitami — RD-50).
- Mutex z jawnym ACL od razu (SEC-22.4) — wymaga testu na Windows; dziś domyślne ACL (W-33).
- Pozostawienie CurrentUserOnly także w usłudze (CLI/GUI użytkowników nie połączą się z usługą — odrzucone).

## Konsekwencje / koszt zmiany zdania
- Mały: domyślna ścieżka i źródło konfiguracji to jedno miejsce (`AgentPaths.Resolve`). Do czasu BL-17 każdy administrator lokalny i członek „Kobian Operators” ma rolę Operator (nie Admin) — metody Admin (np. zwolnienie wstrzymania retencji) wymagają nadal konsoli serwisowej.
