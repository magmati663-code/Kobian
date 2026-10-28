# Konwencje kodu — Kobian Backup

Źródło: `plan.md` D2.7, D1.7, D1.8, D10.5, zasady bezpieczeństwa promptu budowy. Utworzone w iteracji 2 (Architekt).

## Solucja i projekty
- `Kobian.sln`, 17 projektów produkcyjnych w `src/` (R2.2.1, K22.1). Biblioteki `net10.0`; projekty zależne od Windows (`Platform.Windows`, `Agent`, `Worker`, `Restore.Standalone`) — `net10.0-windows10.0.17763.0` z `EnableWindowsTargeting` (kompilują się w Linuksie, testy → `budowa/do-uruchomienia-na-windows.md`).
- Reguły referencji: `tests/Kobian.Tests.Architecture` (statyczna analiza `.csproj`: biała lista bezpośrednich referencji, zakazy przechodnie, brak cykli, zakaz `Microsoft.AspNetCore*`/`Microsoft.Data.Sqlite`/`Grpc.*` w domknięciu `Restore.Standalone`). Nowy projekt = najpierw reguła w teście.
- Kod platformowy tylko za portami (interfejsy w `Kobian.Application`), adaptery w `Kobian.Platform.Windows`.

## Kompilacja
- `Nullable=enable`, `TreatWarningsAsErrors=true`, `AnalysisLevel=latest-recommended`, `EnforceCodeStyleInBuild`, `Deterministic` (Directory.Build.props). `InvariantGlobalization=false` (od it. 7): strefy IANA (`Europe/Warsaw`) na Windows i lokalizacja UI wymagają ICU; formatowanie deterministyczne zawsze z `CultureInfo.InvariantCulture` (CA1305 jako błąd).
- Zielony build: `tools/ci.sh [--locked]` (restore → build Release `-warnaserror` → testy xUnit v3 uruchamiane jako exe → weryfikatory DDL w Pythonie → `dotnet list package --vulnerable`; podatny pakiet = porażka). Na końcu tabela czasów etapów (NFR-20 — informacyjnie, środowisko nie jest referencyjne).

## Zależności
- Centralne wersje w `Directory.Packages.props`, przechodnie przypinanie, `packages.lock.json` w każdym projekcie, `RestoreLockedMode` w CI (`-p:ContinuousIntegrationBuild=true`), `NuGetAudit` (all, low).
- Tylko pakiety „Przyjęte/Przyjęte warunkowo” z „Zależności — audyt” w `plan.md`; każdy nowy — najpierw wiersz w `budowa/zaleznosci.md`.
- `NuGet.config`: jedno źródło nuget.org z `packageSourceMapping`.

## Testy
- xUnit v3; nazwy `Metoda_scenariusz_oczekiwanie` lub zdanie z podkreśleniami.
- Tagowanie: `[Trait("AC","AC-05.1")]`, `[Trait("NFR","NFR-11")]`, `[Trait("SP","SP-17")]`, `[Trait("SEC","SEC-3.3")]`, `[Trait("V","V-01")]`. Testy wymagające Windows: `[Trait("Category","Windows")]`; pomiary: `[Trait("Category","Perf")]`.
- Wyłącznie dane syntetyczne w katalogach tymczasowych (`Path.GetTempPath()` + losowy podkatalog, sprzątany w `Dispose`); sekrety generowane losowo w teście.

## Błędy i kody KB
- Każdy błąd widoczny dla użytkownika ma kod z `docs/kb-codes.yaml` (D1.8; zakresy per dziedzina). W kodzie tylko stałe z wygenerowanej klasy `KbCode` — nigdy literały.
- Klasy błędów: przejściowy / trwały / integralność / bezpieczeństwo (4.7, D3.14).
- Komunikaty wyjątków i logów bez sekretów i bez danych klienta (NFR-11, SEC-11.5); ścieżki w logach — przez maskowanie.

## Bezpieczeństwo (skrót listy kontrolnej roli 11)
- Zakazane: `BinaryFormatter`, `ScpClient`, wyłączanie walidacji certyfikatów, `Process.Start` z `UseShellExecute=true` lub składaniem poleceń z danych, MD5/SHA-1 do celów bezpieczeństwa, własne konstrukcje krypto, `Random` do nonce/kluczy (tylko `RandomNumberGenerator`).
- Porównania MAC/tagów: `CryptographicOperations.FixedTimeEquals`; bufory z sekretami: `CryptographicOperations.ZeroMemory`.
- Parsery niezaufanych danych (`Kobian.Format`): jawne limity (SEC-3.3), brak rekursji bez limitu głębokości, `checked` przy arytmetyce rozmiarów.
- SQL: tylko parametry; identyfikatory przez `QUOTENAME` po stronie serwera.

## Nazewnictwo i język
- Kod, identyfikatory i komentarze techniczne — angielski lub polski konsekwentnie w pliku; dokumentacja i komunikaty — PL/EN przez `.resx`.
- Odwołania do planu w komentarzach po ID (`D3.9`, `AC-04.2`, `KB-2104`), bez kopiowania treści planu.

## Środowisko budowy (stan iteracji 2)
- Brak git (S5d) — historia w `budowa/log.md`. Kod budowany i testowany w środowisku Linux z .NET SDK 10.0.401 (zweryfikowana suma SHA-512 z `releases.json`), następnie synchronizowany do folderu projektu. Szczegóły: `budowa/narzedzia.md`.
