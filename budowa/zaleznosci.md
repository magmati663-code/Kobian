# Zależności — audyt podczas budowy

Pakiety bezpośrednie muszą mieć status „Przyjęte/Przyjęte warunkowo” w „Zależności — audyt” w `plan.md`. Tu: przechodnie i nowe.

| Data | Pakiet | Wersja | Bezpośredni/przechodni | Licencja | CVE / ostrzeżenia | Decyzja |
|---|---|---|---|---|---|---|
| 2026-10-12 | xunit.v3 (+ xunit.v3.assert/common/core.mtp-v2/extensibility.core/mtp-v2/runner.common/runner.inproc.console) | 4.0.2 | bezpośredni (tylko testy) | Apache-2.0 | `dotnet list package --vulnerable --include-transitive`: brak (2026-10-12) | Przyjęte (plan: audyt it. 20) |
| 2026-10-12 | xunit.analyzers | 2.2.0 | przechodni (testy) | Apache-2.0 | brak | Przyjęte |
| 2026-10-12 | Microsoft.Testing.Platform (+ .MSBuild, Extensions.Telemetry, TrxReport.Abstractions) | 2.5.1 | przechodni (testy) | MIT | brak | Przyjęte; telemetria wyłączona `TESTINGPLATFORM_TELEMETRY_OPTOUT=1` w `tools/ci.sh` |
| 2026-10-12 | Microsoft.ApplicationInsights | 2.23.0 | przechodni (Testing.Extensions.Telemetry, tylko testy) | MIT | brak | Przyjęte warunkowo — tylko testy, telemetria wyłączona; nie trafia do produktu |
| 2026-10-12 | Microsoft.Bcl.AsyncInterfaces 6.0.0, Microsoft.Win32.Registry 5.0.0, System.Security.AccessControl 6.0.1 | jw. | przechodni (testy) | MIT | brak | Przyjęte (tylko testy) |
| 2026-10-12 | System.Formats.Cbor | 10.0.12 | bezpośredni (Kobian.Format) | MIT | brak (`--vulnerable`) | Przyjęte (plan D3.8) |
| 2026-10-12 | Cronos | 0.13.0 | bezpośredni (Kobian.Scheduler) | MIT | brak (`--vulnerable`) | Przyjęte (plan D7.1) |
| 2026-10-12 | ZstdSharp.Port | 0.8.8 | bezpośredni (Kobian.Engine) | MIT | brak (`--vulnerable`); nuget.org bez ostrzeżeń; ryzyko bus factor (plan, re-audyt it. 23) | Przyjęte (plan D3.3) |
| 2026-10-12 | Microsoft.Data.Sqlite.Core | 10.0.12 | bezpośredni (Kobian.Catalog.Sqlite) | MIT | brak (`--vulnerable`) | Przyjęte (plan D2.3; re-audyt it. 22) |
| 2026-10-12 | SQLitePCLRaw.bundle_e_sqlite3 (+ core, provider.e_sqlite3, config.e_sqlite3) | 3.0.5 | bezpośredni + przechodnie przypięte | Apache-2.0 | brak; zgodność z Microsoft.Data.Sqlite.Core 10.0.12 (deklaruje core 2.1.12) potwierdzona testami katalogu (9 testów) | Przyjęte (plan: 3.0.5) — przypięcie przechodnie wymusza 3.0.5 |
| 2026-10-12 | SQLite (natywna biblioteka e_sqlite3) | 3.53.4 | przechodni | public domain | CVE-2026-11822 naprawione w 3.53.2 — wersja ≥ minimum, sprawdzana przy starcie (test) | Przyjęte |
| 2026-10-12 | CommunityToolkit.Mvvm | 8.4.2 | bezpośredni (Kobian.UI) | MIT | brak (`--vulnerable`) | Przyjęte (plan D6.1, re-audyt it. 16) |
| 2026-10-13 | Grpc.AspNetCore, Grpc.Net.Client, Grpc.Core.Api, Grpc.Tools | 2.84.0 | bezpośredni (Agent / Cli / Api.Contracts; Tools tylko build) | Apache-2.0 | `dotnet list package --vulnerable --include-transitive`: brak (it. 46, ci.sh); wcześniejsze CVE dotyczyły natywnego Grpc.Core — nieużywany | Przyjęte (plan D2.10, audyt it. 12/22); wersje przypięte + packages.lock.json |
| 2026-10-13 | Google.Protobuf | 3.36.2 | bezpośredni (Api.Contracts) | BSD-3-Clause | brak (audyt it. 46) | Przyjęte (przechodni Grpc.*, przypięty centralnie) |
| 2026-10-13 | SSH.NET | 2026.0.0 | bezpośredni (Kobian.Providers.Storage; tylko SftpClient) | MIT | CVE-2026-48798 / GHSA-q939-rpr3-3284 (ScpClient, path traversal, CVSS 7.1) naprawione w 2026.0.0 — wersja minimalna z D4.4; ScpClient zakazany (konwencje); nuget.org: 2026.0.0 bez ostrzeżeń, wersje ≤ 2025.1.0 oznaczone jako podatne; `--vulnerable` w ci.sh (it. 51) | Przyjęte (plan D4.4, SEC-4.7) |
| 2026-10-13 | BouncyCastle.Cryptography | ≥ 2.7.0 | przechodni (SSH.NET) | MIT | do sprawdzenia `--vulnerable --include-transitive` w ci.sh (it. 51) | Przyjęte warunkowo — audyt ci.sh |
| 2026-10-13 | Microsoft.Extensions.Logging.Abstractions | ≥ 8.0.3 | przechodni (SSH.NET) | MIT | do sprawdzenia w ci.sh (it. 51) | Przyjęte warunkowo — audyt ci.sh |
| 2026-10-14 | Avalonia, Avalonia.Desktop, Avalonia.Themes.Fluent | 12.1.4 | bezpośredni (Kobian.UI) | MIT | OSV (NuGet): brak wpisów dla Avalonia/Avalonia.Desktop/HarfBuzzSharp; `dotnet list package --vulnerable --include-transitive` czysty (it. 57) | Przyjęte (plan D6.1, audyt planu „Avalonia 12.1.4 — Przyjęte”); tylko rdzeń MIT, bez Avalonia Accelerate |
| 2026-10-14 | Avalonia.Headless, Avalonia.Skia | 12.1.4 | bezpośredni (tylko tests/Kobian.Tests.UI) | MIT | j.w. | Przyjęte (plan R2.6.1/AC-UX.3: Avalonia.Headless) |
| 2026-10-14 | SkiaSharp (+NativeAssets.Linux/WebAssembly) | 3.119.4 | przechodni (Avalonia.Skia) | MIT | OSV: GHSA-j7hp-h8jx-5ppr (libwebp CVE-2023-4863) dotyczy starszych wersji — zapytanie OSV z wersją 3.119.4: brak; ci.sh `--vulnerable` czysty | Przyjęte warunkowo — audyt co rundę (natywny kod: dekodery obrazów; UI dekoduje tylko własne ikony, podgląd plików SEC-6.6 z limitem) |
| 2026-10-14 | HarfBuzzSharp | 8.3.1.3 | przechodni | MIT | OSV: brak | Przyjęte warunkowo — audyt co rundę |
| 2026-10-14 | Tmds.DBus.Protocol | 0.94.1 | przechodni (Avalonia.FreeDesktop — tylko Linux) | MIT | GHSA-xrw6-gwf8-vvr9 naprawione w 0.92.0 — używana 0.94.1 poza zakresem | Przyjęte warunkowo (na Windows nieładowany) |
| 2026-10-14 | MicroCom.Runtime 0.11.6, Avalonia.Fonts.Inter 12.1.4 (tylko testy), Avalonia.BuildServices 11.3.2 | — | przechodni | MIT / OFL (Inter) | brak wpisów OSV | Przyjęte warunkowo. **Avalonia.BuildServices = telemetria przy kompilacji** → cel `AvaloniaStats` nadpisany pustym w `Directory.Build.targets` + `AVALONIA_TELEMETRY_OPTOUT=1` w ci.sh (zweryfikowane: „Overriding target AvaloniaStats”) |
| 2026-10-14 | Grpc.Net.Client | 2.84.0 | bezpośredni (także Kobian.UI) | Apache-2.0 | jak wyżej (it. 46) | Przyjęte — UI jest klientem API (D2.4) |
| 2026-10-14 | Microsoft.Data.SqlClient | 7.1.1 | bezpośredni (Kobian.Providers.Sources) | MIT | re-audyt it. 71: nuget.org — 7.1.1 nadal najnowsza; OSV (zapytanie z wersją): brak; historyczne CVE-2024-0056 nie dotyczy | Przyjęte (plan: audyt 5.2, re-audyt it. 15) |
| 2026-10-14 | Microsoft.Data.SqlClient.SNI.runtime 7.1.0, .Extensions.Abstractions 7.1.1, .Internal.Logging 7.1.1, Microsoft.SqlServer.Server 1.0.0 | — | przechodni (SqlClient) | MIT | OSV: brak | Przyjęte (SNI = natywny kod Microsoft, tylko Windows) |
| 2026-10-14 | Microsoft.IdentityModel.* (Abstractions, JsonWebTokens, Logging, Protocols, Protocols.OpenIdConnect, Tokens), System.IdentityModel.Tokens.Jwt | 8.16.0 | przechodni (SqlClient — uwierzytelnienie Entra, nieużywane) | MIT | OSV: brak | Przyjęte warunkowo — kod martwy w produkcie (tylko Integrated/SQL login); audyt co rundę |
| 2026-10-14 | Microsoft.Bcl.Cryptography, Microsoft.Extensions.Caching.Abstractions/Memory, Options, Primitives, System.Configuration.ConfigurationManager, System.Diagnostics.EventLog, System.Security.Cryptography.ProtectedData, System.Threading.RateLimiting | 9.0.18 | przechodni (SqlClient) | MIT | OSV: brak (zapytania z wersją) | Przyjęte |
| 2026-10-14 | **System.Security.Cryptography.Pkcs** | **10.0.13** (przypięcie przechodnie; SqlClient żąda 9.0.18) | przechodni | MIT | 9.0.18: GHSA-2fmv-rv2c-59x7 / GHSA-846f-f764-76mx (CVE-2026-70162/84008, DoS, naprawione 9.0.21/10.0.13); 10.0.13: brak | Przyjęte z przypięciem — usuń przypięcie, gdy SqlClient zależy od wersji naprawionej |
| 2026-10-14 | Microsoft.Data.Sqlite.Core, System.Formats.Cbor | 10.0.13 (z 10.0.12) | bezpośredni | MIT | OSV: brak | Przyjęte — seria serwisowa razem z runtime 10.0.13 |
| 2026-10-14 | **Microsoft.Extensions.Hosting.WindowsServices** (+ przechodnie Microsoft.Extensions.Hosting, Microsoft.Extensions.Logging.EventLog, System.ServiceProcess.ServiceController, System.Diagnostics.EventLog) | 10.0.13 | bezpośredni, tylko net10.0-windows (Kobian.Agent, BL-72) | MIT | OSV (zapytania z wersją 10.0.13): brak dla wszystkich pięciu; plan: „Przyjęte” (audyt zależności); wydawca Microsoft (dotnet/runtime) | Przyjęte — seria serwisowa razem z runtime 10.0.13 |

