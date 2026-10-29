# Kobian Backup — instalacja na Windows (usługa KobianAgent). Uruchom w PowerShell „jako administrator” z rozpakowanego folderu:
#   powershell -ExecutionPolicy Bypass -File .\install.ps1
# Wersja przedpremierowa: instalacja skryptem zamiast MSI (MSI w przygotowaniu). Kroki jak w docs/admin/usluga-windows.md.
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$dest = Join-Path $env:ProgramFiles 'Kobian'
if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Uruchom PowerShell jako administrator.' }
if (Get-Service KobianAgent -ErrorAction SilentlyContinue) { Stop-Service KobianAgent -Force -ErrorAction SilentlyContinue }
New-Item -ItemType Directory -Force -Path $dest | Out-Null
Copy-Item -Path (Join-Path $here 'Kobian\*') -Destination $dest -Recurse -Force
$exe = Join-Path $dest 'Kobian.Agent.exe'
if (-not (Get-Service KobianAgent -ErrorAction SilentlyContinue)) {
    & sc.exe create KobianAgent binPath= "`"$exe`"" start= delayed-auto obj= "NT SERVICE\KobianAgent" DisplayName= "Kobian Backup Agent" | Out-Host
}
& sc.exe privs KobianAgent SeBackupPrivilege/SeRestorePrivilege/SeChangeNotifyPrivilege/SeShutdownPrivilege | Out-Host
& sc.exe sidtype KobianAgent unrestricted | Out-Host
& sc.exe failure KobianAgent reset= 86400 actions= restart/60000/restart/60000/restart/300000 | Out-Host
& sc.exe failureflag KobianAgent 1 | Out-Host
if (-not [Diagnostics.EventLog]::SourceExists('KobianAgent')) { New-EventLog -LogName Application -Source KobianAgent }
# Katalog danych, grupy „Kobian Admins”/„Kobian Operators”, ACL i wpis VSS (to samo, co custom action MSI).
& $exe --provision | Out-Host
Start-Service KobianAgent
Get-Service KobianAgent | Format-Table -AutoSize
$machinePath = [Environment]::GetEnvironmentVariable('Path', 'Machine')
if ($machinePath -notlike "*$dest*") { [Environment]::SetEnvironmentVariable('Path', "$machinePath;$dest", 'Machine') }
Write-Host "Kobian zainstalowany w $dest. Dodaj użytkowników do grupy „Kobian Operators” (kopie i odtwarzanie) lub „Kobian Admins”."
Write-Host "Sprawdzenie (nowe okno konsoli): kobian status   |   Aplikacja: $dest\Kobian.UI.exe"
