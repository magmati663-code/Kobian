# Powiadomienia e-mail — OAuth2 (Microsoft 365) — BL-45

Microsoft 365 wyłącza Basic Auth dla SMTP (KB-6201, „5.7.30”). Kanał e-mail obsługuje przepływ **client credentials** (aplikacja bez użytkownika) i SASL XOAUTH2.

## Konfiguracja (PROWIZORYCZNIE Qb105)
| Zmienna | Znaczenie |
|---|---|
| `KOBIAN_SMTP_AUTH=oauth2` | włącza OAuth2 (domyślnie `password`) |
| `KOBIAN_SMTP_OAUTH_TENANT` | identyfikator dzierżawy (GUID) albo domena (`contoso.onmicrosoft.com`) |
| `KOBIAN_SMTP_OAUTH_CLIENT_ID` | identyfikator aplikacji (client id) |
| `KOBIAN_SMTP_OAUTH_USER` | skrzynka nadawcza (domyślnie `KOBIAN_SMTP_FROM`) |
| `KOBIAN_SMTP_OAUTH_SCOPE` | opcjonalnie; domyślnie `https://outlook.office365.com/.default` |

Sekret klienta: plik `<sekrety>/smtp.oauth` — **jedna** linia (nigdy zmienna środowiskowa). Reszta (`KOBIAN_SMTP_HOST=smtp.office365.com`, `KOBIAN_SMTP_TLS=starttls`, `FROM`, `TO`) jak w trybie hasła. Po stronie Microsoft: aplikacja z uprawnieniem `SMTP.SendAsApp`, zarejestrowana jako principal skrzynki (Exchange Online).

## Zachowanie
- Token: `POST https://login.microsoftonline.com/<tenant>/oauth2/v2.0/token`, tylko HTTPS, bez przekierowań, odpowiedź ≤ 16 KB, limit 20 s; token w pamięci do `expires_in` − 5 min; po odrzuceniu uwierzytelnienia przez SMTP token jest unieważniany.
- Błędy: HTTP 4xx (poza 408/429) i odpowiedź niepoprawna → **KB-6208** (trwałe, wpis `dead`); 408/429/5xx/sieć → ponowienie (KB-6203). Zła konfiguracja → **KB-6209**; OAuth2 przez jawne SMTP odrzucone (KB-6207).
- Komunikaty błędów nie zawierają treści odpowiedzi serwera tożsamości, sekretu ani tokenu (NFR-11).
- Nie dodano pakietów (HttpClient + System.Text.Json; XOAUTH2 z MailKit).

## Poza zakresem (odłożone)
Przepływ delegowany/interaktywny (konto użytkownika), Google OAuth2, magazyn sekretu DPAPI zamiast pliku (SEC-9.4), konfiguracja w UI.
