# Notatki — powiadomienia e-mail SMTP (BL-45) — agent „email”, kopia /tmp/kb/w-email

## Co zrobiono (ID planu)
- BL-45 (cz. e-mail), 9.6 (wiersz „E-mail SMTP | MailKit 4.18.1”), 9.7 (wynik uruchomienia domyślnie tylko Warning/Failed; raport dzienny), D9.10 (outbox, ponowienia, dead-letter), D9.11/RD-61 (raport dzienny z kotwicą audytu także e-mailem), SEC-9.1 / CVE-2026-30227, SEC-9.4 (hasło tylko z pliku sekretu, maskowane), SEC-9.6 (tryb minimalny, brak ścieżek/sekretów), R2.9.5 (temat ≤ 120 znaków, HTML + text/plain, PL/EN), KB-6201 (z planu).
- Wszystkie 5 części zadania zrobione i zielone.

1. Pakiety MailKit 4.18.1 + MimeKit 4.18.1 w Kobian.Notifications (przechodnio BouncyCastle.Cryptography 2.7.0; System.Security.Cryptography.Pkcs przypięte centralnie na 10.0.13).
2. `EmailConfig` (KOBIAN_SMTP_HOST/PORT/TLS/INSECURE/FROM/TO/LANG/NOTIFY/MINIMAL + `<sekrety>/smtp.credentials`: wiersz 1 login, wiersz 2 hasło), `EmailAddressPolicy` (CR/LF/sterujące/nie-ASCII odrzucane przed parserem, tylko addr-spec, MimeKit Strict + wynik identyczny z wejściem, limity 64/253/254), limit 10 odbiorców, deduplikacja. STARTTLS domyślnie i WYMAGANY; 465 → TLS od początku; `none` tylko z `KOBIAN_SMTP_INSECURE=1` (ostrzeżenie KB-6207 na stderr + wpis audytu) i bez poświadczeń. Komunikaty błędów: nazwa zmiennej + kod KB, bez wartości.
3. Port `ISmtpTransport` + adapter `MailKitSmtpTransport` (walidacja certyfikatu domyślna — callback NIE ustawiany; TLS 1.2/1.3; timeout operacji 30 s; EHLO stałą nazwą `kobian.localdomain`; odmowa AUTH na niezaszyfrowanym połączeniu). `EmailMessageBuilder` (CloudEvent run.finished / report.ready → MimeMessage; zamknięta lista pól z walidacją; nazwa zadania oczyszczona ze sterujących/formatujących i ≤ 60 zn.; Message-ID = `<eventId>@<domena nadawcy>`, `X-Kobian-Event-Id`, `Auto-Submitted`, `X-Auto-Response-Suppress`), `EmailTemplates` PL/EN, `EmailSender : IOutboxChannel` (limit próby 90 s; klasyfikacja: Auth/530/534/535/538/„5.7.30” → trwały KB-6201; 5xx → trwały KB-6203; 4xx/protokół/sieć/timeout → przejściowy KB-6203; TLS/certyfikat/brak STARTTLS → przejściowy KB-6206; nieobsługiwany ładunek → trwały KB-6203; nieznany wyjątek propaguje).
4. Outbox uogólniony: port `IOutboxChannel` (+ `WebhookOutboxChannel`) w Kobian.Notifications/Outbox; `OutboxDispatcher` przyjmuje kanał (stary konstruktor z `IWebhookSender` zachowany), audyt dead = `notify.<kanał>` z kodem KB z próby albo domyślnym kanału. `CatalogJobService.NotifyChannels` (rekord `NotifyChannel(Name, Minimal, ProblemsOnly)`): jedno zdarzenie (wspólny id), osobny wpis outboxa i ładunek w trybie minimalnym danego kanału; `NotifyMinimal` działa jak dotąd (zgodność). `DailyReportService.Channels` — raport do każdego kanału w tej samej transakcji (idempotencja bez zmian). Program.cs: wczytanie konfiguracji (błąd = kod wyjścia 64), drugi dyspozytor dla kanału „email”, raport dzienny gdy włączony dowolny kanał.
5. Testy + dokumentacja admina PL/EN (docs/admin/powiadomienia{,.en}.md — nowa sekcja e-mail; raport dzienny dla każdego kanału). THIRD-PARTY-NOTICES.md uzupełniony.
- Dodatkowo: reguła `CertValidationBypass` w `BannedApiTests` łapie też MailKit `ServerCertificateValidationCallback = ... => true` (+ InlineData).

