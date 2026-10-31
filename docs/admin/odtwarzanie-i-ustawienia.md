# Odtwarzanie, cele, ustawienia i dziennik audytu — przewodnik administratora

Stan: iteracja 89 (runda 6). Dotyczy usługi Kobian (Agent) i programu Kobian (GUI) oraz CLI `kobian`. English: `odtwarzanie-i-ustawienia.en.md`.

## 1. Odtworzenie plików
| Sposób | Jak |
|---|---|
| GUI | **Historia** → wybierz kopię → **Odtwórz…** (SCR-06): wybór plików/folderów, folder docelowy, wynik z kodem KB |
| CLI | `kobian snapshots --job <zadanie>` → `kobian restore --snapshot <id> [--path <ścieżka w kopii>] --to <folder>` |

Zasady (D8.9): odtworzenie **nigdy nie nadpisuje** istniejących plików (KB-5105) i nie przechodzi przez dowiązania w folderze docelowym (KB-5104); plik o niezgodnej sumie jest usuwany i raportowany (KB-5201). Folder docelowy musi istnieć.

`--snapshot` przyjmuje pełny identyfikator, jednoznaczny prefiks (≥ 8 znaków hex) albo `latest`. **Uwaga:** samo `latest` oznacza najnowszą kopię w całym katalogu usługi (wszystkich zadań) — przy wielu zadaniach dodaj `--job <zadanie>` (`--snapshot latest --job Pliki` = najnowsza kopia zadania „Pliki”; BL-70) albo podaj identyfikator z `kobian snapshots --job <zadanie>`. Z identyfikatorem `--job` nic nie zmienia. GUI zawsze wysyła identyfikator. Brak/niejednoznaczny prefiks → KB-5002.

**Podgląd przed odtworzeniem (it. 98, R2.8.1):** w GUI „Odtwórz” najpierw sprawdza kopię i folder docelowy — liczba plików i rozmiar, wolne miejsce, pliki, które już istnieją i zostaną pominięte (wtedy drugi przycisk „Odtwórz mimo to”); blokady: brak prawa zapisu (KB-5001), za mało miejsca (KB-5106), brak bloków w indeksach (KB-5202), ścieżki nie ma w kopii (KB-5003). W CLI: `kobian restore … --dry-run [--json]` — nic nie zapisuje; kod wyjścia 0 = bez pominięć, 1 = będą pominięcia, 2 = blokada. Od it. 101 w usłudze Operator może odtwarzać (i sprawdzać podgląd) tylko do folderu, w którym **jego konto** ma prawo dodawania plików i folderów (AccessCheck tokenem klienta, SEC-6.3) — inaczej KB-5001 przed odczytem kopii; Administrator bez tej kontroli.

Bez usługi (awaria serwera): narzędzie `kobian-restore` czyta repozytorium bezpośrednio (`kobian-restore restore … --password-file <plik>`), patrz `docs/check.md` i `docs/format-spec/`.

## 2. Odtworzenie bazy SQL Server (SCR-08)
| Sposób | Jak |
|---|---|
| GUI | **Historia** → kopia z pigułką „kopia bazy” → **Odtwórz jako nową bazę…** → (opcjonalnie) nazwa → wynik |
| CLI | `kobian restore-db --snapshot <id> [--as <nazwa>]` |

- Baza jest odtwarzana zawsze jako **nowa** baza: domyślnie `<źródło>_restore_RRRRMMDD`. Istniejąca baza o tej nazwie → KB-3501 (Kobian nie używa `REPLACE`; podmiana produkcji to świadoma operacja DBA).
- Przed odtworzeniem: `RESTORE VERIFYONLY … WITH CHECKSUM`; pliki trafiają do domyślnych katalogów danych/dziennika serwera (brak → KB-3504); `TRUSTWORTHY OFF`.
- Wymagane uprawnienie konta usługi: `CREATE ANY DATABASE` / rola `dbcreator` (KB-3502). FILESTREAM i pełnotekstowe — nieobsługiwane (KB-3503, odtwórz ręcznie w SSMS).
- Plik `.bak` jest najpierw pobierany do katalogu tymczasowego usługi (`KOBIAN_DB_STAGING`); za mało miejsca → KB-3102. Katalog jest sprzątany zawsze, także po błędzie.
- Serwer SQL musi być dozwolony (sekcja 5).

## 3. Cele (SCR-09)
Ekran **Cele** pokazuje cele zapisane w zadaniach (schemat, ścieżka/host bez sekretów, odcisk klucza hosta SFTP, zadania korzystające z celu). **Sprawdź miejsce** łączy się z celem na żądanie (sesja tylko do odczytu) i pokazuje zajętość; „mało miejsca” jest opisane tekstem, nie tylko kolorem. Lista wymaga roli Operator.

## 4. Ustawienia (SCR-11)
- Motyw (jasny/ciemny/systemowy) — działa od razu; język PL/EN — po ponownym uruchomieniu programu.
- Plik ustawień użytkownika: `%APPDATA%\Kobian\ui-settings.json` (≤ 4 KiB; uszkodzony lub za duży plik = ustawienia domyślne). Nie zawiera sekretów.
- Sekcja **Usługa**: wersja, rola bieżącego użytkownika, poziom poprawek .NET; przycisk **Sprawdź dziennik** (sekcja 6).
- **KB-7013** (baner na pulpicie i w Ustawieniach): usługa działa na niezałatanym .NET (poniżej 10.0.13). Działanie: zainstaluj aktualizację .NET 10 i uruchom ponownie usługę.

