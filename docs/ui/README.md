# UI Kobian — pulpit (SCR-01), zadania (SCR-02), kreator (SCR-03), historia (SCR-10) i system wizualny

Stan: iteracja 59 (SCR-03 kreator, 3 rundy przeglądu zrzutów); it. 58 — SCR-02 i SCR-10; it. 57 — pulpit i tokeny (5 rund). Avalonia 12.1.4 + Fluent + CommunityToolkit.Mvvm (D6.1).

## Ekrany
| ID | Ekran | Stan |
|---|---|---|
| SCR-01 | Pulpit: stan globalny (najgorszy z zadań), jedno zdanie przyczyny, **jedna** akcja główna, kafelki zadań, miejsce na celach | zrobione (dane z Agenta: GetStatus + ListSnapshots) |
| SCR-16 | Brak połączenia z usługą (KB-9001, „Spróbuj ponownie”) — w nagłówku pulpitu | zrobione (bez uruchamiania usługi — UAC/Windows) |
| SCR-02 | Zadania: wszystkie zadania (najgorszy stan pierwszy), filtr Wszystkie / Wymagają uwagi / Wstrzymane z licznikami, harmonogram słownie („codziennie o 22:00”, „w dni robocze o 21:30”, „tylko ręcznie”, „wstrzymany (…)”), ostatnia udana kopia, akcje **Uruchom teraz** i **Wstrzymaj/Wznów** (od roli Operator), komunikat wyniku (KB + akcja albo potwierdzenie), stany pusty/bez usługi/nieaktualne | zrobione (GetStatus + ListSnapshots + ListSchedules; RunJob wait=false; SetSchedule bez cron = wstrzymanie, wyrażenie zostaje i wraca przy wznowieniu) |
| SCR-10 | Historia: kopie od najnowszej w grupach dni (Dziś / Wczoraj / „Poniedziałek, 12 października 2026”) w strefie użytkownika, tryb (pełna/przyrostowa — tekst + kolor), liczba plików z separatorem tysięcy, rozmiar (B…PB, binarnie), skrót ID (8 znaków jak w CLI), filtr zadania, **Odtwórz** → SCR-06, limit 500 wierszy z informacją | zrobione (ListSnapshots; PROWIZORYCZNIE Qb19: tylko udane kopie — nieudane uruchomienia po RPC ListRuns, BL-60) |
| SCR-03 | Kreator nowego zadania (D6.7): pasek kroków 1–6 (✓ ukończone, akcent bieżący; ≤ 820 px — podpis „Krok n z 6”), S1 foldery (dodaj/usuń, duplikat bez wielkości liter pomijany, nazwa zadania podpowiadana z pierwszego folderu), S2 cel lokalny/USB albo SFTP (serwer, port, użytkownik, folder) + probe, S2 tożsamość serwera (odcisk klucza do porównania, zatwierdzenie = przypięcie — D4.11), S3 presety Codziennie / Dni robocze / Co godzinę / Tylko ręcznie z podglądem słownym, S4 hasło + powtórzenie → „Utwórz repozytorium”, S4 klucz odzyskiwania (numerowane grupy, przepisanie 2 losowych różnych grup — AC-28.1, bez „Wstecz”), S5 retencja dzienne/tygodniowe/miesięczne, S6 podsumowanie + „Wykonaj pierwszą kopię teraz”, wynik pierwszej kopii; komunikaty KB + jedna akcja | widok i ViewModel zrobione na porcie `IWizardService` (atrapa w testach); adapter do Agenta — PROWIZORYCZNIE Qb20 (BL-60: UpsertJob validate_only, ProbeDestination, InitRepository z kluczem odzyskiwania); w aplikacji bez adaptera nadal zaślepka |
| SCR-04…SCR-15 (poza 10) | pozostałe | w nawigacji jako „pojawi się w kolejnej wersji” |

## Model stanu (6.3) — `ProtectionEvaluator`
Wstrzymane (zadanie wyłączone) → Zagrożone (repozytorium uszkodzone, klucz serwera, ostatnie uruchomienie Failed, brak udanej kopii, zaległość > cykl + max(1 h, 25% cyklu)) → Uwaga (Warning, brak V2, < 15% miejsca, cel bez ochrony przed usunięciem, zaległe V3, nowe zadanie przed pierwszą kopią) → Chronione.
Kolejność wagi: Chronione < Wstrzymane < Uwaga < Zagrożone. Tolerancja zaległości PROWIZORYCZNIE (Qb17).

