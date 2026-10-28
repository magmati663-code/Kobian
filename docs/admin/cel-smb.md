# Cel SMB (udział sieciowy) — przewodnik administratora (BL-73, it. 92)

Stan: dostawca „smb” zaimplementowany i przetestowany na fałszywym konektorze (Linux). Natywne połączenie Windows (`WNetAddConnection2`)
**niezweryfikowane na Windows** — kolejka W-35. Od it. 93 cel SMB działa w definicji zadania (`docs/agent-job-definition.md`), w `kobian init`
i w kreatorze przez Agenta (`InitRepository` z `DestinationSpec.secret_ref`); pole SMB w samym widoku kreatora — kolejna iteracja.

```text
kobian init Biuro --source C:\Dane --dest \\nas\kopie\biuro --dest-scheme smb --secret-ref file:nas --password-file C:\sekret\pw.txt
```

Ścieżka UNC i `--secret-ref` idą do Agenta bez zmian (CLI nie zamienia ich na ścieżkę lokalną); Agent sprawdza je przed utworzeniem repozytorium
(brak odwołania = KB-2102, nic nie powstaje na udziale).

## Jak Kobian łączy się z udziałem (D4.2, D4.15)
- Ścieżka celu: `\\serwer\udział\folder` (separator `/` też przyjmowany). Folder musi istnieć.
- Połączenie **bez litery dysku**, w sesji konta usługi, tylko na czas zadania (`CONNECT_TEMPORARY`); nie widać go w `net use` użytkownika (AC-04.3).
- Równoległe sesje jednego zadania (do 4) korzystają z jednego połączenia; rozłączenie po zamknięciu ostatniej.
- Kobian **nie zmienia** ustawień klienta SMB (podpisywanie, szyfrowanie, NTLM) — serwer musi spełniać politykę systemu (Windows 11 24H2 / Server 2025 wymagają podpisywania).
- Pliki zapisywane zawsze jako nowe (bez nadpisywania — KB-2301); usuwanie wyłącznie poświadczeniem roli „maintenance” (D4.1, D4.6).

## Odrzucane ścieżki (KB-2106 / KB-2302)
`\\?\…`, `\\.\…`, litery dysku, strumienie ADS (`:`), segmenty `.`/`..`, nazwy urządzeń (`CON`, `NUL.txt`…), kropka lub spacja na końcu nazwy,
udziały administracyjne (`C$`, `ADMIN$`, `IPC$`, `PRINT$`), adresy IPv6 i „liczbowe” nazwy niebędące poprawnym IPv4. Podanie adresu IPv4 działa,
ale przy zablokowanym NTLM wymaga nazwy (Kerberos) — ostrzeżenie kreatora (D4.15).

## Poświadczenia (PROWIZORYCZNIE Qb16/Qb42)
`SecretRef` celu = `file:<nazwa>` → plik `<katalog sekretów>\<nazwa>.json` (tylko konto usługi; docelowo DPAPI — W-21):

```json
{"backup":{"user":"FIRMA\\kobian-backup","password":"<hasło>"},"maintenance":{"user":"FIRMA\\kobian-maint","password":"<hasło>"}}
```

Zalecenie (SEC-1.3, 4.6): konto „backup” z prawem tworzenia plików i folderów **bez** prawa usuwania; konto „maintenance” używane tylko przez retencję.
Uwaga: dwa różne konta do tego samego serwera w tym samym czasie są niemożliwe w Windows (błąd 1219) — Kobian odmawia od razu (KB-2101).

## Komunikaty
| Kod | Kiedy | Co zrobić |
|---|---|---|
| KB-2101 | inne konto jest już połączone z serwerem (1219) | zgłoś błąd; poczekaj na koniec innego zadania |
| KB-2102 | hasło/konto odrzucone (1326, 86, 2202) — bez ponowień | popraw poświadczenie w pliku sekretu |
| KB-2103 | konto zablokowane / wyłączone / hasło wygasło (1909, 1330, 1907, 1331, 1793) | odblokuj konto na serwerze |
| KB-2105 | brak uprawnień (5, 1385) | nadaj prawa do udziału i folderu |
| KB-2106 | udział lub folder nie istnieje (53, 67, 1203) | sprawdź ścieżkę |
| KB-2112 | limit połączeń serwera (1238) | brak — ponowienie |
| KB-2114 | 3 nieudane otwarcia sesji do celu w ciągu godziny — próby wstrzymane na 10 min (it. 97, D4.16 pkt 3, SEC-24.4) | popraw poświadczenie / sprawdź serwer; kolejna próba po przerwie |
| KB-2001 | błąd sieci (64, 59, 121, 1231…) | brak — ponowienie |

## Kreator, kolejka i limit łączenia (it. 94)
- **Kreator (SCR-03, krok S2):** opcja „Udział sieciowy (SMB)” — folder jako ścieżka UNC i **nazwa wpisu** poświadczenia (`file:<nazwa>`). Hasła kreator nie przyjmuje; plik sekretu zakłada administrator (wyżej). SMB nie ma kroku potwierdzania tożsamości serwera (to SFTP, D4.11).
- **Kolejka (D7.4/D7.10, PROWIZORYCZNIE Qb44):** zaplanowane zadania do tego samego serwera z **różnymi wpisami** poświadczeń czekają na siebie w kolejce (stan Queued) zamiast kończyć się KB-2101; zadania z tym samym wpisem idą równolegle (limit globalny usługi). Uwaga: jeśli jeden wpis ma różne konta `backup` i `maintenance`, dwa równoległe zadania z tym wpisem mogą nadal trafić na KB-2101 w fazie retencji — wtedy użyj osobnych wpisów albo różnych godzin. Od it. 96 także uruchomienie ręczne („Uruchom teraz”) czeka w tej kolejce, a od it. 97 widać je jako „W kolejce” na liście zadań i w historii.
- **Dławienie otwarć (D4.16 pkt 3, it. 97, PROWIZORYCZNIE Qb47):** dla celów SMB i SFTP najwyżej 1 otwarcie sesji na 2 s na cel (2 od razu); po 3 nieudanych otwarciach w ciągu godziny (także złe hasło) próby są wstrzymane na 10 min z KB-2114 — chroni konto przed blokadą i adres przed auto-blokadą NAS/fail2ban. Stan tylko w pamięci usługi (restart go zeruje).
- **Limit łączenia (D4.13):** jedna próba połączenia z udziałem trwa najwyżej 30 s; potem KB-2001 (błąd przejściowy — ponowienie). Spóźnione połączenie systemowe jest zamykane, gdy Windows je zwróci; kolejna próba do tego serwera najpierw czeka na nie (najwyżej 30 s), a gdy nie wraca — KB-2001. Do sprawdzenia na Windows: W-36.
