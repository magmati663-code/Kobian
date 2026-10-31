# ADR-0023 — Lista dozwolonych serwerów SQL dla konta usługi (RD-39, PROWIZORYCZNIE Qb35)

Data: 2026-10-14 (it. 78, rola 3). Status: prowizoryczne (odwracalne). Zamyka znalezisko Średnie z it. 72–74 (Operator mógł skierować konto usługi — z uwierzytelnieniem Windows — na dowolny host).

## Decyzja
- `SqlServerAllowList` (Providers.Sources/MsSql) z konfiguracji Agenta `KOBIAN_SQL_ALLOWED_SERVERS` (wpisy `host`, `host\instancja`, `host:port`, `*`; rozdzielane `;` lub `,`). Brak konfiguracji = **tylko ten komputer** (Agent na hoście SQL, D5.10).
- Egzekwowanie PRZED połączeniem w czterech miejscach: discovery (`DatabaseDiscoveryService.Validate` — ta sama ścieżka dla `UpsertJob`/`InitRepository` z bazą), pobranie i zatwierdzenie certyfikatu (`ServerCertificateService`), uruchomienie zadania (`CatalogJobService`, także dla zadań zapisanych przed zawężeniem listy). Odmowa = KB-3009 (klasa Security), komunikat bez nazwy serwera.
- Dopasowanie nazw bez DNS (nazwa jak w zadaniu, bez rozróżniania wielkości liter); niepoprawny wpis = błąd startu Agenta (nie ciche pominięcie).
- Lista jest konfiguracją ADMINISTRATORA hosta (zmienna środowiskowa usługi), nie zmienia się przez API — Operator nie może jej poszerzyć.

## Alternatywy
- Lista w katalogu SQLite edytowana RPC dla Administratora (wygodniejsze w GUI; koszt: RPC + step-up D2.9) — kandydat na SCR-11 Ustawienia.
- Brak listy, tylko ostrzeżenie w UI (odrzucone: nie chroni przed świadomym nadużyciem).
- Rozwiązywanie DNS i porównanie adresów (odrzucone: TOCTOU, rebinding).

## Konsekwencje
- Instalacja na innym hoście niż SQL Server wymaga ustawienia `KOBIAN_SQL_ALLOWED_SERVERS` (MSI: pole w instalatorze — BL-MSI).
- Koszt zmiany zdania: mały (domyślna wartość albo źródło konfiguracji).
