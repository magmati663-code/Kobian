# ADR-0021 — Zatwierdzanie certyfikatu TLS serwera SQL w Agencie (BL-40 cz. 5, it. 74)

Status: PROWIZORYCZNIE (Qb32). Realizuje warunek pinu z ADR-0019/Qb30 (plik `.cer` w `<stan>/certs`), R2.5.7 (KB-3004), SEC-5.6, D4.11 (analogia: pinning klucza hosta SFTP).

## Decyzja
- **TOFU z jawnym potwierdzeniem Operatora**: RPC `FetchSqlCertificate` (Operator, niemodyfikujące) zwraca odcisk SHA-256 certyfikatu liścia + podmiot/wystawcę/ważność/„samopodpisany”/„zaufany w systemie”;
  RPC `ApproveSqlCertificate` (Operator, modyfikujące — kontrola rewizji) przyjmuje **odcisk i nazwę**, a nie bajty certyfikatu: Agent pobiera certyfikat ponownie, porównuje stałoczasowo
  (`CryptographicOperations.FixedTimeEquals`) i zapisuje DER do `<stan>/certs/<nazwa>.cer` (tmp + `Move` bez nadpisania). Zmiana → KB-3007; nazwa zajęta inną treścią → KB-3008; ten sam certyfikat = idempotentnie.
- **Pobranie bez logowania**: TDS 7.x — PRELOGIN (`ENCRYPTION=ON`), handshake TLS w pakietach PRELOGIN (MS-TDS 2.2.6.5); TDS 8 — TLS od pierwszego bajtu z ALPN `tds/8.0`;
  callback walidacji zapamiętuje certyfikat i zawsze zwraca `false` (LOGIN7 nigdy nie jest wysyłany). Instancja nazwana → SSRP (UDP 1434). Limit 15 s; brak TLS → KB-3006.
- Własna implementacja PRELOGIN (~350 linii, bez nowych pakietów) zamiast `SqlConnection` z callbackiem: SqlClient 7.1 nie udostępnia certyfikatu serwera przed logowaniem.
- Rola Operator (jak ProbeDestination/SFTP UNKNOWN_IDENTITY); step-up Administratora dla zaufania — nie w tej iteracji.

## Alternatywy
- (a) Odcisk SHA-256 w definicji zadania zamiast pliku `.cer` (Qb29 alt.): prostsze, ale `SqlConnectionStringBuilder.ServerCertificate` przyjmuje plik — wymagałoby własnej walidacji w SqlClient; odroczone.
- (b) Klient przesyła certyfikat (np. z pliku admina): Agent ufałby danym z UI — odrzucone (klient nie wskazuje Agentowi zaufania).
- (c) Tylko łańcuch systemowy (bez pinu): typowe instalacje SQL Express mają certyfikat samopodpisany → kopia niemożliwa bez `TrustServerCertificate` — odrzucone (SEC-5.6).
- (d) Zatwierdzenie wymaga Administratora (step-up): bezpieczniejsze przy wielu operatorach; do decyzji właściciela (Qb32).

## Konsekwencje
+ Kreator S1-db: „Pobierz certyfikat serwera” → odcisk do porównania → „Zatwierdź odcisk” wypełnia pole `.cer`; discovery i zadanie używają pinu.
+ Brak nowych zależności; testy z serwerem zastępczym TDS/TLS na pętli zwrotnej (TDS 7.x, TDS 8 z ALPN, SSRP, brak TLS, usługa nie-TDS, limit czasu).
− TOFU: atakujący w sieci w chwili pierwszego pobrania może podsunąć swój certyfikat — mitygacja: porównanie odcisku z administratorem SQL (tekst w UI i przewodniku), KB-3007 przy zmianie między pobraniem a zatwierdzeniem.
− Zgodność z prawdziwym SQL Server (TDS 7.4/8, instancja nazwana, TLS 1.3) niezweryfikowana — W-28.
Koszt zmiany zdania: mały — `MethodPolicy` (rola), `ServerCertificateService` (format pinu); proto tylko dodawane.
