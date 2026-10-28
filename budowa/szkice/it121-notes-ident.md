# Notatki — agent „ident” (BL-35 cz.: D4.11, SEC-14.5, AC-12.2; zamyka L6; SCR-09 backend)

Kopia: `/tmp/kb/w-ident`. Wszystko zielone: build Release `-warnaserror` (Unit, Architecture, UI tests), pełny `Kobian.Tests.Unit` 1372/1372.

## Co zrobiono
1. **Migracja `repo/0004_dest_identities.sql`** — DDL wg R2.4.7 `dest_identities` (+ `endpoint`, `display`, `first_seen`, `last_seen`) jako
   `repo_dest_identities` (PK cel+odcisk, STRICT, WITHOUT ROWID; CHECK: id celu `dest-[0-9a-f]{16}`, odcisk `SHA256:` + base64 bez `=`/przecinka ≤ 64,
   kind `ssh-hostkey|tls-spki`, state `pending|pinned|rejected|retired`, pending ⇔ brak decyzji, inne ⇔ `approved_by_sid`+`approved_at`)
   oraz `repo_dest_identity_policy` (SEC-14.5 „wysokie ryzyko”: `require_pin` 0/1, kto/kiedy). Weryfikator `tools/verify-ident-ddl.py` (20 sprawdzeń, dopięty do `tools/ci.sh`).
2. **Czysta maszyna stanów** `src/Kobian.Agent/Jobs/ServerIdentityMachine.cs` (`Apply(rows, event, now)`): zdarzenia Observed (odmowa połączenia z odciskiem),
   Approve(add|replace), Reject, SeedFromDefinition (kompatybilność), TrustedByCa (TLS z ważnym CA). Reguły: pierwszy kontakt → pending (brak TOFU);
   zatwierdzenie tylko DOKŁADNIE oczekującego odcisku (KB-2141), ≤ 24 h (KB-2142, stan „expired” wyliczany); replace → pozostałe piny `retired`; add ≤ 4 piny
   (KB-2143, AC-12.2/CE-25); zmiana klucza przy pinie → nowy pending, stary pin zostaje (kopia staje z KB-2104/KB-2109); rejected pozostaje rejected;
   retired, który wraca → pending; CA → zapis informacyjny `pinned` z aktorem `system:ca-chain`, który NIE jest pinem połączenia (≤ 4, najstarsze retired);
   przy „wysokim ryzyku” taki SPKI po odmowie → pending. Limity: 8 pending/cel, 32 wiersze/cel, 500 pending globalnie (store), opis z serwera bez znaków sterujących ≤ 200.
3. **`ServerIdentityStore`** (katalog; przejścia atomowo w `CatalogWriter` — odczyt+Apply+UPSERT w jednej transakcji), `List`, `RequiresPin`, `SetRequirePinAsync`,
   `MigrateJobDefinitionsAsync` (start Agenta: `hostKey` z definicji SFTP/FTPS → pinned `system:job-definition`, gdy cel nie ma pinu). Fail-closed:
   nieznany stan → rejected, nieparsowalny czas → epoka (pending wygasły).
4. **Podłączenie do sesji** — `ServerIdentityGate` (dekorator `IStorageProviderFactory` dla sftp/ftps; `AgentProviders.Create(..., identities:)`, Program.cs):
   id celu = `JobDestination.Id` z konfiguracji (wspólne dla job/init/check/restore/prune/probe); piny do opcji dostawcy z katalogu (`hostkey`/`spki` lista);
   pin z definicji przenoszony tylko przy braku pinu (Operator nie podmieni pinu edycją zadania); ProbeDestination (`Id="probe"`) niczego nie przenosi — odcisk
   z żądania sprawdzany tylko w tym połączeniu; SFTP bez pinu → `identity_probe` (bez poświadczenia); odmowa z odciskiem → Observed; FTPS przyjęty z SPKI spoza
   pinów → TrustedByCa; `require_pin=1` dla celów wysokiego ryzyka. Adaptery: `SshNetOptions` lista ≤ 4 pinów + `HostKeyPin.MatchesAny`; `FtpOptions.RequirePin`,
   `FtpsCertificatePolicy.Evaluate(..., requirePin)` (CA nie wystarcza).
