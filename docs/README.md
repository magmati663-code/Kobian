# Kobian Backup — dokumentacja deweloperska (stan budowy)

Źródło prawdy o produkcie: `plan.md`. Ten katalog opisuje stan kodu.

| Dokument | Zawartość |
|---|---|
| `konwencje.md` | zasady kodu, budowy, testów i bezpieczeństwa |
| `kb-codes.yaml` / `kb-codes.md` | rejestr kodów błędów (D1.8); `.md` generowany `tools/kb.py docs` |
| `format-spec/pack-v1.md` | zaimplementowany układ packa, kluczy, klucza odzyskiwania |
| `ac-coverage.md` | pokrycie kryteriów akceptacji testami (generowane `tools/ac-coverage.py`) |
| `admin/kopia-bazy-sql.md` (+ `.en.md`) | przewodnik administratora: kopia bazy SQL Server, zatwierdzenie certyfikatu, komunikaty KB (it. 74) |
| `admin/odtwarzanie-i-ustawienia.md` (+ `.en.md`) | przewodnik administratora: odtwarzanie plików i bazy, cele, ustawienia, zmienne środowiskowe usługi, dziennik audytu, KB-7013 (it. 89) |
| `admin/usluga-windows.md` (+ `.en.md`) | Agent jako usługa Windows: profil (konto wirtualne, uprawnienia, odzyskiwanie), ścieżki w trybie usługi, pojedyncza instancja KB-7007, ACL pipe (it. 91); instalacja MSI i `--provision`, folder docelowy odtwarzania, Worker VSS (it. 100–104) |
| `../installer/README.md` (+ `.en.md`) | szkielet instalatora MSI (WiX, poza solucją; it. 100) |
| `admin/cel-smb.md` (+ `.en.md`) | cel SMB: ścieżki UNC i ich walidacja, połączenie bez litery dysku, poświadczenia ról, kody KB-21xx (it. 92, BL-73) |
| `admin/eksport-import-zadan.md` (+ `.en.md`) | eksport/import zadań `kobian-export v1` bez sekretów: format kanoniczny, limity, konflikty nazw, odrzucanie skryptów i sekretów, KB-0120–0124 (US-29) |
| `admin/cel-ftps.md` (+ `.en.md`) | cel FTPS: Explicit/Implicit TLS, pin SPKI, zapis bez nadpisania i okno wyścigu RNTO, wymagania serwera, kody KB-2109/2110/2115 (BL-34) |
| `admin/tozsamosc-serwera.md` (+ `.en.md`) | tożsamość serwera celu SFTP/FTPS: stany klucza, zatwierdzanie nowego klucza (`kobian destinations identity`), CA i „wysokie ryzyko”, KB-2141/2142/2143 (BL-35, D4.11) |
| `admin/weryfikacja-okresowa.md` (+ `.en.md`) | okresowa weryfikacja próbkowa V3 przez Agenta: ustawienia `verify`, koszt transferu, współpraca z kopią, KB-1510/1511 (BL-26, 3.7) |
| `mssql-source.md`, `agent-api.md`, `agent-job-definition.md`, `agent-scheduler.md`, `check.md`, `retention.md` | dokumentacja techniczna modułów (stan na it. 74) |
| `../THIRD-PARTY-NOTICES.md` | licencje zależności |