## Pliki
Nowe:
- src/Kobian.Notifications/Email/EmailAddressPolicy.cs
- src/Kobian.Notifications/Email/EmailConfig.cs
- src/Kobian.Notifications/Email/EmailMessageBuilder.cs
- src/Kobian.Notifications/Email/EmailSender.cs
- src/Kobian.Notifications/Email/EmailTemplates.cs
- src/Kobian.Notifications/Email/SmtpTransport.cs
- src/Kobian.Notifications/Outbox/OutboxChannel.cs
- src/Kobian.Agent/Notifications/NotifyChannel.cs
- tests/Kobian.Tests.Unit/Notifications/EmailConfigTests.cs
- tests/Kobian.Tests.Unit/Notifications/EmailDeliveryTests.cs
- tests/Kobian.Tests.Unit/Notifications/FakeSmtpServer.cs
Zmienione:
- Directory.Packages.props, src/Kobian.Notifications/Kobian.Notifications.csproj
- packages.lock.json: src/Kobian.Notifications, src/Kobian.Agent, tests/Kobian.Tests.Unit
- src/Kobian.Agent/Notifications/OutboxDispatcher.cs, src/Kobian.Agent/Notifications/DailyReport.cs, src/Kobian.Agent/Jobs/CatalogJobService.cs, src/Kobian.Agent/Program.cs
- docs/kb-codes.yaml, docs/kb-codes.md, src/Kobian.Domain/KbCode.cs, src/Kobian.UI/Resources/KbMessages*.resx (wygenerowane tools/kb.py gen/resx/docs)
- docs/admin/powiadomienia.md, docs/admin/powiadomienia.en.md, THIRD-PARTY-NOTICES.md
- tests/Kobian.Tests.Architecture/BannedApiTests.cs

## Nowe pakiety — wiersz audytu (do budowa/zaleznosci.md — koordynator)
| Pakiet | Wersja | Licencja | CVE/ostrzeżenia | Decyzja |
|---|---|---|---|---|
| MailKit | 4.18.1 (najnowsza wg planu, re-audyt it. 19) | MIT | CVE-2026-30227 dotyczy MimeKit < 4.15.1; nuget.org oznacza 4.5.0–4.15.1; `dotnet list --vulnerable --include-transitive` (Notifications, Agent): brak | Przyjęte (plan „Zależności — audyt”, 9.6, SEC-9.1) |
| MimeKit | 4.18.1 | MIT | jw. — brak ostrzeżeń dla 4.18.1 | Przyjęte (jw.) |
| BouncyCastle.Cryptography (przechodnia MimeKit) | 2.7.0 | MIT | brak ostrzeżeń (`--vulnerable`), brak deprecjacji | Przyjęte w ramach wiersza MimeKit — PROSZĘ potwierdzić osobnym wierszem w audycie (plan go nie wymienia) |
| System.Security.Cryptography.Pkcs (przechodnia MimeKit ≥ 10.0.0) | 10.0.13 (przypięcie centralne istniejące) | MIT | GHSA naprawione w 10.0.13 | bez zmian |

## Nowe kody KB (dziedzina 62xx — kanały e-mail; 6201 z planu R2.9.6)
- KB-6201 SmtpAuthRejected (permanent/error) — z planu
- KB-6203 EmailDeliveryFailed (transient/warning)
- KB-6204 SmtpConfigInvalid (permanent/error)
- KB-6205 EmailAddressInvalid (security/error)
- KB-6206 SmtpTlsFailed (security/error)
- KB-6207 SmtpInsecureEnabled (security/warning)
(KB-6202 zostawiony dla M365 OAuth wg planu.)

