# Kolejka testów Windows (BL-11, S4=C). Uruchom w PowerShell w folderze projektu:
#   powershell -ExecutionPolicy Bypass -File tools\windows\run-queue.ps1
# Wymaga .NET SDK 10.0.401 (global.json). Nie wymaga uprawnień administratora (poza W-2: mklink /J).
# Wynik: budowa\wyniki-windows-<data>.txt — wklej lub zostaw w folderze; kolejny przebieg pętli go odczyta.
$ErrorActionPreference = 'Stop'
Set-Location (Join-Path $PSScriptRoot '..\..')
$stamp = Get-Date -Format 'yyyyMMdd-HHmm'
$out = "budowa\wyniki-windows-$stamp.txt"
"Kobian Backup — wyniki kolejki Windows $stamp" | Out-File $out -Encoding utf8
"OS: $([Environment]::OSVersion.VersionString)  .NET: $(dotnet --version)" | Out-File $out -Append -Encoding utf8
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'; $env:TESTINGPLATFORM_TELEMETRY_OPTOUT = '1'
dotnet build Kobian.sln -c Release -warnaserror 2>&1 | Select-Object -Last 5 | Out-File $out -Append -Encoding utf8
foreach ($t in Get-ChildItem tests -Directory -Filter 'Kobian.Tests.*' | Where-Object { $_.Name -notlike '*.Support' }) {
    $dll = Join-Path $t.FullName "bin\Release\net10.0\$($t.Name).dll"
    if (Test-Path $dll) {
        "== $($t.Name) ==" | Out-File $out -Append -Encoding utf8
        dotnet $dll -notrait 'Category=Perf' 2>&1 | Select-String -Pattern 'FAIL|Total:' | Out-File $out -Append -Encoding utf8
    }
}
"Gotowe: $out" | Write-Host
