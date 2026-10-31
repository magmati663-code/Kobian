# Definicja zadania „files” — wersja 1 (job_jobs.definition)

JSON UTF-8, ≤ 64 KiB (ADR-0005, PROWIZORYCZNIE Qb13):

```json
{"version":1,"sources":["C:\\Dane"],"destination":{"scheme":"local","root":"D:\\Kopie"},
 "key":{"file":"D:\\Kopie\\keys\\a.key","passwordFile":"C:\\sekret\\pw.txt","repoHeader":"D:\\Kopie\\kobian.repo"},"packTargetSize":4194304}
```

- Dokładnie jedno źródło (ścieżka bezwzględna). Cel `local`, `smb`, `sftp` lub `ftps` (it. 93; `ftps` — BL-34, BL-73 cz. 2, ADR-0026, PROWIZORYCZNIE Qb43) — reguły w `JobDestination`:

| Schemat | Sekcja `destination` | Wymagane | Odmowa |
|---|---|---|---|
| `local` | `{"scheme":"local","root":"D:\\Kopie"}` | ścieżka bezwzględna | `secretRef`/`host` w celu lokalnym (KB-0103) |
| `smb` | `{"scheme":"smb","root":"\\\\nas\\kopie","secretRef":"file:nas"}` | UNC wg `UncPath` (D8.9), `secretRef` | brak/niepoprawny `secretRef` (KB-2102), udział administracyjny, `..`, ADS |
| `sftp` | `{"scheme":"sftp","root":"/srv/kopie","host":"h","port":22,"hostKey":"SHA256:…","secretRef":"file:s1"}` | ścieżka uniksowa od `/`, `host`, przypięty `hostKey` (D4.11), `secretRef` | brak klucza hosta (KB-2104, bez TOFU — Qb15), `..`, `\` |
| `ftps` | `{"scheme":"ftps","root":"/kopie","host":"h","port":21,"hostKey":"SHA256:<SPKI>","secretRef":"file:f1"}` | ścieżka uniksowa od `/`, `host`, opcjonalny pin SPKI w `hostKey` (D4.11 — certyfikat z zaufanego CA go nie wymaga), port 990 = Implicit FTPS (PROWIZORYCZNIE), `secretRef` | zły kształt pinu (KB-2109), brak `secretRef` (KB-2102), `..`, `\`; schemat `ftp` (jawny) = KB-2115 / KB-0103 |

  Nieznane pola w `destination` = KB-0103 (poświadczenie nigdy nie trafia do definicji — tylko odwołanie `file:<nazwa>` do magazynu sekretów Agenta).
  Identyfikator celu (`repo_repositories`, zasób kolejki) = skrót `schemat:korzeń`, dla SFTP z hostem i portem; dla `local` bez zmian względem it. 47–92.
- Klucz: `file` obowiązkowy; `passwordFile` dla klucza opakowanego; `repoHeader` (kobian.repo) weryfikuje MAC i listę kluczy (KB-1104/1102).
- `packTargetSize` 1–512 MiB (domyślnie z możliwości dostawcy).
- `verify` (opcjonalne, BL-26, PROWIZORYCZNIE Qb101): `{"enabled":true,"intervalDays":7,"samplePercent":5}` — okresowa weryfikacja próbkowa V3 przez Agenta (3.7). Każde pole opcjonalne; brak sekcji = wartości domyślne z planu (co 7 dni, 5% packów rotacyjnie — 100% w 20 cyklach). `intervalDays` 1–366, `samplePercent` 1–100; nieznane pole/zakres = KB-0103. Szczegóły: `docs/check.md`.
- Błędy: KB-0103 (definicja), KB-0104 (brak zadania), KB-0105 (zadanie wyłączone → Skipped), KB-4103 (poprzednie uruchomienie trwa), KB-0101 (źródło niedostępne), KB-1402 (zapis do repo).
- Każde podejście = wiersz `run_runs` (cause `manual`/`retry`) + `run_transitions`; udany backup = `snap_snapshots` (przyrost, gdy jest poprzedni snapshot).

## Zadanie „database” (MSSQL) — it. 71, ADR-0018 (PROWIZORYCZNIE Qb29)

`job_jobs.type = 'database'`; zamiast `sources` sekcja `database`:

```json
{"version":1,
 "database":{"engine":"mssql","server":"srv01\\SQLEXPRESS","name":"Firma","auth":"integrated",
             "certificate":"C:\\ProgramData\\Kobian\\certs\\srv01.cer","staging":"D:\\Kobian\\staging","lowImpact":true,"conflictWaitMinutes":30},
 "destination":{"scheme":"local","root":"E:\\Kopie"},"key":{"file":"…","passwordFile":"…","repoHeader":"…"}}
```

- `auth`: `integrated` (domyślnie, konto usługi) albo `sql` + `secretRef` (`file:<nazwa>`); hasło nigdy w definicji (nieznane pola = KB-0103).
- `certificate` — pin (.cer); `allowLocalhostUntrusted` tylko dla localhost; `strict` = TDS 8.
- `staging` — ścieżka Agenta; `sqlStaging` — ta sama lokalizacja widziana przez SQL Server (ścieżka Windows); bez `sqlStaging` `staging` musi być ścieżką Windows.
- `conflictWaitMinutes` 0–1440 (AC-02.2), `lowImpact` (NFR-07).
- Snapshot w trybie `Database` (wpis `database.bak`, `dbMeta`); błędy KB-3xxx w `run_runs.kb_code`.

## Eksport/import (US-29)

Definicja v1 jest przenoszona bez zmian w pliku `kobian-export v1` (pole `jobs[].definition`, postać kanoniczna, tylko pola schematu) — `docs/admin/eksport-import-zadan.md`. Pola nieznane na KAŻDYM poziomie (także najwyższym i w `key`, których parser uruchomieniowy nie sprawdza) import odrzuca (KB-0120).