## Testy (uruchomione)
- `Kobian.Tests.Unit.Notifications.EmailConfigTests` — 16 metod (Fact/Theory; m.in. 15 przypadków niepoprawnej konfiguracji, 20 przypadków adresów) — zielone.
- `Kobian.Tests.Unit.Notifications.EmailDeliveryTests` — 13 metod (10 przypadków klasyfikacji błędów, timeout, outbox: ponowienie → sent, dead po harmonogramie, trwały → dead + audyt KB-6201, routing per kanał z trybem minimalnym i filtrem problems, raport dzienny do 2 kanałów, 3 testy protokołu MailKit ↔ FakeSmtpServer na 127.0.0.1: jawne SMTP z flagą + brak wstrzyknięcia komend, STARTTLS z niezaufanym certyfikatem odrzucony przed AUTH, brak STARTTLS → brak wysyłki jawnym tekstem) — zielone.
- Razem obie klasy: 74 przypadki, 0 błędów.
- Pełny `Kobian.Tests.Unit` (bez Category=Windows/Perf): 1228, 0 błędów (84 s).
- `Kobian.Tests.Architecture`: 16, 0 błędów.
- Build Release `-warnaserror -m:1`: Kobian.Notifications, Kobian.Agent, Kobian.Tests.Unit, Kobian.Tests.Architecture — zielone.
- Tagi: `Trait("Plan","BL-45")`, `SEC-9.1`, `SEC-9.4`, `SEC-9.6`, `RD-61`, `Plan 9.7`, `D9.10`, `CVE-2026-30227`.

## Przegląd bezpieczeństwa
Lista kontrolna: brak wyłączania walidacji certyfikatu (domyślny callback MailKit; reguła BannedApi rozszerzona o MailKit) ✔; brak sekretów w logach/wyjątkach/komunikatach (konfiguracja zwraca tylko nazwy zmiennych; serwerowe komunikaty SMTP nie są przenoszone; `SmtpCredential.ToString` maskowany; test „brak sekretów w wiadomości”) ✔; hasło tylko z pliku sekretu (bez dowiązań, ≤ 4 KiB) ✔; walidacja wejść (adresy, host, port, długości, nazwa zadania) ✔; limity i timeouty (30 s/operację, 90 s/próbę, ≤ 10 odbiorców, ładunek ≤ 64 KiB, JSON MaxDepth 8, temat ≤ 120) ✔; nagłówki tylko przez API MimeKit ✔; brak Process.Start/MD5/Random ✔; testy wyłącznie na localhost ✔.

