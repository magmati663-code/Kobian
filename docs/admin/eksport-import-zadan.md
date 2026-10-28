# Eksport i import zadań (kobian-export v1) — przewodnik administratora

Stan: US-29 (BL-47, AC-29.1–29.2, SEC-6.7). Dotyczy usługi Kobian (Agent) i CLI `kobian`. English: `eksport-import-zadan.en.md`.

Eksport służy do przeniesienia konfiguracji zadań na inny serwer (migracja, odtworzenie po awarii, ta sama konfiguracja u wielu klientów serwisanta — 1.2 P2) i do przeglądu konfiguracji. **Plik nie zawiera sekretów**: hasła i loginy zostają w magazynie sekretów Agenta, a w pliku są tylko odwołania.

## 1. Polecenia

| Polecenie | Działanie | Kody wyjścia |
|---|---|---|
| `kobian export [--job <nazwa>]... [--output <plik> [--force]] [--json]` | eksport wszystkich (albo wskazanych) zadań „files” i „database”; bez `--output` dokument idzie na standardowe wyjście | 0 OK, 2 błąd (KB w JSON/na stderr), 3 brak usługi **lub brak uprawnień**, 64 składnia / plik istnieje (bez `--force`) |
| `kobian import <plik> --dry-run [--on-conflict skip\|overwrite\|rename] [--json]` | plan zmian bez zapisu: `new` (nowe), `unchanged` (identyczne), `conflict` (ta sama nazwa, inna definicja) | 0 import by się udał, 2 odrzucony (lista błędów), 3, 64 |
| `kobian import <plik> [--on-conflict skip\|overwrite\|rename] [--json]` | zapis — **wszystko albo nic** | jak wyżej |

Oba wywołania wymagają roli **Administrator** (kontrakt D2.10). Eksport i każdy import (także `--dry-run`) trafiają do dziennika audytu (`kobian audit verify`): przy imporcie zapisywany jest skrót SHA-256 pliku, jego rozmiar i tryb — nie treść.

Konflikt nazw (zadanie o tej nazwie istnieje z **inną** definicją lub harmonogramem):

| `--on-conflict` | Skutek |
|---|---|
| (brak) | cały import odrzucony: KB-0124 |
| `skip` | istniejące zadanie zostaje bez zmian |
| `overwrite` | definicja, strefa i harmonogram zastąpione; zadanie zachowuje identyfikator i historię, **zostaje wyłączone**. Zadania chronionego (SEC-7.6) import nie nadpisuje (KB-0124) |
| `rename` | nowe zadanie „nazwa (import)”, „nazwa (import 2)”, … |

## 2. Co robi import, a czego nie robi

- **Nie uruchamia niczego.** Nowe i nadpisane zadania są zapisywane jako **wyłączone**; harmonogram zapisuje się, ale nie działa, dopóki Administrator nie włączy zadania (kreator / `UpsertJob`). Import nie łączy się z celem kopii.
- **Nie przyjmuje skryptów ani akcji** (pre/post, `runSql`, `stopService`, polecenia): takie pola = odrzucenie całego pliku (KB-0123; SEC-6.7, AS-02). Skrypty konfiguruje się w programie po imporcie.
- **Nie przyjmuje sekretów:** pola o nazwach typu `password`, `token`, `privateKey` oraz wartości wyglądające na sekret (blok PEM, `password=…`, `https://użytkownik:hasło@…`, JWT, długi ciąg szesnastkowy, losowo wyglądający ciąg ≥ 20 znaków) = KB-0122. Poświadczenie podaje się wyłącznie jako `secretRef` (`file:<nazwa>`) z wpisem w magazynie sekretów Agenta.
- **Odrzuca pola nieznane** (nie ignoruje): literówka albo pole z nowszej wersji = KB-0120 z dokładną ścieżką.
- **Sprawdza zadanie tymi samymi regułami co kreator:** źródło musi istnieć i być czytelne na tym serwerze (KB-0101), cel poprawny dla schematu i obsługiwany (KB-0103/KB-21xx), brak kopii do wnętrza źródła (KB-0102), harmonogram ≥ 5 min (KB-0201), retencja (KB-0401). Pliki klucza/nagłówka/hasła muszą być ścieżkami lokalnymi (nie UNC).
- **Przygotowuje kreator:** w planie przy każdym zadaniu `needsCredentials` (brak wpisu `secretRef` w magazynie sekretów tego serwera — kreator poprosi o poświadczenia, AC-29.1) i `missingKeyMaterial` (brak pliku klucza/nagłówka/hasła na tym serwerze).