5. **RPC** (agent.proto): `ListServerIdentities` (Operator, niemodyfikujące — jak ListDestinations Qb38), `ApproveServerIdentity` {destination_id, fingerprint,
   decision ADD|REPLACE|REJECT} (Administrator, modyfikujące), `SetServerIdentityPolicy` (Administrator, modyfikujące); MethodPolicy, AgentCallAudit (cel,
   odcisk, decyzja / require_pin), SID decydującego z `ResolveCaller` zapisywany w `approved_by_sid`; walidacja kształtu (KB-0103), decyzja UNSPECIFIED odrzucana.
   IAgentBackend — domyślne KB-7012; CatalogAgentBackend `Identities` (init).
6. **CLI**: `kobian destinations identity list [--destination <id>] [--json] | approve <id> <SHA256:…> [--replace] | reject <id> <SHA256:…> | require-pin <id> on|off`
   (IAgentClient domyślne metody, GrpcAgentClient). UI — nie ruszane (ekran robi inny agent; kontrakt gotowy).
7. **Dokumentacja**: `docs/admin/tozsamosc-serwera.md` + `.en.md` (stany, jak zatwierdzić nowy klucz, porównanie poza pasmem ssh-keygen/openssl, CA i
   „wysokie ryzyko”, KB-2141/2142/2143), odnośniki w `docs/README.md`, `docs/agent-api.md`, `docs/admin/cel-ftps(.en).md`.

## Pliki
Nowe: `src/Kobian.Catalog.Sqlite/Migrations/repo/0004_dest_identities.sql`, `src/Kobian.Agent/Jobs/ServerIdentityMachine.cs`, `.../ServerIdentityStore.cs`,
`.../ServerIdentityGate.cs`, `tools/verify-ident-ddl.py`, `docs/admin/tozsamosc-serwera.md`, `docs/admin/tozsamosc-serwera.en.md`,
`tests/Kobian.Tests.Unit/Agent/ServerIdentityMachineTests.cs`, `.../ServerIdentityStoreTests.cs`, `.../ServerIdentityGateTests.cs`, `.../ServerIdentityApiTests.cs`,
`tests/Kobian.Tests.Unit/Storage/PinListOptionsTests.cs`.
Zmienione: `src/Kobian.Api.Contracts/Protos/kobian/v1/agent.proto`, `src/Kobian.Agent/Hosting/{MethodPolicy,AgentCallAudit,AgentGrpcService,IAgentBackend,CatalogAgentBackend,AgentProviders}.cs`,
`src/Kobian.Agent/Program.cs`, `src/Kobian.Providers.Storage/Sftp/{SshNetSupport,SshNetSftpConnector}.cs`, `src/Kobian.Providers.Storage/Ftp/{FluentFtpSupport,FluentFtpConnector}.cs`,
`src/Kobian.Cli/Commands/{AgentClient,GrpcAgentClient,CliApp}.cs`, `tests/Kobian.Tests.Unit/TestDoubles/{FakeSftpServer,FakeFtpServer}.cs` (opcjonalna weryfikacja
klucza/TLS, domyślnie wyłączona — dotychczasowe testy bez zmian), `tools/ci.sh` (weryfikator), `docs/kb-codes.yaml` + wygenerowane `src/Kobian.Domain/KbCode.cs`,
`docs/kb-codes.md`, `src/Kobian.UI/Resources/KbMessages*.resx`, `docs/README.md`, `docs/agent-api.md`, `docs/admin/cel-ftps.md`, `docs/admin/cel-ftps.en.md`.

## Pakiety
Brak nowych.

## Nowe kody KB (zakres 21xx transport; numery 2141–2143 wybrane z dala od 2116+, by nie kolidować z innymi agentami)
- KB-2141 `ServerIdentityNotPending` (permanent/error) — odcisk nie oczekuje dla celu.
- KB-2142 `ServerIdentityApprovalExpired` (permanent/warning) — oczekiwanie > 24 h.
- KB-2143 `ServerIdentityPinLimit` (permanent/error) — 4 piny, użyj „zastąp”.

## Testy (uruchomione)
- `Agent.ServerIdentityMachineTests` 13 (wszystkie przejścia + zabronione + test właściwości 300×60 zdarzeń; mutacja „approve wygasłego” wykryta przez 3 testy).
- `Agent.ServerIdentityStoreTests` 6 (DDL/CHECK, utrwalanie i polityka, równoległe przejścia, globalny limit, fail-closed czasu, migracja pinów z definicji).
- `Agent.ServerIdentityGateTests` 6 (SFTP zmiana klucza → KB-2104 bez poświadczenia → zatwierdzenie → działa; Operator nie podmieni pinu definicją; pierwszy
  kontakt przez identity_probe; AC-12.2 20 połączeń naprzemiennie, trzeci klucz → KB-2109; CA informacyjnie vs „wysokie ryzyko”; prawdziwy TLS na 127.0.0.1
  self-signed → KB-2109, hasło niewysłane → zatwierdzenie → probe OK bez pinu w żądaniu).
- `Agent.ServerIdentityApiTests` 4 (gRPC+CLI: Reader/Operator odrzuceni i audyt „denied”; Administrator: KB-2141/KB-0103 „failed”, zatwierdzenie/odrzucenie/
  require-pin „allowed” z SID i odciskiem; mapa ról; walidacja; wygasłe).
- `Storage.PinListOptionsTests` 2 (lista pinów SSH, require_pin FTPS).
- Regresja: pełny `Kobian.Tests.Unit` **1372 passed / 0 failed** (77 s); `Kobian.Tests.Architecture` 16/16; build `Kobian.Tests.UI` OK.
  Uwaga: w pierwszym pełnym przebiegu raz padł `Engine.DedupMemoryPerfTests` (50 B/blok > 48 — pomiar pamięci pod obciążeniem maszyny); osobno i w drugim
  pełnym przebiegu przechodzi — niezwiązane z tą zmianą (flaky perf).
- `python3 -I tools/verify-ident-ddl.py` — 20 OK.

## Przegląd bezpieczeństwa
Lista kontrolna: brak sekretów w logach/audycie (tylko publiczne odciski, endpoint bez poświadczeń); porównania pinów stałoczasowe (istniejące `Matches`),
zatwierdzenie = porządkowe porównanie z wartością z katalogu (nie ze źródła klienta); SQL tylko parametry; walidacja id celu/odcisku/aktora (+ CHECK w DDL);
limity (pending/cel, wiersze/cel, pending globalnie, długość opisu); brak wyłączania walidacji certyfikatów (require_pin ZAOSTRZA); brak `Random` do kluczy
(Random tylko w teście właściwości jako generator zdarzeń); poświadczenie nie trafia do serwera z niezatwierdzonym kluczem (identity_probe / TLS przed USER).
STRIDE nowych elementów:
| Element | Zagrożenie | Mitygacja |
|---|---|---|
| ApproveServerIdentity | S/E: malware w sesji Operatora zatwierdza klucz MITM | Administrator (MethodPolicy), audyt z SID, tylko odcisk przedstawiony przez serwer; step-up — brak (niżej) |
| hostKey w definicji (Operator) | E: podmiana zaufanego serwera edycją zadania | pin z definicji tylko przy braku pinu; dalej wyłącznie Administrator (test) |
| Gate/Observed | D: zalanie katalogu kluczami (probe wielu celów, serwer rotujący klucze) | 8 pending/cel, 32 wiersze/cel, 500 pending globalnie |
| Zapis „CA” | T/S: unieważniony certyfikat przyjęty „przez pin” | zapis CA nie jest pinem połączenia (IsEffectivePin) — test |
| repo_dest_identities | T: ręczna edycja katalogu | CHECK w DDL; nieznany stan → rejected; zły czas → wygasłe (fail-closed); poza tym jak cały katalog (D2.14) |
| display z serwera | I/T: wstrzyknięcie znaków sterujących do CLI/UI | czyszczenie + 200 znaków |
Znaleziska: **Średnie** — brak step-up (SEC-4.1) przy zatwierdzaniu (w kodzie nie ma infrastruktury step-up); **Niskie** — ListServerIdentities dla Operatora
ujawnia SID-y decydujących (dane administracyjne, nie sekret); brak osobnego zdarzenia/alertu przy zapisie „CA” (jest wiersz z aktorem system:ca-chain,
nie ma wpisu w core_events/alertu) i przy nowym pending (alarm idzie z kodem KB-2104/2109 przebiegu jak dotąd). Wysokie — brak.

## Decyzje PROWIZORYCZNE (do nadania numerów Qb)
- **Qb-NEW-dest-prefix**: tabela jako `repo_dest_identities` w module `repo` (jak `repo_dest_parallelism`), nie `dest_identities` w nowym module `dest`;
  brak FK do `dest_targets` (nie istnieje; cel = skrót z definicji). Alternatywa: moduł `dest` w `CatalogBootstrap.ModuleOrder` + `dest_targets`. Koszt zmiany: mały (migracja kopiująca).
- **Qb-NEW-ident-list-role**: `ListServerIdentities` = Operator (spójnie z Qb38 ListDestinations); R2.6.1 SCR-09 = Admin. Koszt: 1 linia MethodPolicy.
- **Qb-NEW-ident-expiry**: timeout 24 h jako stan wyliczany „expired” (nie zapis „rejected”); nowe połączenie odnawia oczekiwanie. Alternatywa: zapis rejected
  przez zadanie okresowe (wtedy odrzucony „czasem” musiałby być rozróżniany od odrzuconego przez Admina). Koszt: mały.
- **Qb-NEW-ident-ca-record**: SPKI przy ważnym CA zapisywany jako `pinned` z aktorem `system:ca-chain`, ale nie przekazywany jako pin (inaczej unieważniony
  certyfikat przeszedłby „przez pin”). Alternatywa: osobny stan `observed` w DDL. Koszt: mały.
- **Qb-NEW-ident-seed**: pin z `hostKey` definicji przenoszony jako zatwierdzony (aktor `system:job-definition`) tylko przy braku pinu; późniejsze zmiany
  `hostKey` ignorowane (pole w definicji staje się „wartością początkową”). Alternatywa: usunąć pole z definicji v2 / walidować zgodność przy UpsertJob (KB). Koszt: średni (UI kreatora).
- **Qb-NEW-ident-step-up**: brak step-up (SEC-4.1) — tylko rola Administrator + audyt; do dodania razem z infrastrukturą step-up (D2.9).
- Identyfikacja tożsamości per cel (host+port+katalog), nie per host:port — zgodnie z DDL planu; dwa katalogi na tym samym serwerze = dwa zatwierdzenia.

## Kolejka Windows / prawdziwe serwery
- Brak nowych testów `[Trait("Category","Windows")]`. Do kolejki W-20/SP-04 (istniejące): na prawdziwym OpenSSH — `kobian init` z `--host-key`, następnie
  wymiana klucza hosta (`ssh-keygen -A` po usunięciu kluczy) → `kobian job run` = KB-2104, `kobian destinations identity list` pokazuje pending z odciskiem równym
  `ssh-keygen -lf /etc/ssh/ssh_host_ed25519_key.pub`, `approve … --replace` (konto Administratora) → kopia OK; vsftpd self-signed: analogicznie KB-2109;
  certyfikat z CA + `require-pin on` → KB-2109 do zatwierdzenia. Oczekiwane: poświadczenie nie trafia do serwera przed zatwierdzeniem (log serwera bez USER/userauth).

## Niedokończone / następne kroki
- Step-up przy ApproveServerIdentity/SetServerIdentityPolicy (SEC-4.1).
- Zdarzenia `core_events` / alert A09 przy nowym pending i informacyjne przy zmianie SPKI z CA (SEC-14.5 „logowana jako zdarzenie informacyjne” — obecnie tylko wiersz w katalogu).
- Adapter UI (SCR-09) — kontrakt RPC gotowy; widoków nie zmieniano.
- `DestinationProbeService` przy celu z pinem w katalogu i innym kluczu zwraca UnknownIdentity (nie Failed), bo żądanie nie zawiera hostKey — UI powinno pokazać
  „klucz zmieniony, czeka na zatwierdzenie” na podstawie `ListServerIdentities`.
- `docs/ac-coverage.md` nie regenerowany (robi to ci.sh).