## Co istnieje (stan na iterację 39, runda 3 — HISTORYCZNE; aktualny stan: `budowa/nastepne-kroki.md` i dokumenty modułów powyżej)
| Moduł | Zakres zaimplementowany | Decyzje |
|---|---|---|
| Kobian.Domain | KobianException, ErrorClass, KbCode (generowane), port AEAD, JCS (RFC 8785, podzbiór) | D1.8, D3.11, D9.9 |
| Kobian.Crypto | hierarchia kluczy HKDF, AES-256-GCM, nonce, klucz odzyskiwania | D3.9, K14.1 |
| Kobian.Format | zapis/odczyt packa z limitami parsera; kodeki drzewa, indeksu i snapshotu; układ obiektów `RepoPaths` | D3.10, R2.3.5, R2.3.8 |
| Kobian.Engine | **potok backupu** (`BackupPipeline`, commit D3.14), `VerifiedUploader`, `MetadataStore`, `RepoReader`, **check V2/V3** (`RepoChecker`, docs/check.md), **restore** (`RestoreExecutor`, `OfflineRestore` = kobian-restore), kompresja zstd, MapPath, planer odczytów | D3.3, D3.14, D8.1, D8.3, D8.9, R2.8.1 |
| Kobian.Application | kontrakt magazynu, walidacja zadań (S1/S4/S5) | D4.6, D6.7 |
| Kobian.Providers.Storage | dostawca local | D4.7, D4.8 |
| Kobian.Providers.Sources | T-SQL MSSQL, skrypt uprawnień, profile ERP | R2.5, D5.7, D5.9 |
| Kobian.Scheduler | maszyna stanów, `RunExecutor` (ponowienia D3.14), cron w strefie zadania, misfire, blokady, pętla | D7.8–D7.10 |
| Kobian.Notifications | CloudEvents, alerty (reguły A01–A17 jako dane JSON + ewaluator), łańcuch audytu, podpis webhooka | D9.6, D9.7, D9.8, D9.9, D9.10 |
| Kobian.UI | maszyna stanów kreatora, WizardViewModel (walidacja w usłudze, komunikaty KB PL/EN) | D6.7 |
| Kobian.Catalog.Sqlite | utwardzenie SQLite (D2.14, wersja ≥ 3.53.2), migracje z kopią VACUUM INTO, CatalogWriter z priorytetem sterowania; DDL core/job/sched/run/repo/snap | D2.11, D2.14, D7.7, D8.8 |

CLI `kobian` (BL-47): polecenia i kody wyjścia gotowe, transport gRPC — BL-14 (dziś zawsze kod 3). Klucz repo: tymczasowo plik deweloperski (ADR-0002) do BL-24.

Brak jeszcze: Agent (usługa, gRPC, autoryzacja), host katalogu (BackgroundTaskRegistry), KDF hasła (BL-24), dedup D3.15 warstwy 2–3, SMB/SFTP, UI Avalonia, instalator. Moduły Windows — kolejka `budowa/do-uruchomienia-na-windows.md`.

## Budowa i testy
`tools/ci.sh` (Linux) lub `tools/windows/run-queue.ps1` (Windows). Wymagany .NET SDK 10.0.402 (`global.json`, runtime ≥ 10.0.13 — BL-62).

## Narzędzia repo (`tools/`)
| Narzędzie | Rola | Uwaga |
|---|---|---|
| `ci.sh` | restore/build/testy/weryfikatory DDL/audyt zależności | wymaga .NET SDK |
| `verify-sched-ddl.py`, `verify-restore-ddl.py` | niezmienniki DDL (Python sqlite3) | działają bez .NET |
| `model-d3-14-chaos.py`, `model-d3-15-bloom.py` | **modele** protokołu D3.14 i filtra D3.15 | to modele, nie pomiary kodu; nie zamykają SP-18/SP-19 ani NFR-22/26 |
| `kb.py`, `ac-coverage.py`, `bl-status.py`, `gen-specs.py` | rejestr KB, pokrycie AC, status backlogu, Gherkin | |
| `Kobian.Bench`, `Kobian.CorpusGen` | benchmarki i generator korpusu D10.9 | wyniki: `budowa/pomiary.md` (tylko faktycznie uruchomione) |
| `windows/` | kolejka testów Windows (`run-queue.ps1`) | niewykonana — czeka na właściciela (Qb9) |

Zasada: wartości NFR podajemy wyłącznie z `budowa/pomiary.md`; wszystko inne jest „niezmierzone” lub „model”.

## CLI: `kobian init` (it. 49, BL-24)
`kobian init <nazwa> --source <katalog> --dest <katalog repozytorium> --password-file <plik> [--dest-scheme local] [--json]` — tworzy repozytorium na celu (kobian.repo + klucz opakowany hasłem) i zadanie „files” w katalogu Agenta. Hasło wyłącznie z pliku (Qb14). Kody wyjścia: 0 OK, 2 błąd domenowy (np. KB-1003 repozytorium już istnieje, KB-0103 niepoprawna definicja), 3 brak usługi, 4 klient zbyt stary, 64 składnia/niepoprawna ścieżka. Agent: `KOBIAN_CATALOG` (wymagane), `KOBIAN_STATE_DIR` (opcjonalne; lokalne kopie nagłówka i kluczy).
