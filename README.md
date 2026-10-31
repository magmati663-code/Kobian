# Kobian Backup

System kopii zapasowych dla Windows: silnik z szyfrowaniem AES-256-GCM i kompresją zstd, katalog w SQLite, agent (usługa) z gRPC po named pipes, GUI w Avalonia, CLI oraz samodzielny `kobian-restore`. Platforma: C# / .NET 10 (`global.json`: SDK 10.0.402).

## Start
1. Zainstaluj .NET SDK zgodnie z `global.json`.
2. `tools/ci.sh --locked` (bash; na Windows Git Bash/WSL) — build, testy, audyt zależności.
3. Stan prac i następne zadanie: `budowa/nastepne-kroki.md` (ostatnia sekcja) oraz `budowa/backlog.md`.

## Układ
`src/` kod (17 projektów) · `tests/` testy · `docs/` dokumentacja · `specs/` scenariusze Gherkin · `tools/` skrypty CI i generatory · `installer/` WiX · `budowa/` backlog, log, ADR · `plan.md` plan budowy.

Moduły wymagające Windows (MSI, VSS): `budowa/do-uruchomienia-na-windows.md`.
