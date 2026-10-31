# ADR-0012 — ProbeDestination i UpsertJob(validate_only) w Agencie (BL-60 cz. 2, it. 62)

Status: PROWIZORYCZNIE (Qb23). Uzupełnia ADR-0010 (kreator na porcie usługi) i ADR-0006 (pinning SFTP).

## Decyzja
- `ProbeDestination` (Operator, niemodyfikujące): sesja Backup, tożsamość serwera, wolne miejsce (+ pojemność dla local), obecność `kobian.repo`. Nic nie jest zapisywane.
- SFTP bez przypięcia: nowa opcja dostawcy `identity_probe=1` — `HostKeyReceived` zawsze `CanTrust=false`, metoda uwierzytelnienia „none” (żadne poświadczenie nie jest rozwiązywane ani wysyłane). Odcisk wraca przez `SftpProtocolException.PresentedIdentity` → `StorageException.PresentedIdentity`. Bez sondy brak przypięcia nadal = odmowa (Qb15 bez zmian).
- `UpsertJob` validate_only: `JobDraftValidator` — jedno źródło reguł pól dla UI i CLI; kody KB per pole bez wartości pól (NFR-11). Nowy kod KB-0106 (nazwa zajęta). Cel wewnątrz źródła (i odwrotnie) = KB-0102.
- Zapis (validate_only=false) jeszcze nie: KB-7012 (Qb23 b).

## Konsekwencje
+ Kreator może walidować krok S1–S3 i S2 (probe) na prawdziwym Agencie.
− Sonda SFTP niezweryfikowana na prawdziwym serwerze (W-20); brak statvfs dla SFTP (wolne miejsce = null).
Koszt zmiany zdania: mały.
