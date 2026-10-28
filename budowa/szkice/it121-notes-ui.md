# Notatki UI — it. 120: cel FTPS w kreatorze (SCR-03 S2) i na SCR-09 (BL-34, D4.3, D4.11, AC-01.2, AC-01.3, AC-UX.3)

Kopia: /tmp/kb/w-ui. Protos NIE ruszane (tylko istniejące RPC: UpsertJob validate_only, ProbeDestination, ListDestinations).

## Co zrobiono
1. **Kreator S2 „Serwer FTPS”** (`DestinationKind.Ftps`, `FtpsTlsMode`): serwer + port, tryb szyfrowania słownie — „Standardowe (zalecane) · port 21” (Explicit) /
   „Starsze serwery NAS · port 990” (Implicit). Tryb ↔ port zsynchronizowane zgodnie z Qb68 (990 = Implicit, inny = Explicit; wybór trybu ustawia port,
   wpisanie 990 przełącza tryb). Poświadczenie = nazwa wpisu w magazynie sekretów (`secretRef` = `file:<nazwa>`, jak SFTP/SMB, AC-01.3). Folder uniksowy.
   Probe przez istniejący ProbeDestination; `UNKNOWN_IDENTITY` (KB-2109, certyfikat niezaufany przez system) → istniejący stan S2Identity z tekstami FTPS
   (serwer `host:port · tryb`, odcisk SHA-256 SPKI czcionką stałej szerokości, skąd administrator weźmie odcisk) → „Odciski są zgodne — zatwierdź” → ponowny
   probe z pinem; pin w `DestinationSpec.host_key` (= pole `hostKey` definicji zadania). Certyfikat zaufany → bez pinu.
   Jawny FTP nie jest oferowany (D4.3) — notka informacyjna pod formularzem.
2. **Walidacja pól** przed wywołaniem usługi (`FtpsFieldRules`, zgodna z `JobDestination`/`DestinationProbeService` Agenta): host bez schematu, ścieżki, „@”,
   „:port” (IPv6 dozwolony), tylko ASCII litery/cyfry/.-_:, ≤253; port 1–65535; folder od „/”, bez „\”, bez segmentu „..”, bez znaków sterujących, ≤1024;
   nazwa wpisu jak `FileSftpCredentialProvider.IsSafeName`. Komunikaty przy polach (PL/EN z UiStrings), zdejmowane po poprawce; KB z rejestru (KbMessages)
   przy błędach probe (KB-2110/2102/2105/2109…). Przypięty klucz ≠ przedstawiony (Failed + odcisk) → KB + linia „Odcisk przedstawiony przez serwer: …”
   (alarm, nie pytanie — dotyczy też SFTP KB-2104).
3. **Pin zapominany** przy zmianie rodzaju celu, hosta lub portu (FTPS i SFTP) — wcześniej zatwierdzony klucz SFTP przechodził na zmieniony host (Niskie, naprawione).
4. **SCR-09**: kłódka (nowa ikona `Kb.Icon.Lock`) + „Serwer FTPS”; linia szczegółów `host:port · Explicit/Implicit TLS · przypięty klucz SHA256:xxxxxxxx…yyyyyy`
   (pełny w podpowiedzi) lub „certyfikat zaufany przez system”. ListDestinations v1 zwraca tylko id/scheme/root → „Szyfrowanie TLS — tryb i odcisk certyfikatu
   zapisane w definicji zadania”. `DestinationItem` ma opcjonalne Host/Port/HostKey (dziś null z adaptera); `CheckDestinationAsync` przekazuje je, gdy są.
   „Sprawdź miejsce” dla SFTP/FTPS bez adresu serwera wyłączone z wyjaśnieniem (Agent odpowiadał mylącym KB-2106 — dotyczyło też istniejącego SFTP).
   Poprawka: cel bez danych o miejscu (FTPS bez AVBL, SFTP) pokazywał pełny pomarańczowy pasek „mało miejsca” — teraz sam tekst (`SpaceKnown`).
5. Segmenty rodzaju celu w `WrapPanel` (4 opcje; 800 px/200% bez poziomego przewijania).
6. Dokumentacja: docs/ui/README.md (sekcja it. 120), docs/admin/cel-ftps.md + .en.md (sekcja „Kreator w aplikacji”).

## Pliki
- nowe: src/Kobian.UI/Wizard/WizardViewModel.Ftps.cs, tests/Kobian.Tests.UI/WizardFtpsTests.cs
- zmienione: src/Kobian.UI/Wizard/{WizardModels.cs, WizardViewModel.cs, WizardView.axaml}, src/Kobian.UI/Services/{GrpcWizardService.cs, GrpcDashboardSource.cs},
  src/Kobian.UI/Destinations/{DestinationsViewModel.cs, DestinationsView.axaml}, src/Kobian.UI/Theme/Tokens.axaml, src/Kobian.UI/Resources/UiStrings{,.en}.resx (+37 kluczy),
  tests/Kobian.Tests.UI/DestinationsScreenTests.cs (zrzut „checked” sprawdza tylko karty z CanCheck), docs/ui/README.md, docs/admin/cel-ftps{,.en}.md

## Pakiety / KB
Brak nowych pakietów. Brak nowych kodów KB (użyte istniejące: KB-2102, 2104, 2105, 2106, 2109, 2110, 2115 w tekście).