## Tokeny (Theme/Tokens.axaml)
- Kolory stanów: `Kb.Color.<Stan>.Text/Bg/Solid` w motywie jasnym i ciemnym; test `ThemeContrastTests`: tekst ≥ 4,5:1 (na tle stanu i na powierzchni), dysk ikony ≥ 3:1, biały glif na dysku ≥ 3:1.
- Powierzchnie: `Bg`, `Surface`, `Sunken`, `Border`; tekst `Text`, `TextMuted`; akcent `Accent` (głęboki morski), nawigacja `NavBg/NavText/NavMuted/NavSelected`.
- Odstępy 4/8/12/16/24/32, promienie 12 (karta) / 8 (kontrolka) / pigułka; typografia 26/18/14/12.
- Ikony: własne geometrie 24×24 (`Kb.Icon.*`).

## Komponenty
- `ProtectionBadge` — ikona + tekst + kolor (WCAG 1.4.1), `AutomationProperties.Name` = tekst stanu, klasa `large` w nagłówku.
- `AdaptiveGrid` — kafelki o równej szerokości, 1–3 kolumny wg szerokości (100–200% skali).
- `Border.code` (kod KB / skrót ID, czcionka stałej szerokości), `Border.pill` (+ `.full`), `Border.notice` (+ `.error`), `ListBox.segmented` (filtr), `Border.histrow` (separator tylko między wierszami — `:nth-last-child(1)` bez linii).
- Wiersz zadania: położenie kolumn wyłącznie w stylach (wartość lokalna wygrywa ze stylem/ContainerQuery); ≤ 860 px treści — metadane i akcje pod nazwą.
- Historia: kolumny Auto współdzielone (`Grid.IsSharedSizeScope` + `SharedSizeGroup`) — równe kolumny w wierszach wszystkich dni; ≤ 760 px chowa liczbę plików i ID (zostają w nazwie dostępnej wiersza).
- Kreator: pola formularza `StackPanel.field` o stałej szerokości 520 px (`.short` 150 px), wąsko — pełna szerokość; panel jednego kroku w karcie, pasek akcji Anuluj | Wstecz · Dalej (tekst „Dalej” zależny od kroku). Akcent kontrolek Fluent (CheckBox, fokus TextBox) z tokenu `Kb.Color.Accent` (`Theme/Controls.axaml`).
- Nawigacja zwija się do ikon przy szerokości ≤ 960 px (ContainerQuery); nazwy zostają w podpowiedziach i dla czytnika ekranu.

## Lokalizacja
`Resources/UiStrings.resx` (PL) + `UiStrings.en.resx` — test pełnego pokrycia kluczy; liczba mnoga PL wg CLDR (`Plural`), czasy względne „dziś/wczoraj/jutro” w strefie użytkownika.

## Zrzuty (Avalonia.Headless + Skia, generowane przez testy `RenderTests`)
Pliki `SCR-01-*.png`: mieszane stany (jasny/ciemny, 1180/800 px), wszystko chronione, pusty, bez usługi, nieaktualne dane. `SCR-02-*.png`: lista zadań (jasny z komunikatem akcji, ciemny, 800 px, bez usługi). `SCR-10-*.png`: historia (jasny, ciemny, 800 px). `SCR-03-*.png`: kreator — S1 (jasny/ciemny), S2 SFTP (800 px), S3, klucz odzyskiwania (jasny 1180, ciemny 800; klucz losowy z atrapy), S6, wynik.
Odtworzenie: `KOBIAN_UI_SNAPSHOTS=<katalog> dotnet tests/Kobian.Tests.UI/bin/Release/net10.0/Kobian.Tests.UI.dll -class Kobian.Tests.UI.RenderTests`.

## Ograniczenia (PROWIZORYCZNIE Qb18)
Kontrakt `kobian.v1` nie podaje jeszcze: następnego uruchomienia, cyklu, poziomu weryfikacji, celu i wolnego miejsca, roli wywołującego — adapter przyjmuje: brak kontroli zaległości, V2 z wyniku uruchomienia (od it. 56 Agent wykonuje V2 po każdym backupie), rola Operator (autoryzację egzekwuje Agent). Pola do dodania: BL-60. Weryfikacja tożsamości serwera pipe (D2.9) — Windows (W-17). Telemetria budowania Avalonia wyłączona (`Directory.Build.targets`).

## SCR-11 Ustawienia (it. 81)
Wygląd (motyw od razu, język po ponownym uruchomieniu — plik `%APPDATA%\Kobian\ui-settings.json`, ≤ 4 KiB, nieznane wartości = domyślne), usługa (wersja, rola, poprawki .NET — KB-7013), dziennik audytu (VerifyAudit). Pulpit: baner KB-7013. Zrzuty `SCR-11-*`, `SCR-01-runtime-*`.

