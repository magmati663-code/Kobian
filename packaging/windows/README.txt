Kobian Backup — Windows x64 (wydanie przedpremierowe)

Instalacja: PowerShell jako administrator, w rozpakowanym folderze:
    powershell -ExecutionPolicy Bypass -File .\install.ps1

Instaluje do C:\Program Files\Kobian usługę „Kobian Backup Agent” (konto NT SERVICE\KobianAgent, uprawnienia kopii/odtwarzania),
grupy „Kobian Admins” i „Kobian Operators”, polecenia kobian.exe i kobian-restore.exe oraz aplikację Kobian.UI.exe.
Dane: C:\ProgramData\Kobian. Szczegóły: docs/admin/usluga-windows.md w repozytorium.

UWAGA: ta paczka Windows została zbudowana na Linuksie i NIE była uruchamiana na Windows (usługa, VSS, ACL, MSI — kolejka testów Windows).
Instalator MSI jest w przygotowaniu; do tego czasu install.ps1.