Błędy pliku zwracane są jako lista: ścieżka JSON (np. `$.jobs[0].definition.destination.root`), kod KB i powód — bez wartości pól (AC-29.2). Do 100 pozycji.

## 3. Format `kobian-export v1`

```json
{
  "exportedAt": "2026-10-17T10:00:00Z",
  "format": "kobian-export",
  "jobs": [
    {
      "definition": {
        "destination": {"root": "/srv/kopie", "scheme": "sftp", "host": "nas.example", "hostKey": "SHA256:…", "secretRef": "file:nas"},
        "key": {"file": "C:\\ProgramData\\Kobian\\keys\\…\\….key", "passwordFile": "C:\\ProgramData\\Kobian\\secrets\\repo-….pw"},
        "retention": {"keepDaily": 14, "keepLast": 7, …},
        "sources": ["D:\\Dane"],
        "version": 1
      },
      "enabled": true,
      "misfire": "skip",
      "name": "Dokumenty",
      "schedule": {"cron": "0 21 * * *", "enabled": true},
      "timeZone": "Europe/Warsaw",
      "type": "files"
    }
  ],
  "programVersion": "1.0.0",
  "version": 1
}
```

| Pole | Znaczenie |
|---|---|
| `format`, `version` | stałe `kobian-export` i `1`; inna wartość = KB-0120 |
| `programVersion`, `exportedAt` | wersja Agenta i czas eksportu (UTC, `yyyy-MM-ddTHH:mm:ssZ`) — informacyjnie |
| `jobs[].name`, `type` | nazwa (≤ 200 znaków, bez znaków sterujących i spacji na brzegach), typ `files` albo `database` |
| `jobs[].enabled` | stan w chwili eksportu — **informacyjnie**; import zawsze tworzy zadania wyłączone |
| `jobs[].timeZone`, `misfire`, `misfireWithinSeconds` | strefa harmonogramu (IANA/Windows), polityka zaległych wystąpień (`skip`, `runOnceIfMissed`, `runIfMissedWithin` + sekundy ≤ 7 dni) |
| `jobs[].schedule` | `cron` (5 pól, strefa zadania) i `enabled`; brak = zadanie ręczne |
| `jobs[].definition` | definicja v1 zadania (`docs/agent-job-definition.md`) — tylko pola schematu |

**Bez sekretów:** `secretRef` to nazwa wpisu w magazynie sekretów Agenta, `key.file`/`passwordFile`/`repoHeader`/`certificate` to **ścieżki** (nigdy treść pliku). Po przeniesieniu na inny serwer trzeba tam odtworzyć wpisy sekretów i pliki kluczy (albo założyć nowe repozytorium kreatorem).

**Postać kanoniczna:** klucze obiektów posortowane porządkowo na każdym poziomie, zadania posortowane po nazwie, brak `null`, wcięcie 2 spacje, LF, UTF-8 bez BOM, znaki spoza ASCII jako `\uXXXX`. Ten sam stan katalogu daje identyczny plik (poza `exportedAt`) — nadaje się do porównywania (`diff`) i kontroli wersji. Import przyjmuje też plik ręcznie sformatowany (inna kolejność kluczy, BOM).

**Limity (SEC-3.3):** plik ≤ 60 KiB (mieści się w limicie wiadomości API 64 KiB — PROWIZORYCZNIE; większy eksport → KB-0121, eksportuj częściami `--job`), zagnieżdżenie ≤ 8, ≤ 200 zadań, napis ≤ 1024 znaki, duplikaty kluczy JSON = odrzucenie.

## 4. Kody KB

| Kod | Kiedy |
|---|---|
| KB-0120 | plik niezgodny ze schematem: zły JSON, pole nieznane/wymagane/zły typ, duplikat klucza lub nazwy zadania, inny `format`/`version` |
| KB-0121 | przekroczony rozmiar, głębokość, liczba zadań lub długość napisu |
| KB-0122 | pole lub wartość wyglądająca na sekret |
| KB-0123 | skrypt lub akcja w pliku |
| KB-0124 | konflikt nazwy bez `--on-conflict` albo próba nadpisania zadania chronionego |
| KB-0101/0102/0103/0201/0401, KB-21xx | zadanie odrzucone regułami kreatora (źródło, cel, harmonogram, retencja) |
| KB-0104 | `export --job` z nieistniejącą nazwą |
| KB-7011 | brak roli Administrator (CLI: kod 3) |