## Testy (uruchomione)
- `dotnet build tests/Kobian.Tests.UI/Kobian.Tests.UI.csproj -c Release -m:1 -warnaserror` — 0 ostrzeżeń, 0 błędów.
- `KOBIAN_UI_SNAPSHOTS=/tmp/kb/shots-ui dotnet tests/Kobian.Tests.UI/bin/Release/net10.0/Kobian.Tests.UI.dll` — **335/335 zielone** (było 277 → +58 w `WizardFtpsTests`).
- `WizardFtpsTests` (58 przypadków): pin SPKI po zatwierdzeniu (D4.11, AC-01.3), certyfikat zaufany bez pinu, tryb↔port Qb68, brak jawnego FTP (D4.3),
  reguły host/ścieżka/port/poświadczenie (Theory, SEC: walidacja wejść/traversal), błędy przy polach bez wywołań usługi, czyszczenie błędów przy zmianie celu,
  KB-2110/2102/2105 z akcją, zmieniony klucz = alarm z odciskiem, zapominanie pinu przy zmianie hosta, teksty EN, karta SCR-09 (szczegóły/skrót odcisku/
  brak możliwości sprawdzenia/brak paska), zrzuty 6× SCR-03 + 2× SCR-09.
- Kobian.Tests.Unit `SecretScanTests` (NFR-11) 6/6; Kobian.Tests.Architecture 16/16.
- Zrzuty przejrzane (Read PNG): SCR-03-S2-ftps-Light-1180, -Dark-800, -invalid-Light-800, -identity-Light-1180/-Dark-800, -kb2110-Light-1180, SCR-09-ftps-Light-1180/-Dark-800,
  oraz regresja SCR-03-S2-sftp-Light-800 i SCR-09-checked-Light-1180. Poprawki po przeglądzie: notka „jawny FTP” mniejsza (13 px, padding 14,8) — konkurowała z błędem KB;
  linia szczegółów SCR-09 zawija się zamiast ucinać odcisk; usunięty mylący pełny pasek przy nieznanym miejscu; wyłączony „Sprawdź” dla celów bez adresu.

## Przegląd bezpieczeństwa
Lista kontrolna: brak sekretów w UI/VM/logach (tylko nazwa wpisu; hasła kreator nie przyjmuje) ✔; odcisk SPKI to dana publiczna ✔; brak TOFU — pin tylko po
jawnym zatwierdzeniu, przed zatwierdzeniem szkic bez pinu (test) ✔; pin nie przechodzi na inny host/port ✔; walidacja wejść (host bez schematu/ścieżki/@,
ścieżka bez „..”/„\”/znaków sterujących, limity długości) — UI wstępnie, Agent ponownie ✔; brak nowych procesów/plików/kryptografii ✔; brak opcji „akceptuj każdy
certyfikat” ✔; jawny FTP nieosiągalny z kreatora ✔. Sekret testowy: odcisk syntetyczny (nie sekret).
STRIDE (nowe elementy): S — podszycie serwera FTPS: pin SPKI tylko po porównaniu poza pasmem, zmiana klucza = alarm (KB-2109) bez opcji zatwierdzenia w tym
miejscu; T — modyfikacja szkicu: brak, szkic w pamięci, zapis przez usługę (step-up/rola egzekwuje Agent); R — brak nowych akcji audytowalnych w UI (zapis
zadania audytuje Agent); I — brak sekretów; odcisk i host widoczne tylko dla zalogowanego operatora; D — walidacja bez wywołań sieciowych dla oczywistych
pomyłek (mniej prób → mniej ryzyka blokady konta KB-2114); E — brak (uprawnienia po stronie Agenta).
Znaleziska: Wysokie 0; Średnie 0; Niskie 2 (naprawione): (a) zatwierdzony klucz SFTP przechodził na zmieniony host w tym samym szkicu (Agent i tak odrzuciłby
niezgodny klucz — KB-2104 — więc bez obejścia weryfikacji); (b) SCR-09 „Sprawdź” dla celu sieciowego zwracał mylący KB-2106.

## Decyzje PROWIZORYCZNE
- **Qb-NEW-dest-info-ftps** (DestinationsViewModel.cs): `DestinationInfo` (ListDestinations) nie ma host/port/host_key → tryb TLS i skrócony odcisk na SCR-09
  pokazywane tylko, gdy usługa je poda; dziś tekst „szczegóły w definicji zadania”, a „Sprawdź miejsce” dla SFTP/FTPS wyłączone. Pytanie: czy rozszerzyć
  `DestinationInfo` o `optional string host = 6; optional int32 port = 7; optional string host_key = 8;` (publiczne dane, bez sekretów)? Wybrano: UI gotowe
  na pola opcjonalne, adapter ich nie mapuje (proto nie ruszane — inny agent). Alternatywa: osobne RPC GetDestination. Koszt zmiany: mały — 3 linie w
  `GrpcDashboardSource.MapDestinations` + test adaptera; Agent: `CatalogStatusReader.ReadDestinations` z `JobDefinition`.
- Tryb TLS ↔ port wg istniejącego Qb68 (bez nowego Qb).

## Kolejka Windows
Brak (UI headless; brak kodu zależnego od Windows).

## Niedokończone / dla koordynatora
- Mapowanie host/port/host_key w `MapDestinations` po rozszerzeniu proto (Qb-NEW-dest-info-ftps) + odblokowanie „Sprawdź miejsce” dla celów sieciowych.
- Odczyt pełnego odcisku przez czytnik ekranu: SelectableTextBlock ma AutomationId; brak testu UIA (Windows) — jak dla SFTP.
- Komunikaty walidacji pól nie mają KB (to błędy wprowadzania, nie zdarzenia) — gdyby wymagano KB, potrzebny byłby nowy kod (np. KB-0105) — nie dodano.
