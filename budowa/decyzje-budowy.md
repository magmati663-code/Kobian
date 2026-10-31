# Decyzje właściciela — budowa

Wpisz odpowiedź po „DECYZJA:” i zapisz plik. „odłóż” zostawia pytanie otwarte.

Qb1: S6 — co dokładnie znaczy „rozwiń do 1t iteracji” (np. 1 iteracja na tydzień? długość rundy zamiast 14? większy limit zmian na iterację?). Do czasu odpowiedzi stosuję domyślne: co 1 h, 14 iteracji w rundzie, ≤ 800 linii. runda = 15 iteracji (właściciel). Wymaga zmiany ROUND w .budowa_tool.py i promptu (SHA); do tego czasu 14.
Qb2: S5(c) Docker i S5(d) git/usuwanie nie były w odpowiedzi — traktuję jako „nie”. Czy włączyć? → DECYZJA: usuwanie plików tak, tylko w folderze roboczym „kobian backup”; git i Docker — bez odpowiedzi (nadal nie).

Qb3: S5(d) git — bez git historia zmian kodu to tylko log.md i kopie; czy zezwolić na `git init` w folderze „kobian backup” (lokalne repo, bez zdalnych)? → DECYZJA:
Qb4: S5(c) Docker — bez niego testy SFTP/FTP/SMB/SQL Server idą in-proc/fake'ami; czy masz do tego inne środowisko (serwer SFTP, SQL Express), z którego mogę korzystać? → DECYZJA:
Qb5: Lista popularnych haseł (SEC-6.5) — plan wymaga weryfikacji licencji źródła (np. SecLists). Opcje: A — sprawdzę licencję i dołączę listę top 100 000 (rekomendowane, po audycie), B — zostaje krótka lista własna, C — inne źródło (jakie?). → DECYZJA:
Qb6: Doprecyzowania formatu wynikłe z implementacji (docs/format-spec/pack-v1.md): offset w trailerze wskazuje pole frameLength; CRC klucza odzyskiwania = CRC-16/CCITT-FALSE big-endian. Czy zatwierdzasz jako część specyfikacji formatu v1 (nieodwracalne po pierwszym wydaniu)? [tak / zmień] → DECYZJA:

## Pytania po rundzie 1 (korekta kursu)
Qb7: Przebiegi zaplanowane (co godzinę) działają na Twoim komputerze bez .NET SDK (instalacja SDK do folderu na dysku Windows trwała zbyt długo, a w środowisku Linux maszyny jest ~100 MB wolnego). Opcje: A — zainstaluj .NET SDK 10 na Windows (pomoże w kolejce testów Windows, ale nie w przebiegach zaplanowanych, które działają w VM Linux), B — przebiegi zaplanowane robią tylko zadania dokumentacyjne/analityczne, a kod powstaje w sesjach ręcznych (rekomendowane przy obecnych ograniczeniach), C — zwiększ miejsce dla VM Linux (jeśli możliwe), wtedy SDK lokalnie. → DECYZJA:
Qb8: Ścieżka krytyczna rundy 2 — proponuję: katalog SQLite (D2.11/D2.14), potok backupu plików end-to-end na dostawcy local (D3.14, D3.15) z restore (D8.1) i weryfikacją V1/V2, następnie CLI (US-37). Czy zatwierdzasz tę kolejność zamiast rotacji „po równo” dla wszystkich ról? [tak / inna kolejność] → DECYZJA:
Qb9: Uruchom proszę `tools\windows\run-queue.ps1` na Windows (wymaga .NET SDK 10.0.401) i zostaw wynik w `budowa\` — to zweryfikuje testy NTFS/ICU (W-1, W-4). → DECYZJA (wykonane / później):

## Pytania po rundzie 2
Qb10: Runda 3 — czy zgadzasz się, by iteracje 31–39 (sesja ręczna z SDK w chmurze) realizowały pionowy przekrój: kodeki indeksu/snapshotu → potok backupu na dostawcy local (D3.14) → restore → CLI, zamiast zadań „po równo” dla każdej roli? [tak / nie] → DECYZJA:
Qb11: ADR-0002 — `kobian-restore` używa tymczasowo jawnego pliku klucza deweloperskiego (tylko dane syntetyczne) do czasu BL-24 (hasło + PBKDF2 + opakowanie klucza). Akceptujesz to odstępstwo od D3.11 na czas budowy, a BL-24 jako priorytet roli 3 w rundzie 3? [tak / nie — najpierw BL-24] → DECYZJA:

Qb12 (PROWIZORYCZNIE, ADR-0003): kobian-restore z plikiem klucza hasła wymaga jawnego `--repo-id` i `--password-file` do czasu czytnika `kobian.repo`. Akceptujesz, czy wolisz prompt interaktywny jako domyślny? [akceptuję / prompt] → DECYZJA:

## Pytania w rundzie 4
Qb13 (PROWIZORYCZNIE, ADR-0005): definicja zadania „files” w katalogu (job_jobs.definition, JSON v1) wskazuje klucz ścieżkami plików (key.file / passwordFile / repoHeader), jedno źródło i cel „local”. Do czasu inicjalizacji repozytorium w Agencie (it. 48) i magazynu poświadczeń (DPAPI, Windows). Akceptujesz, czy wolisz od razu SecretRef do magazynu poświadczeń? [akceptuję / SecretRef] → DECYZJA:

Qb14 (PROWIZORYCZNIE, it. 49): `kobian init` przyjmuje hasło wyłącznie z pliku (`--password-file`), bez argumentu i zmiennej środowiskowej (argumenty widać w liście procesów); Agent trzyma lokalne kopie nagłówka i klucza w `KOBIAN_STATE_DIR` (domyślnie katalog z catalog.db). Alternatywa: prompt interaktywny w CLI i przekazanie hasła kanałem pipe (większy koszt, wymaga trybu TTY). Koszt zmiany zdania: mały (zmiana CLI + pole w InitJobArgs). DECYZJA:

Qb15: Pinning klucza hosta SFTP — PROWIZORYCZNIE „pin wymagany, brak TOFU” (ADR-0006). Zostawić czy dopuścić TOFU z zatwierdzeniem w UI? → DECYZJA:

Qb16 (PROWIZORYCZNIE, it. 52, ADR-0007): poświadczenia SFTP w Agencie z plików JSON w katalogu sekretów (`SecretRef` `file:<nazwa>`, uprawnienia tylko właściciela) do czasu magazynu DPAPI na Windows. Akceptujesz, czy od razu wymagać DPAPI (`dpapi:`)? Koszt zmiany zdania: mały. DECYZJA:

## Runda 4 — UI (it. 57)
Polecenie właściciela (sesja 2026-10-14, na żywo): „Po wykonaniu obecnego zadania skup się na rozwoju UI i designie. Iteruj 5 razy.” — wykonane w it. 57 (5 rund: zrzut → krytyka → poprawka). Kolejne przebiegi: priorytet UI (patrz nastepne-kroki.md), chyba że właściciel zdecyduje inaczej.

Qb17 (PROWIZORYCZNIE, it. 57, ADR-0008): tolerancja zaległości kopii na pulpicie = max(1 h, 25% cyklu) ponad jeden cykl harmonogramu (6.3 „> 1 cyklu + tolerancja”); próg miejsca < 15% (z planu). Koszt zmiany zdania: mały (stała w ProtectionEvaluator). DECYZJA:

Qb18 (PROWIZORYCZNIE, it. 57, ADR-0008): pulpit UI czyta Agenta przez istniejące RPC (GetStatus + ListSnapshots). Do czasu rozszerzenia kontraktu (BL-60): brak następnego uruchomienia i kontroli zaległości, poziom V2 wyprowadzony z wyniku uruchomienia, rola wywołującego = Operator w UI (autoryzację egzekwuje Agent). Akceptujesz kolejność „najpierw rozszerzyć kontrakt (BL-60), potem kolejne ekrany”? [tak / najpierw ekrany] → DECYZJA:

Qb19 [it. 64: pkt „historia tylko udane” zrealizowany — ListRuns w SCR-10] (PROWIZORYCZNIE, it. 58, ADR-0009): ekrany SCR-02 (zadania) i SCR-10 (historia) na istniejących RPC: historia pokazuje tylko udane kopie (ListSnapshots) do czasu RPC ListRuns (BL-60); „Wstrzymaj” wyłącza harmonogram z zachowaniem wyrażenia, „Wznów” przywraca je; cykl do kontroli zaległości zgadywany z typowych wzorców cron. Akceptujesz, czy historia ma od razu pokazywać nieudane uruchomienia (najpierw ListRuns)? Koszt zmiany zdania: mały. DECYZJA:

Qb20 (PROWIZORYCZNIE, it. 59, ADR-0010): kreator SCR-03 zbudowany na porcie usługi; adapter do Agenta dopiero po rozszerzeniu kontraktu (UpsertJob validate_only, ProbeDestination, InitRepository z kluczem odzyskiwania, hasło w wiadomości po uwierzytelnionym pipe zamiast pliku). Presety: codziennie 21:00; retencja 7 dziennych / 4 tygodniowe / 12 miesięcznych. Akceptujesz presety i kolejność (najpierw kontrakt BL-60, potem adapter)? Koszt zmiany zdania: mały. DECYZJA:

## Koniec rundy 4 (it. 60)
Qb21 (korekta kursu, PROWIZORYCZNIE): runda 5 = (1) BL-60 rozszerzenie kontraktu gRPC (ListRuns, JobStatus z następnym uruchomieniem/cyklem/verify_level/celem, UpsertJob validate_only, ProbeDestination), (2) adapter kreatora SCR-03 do Agenta — UI działa end-to-end, (3) retencja GFS + prune (BL-25), (4) RD-22 limit RAM dedup, (5) MSSQL (BL-40…). Priorytet UI z it. 57 utrzymany jako „UI na prawdziwym Agencie”, nie nowe ekrany na atrapach. Alternatywa: kolejne ekrany (SCR-06 odtwarzanie, ustawienia) przed kontraktem. Koszt zmiany zdania: mały (kolejność). DECYZJA:

Qb22 (PROWIZORYCZNIE, it. 61, ADR-0011): rozszerzenie GetStatus (BL-60) — (a) zadania wyłączone są zwracane z `disabled=true` i nie wpływają na stan ogólny; (b) najnowsza kopia oznaczona `suspect` ustawia stan ogólny Agenta na Failed (6.3: uszkodzone repozytorium = czerwony), także gdy ostatnie uruchomienie się udało; (c) cel w API wyłącznie jako nazwa schematu (bez ścieżki/hosta, NFR-11) — nazwa celu na pulpicie = schemat do czasu nazwanych celów; (d) cykl = odstęp dwóch najbliższych wystąpień cron. Alternatywa: (b) tylko ostrzeżenie; (c) zwracać nazwę celu nadaną przez użytkownika (wymaga pola w definicji). Koszt zmiany zdania: mały. DECYZJA:

Qb23 (PROWIZORYCZNIE, it. 62, ADR-0012): kreator ↔ Agent — (a) strefa czasowa nowego zadania = strefa systemu Agenta (preset „21:00” = czas lokalny serwera); (b) UpsertJob validate_only=false zwraca KB-7012 — nowe zadanie powstaje razem z repozytorium (InitRepository, it. 63); (c) ProbeDestination wymaga roli Operator (łączy się z celem na koncie usługi), ale nie podlega kontroli rewizji klienta (niczego nie zmienia); (d) SFTP bez przypiętego klucza: sonda „identity_probe” bez poświadczeń, klucz nigdy nie zaufany — użytkownik zatwierdza odcisk. Alternatywa (a): strefa z UI; (c): Reader. Koszt zmiany zdania: mały. DECYZJA:

Qb24 (PROWIZORYCZNIE, it. 63, ADR-0013): kreator ↔ Agent — (a) hasło repozytorium w wiadomości InitRepository po uwierzytelnionym pipe; Agent trzyma je w `<stan>/secrets/repo-<repoId>.pw` (0600; Windows: ACL usługi + DPAPI — W-21) do uruchomień bez użytkownika; (b) klucz odzyskiwania tworzony zawsze z kreatora i zwracany jednorazowo; (c) zadanie powstaje wyłączone, „Zapisz” je włącza (porzucony kreator nie uruchamia kopii; repozytorium zostaje na celu — sprzątanie ręczne); (d) poświadczenie SFTP z kreatora = `file:<użytkownik>` (plik sekretu zakłada administrator); (e) retencja z S5 nie jest jeszcze zapisywana (BL-25). Alternatywy: (a) DPAPI od razu (tylko Windows), (c) zadanie od razu włączone. Koszt zmiany zdania: mały–średni (a). DECYZJA:

Qb25 (PROWIZORYCZNIE, it. 65, ADR-0014): retencja — (a) polityka w definicji zadania v1 (`retention`), brak sekcji = nic nie jest zapominane; (b) `forget` tylko logiczny (katalog), fizyczne usuwanie wyłącznie przez prune dwufazowy z poświadczeniem Maintenance; (c) retencja uruchamiana ręcznie (`kobian forget`) — automatyczne uruchamianie po udanej kopii dopiero po prune i wykrywaniu skoku zegara (SEC-17.4); (d) kopie podejrzane (suspect) nigdy nie są zapominane automatycznie. Alternatywa (c): forget po każdym backupie od razu. Koszt zmiany zdania: mały. DECYZJA:

Qb26 (PROWIZORYCZNIE, it. 67, ADR-0015): prune — (a) pack/indeks żywy, gdy wymienia go którykolwiek indeks żywego snapshotu (osiągalność przez indeksy, nie przez drzewa) — bezpieczne dla deduplikacji, ale przyrosty bez kopii pełnej nie odzyskują miejsca; (b) snapshot nieznany katalogowi = żywy; (c) karencja 24 h od oznaczenia; (d) w tej wersji tylko `kobian prune --dry-run` — fizyczne usuwanie w kolejnych iteracjach. Alternatywy (a): B — żywość przez drzewa + przepisywanie indeksów (D3.16) + wykluczanie oznaczonych packów z deduplikacji; C — okresowa kopia pełna co N przyrostów. Rekomendacja: A teraz + C jako opcja zadania; B po 1.0. Koszt zmiany zdania: średni. DECYZJA:

Qb27 (PROWIZORYCZNIE, it. 68, ADR-0016): blokady i prune wykonawczy — (a) blokada kopii nie jest zwalniana jawnie (sesja Backup nie usuwa, D4.1) i wygasa 30 min po kopii; prune przy żywej blokadzie kończy się „pominięty” (KB-1202) bez czekania; (b) prune wymaga osobnego poświadczenia Maintenance (dla SFTP wpis „maintenance” w magazynie sekretów); (c) pole `magic: "kobian-lock"` w pliku blokady. Alternatywy (a): plik zwolnienia po kopii (okno prune bez 30 min przerwy) albo prune czekający w kolejce. Rekomendacja: A teraz, plik zwolnienia przed M1, jeśli harmonogramy kopii są częstsze niż co godzinę. Koszt zmiany zdania: mały. DECYZJA:

Qb28 (PROWIZORYCZNIE, it. 69, ADR-0017): retencja automatyczna i skok zegara — (a) po każdej udanej kopii (V2 OK) zadania z polityką Agent sam wykonuje logiczne zapomnienie (przyczyna `dependency`); prune (fizyczne usuwanie) nadal ręcznie; (b) skok zegara spełniający A17 (> 24 h w przód lub > 2 min w tył) wstrzymuje retencję całego hosta do potwierdzenia przez Administratora (`kobian retention release --confirm`); (c) kopie działają normalnie w czasie wstrzymania. Alternatywy: (a) tylko ręcznie; (b) wstrzymanie per zadanie albo automatyczne zwolnienie po N dobach. Koszt zmiany zdania: mały (`KOBIAN_AUTO_RETENTION=0`). DECYZJA:

Qb29 (PROWIZORYCZNIE, it. 71, ADR-0018): MSSQL w Agencie — (a) pin certyfikatu SQL = plik .cer (`ServerCertificate`), bez pinu walidacja łańcucha systemowego; `TrustServerCertificate` wyłącznie dla localhost po jawnej zgodzie; (b) login SQL i hasło w magazynie sekretów Agenta (`file:<nazwa>`, jak SFTP — Qb16), domyślnie uwierzytelnienie Windows konta usługi; (c) zadanie typu `database` (DDL planu) z sekcją `database` (silnik `mssql`), snapshot w trybie Database z deduplikacją względem poprzedniej kopii; (d) kolizja z inną operacją (KB-3101) kończy uruchomienie jako Failed (nie Skipped); (e) ostrzeżenia przygotowania (np. SQL 2014/2016 → KB-3401) dają wynik „z ostrzeżeniem”. Alternatywy: (a) odcisk SHA-256 w definicji (wymaga własnej walidacji TLS — odradzane); (d) Skipped. Koszt zmiany zdania: mały. DECYZJA:

Qb30 (PROWIZORYCZNIE, it. 72, ADR-0019): discovery MSSQL dla kreatora S1-db — (a) RPC DiscoverDatabases wymaga roli Operator (łączy się z serwerem SQL na koncie usługi), (b) pin certyfikatu w żądaniu = NAZWA pliku .cer w <stan>/certs (nie ścieżka), (c) skrypt uprawnień tylko dla uwierzytelnienia Windows, (d) brak listy dozwolonych serwerów SQL (ryzyko Średnie: przekazanie NTLM konta usługi do obcego hosta — propozycja RD-39). Alternatywa: (b) odcisk SHA-256 zamiast pliku; (d) lista dozwolonych serwerów od razu (koszt ok. 2 osobodni, ogranicza scenariusz „zdalny SQL” do skonfigurowanych hostów). Koszt zmiany zdania: mały.
DECYZJA:

Qb31 (PROWIZORYCZNIE, it. 73, ADR-0020): kreator S1-db — (a) staging kopii bazy ustala Agent: `KOBIAN_DB_STAGING` (domyślnie `<stan>/staging`, na Windows `%ProgramData%\Kobian\staging`), opcjonalnie `KOBIAN_DB_SQL_STAGING`; kreator o staging nie pyta; (b) jedna baza na zadanie (definicja v1); (c) login SQL w kreatorze = nazwa poświadczenia w katalogu sekretów Agenta (`file:<nazwa>`), plik zakłada administrator. Alternatywy: (a) pole „katalog roboczy” w kreatorze (tryb zaawansowany); (b) wiele baz w zadaniu (zmiana definicji). Rekomendacja: A teraz. Koszt zmiany zdania: mały.
DECYZJA:

Qb32 (PROWIZORYCZNIE, it. 74, ADR-0021): zatwierdzanie certyfikatu TLS serwera SQL — (a) TOFU z potwierdzeniem: Agent pobiera certyfikat z handshake TLS bez logowania i pokazuje odcisk SHA-256; zatwierdzenie = Agent pobiera ponownie, porównuje i zapisuje `.cer` w <stan>/certs; (b) klient nigdy nie przesyła certyfikatu, tylko odcisk; (c) nazwa zajęta inną treścią = odmowa (KB-3008), odnowienie certyfikatu = nowa nazwa; (d) zatwierdza Operator (bez step-up Administratora). Alternatywy: (d) wymagać Administratora do zatwierdzenia (bezpieczniej przy wielu operatorach, koszt mały: wpis w MethodPolicy); (c) nadpisywanie pinu pod tą samą nazwą z potwierdzeniem (wygodniejsze odnowienie, ryzyko cichej podmiany zaufania). Rekomendacja: A teraz, (d) Administrator przed 1.0. Koszt zmiany zdania: mały.
DECYZJA:

## Koniec rundy 5 (it. 75)
Qb33 (korekta kursu, PROWIZORYCZNIE): runda 6 = (1) SCR-06 odtwarzanie w UI (pliki, później `database.bak` → RESTORE WITH MOVE), (2) RD-39 lista dozwolonych serwerów SQL (Średnie), (3) RD-42 audyt zmian zaufania (D9.9), (4) BL-62 kontrola runtime przy starcie Agenta, (5) RD-22 limit RAM dedup, (6) SMB, usługa Windows, MSI. Alternatywa: najpierw usługa Windows + MSI (szybciej do instalowalnej wersji, ale bez testów Windows po stronie pętli). Koszt zmiany zdania: mały (kolejność).
DECYZJA:

Qb34 (PROWIZORYCZNIE, it. 77, ADR-0022): SCR-07 odtwarzanie z UI — (a) filtr z przeglądarki: folder `a/b` → `a/b/*`, plik → dokładna ścieżka (nazwy z `*`/`?` działają jak wzorzec — Niskie, nic nie jest nadpisywane), (b) ścieżka z kopii zachowana w folderze docelowym, (c) bez PlanRestore i postępu na żywo do rozszerzenia kontraktu. Alternatywa: pole `exact_paths` w RestoreRequest (koszt mały) przed 1.0; odtwarzanie „spłaszczone” (bez ścieżki) jako opcja. Koszt zmiany zdania: mały.
DECYZJA:

Qb35 (PROWIZORYCZNIE, it. 78, ADR-0023): lista dozwolonych serwerów SQL — (a) konfiguracja administratora `KOBIAN_SQL_ALLOWED_SERVERS` (zmienna usługi), (b) brak konfiguracji = tylko ten komputer, (c) dopasowanie bez DNS, (d) egzekwowanie przed połączeniem w discovery, certyfikacie, zapisie i uruchomieniu zadania (KB-3009). Alternatywy: lista w katalogu edytowana RPC przez Administratora (SCR-11, step-up), domyślnie „dowolny serwer” z ostrzeżeniem. Koszt zmiany zdania: mały.
DECYZJA:

Qb36 (PROWIZORYCZNIE, it. 79): audyt API — (a) wpis dla każdej odmowy i każdej metody modyfikującej (nie dla odczytów), (b) tożsamość „local-pipe” do czasu SID z impersonacji potoku (W-30), (c) VerifyAudit dostępny dla roli Reader (zwraca tylko liczby i hash), (d) kotwice dzienne (e-mail/repo/webhook) w kolejnej iteracji. Alternatywy: audyt także odczytów (koszt: rozmiar katalogu, RODO); VerifyAudit tylko dla Administratora. Koszt zmiany zdania: mały.
DECYZJA:

Qb37 (PROWIZORYCZNIE, it. 80): (a) niezałatany .NET przy starcie Agenta = WARNING KB-7013 + zdarzenie, kopie działają dalej (alternatywa: odmowa startu — bezpieczniej dla Kestrel, ale grozi brakiem kopii); (b) RestoreRequest.exact_paths zastępuje wzorce w UI (Qb34 domknięte); UI i Agent wdrażane razem — starszy Agent zignorowałby pole i odtworzył całą kopię bez nadpisań (alternatywa: pole wersji API w GetStatus i blokada w UI). Koszt zmiany zdania: mały.
DECYZJA:

Qb38 (PROWIZORYCZNIE, it. 82): SCR-09 Cele — (a) lista celów wyprowadzana z definicji zadań (brak osobnej tabeli celów), (b) ListDestinations dla roli Operator (plan R2.6.1: Admin) — tylko odczyt ścieżek/hostów bez sekretów; tryb deweloperski Agenta ma stałą rolę Operator, (c) edycja celów i poświadczeń później (Admin + step-up). Alternatywa: Admin od razu (ekran pusty w trybie deweloperskim). Koszt zmiany zdania: mały (MethodPolicy).
DECYZJA:

Qb39 (PROWIZORYCZNIE, it. 83): odtwarzanie bazy MSSQL — (a) zawsze do NOWEJ bazy (domyślnie `<źródło>_restore_RRRRMMDD`), istniejąca → KB-3501, bez REPLACE; nadpisanie produkcji (AC-32.2, step-up Administratora i wpisanie nazwy) — później; (b) pliki do domyślnych katalogów serwera, nazwy wyłącznie z nazwy bazy docelowej; (c) FILESTREAM/full-text → KB-3503 (ręcznie w SSMS). Alternatywy: REPLACE z potwierdzeniem już teraz (ryzyko utraty danych produkcji); własne katalogi z UI. Koszt zmiany zdania: mały/średni.
DECYZJA:

Qb40 (korekta kursu rundy 7, PROWIZORYCZNIE, it. 90): runda 7 = (1) host usługi Windows + Worker (Microsoft.Extensions.Hosting.WindowsServices za portem, konto wirtualne wg planu; testy w kolejce Windows), (2) dostawca SMB (BL-3x), (3) kotwice dzienne łańcucha audytu + RD-51, (4) RD-22 warstwa 2 (filtr Blooma), (5) PlanRestore w SCR-07 (BL-64), (6) MSI. Dodatkowo BL-70: odwołanie `latest:<zadanie>` w Agencie i `--job` w CLI; samo `latest` zostaje globalne (alternatywa: odmowa samego `latest` przy wielu zadaniach — bezpieczniej, ale łamie skrypty; koszt zmiany zdania: mały). Alternatywa kursu: najpierw MSI + usługa przed SMB (szybciej instalowalna wersja). Koszt zmiany zdania: mały (kolejność).
DECYZJA:

Qb41 (it. 91, PROWIZORYCZNIE, ADR-0024): Agent jako usługa Windows — (a) konfiguracja nadal przez zmienne środowiskowe usługi, domyślny katalog `%ProgramData%\Kobian\catalog.db`; (b) ACL pipe w usłudze: Deny NETWORK, SYSTEM/Administrators/„Kobian Operators” tylko odczyt/zapis, każdy dopuszczony = Operator do czasu BL-17 (role z tokenu); (c) mutex pojedynczej instancji z domyślnym ACL (jawne ACL po W-33). Alternatywa (a): plik `agent.json` w %ProgramData%\Kobian (lepsze pod MSI). Koszt zmiany zdania: mały.
DECYZJA:

Qb42 (it. 92, PROWIZORYCZNIE, ADR-0025): cel SMB — (a) poświadczenia w tym samym pliku sekretów co SFTP (`file:<nazwa>`, role backup/maintenance, wymagane hasło) do czasu DPAPI; (b) drugie konto do tego samego serwera podczas trwającego połączenia → natychmiastowa odmowa KB-2101 (zamiast czekania w dostawcy; kolejkowanie należy do D7.4); (c) cel „smb” tylko w kompilacji Windows Agenta; (d) udziały administracyjne (C$, ADMIN$, IPC$, PRINT$) i IPv6 odrzucane jako korzeń. Alternatywa (d): dopuścić C$ dla administratorów (wygoda kosztem zasięgu ransomware). Koszt zmiany zdania: mały.
DECYZJA:

Qb43 (it. 93, PROWIZORYCZNIE, ADR-0026): cel zdalny w definicji zadania — (a) smb i sftp dozwolone w definicji v1, sekcja `destination` z `secretRef` (`file:<nazwa>`) oraz dla SFTP `host`/`port`/`hostKey`; (b) zadanie SFTP bez przypiętego klucza hosta = odmowa KB-2104 (bez TOFU), zadanie SMB/SFTP bez odwołania do poświadczenia = KB-2102 przed utworzeniem repozytorium; (c) cel nadal zapisany w definicji zadania (nie osobny wiersz `dst_destinations`) do czasu wielu zadań na jeden cel. Alternatywa: osobna tabela celów teraz (DDL + migracja). Koszt zmiany: mały.
DECYZJA:

Qb44 (it. 94, PROWIZORYCZNIE, ADR-0027): kolejka i SMB — (a) zaplanowane zadania do jednego serwera SMB z różnymi wpisami poświadczeń czekają na siebie w kolejce (zasób `smb:<serwer>`, kontem jest nazwa wpisu `secretRef`, nie użytkownik); ten sam wpis = równolegle; (b) limit pojedynczej próby połączenia SMB 30 s (D4.13), potem KB-2001 i ponowienie; (c) ręczne „Uruchom teraz” jeszcze poza kolejką (BL-74). Alternatywa (a): wszystkie zadania SMB do jednego serwera po kolei (prościej, wolniej) albo odczyt nazw kont z magazynu sekretów (dokładniej). Koszt zmiany: mały.
DECYZJA:

Qb45 (it. 95, PROWIZORYCZNIE, ADR-0028): role z tokenu klienta pipe (BL-17) — (a) grupa „Kobian Admins” dostaje wpis ACL pipe tylko odczyt/zapis (D2.9 wymienia ją w mapie ról, ale nie w ACL); (b) dopuszczony przez ACL użytkownik spoza grup Kobian i bez podniesienia UAC = rola Odczyt; (c) brak tokenu/anonimowy/sieciowy = odmowa KB-7001. Alternatywa (a): bez wpisu — rola Administrator tylko dla podniesionych administratorów lokalnych. Koszt zmiany: mały.
DECYZJA:

Qb46 (it. 96, PROWIZORYCZNIE, ADR-0029): (a) „Uruchom teraz” czeka w tej samej kolejce co harmonogram (limit 2 równoległych łącznie, zasób `smb:<serwer>`); (b) limit czekania 6 h dla wszystkich zadań (D7.10), potem pominięcie z KB-4102; (c) UI przy roli Podgląd pokazuje baner z instrukcją („Uruchom jako administrator” albo grupa „Kobian Operators”), bez samodzielnego restartu z podniesieniem i bez nowego RPC. Alternatywy: (a) ręczne z pierwszeństwem poza kolejką; (b) limit w definicji zadania; (c) przycisk restartu z podniesieniem (wymaga uruchamiania procesu przez powłokę). Koszt zmiany: mały.
DECYZJA:

Qb47 (it. 97, PROWIZORYCZNIE, ADR-0030): dławienie otwarć sesji (D4.16 pkt 3) — (a) nowy kod KB-2114 „próby wstrzymane na 10 min” zamiast KB-2003 (ten zostaje dla breakera operacji Polly z D4.10); (b) liczy się każde nieudane otwarcie, także złe hasło i probe z kreatora (ochrona konta, SEC-24.4), udane otwarcie nie zeruje okna godzinnego; (c) klucz = schemat + identyfikator celu (nie serwer), stan tylko w pamięci usługi. Alternatywy: (b) zerowanie po sukcesie / probe poza limitem; (c) klucz per serwer i konto (lepsza ochrona przy wielu celach na jednym NAS). Koszt zmiany: mały.
DECYZJA:

Qb48 (it. 98, PROWIZORYCZNIE, ADR-0031): podgląd odtworzenia (PlanRestore, SCR-07) — (a) „Odtwórz” najpierw liczy plan; przy plikach istniejących w folderze docelowym zatrzymuje się i pokazuje „Odtwórz mimo to” (pliki istniejące zawsze pomijane, nigdy nadpisywane); (b) prawo zapisu sprawdzane plikiem próbnym na koncie USŁUGI (kontrola tokenem użytkownika — SEC-6.3 — później, W-40); (c) za mało miejsca = blokada KB-5106 bez zapasu ponad rozmiar plików. Alternatywa (a): osobny przycisk „Sprawdź”; (c): zapas 5%.
DECYZJA:

Qb49 (it. 99, PROWIZORYCZNIE, ADR-0032): kotwice audytu (D9.9) — (a) kotwica dzienna 06:55 czasu lokalnego zapisywana na razie tylko w katalogu (`audit_anchors`); kopię poza serwerem robi administrator, przepisując „seq:sha256” z `kobian audit verify` (sprawdzenie: `--anchor`); (b) kotwice w e-mailu/webhooku po BL-45, w repozytorium (`journal/`) w formacie v1.1; (c) zmieniony schemat tabel audytu (np. zdjęty wyzwalacz) = osobny kod KB-6502 z pierwszeństwem przed KB-6501. Alternatywa (a): od razu plik `audit-anchors.jsonl` w osobnym katalogu z innym ACL.
DECYZJA:

Qb50 (it. 100, PROWIZORYCZNIE, ADR-0033): szkielet MSI — (a) WiX v7 (WixToolset.Sdk 7.0.0) zgodnie z D10.2, projekt poza solucją, budowany dopiero na Windows po Twojej decyzji Q37 (opłata OSMF); (b) konfigurację systemu (grupy „Kobian Operators”/„Kobian Admins”, ACL %ProgramData%\Kobian, VssAccessControl, prawa LSA) wykonuje sam Agent w trybie `--provision` uruchamianym przez MSI jako LocalSystem; (c) odinstalowanie zostawia dane (katalog, klucze, sekrety) i grupy. Alternatywa (a): inny generator MSI bez opłaty; (c): opcja „usuń dane” z ostrzeżeniem.
DECYZJA:

Qb51 (it. 101, PROWIZORYCZNIE, uzupełnia ADR-0031): kontrola folderu docelowego odtwarzania (SEC-6.3) — (a) Operator: wymagane prawo dodawania plików i folderów dla JEGO konta (AccessCheck tokenem klienta) w folderze docelowym; sprawdzany tylko folder docelowy, nie każdy podfolder (odtworzone pliki dziedziczą jego ACL, właścicielem jest konto usługi); (b) Administrator — bez kontroli (może odtwarzać w dowolne miejsce); (c) usługa musi móc odczytać ACL folderu (inaczej odmowa KB-5001). Zastępuje Qb48 (b). Alternatywa (b): Administrator też sprawdzany; (a): właścicielem plików ustawiany klient.
DECYZJA:

Qb52 (it. 102, PROWIZORYCZNIE, ADR-0034): pamięć dedup (D3.15, RD-22) — (a) bloki z poprzednich kopii trzymane jako posortowana tablica 32 B/blok (zmierzone ~2,3–3,3× mniej pamięci niż dotąd, wyszukiwanie ~1 mln/s zamiast ~10 mln/s — wystarczające); (b) filtr Blooma w pamięci domyślnie wyłączony (pomiar: pomaga tylko nowym blokom), przewidziany przed zapytaniem do katalogu w warstwie 3. Alternatywa (a): tablica mieszająca wg planu (szybsza, ~1,5× więcej pamięci).
DECYZJA:

Qb53 (it. 103, PROWIZORYCZNIE, ADR-0035): proces pomocniczy VSS (D2.8) — (a) protokół: ramki ≤ 1 MiB z JSON, tylko 3 operacje (ping, utwórz migawkę, zwolnij), jedna migawka na proces; (b) limit odpowiedzi 10 min, po każdym błędzie protokołu proces jest kończony; (c) do czasu spike'a SP-01 (wybór biblioteki VSS: CsWin32 lub AlphaVSS — audyt pakietu) Worker zwraca KB-1406 „migawka niedostępna”. Alternatywa (c): od razu AlphaVSS (plan B SP-01).
DECYZJA:

Qb54 (korekta kursu rundy 8, PROWIZORYCZNIE, it. 105): runda 8 = (1) launcher procesu Worker (job object z zabiciem przy zamknięciu, limit pamięci, brak dziedziczenia uchwytów, weryfikacja podpisu, KB-7008), (2) dziennik wznowień odtwarzania `restore_*` (D8.8) i postęp w SCR-07/CLI, (3) `check --repair`, (4) powiadomienia e-mail/webhook z kotwicą audytu w raporcie dziennym (wymaga audytu MailKit), (5) strumieniowe ładowanie indeksów bazowych (pamięć przyrostu), (6) AIMD równoległości celu (D4.16 pkt 2). Alternatywa: najpierw powiadomienia (widoczność awarii u klienta) przed Workerem. Koszt zmiany zdania: mały (kolejność). Przypomnienie: Q37 (WiX) i kolejka testów Windows W-33…W-44 czekają na Ciebie.
DECYZJA:

Qb55 (it. 106, PROWIZORYCZNIE, ADR-0036): uruchamianie procesu Worker — (a) usługa uruchamia Kobian.Worker.exe tylko z ważnym podpisem Authenticode (bez sprawdzania odwołań certyfikatu przez Internet), tryb konsoli bez weryfikacji; (b) Worker dostaje wyłącznie minimalne zmienne środowiskowe i PATH systemowy, działa w obiekcie zadania (śmierć usługi = śmierć Workera, limit 150 MB); (c) odmowa przez WDAC/AppLocker/ASR = KB-7008 z instrukcją reguły wydawcy. Alternatywa (a): zgodność wydawcy z podpisem usługi od razu (RD-67) albo skrót pliku z manifestu instalatora.
DECYZJA:

Qb56 (it. 107, PROWIZORYCZNIE): wznawianie odtwarzania (D8.8) — (a) automatyczne: ponowne „Odtwórz” tej samej kopii, tego samego wyboru do tego samego folderu przejmuje przerwaną próbę (bez osobnego przycisku „Wznów”); (b) pliki zapisywane są pod nazwą roboczą `*.kobian-partial` i przenoszone pod właściwą dopiero po sprawdzeniu sumy; (c) plik docelowy z identyczną treścią po przerwaniu = odtworzony, z inną treścią = pominięty (nigdy nadpisany); (d) w dzienniku zapisywany jest na razie identyfikator „agent” zamiast SID użytkownika. Alternatywa (a): jawne „Wznów”/`--resume`.
DECYZJA:

Qb57 (it. 109, PROWIZORYCZNIE): `check --repair` (D8.10) — (a) na razie wyłącznie ocena zakresu strat (które snapshoty i ile plików) zapisana w katalogu + wymuszenie kopii pełnej; repozytorium bez zmian; (b) tylko Administrator; (c) odbudowa indeksu z packów-sierot i kopiowanie bloków z repliki — w kolejnych iteracjach. Alternatywa (a): od razu tag „damaged” jako nowy obiekt snapshotu w repozytorium (zmiana formatu).
DECYZJA:

Qb58 (it. 111, PROWIZORYCZNIE): powiadomienia webhook (D9.10) — (a) jeden odbiorca na Agenta, z `KOBIAN_WEBHOOK_URL` + `<sekrety>/webhook.secret` (bez UI ustawień do czasu BL-45 cz. 2); (b) tylko zdarzenie run.finished.v1 (wszystkie wyniki, bez filtra „tylko błędy”); (c) ponowienia 1 min/5 min/30 min/2 h/6 h, potem dead; 410 = koniec bez ponowień; (d) zła konfiguracja zatrzymuje start usługi (kod 64), zamiast cichego wyłączenia; (e) e-mail osobno, po audycie MailKit. Alternatywa (b): domyślnie tylko Failed/Warning.
DECYZJA:

Qb59 (it. 112, PROWIZORYCZNIE): raport dzienny (D9.11) — (a) tylko przez webhook (e-mail po audycie MailKit); (b) treść: liczniki wyników doby, do 20 identyfikatorów zadań z błędem (bez nazw, także poza trybem minimalnym), liczba martwych powiadomień, ostatnia kotwica audytu `seq:sha256` (RD-61); (c) okno = 24 h przed terminem 07:00 czasu lokalnego; (d) wpis dead → audyt KB-6301 (alert w UI/e-mail później). Alternatywa (b): nazwy zadań w raporcie, gdy nie tryb minimalny.
DECYZJA:

Qb60 (it. 114, PROWIZORYCZNIE): AIMD równoległości celu (D4.16 pkt 2) — (a) sygnały KB-2112 w ciągu 1 s od ostatniego zmniejszenia liczą się jako jeden (fala odmów z jednej serii nie zbija od razu do 1); (b) wyuczony limit w nowej tabeli `repo_dest_parallelism` (plan: `dest_targets.parallelism` — tabela celów jeszcze nie istnieje), wygasa po 7 dniach, wpis z przyszłości ignorowany; (c) bramka gotowa, ale potok wysyła dziś sekwencyjnie (1 operacja naraz) — AIMD zadziała, gdy pojawią się równoległe wysyłki (RD-73). Alternatywa (a): każdy sygnał ×0,5 (dosłownie wg planu).
DECYZJA:

Qb61 (it. 116, PROWIZORYCZNIE): sprzątanie katalogu (RD-69) — (a) dziennik restore_* usuwany 30 dni po zakończeniu uruchomienia (także sesje Failed — wznowienie po miesiącu traktujemy jako nowe odtwarzanie); (b) core_outbox sent/dead po 30 dniach; (c) okres nie jest konfigurowalny do czasu ustawień polityki (SCR-11); (d) run_runs i audyt bez limitu. Alternatywa (a): 7 dni.
DECYZJA:

Qb62 (it. 118, PROWIZORYCZNIE): równoległe wysyłki packów (RD-73) — (a) w silniku opcjonalne (`BackupPipeline.PackUploaders` — każdy uploader z WŁASNĄ sesją, bo sesja jest jednowątkowa); bez nich zachowanie jak dotąd (sekwencyjnie); (b) Agent jeszcze NIE otwiera dodatkowych sesji — podłączenie (sesje przez SessionOpenGovernor, limit z CatalogParallelismStore, zapis wyuczonego limitu po uruchomieniu) w kolejnej iteracji; (c) pierwszy błąd wysyłki kończy uruchomienie (Failed, bez snapshotu) — bez ponawiania pojedynczego packa na innej sesji; (d) pamięć: do N buforów packów naraz (N × rozmiar packa, np. 3 × 64 MiB). Alternatywa (c): ponowienie packa po KB-2112 na mniejszej równoległości.
DECYZJA:

Qb63 (it. 119, PROWIZORYCZNIE, ADR-0038): równoległe wysyłki packów w Agencie — WŁĄCZONE domyślnie (`KOBIAN_PARALLEL_UPLOADS=0` wyłącza); liczba sesji = wyuczony limit celu albo zalecenie dostawcy (Local 2, SMB 4, SFTP 3), stała na czas uruchomienia; odmowa sesji (KB-2112) zmniejsza limit i jest pamiętana 7 dni. Zmierzone (chmura, informacyjnie): pełna kopia +~17%, szczyt pamięci procesu Bench +~130–180 MiB przy 2 sesjach. Pytanie: (a) zostawić włączone (rekomendowane po W-48), (b) domyślnie wyłączone do pomiaru NFR-05 na Windows, (c) włączone tylko dla celów sieciowych (SMB/SFTP).
DECYZJA:

## Iteracja 120 (sesja ręczna z agentami — polecenie właściciela „zrób jak najwięcej, 5 iteracji”)
Qb64 (PROWIZORYCZNIE, BL-45): konfiguracja SMTP z KOBIAN_SMTP_* + plik `<sekrety>/smtp.credentials` (jak webhook), zamiast tabeli alert_channels + DPAPI. Alternatywa: konfiguracja w katalogu i UI (koszt: migracja + RPC, ~2 dni).
DECYZJA:
Qb65 (PROWIZORYCZNIE): adresy e-mail tylko ASCII (domeny IDN w punycode). Alternatywa: SMTPUTF8 (koszt ~0,5 dnia + testy serwerów).
DECYZJA:
Qb66 (PROWIZORYCZNIE): błąd TLS/certyfikatu SMTP = przejściowy (ponowienia, potem dead + audyt). Alternatywa: trwały od razu.
DECYZJA:
Qb67 (PROWIZORYCZNIE): szablony e-mail PL/EN w kodzie (EmailTemplates), nie w .resx. Alternatywa: .resx (koszt ~0,5 dnia).
DECYZJA:
Qb68 (PROWIZORYCZNIE, BL-34): port 990 w definicji celu ftps = tryb Implicit TLS; inne porty = Explicit AUTH TLS.
DECYZJA:
Qb69 (PROWIZORYCZNIE): FTP/FTPS RecommendedParallelism = 2 sesje na cel (AIMD może zejść do 1).
DECYZJA:
Qb70 (PROWIZORYCZNIE): kanał danych FTP pasywny (PASV z ignorowaniem adresu z odpowiedzi — zawsze adres kanału sterowania), EPSV opcją.
DECYZJA:
Qb71 (PROWIZORYCZNIE): FTP deklaruje AtomicRename (StagedRename) mimo braku RNTO bez nadpisania — sprawdzenie istnienia przed RNTO; okno wyścigu udokumentowane (D4.12). Alternatywa: tylko DirectFinal.
DECYZJA:
Qb72 (PROWIZORYCZNIE): sprawdzanie unieważnienia certyfikatu FTPS włączone; brak CRL → wymagany pin SPKI.
DECYZJA:
Qb73 (PROWIZORYCZNIE): nowy KB-2115 InsecureTransportNotConfirmed (jawny FTP bez flagi „insecure”) zamiast KB-2110. Kod spoza planu — do akceptacji PO.
DECYZJA:
Qb74 (PROWIZORYCZNIE, RD-75): budżet buforów packów 192 MiB (NFR-05 500 MB − NFR-22 150 MB − rezerwa); limit wysyłek w toku = min(sesje, AIMD, budżet/pojemność − 1), min. 1.
DECYZJA:
Qb75 (PROWIZORYCZNIE): BouncyCastle.Cryptography 2.7.0 (MIT) — obowiązkowa zależność przechodnia MimeKit, przyjęta w ramach wiersza MailKit/MimeKit; plan jej nie wymienia osobno — potwierdź.
DECYZJA:

## Iteracja 121 (sesja ręczna z agentami)
Qb76 (PROWIZORYCZNIE, SCR-09): ListDestinations nie zwraca host/port/odcisku — karta FTPS pokazuje opis ogólny; propozycja: pola host/port/host_key w DestinationInfo (proto, ~3 linie mapowania).
DECYZJA:
Qb77 (PROWIZORYCZNIE, D4.11): tabela `repo_dest_identities` (moduł repo) zamiast `dest_identities`, bez FK — `dest_targets` jeszcze nie istnieje.
DECYZJA:
Qb78 (PROWIZORYCZNIE): lista tożsamości serwerów dostępna dla Operatora (spójnie z Qb38); plan SCR-09 mówi Admin.
DECYZJA:
Qb79 (PROWIZORYCZNIE): wygaśnięcie oczekującego klucza po 24 h = stan wyliczany, nie zapisane odrzucenie.
DECYZJA:
Qb80 (PROWIZORYCZNIE, SEC-14.5): SPKI z ważnym łańcuchem CA zapisywany informacyjnie (`system:ca-chain`), nieużywany jako pin.
DECYZJA:
Qb81 (PROWIZORYCZNIE): `hostKey` z definicji zadania staje się pinem tylko, gdy cel nie ma jeszcze pinu (późniejsza edycja zadania nie zmienia pinu).
DECYZJA:
Qb82 (PROWIZORYCZNIE, SEC-4.1): step-up przed zatwierdzeniem klucza NIE jest zaimplementowany — ochrona: rola Administrator + audyt. Znalezisko Średnie, do zrobienia.
DECYZJA:
Qb83 (PROWIZORYCZNIE, US-29): limit pliku importu 60 KiB (host gRPC ≤ 64 KiB).
DECYZJA:
Qb84 (PROWIZORYCZNIE): wartość wyglądająca na sekret w imporcie = odrzucenie całego pliku (nie ostrzeżenie).
DECYZJA:
Qb85 (PROWIZORYCZNIE): pliki klucza/hasła w imporcie tylko ze ścieżek lokalnych (bez UNC).
DECYZJA:
Qb86 (PROWIZORYCZNIE, AC-29.1): definicja v1 nie ma akcji — plik z akcjami/skryptami odrzucany w całości (KB-0123).
DECYZJA:
Qb87 (PROWIZORYCZNIE): zaimportowane zadania zawsze wyłączone.
DECYZJA:
Qb88 (PROWIZORYCZNIE): źródło musi być czytelne na serwerze docelowym już przy imporcie.
DECYZJA:
Qb89 (PROWIZORYCZNIE): ExportJobs/ImportJobs = Administrator (kontrakt RPC planu „Admin (bez sekretów)”); sprzeczność z tabelą SCR-04 (Viewer) — do decyzji PO, zmiana = 1 linia w MethodPolicy.
DECYZJA:

## Iteracja 122 (sesja ręczna z agentami)
Qb90 (PROWIZORYCZNIE): A01 „ponowienia wyczerpane” = próba ≥ 3 albo 2 min bez nowej próby.
DECYZJA:
Qb91 (PROWIZORYCZNIE): A02 (ostrzeżenia) tylko na pulpicie, bez powiadomienia natychmiastowego.
DECYZJA:
Qb92 (PROWIZORYCZNIE): A15: okno liczenia odmów API 1 h (próg 5).
DECYZJA:
Qb93 (PROWIZORYCZNIE): dodatkowa kolumna acknowledged_at w alert_alerts.
DECYZJA:
Qb94 (PROWIZORYCZNIE): eskalacja wysyłana jako przypomnienie z escalated=true do tych samych kanałów (brak drugiego odbiorcy).
DECYZJA:
Qb95 (PROWIZORYCZNIE): godziny ciszy zawsze wyłączone (brak ustawienia).
DECYZJA:
Qb96 (PROWIZORYCZNIE): konsolidacja indeksów pod blokadą WYŁĄCZNĄ (plan D3.16 mówi „współdzielona”) — wejście zależy od zbioru żywych snapshotów.
DECYZJA:
Qb97 (PROWIZORYCZNIE): relacja „zastąpiony przez” w kluczach rozszerzenia 32/33 indeksu (SHA i numery packów) zamiast zmiany klucza 2.
DECYZJA:
Qb98 (PROWIZORYCZNIE): „suma małych > 16” = liczba indeksów < 1 MiB.
DECYZJA:
Qb99 (PROWIZORYCZNIE): odwołanie przez indeks skonsolidowany daje widok packów zastąpionego indeksu, nie całą unię.
DECYZJA:
Qb100 (PROWIZORYCZNIE): konsolidacja jako krok ręcznego prune, bez osobnego harmonogramu.
DECYZJA:
Qb101 (PROWIZORYCZNIE): polityka V3 w definicji zadania (`verify`: 7 dni, 5%), bez okna godzinowego, zamiast ustawienia globalnego.
DECYZJA:
Qb102 (PROWIZORYCZNIE): ponowienie nieukończonego cyklu V3 po stałej 1 h.
DECYZJA:

Qb103 (PROWIZORYCZNIE, it. 123): zapytania kontrolne ERP (D5.11) włączone domyślnie dla wykrytych profili `confirmed`; brak SELECT = ostrzeżenie KB-3010 (nie błąd); niezgodność po restore = Warning KB-3505 (nie błąd). Pytanie: czy generator skryptu uprawnień ma oferować wariant „z kontrolą logiczną” (`db_datareader`)? Wpływ: bez tego kontrola działa tylko dla kont z odczytem. Koszt zmiany: mały (PermissionScript + UI).
DECYZJA:

Qb104: Złote repo v1 używa publicznych stałych testowych (klucz główny, sól) zamiast hasła/Argon2 — PROWIZORYCZNIE (szybkość testu, brak sekretów). Koszt zmiany: regeneracja fixtury. → DECYZJA:

Qb105: OAuth2 dla e-mail (M365) — PROWIZORYCZNIE tylko przepływ client credentials, sekret klienta w pliku `smtp.oauth` obok `smtp.credentials`, bez nowych pakietów (HttpClient; XOAUTH2 z MailKit). Delegowany/interaktywny przepływ i magazyn DPAPI odłożone. Koszt zmiany: dodanie drugiego `IOAuth2TokenProvider` (interfejs gotowy). Pytanie: czy klienci M365 mają rejestrować aplikację z SMTP.SendAsApp (wymaga administratora Exchange) czy potrzebny jest przepływ użytkownika? → DECYZJA:

Qb106: Pulpit SCR-01 — wyciszenie alertu jednym przyciskiem na stałe 4 h (Administrator), potwierdzenie przez Operatora; bez okna wyboru czasu — PROWIZORYCZNIE. Koszt zmiany: okno z wyborem 1 h / 4 h / 24 h / 7 dni (`SilenceAlertAsync` już przyjmuje minuty). Pytanie: czy wyciszenie ma wymagać dodatkowego potwierdzenia (step-up, Qb82)? → DECYZJA:
  (it. 133: okno wyboru czasu wdrożone jako ComboBox 1 h / 4 h / 24 h / 7 dni (bez 7 dni dla alertów bezpieczeństwa), domyślnie 4 h; step-up nadal PROWIZORYCZNIE brak.)

Qb107: WatchRun (postęp biegu) — PROWIZORYCZNIE odpytywanie katalogu co 1 s, strumień do stanu końcowego, limit 12 h na strumień, rola Reader. Koszt zmiany: wymiana wnętrza `WatchRunAsync` (np. powiadomienia z writera), kontrakt bez zmian. Pytanie: czy limit jednoczesnych obserwatorów ma być egzekwowany (UI wielosesyjne na serwerze terminali)? → DECYZJA:

Qb108: Widok postępu (RD-79) — PROWIZORYCZNIE pasek NIEOKREŚLONY (usługa nie zna całkowitej pracy z góry; liczniki odczytane/wysłane/pliki są informacją, nie procentem), ponawianie po zerwaniu strumienia 1/2/4/8/15 s do 5 prób z rzędu (udana wiadomość zeruje licznik), „Przestań obserwować” NIE przerywa uruchomienia w usłudze (anulowanie biegu to osobna akcja z rolą Operator, jeszcze nie ma RPC). Koszt zmiany: stałe `MaxReconnects`/`Backoff` w `RunProgressViewModel`, bez zmian kontraktu. Pytanie: czy po utracie strumienia (5 prób) UI ma sam wracać do odpytywania ListRuns? DECYZJA:

Qb109: Restore w tle (RD-81) — PROWIZORYCZNIE flaga `detach` w RestoreRequest, odpowiedź z run_id od razu, wynik w run_runs/WatchRun; domyślnie bez zmian (czeka). Koszt zmiany: niski (ADR-0044). Pytanie: czy ograniczyć liczbę jednoczesnych odtworzeń w tle (propozycja: 2, RD-82)? DECYZJA:
