# Powiadomienia webhook (D9.10) — PROWIZORYCZNIE Qb58

Agent wysyła zdarzenie `pl.kobian.run.finished.v1` (CloudEvents JSON) po każdym zakończonym uruchomieniu zadania. Zdarzenie jest zapisywane w kolejce `core_outbox` w tej samej transakcji co wynik uruchomienia, więc restart usługi niczego nie gubi.

## Włączenie
| Ustawienie | Znaczenie |
|---|---|
| `KOBIAN_WEBHOOK_URL` | adres odbiorcy; tylko `https://`, host publiczny (adresy prywatne, lokalne i link-local są odrzucane — także IPv6 z osadzonym adresem IPv4 prywatnym (NAT64 `64:ff9b::/96`, 6to4 `2002::/16`), Teredo i nazwy hostów niepoprawne wg IDNA; kontrola powtarzana po rozwiązaniu DNS przy każdym połączeniu) |
| `<sekrety>\webhook.secret` | sekret HMAC: co najmniej 64 znaki szesnastkowe (32 B); plik tylko dla konta usługi i Administratorów |
| `KOBIAN_WEBHOOK_MINIMAL=1` | tryb minimalny: bez nazwy zadania (tylko identyfikatory, wynik, czasy) |

Błędna konfiguracja (zły URL, brak lub zły sekret) zatrzymuje start usługi z kodem 64 i komunikatem KB-6302 — nie ma cichego wyłączenia.

## Żądanie
`POST` z nagłówkami `Kobian-Webhook-Id` (stały przy ponowieniach — deduplikuj po nim), `Kobian-Webhook-Timestamp` (sekundy Unix) i `Kobian-Webhook-Signature: v1=<hex HMAC-SHA256(sekret, timestamp + "." + treść)>`. Odbiorca powinien odrzucić znacznik czasu starszy niż 300 s. Zdarzenie nie zawiera ścieżek plików ani danych klienta.

## Dostarczanie
Odpowiedź 2xx = dostarczono; 410 = odbiorca zrezygnował (bez ponowień); inne kody, przekierowania (nie są śledzone) i błędy sieci = ponowienie po 1 min, 5 min, 30 min, 2 h, 6 h, potem wpis `dead`. Limity: połączenie 10 s, całość 30 s, odpowiedź czytana najwyżej 64 KiB. Proxy z ustawień użytkownika nie jest używane.

## Raport dzienny (D9.11) — PROWIZORYCZNIE Qb59
Gdy włączony jest webhook lub e-mail, codziennie o 07:00 czasu lokalnego (po kotwicy audytu 06:55) Agent wysyła zdarzenie `pl.kobian.report.ready.v1` z podsumowaniem poprzedniej doby: liczby uruchomień wg wyniku, identyfikatory zadań z błędem (do 20, bez nazw), liczba powiadomień, których nie udało się doręczyć, oraz ostatnia kotwica łańcucha audytu `auditAnchor.text` w postaci `seq:sha256`. Zachowaj ją u odbiorcy: `kobian audit verify --anchor <seq:sha256>` wykryje podmianę dziennika audytu na tym komputerze. Po przerwie w pracy usługi wysyłany jest jeden zaległy raport, nie kilka.

Powiadomienie, którego nie udało się doręczyć po wszystkich próbach, zostawia wpis w dzienniku audytu z kodem KB-6301.

# Powiadomienia e-mail (SMTP, BL-45) — PROWIZORYCZNIE Qb64

Agent wysyła e-mail o zakończonym uruchomieniu i raport dzienny przez tę samą trwałą kolejkę `core_outbox` co webhook (osobny wpis dla kanału „email”). Kanały działają niezależnie: można włączyć jeden lub oba. Wysyłka używa MailKit/MimeKit 4.18.1 (poprawka CVE-2026-30227).

## Włączenie
| Ustawienie | Znaczenie |
|---|---|
| `KOBIAN_SMTP_HOST` | serwer SMTP (nazwa DNS lub adres IP); brak = e-mail wyłączony |
| `KOBIAN_SMTP_PORT` | port; domyślnie 587 (STARTTLS), 465 dla `tls`, 25 dla `none` |
| `KOBIAN_SMTP_TLS` | `starttls` (domyślnie; STARTTLS **wymagany** — serwer bez niego = błąd, nie wysyłka jawnym tekstem), `tls` (TLS od początku połączenia, port 465), `none` (jawne SMTP — patrz niżej) |
| `KOBIAN_SMTP_INSECURE=1` | wymagane dla `KOBIAN_SMTP_TLS=none`; przy starcie ostrzeżenie KB-6207 i wpis w dzienniku audytu. Z jawnym SMTP nie można używać pliku poświadczeń (hasło nie jest wysyłane otwartym tekstem) — tylko przekaźnik w sieci lokalnej bez uwierzytelnienia |
| `KOBIAN_SMTP_FROM` | adres nadawcy, np. `kobian@firma.pl` |
| `KOBIAN_SMTP_TO` | odbiorcy rozdzieleni `,` lub `;`, najwyżej 10 |
| `KOBIAN_SMTP_LANG` | `pl` (domyślnie) lub `en` — język tematu i treści |
| `KOBIAN_SMTP_NOTIFY` | `problems` (domyślnie: e-mail tylko przy wyniku „ostrzeżenia” lub „błąd”) albo `all` (każde uruchomienie) |
| `KOBIAN_SMTP_MINIMAL=1` | tryb minimalny: tylko wynik, kod KB, komputer i identyfikator zadania — bez nazwy zadania i statystyk |
| `<sekrety>\smtp.credentials` | opcjonalny: wiersz 1 — login, wiersz 2 — hasło (tylko w tym pliku, nigdy w zmiennych środowiskowych); plik tylko dla konta usługi i Administratorów; brak pliku = wysyłka bez uwierzytelnienia |

Adresy muszą mieć postać `nazwa@domena` (tylko znaki ASCII, bez nazwy wyświetlanej, bez znaków sterujących; domenę IDN podaj w punycode). Błędna konfiguracja zatrzymuje start usługi z kodem 64 i komunikatem z kodem KB-6204 (ustawienia), KB-6205 (adres) albo KB-6207 (jawne SMTP bez flagi) — komunikat podaje nazwę zmiennej, nie jej wartość.

Microsoft 365: Basic Auth dla SMTP jest wyłączany w Exchange Online (odpowiedź `550 5.7.30` → KB-6201). OAuth2 dla M365 nie jest jeszcze dostępny w tej wersji — użyj przekaźnika SMTP organizacji.

## Wiadomość
Temat (≤ 120 znaków), np. `[Kobian] Kopia nieudana KB-2104: Faktury (SERWER-01)`. Treść text/plain + HTML: komputer, wynik, kod KB, zadanie, czasy i liczniki. Bez ścieżek, nazw plików, nazw baz i haseł. Nagłówki `Message-ID` i `X-Kobian-Event-Id` są stałe przy ponowieniach (deduplikacja po stronie skrzynki), `Auto-Submitted: auto-generated` blokuje autoodpowiedzi. Zalecane SPF/DKIM dla domeny nadawcy.

## Doręczanie
Certyfikat serwera jest zawsze sprawdzany (zaufany łańcuch, zgodna nazwa hosta); TLS 1.2/1.3. Limity: 30 s na operację SMTP, 90 s na próbę. Błędy przejściowe (odpowiedzi 4xx, sieć, limit czasu, błąd TLS/certyfikatu — KB-6206) — ponowienia po 1 min, 5 min, 30 min, 2 h, 6 h, potem `dead`. Błędy trwałe bez ponowień: odrzucone uwierzytelnienie (KB-6201), odpowiedź 5xx serwera, np. nieznany odbiorca (KB-6203). Wpis `dead` zostawia wpis w dzienniku audytu (`notify.email`) z kodem przyczyny.

## Raport dzienny
Raport o 07:00 (patrz wyżej) trafia do każdego włączonego kanału, także e-mailem — z kotwicą audytu w treści („Kotwica audytu: seq:sha256” i gotowe polecenie `kobian audit verify --anchor …`). Zachowaj te wiadomości poza tym komputerem.

# Alerty (D9.7, D9.8, BL-82)

Agent co minutę ocenia reguły alertów A01–A17 na podstawie katalogu i zapisuje ich stan (tabela `alert_alerts`). Alert ma klucz deduplikacji `kod:zakres` (np. `A01:job:<id>`) — dopóki warunek trwa, istnieje jeden alert i powiadomienie o nim wysyłane jest **raz**. Powiadomienia powstają tylko z przejść:

| Przejście | Zdarzenie | Kiedy |
|---|---|---|
| otwarcie | `pl.kobian.alert.opened.v1` | warunek reguły spełniony (cisza nocna opóźniałaby tylko ostrzeżenia — dziś bez konfiguracji, PROWIZORYCZNIE) |
| przypomnienie | `pl.kobian.alert.reminder.v1` | co 24 h dla nierozwiązanego (A13, A14, A16 — co 7 dni) |
| eskalacja | `pl.kobian.alert.reminder.v1` z `"escalated": true` | krytyczny/bezpieczeństwa nierozwiązany 24 h; trafia do tych samych kanałów (drugi odbiorca — później) |
| rozwiązanie | `pl.kobian.alert.resolved.v1` | warunek ustał |

Zdarzenia trafiają do każdego włączonego kanału (webhook, e-mail) przez ten sam trwały outbox co wynik uruchomienia, w tej samej transakcji co zmiana stanu alertu — restart usługi nie gubi i nie duplikuje powiadomień. Bez skonfigurowanych kanałów alerty są widoczne tylko na pulpicie (`GetStatus` → lista aktywnych alertów). Ustawienie `KOBIAN_SMTP_NOTIFY` dotyczy wyłącznie wyników uruchomień — alerty e-mailem wysyłane są zawsze.

**Zawartość:** kod reguły, ważność, kod KB, identyfikator zakresu (zadanie/cel/repozytorium/komputer), czasy, liczniki. Nazwa zadania tylko poza trybem minimalnym (`KOBIAN_WEBHOOK_MINIMAL=1`, `KOBIAN_SMTP_MINIMAL=1`). Nigdy: ścieżki, nazwy plików, adresy serwerów, hasła.

## Reguły aktywne w tej wersji

| Kod | Warunek | Źródło danych |
|---|---|---|
| A01 | ostatnie uruchomienie zadania `Failed` po wyczerpaniu ponowień (próba ≥ 3 albo 2 min bez nowej próby) | historia uruchomień |
| A02 | ostatnie uruchomienie `Warning` — **tylko pulpit**, bez natychmiastowego powiadomienia (wynik i tak trafia do kanałów jako wynik uruchomienia) | historia uruchomień |
| A03 | brak udanej kopii dłużej niż 1,5 × cykl harmonogramu (min. 26 h dla dziennych) — tylko zadania z włączonym harmonogramem | snapshoty, harmonogram |
| A04 | repozytorium uszkodzone: stan `corrupt`, kopia z oceną szkód (`check --repair`) albo najnowsza kopia zadania podejrzana (V2/V3) | katalog snapshotów |
| A09 | nowy lub zmieniony klucz serwera celu (SFTP/FTPS) czeka na zatwierdzenie Administratora | tożsamości serwerów |
| A14 | Agent działa na niezałatanym .NET (KB-7013) | kontrola przy starcie |
| A15 | ≥ 5 odmów dostępu do API w ciągu godziny | dziennik audytu |
| A17 | retencja wstrzymana po skoku zegara (KB-6117) — do zwolnienia przez Administratora | wstrzymanie retencji |

**Jeszcze nieaktywne** (Agent nie zbiera danych): A05 (test odtwarzania), A06 (wolne miejsce na celu), A07 (rekoncyliacja), A08 (zmiany retencji/harmonogramu z audytu), A10 (test zdolności celu), A11 (heurystyka ransomware), A12 (heartbeat — z założenia ocenia go usługa zewnętrzna), A13 (wygasanie certyfikatów/sekretów), A16 (poziom łatek systemu/SQL).

## Przykład zdarzenia (webhook)
```json
{"specversion":"1.0","id":"…","source":"urn:kobian:host:…","type":"pl.kobian.alert.opened.v1","time":"2026-10-17T10:00:00Z",
 "datacontenttype":"application/json","subject":"job:7a2e…",
 "data":{"alertId":"…","code":"A01","severity":"critical","category":"availability","kb":"KB-6101","transition":"opened","escalated":false,
  "reminders":0,"scope":{"kind":"job","id":"7a2e…"},"openedUtc":"2026-10-17T10:00:00Z","messageKey":"Alert.A01.Title","job":{"id":"7a2e…","name":"ERP-noc"}}}
```
E-mail: temat np. `[Kobian] Alert A03: Brak udanej kopii dłużej niż dopuszczalna przerwa (RPO) — ERP-noc (SERWER-01)`, w treści ważność, kod KB i zalecane działanie; przypomnienie, eskalacja i „Rozwiązano” mają własny przedrostek tematu.
