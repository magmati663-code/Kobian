# ADR-0010: Kreator SCR-03 na porcie IWizardService, adapter do Agenta później

- Status: PROWIZORYCZNIE (Qb20), iteracja 59, 2026-10-14
- Dotyczy: R2.6.1 (SCR-03, SCR-12), D6.7, AC-28.1, AC-01.1–01.3, D4.11, BL-61, BL-60; uzupełnia ADR-0008/0009

## Kontekst
Priorytet właściciela: UI. Maszyna stanów kreatora (`WizardFlow`) istniała od rundy 3, widoku brakowało. Kontrakt kobian.v1 ma `InitJob` (repo + zadanie naraz, hasło z PLIKU czytanego przez Agenta), brak `UpsertJob(validate_only)`, `ProbeDestination`, `InitRepository` z kluczem odzyskiwania.

## Decyzja
1. Widok i ViewModel SCR-03 na porcie `IWizardService` (ValidateStep, Probe, CreateRepository, Save, RunFirstBackup); szkic `WizardDraft` bez sekretów; hasło trafia tylko do `CreateRepositoryAsync`, po sukcesie jest usuwane z VM.
2. Adapter gRPC NIE powstaje w tej iteracji: jedyna dziś droga (`InitJob`) wymagałaby zapisania hasła z UI do pliku na dysku, który czyta usługa na innym koncie — odrzucone ze względu na NFR-11. W aplikacji bez adaptera „Nowe zadanie” nadal pokazuje zaślepkę (uczciwie).
3. Klucz odzyskiwania: jeśli usługa go zwróci — ekran z numerowanymi grupami i przepisaniem 2 losowych różnych grup (AC-28.1, `RandomNumberGenerator`, porównanie stałoczasowe); jeśli nie zwróci — ekran pomijany (dzisiejszy `InitJob` nie tworzy opakowania recovery).
4. Presety: harmonogram domyślnie „codziennie 21:00”, retencja „Prosty” 7 dziennych / 4 tygodniowe / 12 miesięcznych; mini-test D6.4 zamykany od razu (brak RPC), V2 robi Agent po każdym backupie (it. 56).

## Konsekwencje / koszt zmiany zdania
- Potrzebne w kontrakcie (BL-60): `UpsertJob(validate_only)`, `ProbeDestination` (z odciskiem klucza hosta), `InitRepository` zwracające klucz odzyskiwania, hasło przekazywane w wiadomości po uwierzytelnionym pipe (zamiast pliku). Koszt: średni (proto + backend + adapter UI ~1 iteracja).
- Inne presety = zmiana 3 wartości domyślnych w VM — mały.
- Alternatywa odrzucona: adapter na `InitJob` z plikiem hasła w %TEMP% (sekret na dysku, ACL między kontami — W-kolejka); alternatywa 2: najpierw kontrakt, potem widok — wolniej do widocznego UI.
