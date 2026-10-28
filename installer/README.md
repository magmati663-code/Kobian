# Instalator MSI (szkielet, it. 100 — PROWIZORYCZNIE Qb50, ADR-0033)

- `Package.wxs` — pakiet per-machine (D10.2): usługa `KobianAgent` (konto wirtualne `NT SERVICE\KobianAgent`, Automatic (Delayed), lista uprawnień tokenu D2.12, akcje odzyskiwania 1/1/5 min), pliki Agenta i klienta (CLI/UI), custom action `Kobian.Agent.exe --provision` (deferred, LocalSystem) po InstallServices i `--unprovision` przy odinstalowaniu.
- Kroki `--provision` (`AgentInstallPlan`): grupy „Kobian Operators”, „Kobian Admins”; `%ProgramData%\Kobian` (+ `secrets`, `keys`, `certs`, `staging`) z ACL bez dziedziczenia: SYSTEM, Administrators, `NT SERVICE\KobianAgent` — pełny (2.6); `VssAccessControl` = 2; prawa LSA SeBackup/SeRestore. `--unprovision`: tylko VssAccessControl i prawa LSA. **Dane i grupy zostają** po odinstalowaniu.
- Zgodność `Package.wxs` z `AgentServiceProfile` pilnuje test `InstallerTests` (Linux). Plik nie był kompilowany WiX-em w pętli — pierwsza budowa i testy instalacji: kolejka Windows **W-42**.
- Budowa (Windows, po decyzji Q37 — opłata OSMF WiX v6+): polecenia w komentarzu `Kobian.Installer.wixproj`. Podpisywanie MSI — poza pętlą (klucze nigdy nie powstają w pętli budowy).
- Do zrobienia: instalacja cicha z `CONFIG=`, GPO/winget, aktualizacja (`MajorUpgrade` jest), Worker `--role vss`, zadanie wybudzania (7.5), źródło Event Log.
