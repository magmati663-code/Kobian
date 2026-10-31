# ADR-0042 — WatchRun: strumień postępu uruchomienia (SCR-07)

Data: 2026-10-18 (it. 134). Status: PROWIZORYCZNIE (Qb107). Powiązania: BL-64, R2.6.1 SCR-07, D2.9, D2.16, RD-60.

## Kontekst
SCR-07 (odtwarzanie) i `kobian watch` potrzebują postępu biegu w toku. Agent zapisuje `files`/`bytes_read` w `run_runs` (RD-60, co ≤ 1 s) — brakowało kanału do klienta bez odpytywania przez ListRuns (200 wierszy co sekundę).

## Decyzja
- `rpc WatchRun(WatchRunArgs) returns (stream RunProgress)`; Agent odpytuje JEDEN wiersz `run_runs` (połączenie read-only) co `WatchInterval` (1 s) i wysyła stan początkowy oraz wyłącznie zmiany; po stanie końcowym wiadomość `final=true` i koniec strumienia. Nieznany lub zniekształcony identyfikator → jedna wiadomość `final` z KB-0107 (bez zapytania SQL dla niepoprawnego znakowo identyfikatora).
- Rola Reader, niemodyfikujące (te same dane co ListRuns, bez ścieżek).
- **Bramka:** `AgentCallInterceptor` miał tylko `UnaryServerHandler` — metoda strumieniowa omijałaby kontrolę ról (domyślny Interceptor przepuszcza). Dodano `ServerStreamingServerHandler` ze wspólną bramką `AuthorizeAsync` (uwierzytelnienie, rola, rewizja, audyt odmów); strumień modyfikujący jest odrzucany; handlery strumienia klienta i dwukierunkowe odrzucają zawsze (kontrakt ich nie ma).
- Limit czasu strumienia `WatchMaxDuration` = 12 h (PROWIZORYCZNIE) — po nim strumień kończy się bez `final`, klient wznawia (ochrona zasobów Agenta).

## Alternatywy
Powiadomienia z writera katalogu (push, mniej odpytywania, ale sprzężenie writera z gRPC i obsługa wielu procesów — Worker zapisuje w innym procesie); `PRAGMA data_version` jako tani wyzwalacz (do rozważenia, gdy liczba obserwatorów wzrośnie). Koszt zmiany: wymiana wnętrza `CatalogAgentBackend.WatchRunAsync`, kontrakt bez zmian.

## Konsekwencje
Każdy obserwator = jedno krótkie zapytanie/s (WAL, odczyt nie blokuje zapisów). Brak limitu liczby jednoczesnych obserwatorów — pipe jest lokalny i uwierzytelniony (Reader+); limit do rozważenia przy UI wielosesyjnym (RD).
