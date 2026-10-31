# ADR-0019 — Discovery MSSQL dla kreatora S1-db i pin certyfikatu jako nazwa pliku (BL-40 cz. 3, it. 72)

Status: PROWIZORYCZNIE (Qb30). Realizuje R2.5.2 (D-01/D-02/D-03), D5.7, D5.8, AC-02.3, D2.9, SEC-3.3, SEC-5.1, NFR-11.

## Decyzja
- `MsSqlDiscoveryService` (Providers.Sources): tylko zapytania odczytu (zero ExecuteAsync); D-03 per baza, limit 500 baz (`truncated`), nazwy spoza sysname pomijane.
- Brak dostępu do jednej bazy (916/4060/229…) = `can_backup=false` (+ KB-3003), discovery trwa; utrata połączenia/TLS w trakcie = porażka całości (bez fałszywego „brak uprawnień”).
- RPC `DiscoverDatabases`: rola Operator (połączenie na koncie usługi), niemodyfikujące (bez kontroli rewizji). Walidacja żądania przed połączeniem (KB-0103).
- Pin certyfikatu w żądaniu = **nazwa** pliku `.cer` w `<stan>/certs` (regex, bez separatorów i „..”, plik musi istnieć) — klient nie wskazuje Agentowi dowolnej ścieżki.
- Skrypt uprawnień (`PermissionScript`, wariant minimalny) zwracany tylko dla uwierzytelnienia Windows (CREATE LOGIN … FROM WINDOWS dotyczy konta usługi).

## Alternatywy
- (a) Ścieżka .cer w żądaniu (jak w definicji zadania, ADR-0018): spójne, ale Operator mógłby kazać Agentowi czytać dowolny plik jako certyfikat.
- (b) Odcisk SHA-256 w żądaniu: wymaga własnej walidacji TLS (SqlClient 7.1 nie ma opcji odcisku) — odrzucone jak w ADR-0018 (a).
- (c) Discovery jako Reader: tańsze uprawnienia UI, ale połączenie z serwerem SQL zostawia ślad i koszt — Operator jak ProbeDestination.

## Konsekwencje
+ Kreator S1-db ma źródło danych (lista baz, rozmiary, ostrzeżenia, skrypt D5.7); testy na SQL zastępczym, prawdziwy serwer — W-26.
− Zatwierdzenie certyfikatu (pobranie z handshake TLS i zapis `.cer` w `<stan>/certs`) — następna część; do tego czasu pin wgrywa administrator ręcznie.
Koszt zmiany zdania: mały — jedno pole proto (`certificate`) i walidacja w `DatabaseDiscoveryService.Validate`.
