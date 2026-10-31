# ADR-0004 — Agent wielocelowy (net10.0 + net10.0-windows) i gniazdo Unix poza Windows (PROWIZORYCZNIE)

Data: 2026-10-13 (iteracja 46). Status: prowizoryczny. Dotyczy: D2.4, D2.9, D2.10, R2.2.1 (K22.1), BL-14, BL-15.

## Kontekst
Test (net10.0) nie może referować projektu net10.0-windows, a Kestrel obsługuje named pipes wyłącznie w Windows (`PlatformNotSupportedException` — zweryfikowane w chmurze). Bez tego host gRPC nie jest testowalny w Linuksie, gdzie budujemy (S4=C).

## Decyzja
1. `Kobian.Agent` ma `TargetFrameworks = net10.0;net10.0-windows10.0.17763.0`; referencja do `Platform.Windows` tylko dla wariantu Windows. Produkcja = wariant Windows + named pipe `Kobian.Agent.v1` (CurrentUserOnly = true, fail-closed).
2. Poza Windows (dev/testy) host i klient używają gniazda Unix `<tmp>/<nazwa>.sock` (tryb 0600). Nie jest to wspierana konfiguracja produkcyjna.
3. Rola wywołującego: `ICallerRoleResolver`; produkcja = SID klienta (RunAsClient, SP-02, Windows). Do czasu SP-02 program Agenta używa stałej roli Operator, a jedyną bramką jest pipe CurrentUserOnly. // PROWIZORYCZNIE
4. Pakiety: Grpc.AspNetCore, Grpc.Net.Client, Grpc.Core.Api, Grpc.Tools 2.84.0, Google.Protobuf 3.36.2 (audyt: budowa/zaleznosci.md).

## Alternatywy
Osobny projekt „Agent.Core” (net10.0) — 18. projekt, narusza K22.1; test przez proces zewnętrzny — wolne i kruche.

## Koszt zmiany zdania
Niski–średni: wyodrębnienie `Hosting/` do osobnego projektu to przeniesienie plików; klient i kontrakt bez zmian.
