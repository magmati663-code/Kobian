# ADR-0006 — Adapter SSH.NET: pinning klucza hosta bez TOFU (PROWIZORYCZNIE Qb15)

- Data: 2026-10-13, iteracja 51. Dotyczy: D4.4, D4.11, SEC-4.7, BL-33.
- Decyzja: `SshNetSftpConnector` łączy się tylko z serwerem, którego odcisk SHA-256 klucza hosta (format OpenSSH `SHA256:<base64>`) jest podany w `DestinationConfig.Options["hostkey"]`. Brak opcji = odmowa (KB-2104); niezgodny klucz = KB-2104 (klasa Security, bez ponowień).
- Alternatywa: TOFU z zatwierdzeniem w UI (maszyna stanów D4.11) — wymaga GUI/Agenta w pętli zatwierdzania; odłożone.
- Koszt zmiany zdania: niski — dodanie stanu „oczekuje na zatwierdzenie” w konektorze i opcji `hostkey=tofu`; format opcji bez zmian.
- Zakres: tylko `SftpClient`; `ScpClient` zakazany (CVE-2026-48798). Poświadczenia przez `ISftpCredentialProvider` (SecretRef), klucz prywatny zerowany po połączeniu. Rename zwykły (K24.3). Rozszerzenia OpenSSH (fsync/statvfs/limits) wyłączone do testu na serwerze (W-13).
- Pytanie do właściciela Qb15: czy zostaje „pin wymagany”, czy dopuścić TOFU z zatwierdzeniem? Do tego czasu obowiązuje pin wymagany.
