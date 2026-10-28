# Kopia bazy SQL Server — przewodnik administratora

> Stan budowy (it. 74, przed wydaniem 0.1). Opisuje zachowanie zaimplementowane i przetestowane w Linuksie na serwerze zastępczym;
> elementy oznaczone **(wymaga Windows)** czekają na weryfikację w kolejce `budowa/do-uruchomienia-na-windows.md`. English: `kopia-bazy-sql.en.md`.

## 1. Co robi Kobian
- Łączy się z SQL Server na koncie usługi Agenta (domyślnie `NT SERVICE\KobianAgent`) albo loginem SQL z magazynu sekretów Agenta.
- Wykonuje `BACKUP DATABASE … TO DISK … WITH COPY_ONLY, CHECKSUM` do katalogu tymczasowego (staging), sprawdza zestaw w `msdb` i `RESTORE VERIFYONLY`, szyfruje i wysyła plik do repozytorium, a staging zawsze usuwa.
- `COPY_ONLY` nie przerywa łańcucha kopii prowadzonego przez inne narzędzia (Maintenance Plan, agent ERP).
- Obsługiwane: SQL Server 2017–2025; 2014/2016 warunkowo (ostrzeżenie KB-3401); starsze — odmowa.

## 2. Przygotowanie serwera SQL
| Krok | Kto | Jak |
|---|---|---|
| Szyfrowanie TLS | administrator SQL | SQL Server Configuration Manager → Protokoły → Certyfikat; „Wymuś szyfrowanie” zalecane. Bez TLS kreator zgłosi KB-3006. |
| Uprawnienia | administrator SQL | skrypt z kreatora („Pokaż skrypt uprawnień”, wariant minimalny D5.7): login konta usługi + `db_backupoperator` w wybranych bazach. Kreator nie wykonuje go sam. |
| Staging | administrator systemu | katalog z miejscem na pełną kopię największej bazy; zmienna `KOBIAN_DB_STAGING` (domyślnie `<stan>\staging`), `KOBIAN_DB_SQL_STAGING`, gdy SQL Server widzi ten katalog pod inną ścieżką (np. udział). ACL: tylko Agent i konto usługi SQL **(wymaga Windows)**. |
| Login SQL (opcjonalnie) | administrator systemu | plik `<sekrety>\<nazwa>.json` z `{"backup":{"user":"…","password":"…"}}`, uprawnienia tylko dla konta Agenta; w kreatorze podaje się wyłącznie `<nazwa>`. |

## 3. Kreator — krok „Baza SQL Server”
1. Wpisz serwer: `SERWER`, `SERWER\INSTANCJA` (port z SQL Browser) albo `serwer,1433`. Prefiksy `np:`/`admin:` są odrzucane.
2. Wybierz logowanie: konto usługi albo login SQL (nazwa poświadczenia).
3. **Certyfikat serwera** — kliknij „Pobierz certyfikat serwera”. Agent odczyta certyfikat z handshake TLS bez logowania i pokaże:
   - odcisk SHA-256 w grupach po 4 znaki,
   - podmiot, wystawcę, datę ważności, „samopodpisany” albo „zaufany w systemie — zatwierdzenie opcjonalne”.
4. Porównaj odcisk z administratorem SQL (na serwerze SQL w PowerShell: `$c = Get-Item Cert:\LocalMachine\My\<odcisk SHA-1>; [BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash($c.RawData)) -replace '-'` — okno certyfikatu w `certlm.msc` pokazuje tylko odcisk SHA-1). **Zatwierdź tylko zgodny.** Agent pobierze certyfikat jeszcze raz, porówna i zapisze go w `<stan>\certs`; nazwa pliku pojawi się w polu certyfikatu.
5. „Pokaż bazy” — lista baz z rozmiarem, modelem odzyskiwania i ostrzeżeniami (TDE bez kopii certyfikatu, rosnący dziennik, brak uprawnień). Baza bez prawa kopii blokuje „Dalej” i rozwija skrypt uprawnień.

## 4. Komunikaty
| Kod | Znaczenie | Co zrobić |
|---|---|---|
| KB-3001 | brak połączenia / limit czasu | usługa SQL, zapora (TCP 1433 lub port instancji, UDP 1434 dla SQL Browser) |
| KB-3003 | brak BACKUP DATABASE | skrypt uprawnień z kreatora |
| KB-3004 | certyfikat niezgodny z zatwierdzonym | certyfikat na serwerze zmieniono albo ktoś podszywa się pod serwer — sprawdź z administratorem SQL, zatwierdź nowy pod nową nazwą |
| KB-3006 | serwer nie przedstawił certyfikatu TLS | włącz szyfrowanie na serwerze; sprawdź, czy adres wskazuje SQL Server |
| KB-3007 | certyfikat zmienił się między pokazaniem a zatwierdzeniem | pobierz ponownie i porównaj; powtarzające się zmiany = podejrzenie ataku |
| KB-3008 | nazwa pliku certyfikatu zajęta | zatwierdź pod nową nazwą (proponowana zawiera fragment odcisku) |
| KB-3401 | wersja warunkowo wspierana / niewspierana | aktualizacja SQL Server |

## 5. Odnowienie certyfikatu serwera SQL
Po wymianie certyfikatu kopie zgłoszą KB-3004 (zadanie nie łączy się z niezatwierdzonym serwerem). Otwórz zadanie w kreatorze, pobierz i zatwierdź nowy certyfikat (nowa nazwa pliku), zapisz zadanie. Stare pliki `.cer` w `<stan>\certs` usuwa administrator.

## 6. Bezpieczeństwo
- Kreator i CLI nie widzą haseł SQL; w definicji zadania jest tylko odwołanie `file:<nazwa>`.
- Pobranie certyfikatu nigdy nie wysyła danych logowania; zapis pinu robi Agent z certyfikatu, który sam odebrał (klient przesyła tylko odcisk do porównania).
- Nazwy baz i serwerów nie trafiają do logów ani zdarzeń (NFR-11).
- NTLM: przy logowaniu kontem usługi do serwera spoza domeny zaufanej rozważ ograniczenie listy serwerów (RD-39, w przygotowaniu).

## Serwer SQL na innym komputerze niż usługa Kobian
Domyślnie usługa łączy się tylko z SQL Server na tym samym komputerze. Aby kopiować bazę z innego serwera, administrator ustawia zmienną środowiskową usługi `KOBIAN_SQL_ALLOWED_SERVERS` (np. `sql01;sql02\ERP`) i restartuje usługę. Bez tego kreator pokaże KB-3009.