STRIDE (nowe elementy):
- S: podszycie pod nadawcę — SPF/DKIM po stronie domeny (zalecenie w dokumentacji); MITM serwera SMTP — walidacja certyfikatu + wymagany STARTTLS (bez downgrade'u; test).
- T: wstrzyknięcie nagłówków/komend SMTP (CVE-2026-30227) — MimeKit 4.18.1 + EmailAddressPolicy + oczyszczanie nazwy zadania (testy CR/LF, protokół).
- R: dead → wpis audytu `notify.email` z kodem; włączenie jawnego SMTP → wpis audytu KB-6207.
- I: treść bez ścieżek/nazw plików/baz/sekretów; tryb minimalny; hasło nie idzie otwartym tekstem (insecure + poświadczenia = błąd startu; AUTH na niezaszyfrowanym połączeniu odrzucany w adapterze).
- D: timeouty, limity, ponowienia ograniczone harmonogramem, dyspozytor per kanał (awaria SMTP nie blokuje webhooka).
- E: konfiguracja tylko przez zmienne środowiskowe usługi i katalog sekretów (Admin).

Znaleziska: Wysokie — brak. Średnie — brak nowych. Niskie:
- (N1) `smtp.credentials` w pliku zamiast DPAPI (jak Qb58 dla webhooka, SEC-9.4) — ACL katalogu sekretów nie jest sprawdzany przy odczycie (jak webhook).
- (N2) Sprawdzanie odwołań certyfikatu (`CheckCertificateRevocation`) — zostawiona wartość domyślna MailKit; do decyzji (włączenie może blokować wysyłkę w sieciach bez dostępu do CRL/OCSP).
- (N3) Brak blokady adresów link-local/metadanych dla `KOBIAN_SMTP_HOST` (host ustawia Admin; serwery SMTP w LAN są typowe) — świadomie inaczej niż SEC-9.3 dla webhooków.
- (N4) Brak alertu przy „dead” (tylko audyt + licznik w raporcie dziennym) — jak webhook (Qb58 b / BL-45 cz. 2).

## Decyzje PROWIZORYCZNE (do nadania Qb)
- Qb-NEW-smtp-config — Pytanie: gdzie konfiguracja SMTP przed `alert_channels`/DPAPI? Wybrano: zmienne `KOBIAN_SMTP_*` + plik `<sekrety>/smtp.credentials` (login + hasło, 2 wiersze), wzorem webhooka (Qb58). Alternatywa: tabela `alert_channels` + magazyn DPAPI + kreator. Koszt zmiany: mały — `EmailConfig.Load` → loader z katalogu; `EmailSettings` bez zmian.
- Qb-NEW-smtp-ascii — Pytanie: czy dopuszczać adresy IDN/UTF-8 (SMTPUTF8)? Wybrano: tylko ASCII (domena w punycode) — mniejsza powierzchnia ataku. Alternatywa: IDN przez IdnMapping + SMTPUTF8. Koszt: mały (EmailAddressPolicy + testy).
- Qb-NEW-smtp-tls-retry — Pytanie: błąd TLS/certyfikatu/braku STARTTLS — trwały czy przejściowy? Wybrano: przejściowy (ponowienia ~9 h, potem dead z KB-6206) — nie gubi alertu przy chwilowej awarii certyfikatu. Alternatywa: trwały od razu (szybsza sygnalizacja ataku/MITM). Koszt: jedna linia w `EmailSender.Classify` + test.
- Qb-NEW-email-templates — Pytanie: szablony w .resx (konwencje) czy w kodzie? Wybrano: słownik PL/EN w kodzie (`EmailTemplates`; test zgodności kluczy), biblioteka bez zasobów satelitarnych. Alternatywa: .resx. Koszt: mały, API bez zmian.
- (bez Qb, zgodnie z 9.7) domyślnie e-mail tylko dla Warning/Failed (`KOBIAN_SMTP_NOTIFY=all` — wszystkie); raport dzienny zawsze.
- (bez Qb) EHLO stałą nazwą `kobian.localdomain` zamiast nazwy komputera (minimalizacja metadanych); w temacie/treści jest etykieta hosta (MachineName oczyszczona) — plan: tryb minimalny „kod + stan + host”.

## Kolejka Windows
Brak nowych testów wymagających Windows (kod czysto .NET). Do sprawdzenia ręcznie przy wdrożeniu (opcjonalnie): usługa z `KOBIAN_SMTP_HOST=<serwer testowy>` + `smtp.credentials` → po nieudanej kopii przychodzi e-mail; `KOBIAN_SMTP_TLS=none` bez `KOBIAN_SMTP_INSECURE=1` → usługa nie startuje (kod 64, KB-6207).

## Niedokończone / dalej
- M365 OAuth2 XOAUTH2 (MSAL, KB-6202, D9.11) — nie robione.
- Alert przy dead (KB-6203/KB-6301) i kanał dla zdarzeń alert.opened/reminder/resolved (EmailMessageBuilder obsługuje dziś run.finished i report.ready; inne typy = trwały błąd, ale nie są kierowane do kanału email).
- Dokumentacja „kodów KB” w docs/kb-codes.md wygenerowana; budowa/zaleznosci.md — wiersz audytu wyżej do przeniesienia przez koordynatora.