| 2026-10-17 | WixToolset.Sdk | 7.0.0 (przypięte w `installer/Kobian.Installer.wixproj`) | narzędzie budowania MSI — **poza Kobian.sln, nie przywracane ani uruchamiane w pętli** (it. 100) | MS-RL + EULA OSMF (v6+; Q37) | nie sprawdzano CVE w it. 100 (pakiet nieużywany w pętli); plan: re-audyt it. 20 bez ostrzeżeń — sprawdzić nuget.org/OSV przed pierwszą budową (W-42) | Przyjęte warunkowo (D10.2) — budowa dopiero po decyzji Q37 |

## 2026-10-17 (it. 120) — FluentFTP, MailKit/MimeKit
| Data | Pakiet | Wersja | Licencja | CVE / ostrzeżenia | Decyzja |
|---|---|---|---|---|---|
| 2026-10-17 | FluentFTP | 55.0.0 (55.1.0 = minor, nieprzyjęta bez re-audytu) | MIT | `dotnet list --vulnerable --include-transitive`: brak; zero zależności przechodnich. Znalezisko Średnie: biblioteka akceptuje certyfikat kanału danych bez walidacji (FtpDataStream) — łagodzenie: kanał danych na adres sterowania, dane AEAD, weryfikacja rozmiaru; do SP-04 | Przyjęte (D4.3) |
| 2026-10-17 | MailKit | 4.18.1 | MIT | CVE-2026-30227 dotyczy MimeKit < 4.15.1; nuget.org oznacza 4.5.0–4.15.1; `--vulnerable`: brak | Przyjęte (9.6, SEC-9.1) |
| 2026-10-17 | MimeKit | 4.18.1 | MIT | brak ostrzeżeń | Przyjęte |
| 2026-10-17 | BouncyCastle.Cryptography (przechodnia MimeKit) | 2.7.0 | MIT | brak ostrzeżeń i deprecjacji | Przyjęte warunkowo (Qb75) |
