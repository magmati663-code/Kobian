# ADR-0013 — InitRepository: hasło w wiadomości, klucz odzyskiwania, zadanie wyłączone do zapisu (it. 63)

Status: PROWIZORYCZNIE (Qb24). Zastępuje pkt 2 ADR-0010 (brak adaptera kreatora).

## Kontekst
`InitJob` wymaga pliku hasła czytanego przez Agenta — z UI oznaczałoby to sekret w %TEMP% między kontami (odrzucone w ADR-0010).

## Decyzja
- Nowe RPC `InitRepository` (Operator + rewizja): hasło w wiadomości po pipe z autoryzacją (D2.9; na Windows pipe z ACL — BL-17). Agent waliduje hasło (SEC-6.5) i pola (JobDraftValidator).
- Hasło zapisywane wyłącznie w katalogu stanu Agenta (`secrets/repo-<repoId>.pw`, `FileMode.CreateNew`, Unix 0600 od utworzenia, bufor zerowany). To ten sam model co sekrety SFTP (ADR-0007); DPAPI/ACL — W-21.
- `RepoProvisioner.CreateWithRecovery`: drugi keyId (typ „recovery”, KEK = 32 B klucza K14.1) na uwierzytelnionej liście nagłówka; plik klucza na celu przed nagłówkiem; tekst klucza zwracany raz, nigdzie nie zapisywany (test przeszukuje pliki).
- Zadanie `enabled=0` do `UpsertJob(validate_only=false)`, który włącza zadanie i ustawia harmonogram.
- UI: `GrpcWizardService` na kanale pulpitu; `App` podaje fabrykę kreatora powłoce.

## Konsekwencje
+ Kreator działa end-to-end (test przez pipe: walidacja → probe → repo + klucz odzyskiwania → zapis → pierwsza kopia z V2).
− Sekret hasła na dysku Agenta (jak sekrety SFTP) — wymaga ACL/DPAPI na Windows przed wydaniem (W-21, blokuje M1).
− Porzucony kreator zostawia repozytorium i wyłączone zadanie.
