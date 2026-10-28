# ADR-0040 — OAuth2 (client credentials) dla SMTP Microsoft 365 (BL-45, Qb105)
Status: PROWIZORYCZNIE (it. 129). Powiązane: 9.6, SEC-9.1, SEC-9.4, KB-6201/6208/6209.
Decyzja: `IOAuth2TokenProvider` + `ClientCredentialsTokenProvider` (HttpClient), `SmtpCredential.TokenProvider`, SASL XOAUTH2 w `MailKitSmtpTransport`. Sekret klienta w `smtp.oauth`.
Alternatywy: MSAL (nowy pakiet, duży łańcuch zależności — odrzucone bez audytu); przepływ delegowany (wymaga UI/odświeżania tokenów).
Konsekwencje: brak nowych zależności; jeden tenant na kanał; sekret w pliku tak jak hasło SMTP (docelowo DPAPI). Koszt zmiany zdania: niski (interfejs izoluje dostawcę).