## 5. Zmienne środowiskowe usługi
| Zmienna | Domyślnie | Znaczenie |
|---|---|---|
| `KOBIAN_CATALOG` | — (wymagana) | ścieżka PLIKU katalogu SQLite `catalog.db` (nie folder ani korzeń dysku — od it. 105 błąd konfiguracji, kod 64); brak → usługa nie startuje |
| `KOBIAN_STATE_DIR` | folder z `catalog.db` | stan usługi: kopie kluczy repo, certyfikaty `.cer` |
| `KOBIAN_SECRETS_DIR` | `<stan>\secrets` | sekrety `file:<nazwa>` (PROWIZORYCZNIE, ADR-0007; docelowo DPAPI) |
| `KOBIAN_DB_STAGING` / `KOBIAN_DB_SQL_STAGING` | `<stan>\staging` | staging plików `.bak` (usługa / ścieżka widziana przez SQL Server) |
| `KOBIAN_SQL_ALLOWED_SERVERS` | tylko lokalny serwer | lista dozwolonych serwerów SQL rozdzielona `;` (np. `sql01;sql02\ERP`); spoza listy → KB-3009 |
| `KOBIAN_SCHEDULER` | włączony | `0` wyłącza harmonogram |
| `KOBIAN_VERIFY_SCHEDULE` | włączona | `0` wyłącza okresową weryfikację V3 (`admin/weryfikacja-okresowa.md`) |
| `KOBIAN_AUTO_RETENTION` | włączona | `0` wyłącza retencję po udanej kopii |
| `KOBIAN_PARALLEL_UPLOADS` | włączone | `0` = pliki kopii (packi) wysyłane po jednym. Domyślnie usługa otwiera do celu tyle połączeń, ile zaleca dostawca (dysk lokalny 2, SMB 4, SFTP 3), a po odmowie serwera (KB-2112) zmniejsza ich liczbę i pamięta to przez 7 dni (PROWIZORYCZNIE Qb63). Każde połączenie to dodatkowy bufor pliku kopii w pamięci (pack 16 MiB → bufor ok. 20,6 MiB); bufory są używane ponownie, a ich łączny rozmiar (wysyłane + budowany) ograniczony do 192 MiB (PROWIZORYCZNIE, RD-75) — przy dużych packach (`packTargetSize`) usługa wysyła mniej plików naraz, zawsze co najmniej jeden |

Zmiana zmiennej wymaga restartu usługi.

## 6. Dziennik audytu
Każda operacja modyfikująca przez API (zadania, harmonogram, odtwarzanie, retencja, zatwierdzenie certyfikatu) oraz każda odmowa dostępu zapisuje wpis w łańcuchu audytu (D9.9). Sprawdzenie spójności:
- GUI: **Ustawienia → Sprawdź dziennik**,
- CLI: `kobian audit verify [--json]` → `ok: <n> audit entries, chain intact` albo `failed <KB> seq <n>` (pierwszy uszkodzony wpis). Kod wyjścia ≠ 0 przy niespójności.

**Kotwice (it. 99, D9.9):** usługa codziennie o 06:55 (czas lokalny serwera; po przerwie — przy starcie) zapisuje kotwicę „seq:sha256” ostatniego wpisu. `kobian audit verify` pokazuje ostatnią kotwicę — **zapisz ją poza serwerem** (np. w notatce/zgłoszeniu); później `kobian audit verify --anchor <seq>:<sha256>` sprawdza dziennik względem niej niezależnie od katalogu (wykrywa przepisanie historii z przeliczonym łańcuchem i skrócenie dziennika). Kotwice w raporcie e-mail i w repozytorium (`journal/`, format v1.1) — później. Kody: KB-6501 łańcuch, KB-6502 schemat tabel audytu zmieniony (np. zdjęty wyzwalacz), KB-6503 niezgodność z kotwicą.

Niespójny łańcuch = możliwa ingerencja w katalog: zachowaj kopię pliku katalogu i zgłoś incydent; nie naprawiaj ręcznie.

## 7. Kody KB w tym przewodniku
KB-3009, KB-3102, KB-3501…3504, KB-5001, KB-5002, KB-5003, KB-5104, KB-5105, KB-5106, KB-5201, KB-5202, KB-6501, KB-6502, KB-6503, KB-7013 — pełne opisy: `docs/kb-codes.md`.

## Przechowywanie danych pomocniczych (it. 116) — PROWIZORYCZNIE Qb61
Dziennik wznowień odtwarzania (ścieżki docelowe i nazwy odtwarzanych plików) jest usuwany z katalogu Agenta 30 dni po zakończeniu odtwarzania; odtwarzania w toku nie są ruszane. Wysłane i niedoręczone powiadomienia są usuwane po 30 dniach. Historia uruchomień (bez nazw plików) i dziennik audytu zostają.
