# Tożsamość serwera celu — zatwierdzanie klucza SSH i SPKI TLS (BL-35, D4.11, SEC-14.5)

Dotyczy celów **SFTP** (klucz hosta SSH) i **FTPS** (klucz publiczny certyfikatu, SPKI). Kobian nigdy nie ufa nowemu kluczowi serwera „na słowo”
(brak TOFU): każdy nowy albo zmieniony klucz czeka na decyzję **Administratora**, a do tego czasu kopie na ten cel są wstrzymane z kodem
**KB-2104** (SFTP) lub **KB-2109** (FTPS). Poświadczenie (hasło/klucz) nie jest wysyłane do serwera, którego klucz nie jest zatwierdzony.

## Skąd Agent bierze pin
- Zatwierdzone klucze są w katalogu Agenta (tabela `repo_dest_identities`), osobno dla każdego celu (schemat + host + port + katalog).
- Pole `hostKey` w definicji zadania (`--host-key` w `kobian init`) jest **jednorazowo** przenoszone do katalogu jako zatwierdzone
  (przy starcie Agenta i przy pierwszym połączeniu), tylko gdy cel nie ma jeszcze żadnego pinu. Istniejące zadania działają bez zmian.
- Późniejsza zmiana `hostKey` w zadaniu **nie** zmienia pinu — klucz serwera zmienia wyłącznie Administrator (opis niżej). Dzięki temu
  konto Operatora (albo złośliwe oprogramowanie w jego sesji) nie podmieni zaufanego serwera edycją zadania.

## Stany klucza
| Stan | Znaczenie | Połączenie |
|---|---|---|
| `pending` | serwer przedstawił klucz, którego nikt nie zatwierdził (pierwszy kontakt albo zmiana klucza) | odmowa KB-2104/KB-2109 |
| `expired` | oczekiwanie starsze niż 24 h — nie da się zatwierdzić; następne połączenie odnawia oczekiwanie | odmowa |
| `pinned` | zatwierdzony (Administrator, przeniesiony z definicji zadania albo — dla FTPS z ważnym CA — zapis informacyjny „(CA)”) | dozwolone |
| `rejected` | odrzucony przez Administratora — pozostaje odrzucony przy kolejnych połączeniach | odmowa |
| `retired` | zastąpiony nowym kluczem; jeśli wróci, znów czeka na zatwierdzenie | odmowa |

Przy zmianie klucza **stary pin zostaje** do czasu decyzji — kopia stoi (KB-2104/KB-2109, alarm), a nie przechodzi po cichu na nowy klucz.

## Jak zatwierdzić nowy klucz serwera
1. Zobacz oczekujące klucze (rola Operator lub wyższa):
   ```text
   kobian destinations identity list
   kobian destinations identity list --destination dest-0123456789abcdef --json
   ```
   Kolumny: identyfikator celu, `schemat://host:port`, rodzaj (`ssh-hostkey`/`tls-spki`), stan, odcisk `SHA256:…`, pierwsze wystąpienie (UTC).
2. **Porównaj odcisk poza pasmem** — na samym serwerze, nie przez to samo połączenie sieciowe:
   - SFTP (OpenSSH): `ssh-keygen -lf /etc/ssh/ssh_host_ed25519_key.pub` → `256 SHA256:… (ED25519)`;
   - FTPS: `openssl x509 -in cert.pem -pubkey -noout | openssl pkey -pubin -outform der | openssl dgst -sha256 -binary | openssl base64`
     (poprzedź `SHA256:`, usuń końcowe `=`).
   Jeśli odcisk się nie zgadza — **nie zatwierdzaj** (możliwy atak „man-in-the-middle”); odrzuć go i zbadaj sieć.
3. Zatwierdź (rola **Administrator**), kopiując **dokładnie** odcisk z listy:
   ```text
   kobian destinations identity approve dest-0123456789abcdef SHA256:<odcisk> --replace
   kobian destinations identity approve dest-0123456789abcdef SHA256:<odcisk>
   ```
   - `--replace` — serwer zmienił klucz (reinstalacja, nowy certyfikat z nowym kluczem): poprzednie piny celu przechodzą w `retired`.
   - bez `--replace` (dodaj) — serwer ma kilka kluczy jednocześnie, np. dwa węzły FTPS za load balancerem (AC-12.2); maks. 4 piny na cel (KB-2143).
4. Odrzucenie: `kobian destinations identity reject dest-0123456789abcdef SHA256:<odcisk>`.

Zatwierdzić można wyłącznie odcisk, który **serwer sam przedstawił** i który oczekuje dla tego celu — inny (np. przepisany z pomyłką)
kończy się **KB-2141**; oczekiwanie starsze niż 24 h — **KB-2142** (połącz się ponownie: uruchom zadanie albo sprawdzenie celu w kreatorze).
Każda decyzja i każda odmowa trafia do dziennika audytu (`kobian audit verify`): cel, odcisk, decyzja, SID Administratora.

## FTPS z certyfikatem z zaufanego urzędu (CA) i cele „wysokiego ryzyka” (SEC-14.5)
- Certyfikat poprawny wobec magazynu zaufania Windows i nazwa zgodna z hostem → połączenie przyjęte bez pinu. Zmiana klucza przy odnowieniu
  (np. Let's Encrypt co 90 dni) to **zdarzenie informacyjne**: SPKI zapisywany jako `pinned (CA)` (aktor `system:ca-chain`), bez zatrzymania kopii.
  Taki zapis nie jest pinem: certyfikat z tym kluczem, ale unieważniony albo wygasły, nadal zostanie odrzucony.
- Cel narażony (np. FTPS przez Internet) oznacz jako „wysokiego ryzyka” — wtedy ważny łańcuch CA nie wystarcza i każda zmiana SPKI
  czeka na zatwierdzenie jak klucz SSH:
  ```text
  kobian destinations identity require-pin dest-0123456789abcdef on
  kobian destinations identity require-pin dest-0123456789abcdef off
  ```
  Po włączeniu pierwsze połączenie zakończy się KB-2109, a bieżący SPKI pojawi się jako `pending` — zatwierdź go jak wyżej.

## Ograniczenia (stan budowy)
- Wymagane w planie potwierdzenie tożsamości (step-up, SEC-4.1) przy zatwierdzaniu nie jest jeszcze zaimplementowane — chroni rola Administrator
  i audyt. Ekran SCR-09 w aplikacji — osobny etap; do tego czasu CLI.
- Rotacja kluczy SSH przez `hostkeys-00@openssh.com` nie jest wykorzystywana — nowy klucz zawsze wymaga zatwierdzenia.
- Na prawdziwych serwerach OpenSSH/vsftpd zachowanie potwierdzane w kolejce testów W-20/SP-04.