## SCR-03 S2 cel FTPS i SCR-09 karta FTPS (it. 120, BL-34)
- **S2 „Serwer FTPS”** (czwarty rodzaj celu; segmenty zawijane przy 800 px): Serwer + Port, „Szyfrowanie połączenia” słownie — *Standardowe (zalecane) · port 21* (Explicit, `AUTH TLS`) / *Starsze serwery NAS · port 990* (Implicit). Tryb i port są jedną prawdą (PROWIZORYCZNIE Qb68, jak Agent): wybór trybu ustawia port, wpisanie 990 przełącza na Implicit, inny port = Explicit. Poświadczenie = nazwa wpisu w magazynie sekretów usługi (`secretRef` `file:<nazwa>`, jak SFTP/SMB — kreator nie przyjmuje hasła, AC-01.3). Folder uniksowy od „/”.
- **Walidacja przy polach** (przed wywołaniem usługi, `FtpsFieldRules` — zgodna z `JobDestination` Agenta, który waliduje ponownie): host bez schematu (`ftps://`), ścieżki, „@” i „:port” (IPv6 dozwolony), znaki ASCII; port 1–65535; folder od „/”, bez „\” i segmentu „..”, bez znaków sterujących, ≤ 1024; nazwa wpisu `^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$` bez „..”. Komunikat znika po poprawce pola (nowy pojawia się dopiero po „Dalej”); obramowanie pola w kolorze „Zagrożone” + tekst (WCAG 1.4.1).
- **Jawny FTP** nie jest oferowany (D4.3) — informacja pod formularzem; dostawca „ftp” tylko z flagą `insecure` poza kreatorem (KB-2115).
- **S2 tożsamość (FTPS)**: probe zwraca `UNKNOWN_IDENTITY` z odciskiem SPKI (KB-2109), gdy certyfikatu nie zaufa system → ekran z serwerem (`host:port · Explicit/Implicit TLS`), pełnym odciskiem `SHA256:…` (zaznaczalny, czcionka stałej szerokości) i wskazówką, skąd administrator weźmie odcisk (przewodnik „Cel FTPS”). „Odciski są zgodne — zatwierdź” → ponowny probe z pinem; pin trafia do pola `hostKey` definicji zadania. Certyfikat zaufany przez system → bez ekranu i bez pinu. Pin jest zapominany przy zmianie rodzaju celu, hosta lub portu (nie przechodzi na inny serwer). Przypięty klucz inny niż przedstawiony (KB-2104/KB-2109 jako błąd) = alarm z odciskiem przedstawionym przez serwer, nie pytanie.
- **Błędy probe**: komunikat KB z rejestru + jedna akcja (KB-2110 odmowa AUTH TLS, KB-2102 poświadczenie, KB-2105 uprawnienia, KB-2109…).
- **S6**: `ftps://host:port/folder · Explicit TLS · poświadczenie „nazwa”`.
- **SCR-09**: kłódka przy celach szyfrowanych (SFTP/FTPS), etykieta „Serwer FTPS”, linia szczegółów `host:port · tryb TLS · przypięty klucz SHA256:AbCdEfGh…xYz12` (pełny odcisk w podpowiedzi) albo „certyfikat zaufany przez system”. `ListDestinations` v1 zwraca tylko schemat i korzeń — wtedy „Szyfrowanie TLS — tryb i odcisk certyfikatu zapisane w definicji zadania”, a „Sprawdź miejsce” dla celu sieciowego bez adresu serwera jest wyłączone z wyjaśnieniem (Agent odpowiedziałby mylącym KB-2106). Pola host/port/odcisk w `DestinationInfo` — prośba do kontraktu (PROWIZORYCZNIE Qb76). Cel bez danych o miejscu (FTPS bez `AVBL`): sam tekst, bez paska.
- Zrzuty (`WizardFtpsTests`): `SCR-03-S2-ftps-Light-1180`, `SCR-03-S2-ftps-Dark-800`, `SCR-03-S2-ftps-invalid-Light-800`, `SCR-03-S2-ftps-identity-Light-1180`, `SCR-03-S2-ftps-identity-Dark-800`, `SCR-03-S2-ftps-kb2110-Light-1180`, `SCR-09-ftps-Light-1180`, `SCR-09-ftps-Dark-800`. Odtworzenie: `KOBIAN_UI_SNAPSHOTS=<katalog> dotnet tests/Kobian.Tests.UI/bin/Release/net10.0/Kobian.Tests.UI.dll -class Kobian.Tests.UI.WizardFtpsTests`.
