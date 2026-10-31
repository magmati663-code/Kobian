# ADR-0026 — Cel zdalny (smb/sftp) w definicji zadania v1

- Status: PROWIZORYCZNIE (Qb43), it. 93 (rola 3 prowadzi; 3,4,2,11)
- Dotyczy: ADR-0005 (definicja v1, rozszerzona), ADR-0006/Qb15 (pinning SFTP), ADR-0025/Qb42 (SMB), D4.1, D4.6, D4.11, D8.9, AC-01.3

## Kontekst
Definicja zadania v1 przyjmowała tylko cel `local`; 10 miejsc w Agencie budowało `new DestinationConfig(id, scheme, root)` bez `SecretRef`
i opcji, więc zadanie SMB/SFTP (gdyby powstało) kończyłoby się KB-2102 przy każdym uruchomieniu. `RepoInitService` i `JobDraftValidator`
wymagały `Path.IsPathFullyQualified` dla korzenia — UNC nie spełnia tego poza Windows, ścieżka SFTP jest uniksowa niezależnie od systemu Agenta.

## Decyzja
1. Sekcja `destination` = `scheme`, `root` + opcjonalnie `secretRef`, `host`, `port`, `hostKey`; inne pola → KB-0103 (sekret nie ma miejsca w definicji).
2. Jedno miejsce reguł: `JobDestination.Validate` (parser definicji, init repozytorium, walidacja szkicu kreatora). W zadaniu (inaczej niż w ProbeDestination):
   smb wymaga `secretRef` (`file:<nazwa>`, ta sama reguła nazwy co magazyn sekretów), sftp wymaga `host`, przypiętego `hostKey` (bez TOFU) i `secretRef`;
   cel lokalny nie może mieć poświadczenia.
3. `JobDefinition.ToDestination(id)` i `JobDefinition.DestinationId` zastępują ręczne konstrukcje; identyfikator `local`/`smb` bez zmian
   (skrót `schemat:korzeń`), SFTP z hostem i portem.
4. Kontrakt: `InitJobArgs` + `secret_ref`, `host`, `port`, `host_key` (pola opcjonalne, zgodne wstecz); CLI `kobian init --dest-scheme smb|sftp --secret-ref … [--host --port --host-key]`;
   kreator przez `InitRepository.destination` (DestinationSpec już miał te pola).

## Alternatywy
- Osobny obiekt „cel” w katalogu (`dst_destinations`) z odwołaniem z zadania — zgodne z modelem planu (wiele zadań na jeden cel), ale wymaga DDL i migracji; odroczone.
- TOFU dla SFTP w zadaniu — odrzucone (Qb15: zmiana klucza = alarm).

## Koszt zmiany zdania
Mały: format sekcji jest addytywny; przejście na `dst_destinations` = migracja definicji (pole `destination` → `destinationId`) w jednym miejscu (`JobDestination`).
