# ADR-0007 — Poświadczenia SFTP w Agencie: SecretRef `file:<nazwa>` (PROWIZORYCZNIE Qb16)

- Data: 2026-10-13, iteracja 52. Dotyczy: AC-01.3, D4.1, D4.4, BL-33, BL-15.
- Decyzja: `FileSftpCredentialProvider` (Agent/Secrets) rozwiązuje `SecretRef` postaci `file:<nazwa>` (`[A-Za-z0-9][A-Za-z0-9._-]{0,63}`, bez `..`) do pliku `<KOBIAN_SECRETS_DIR>/<nazwa>.json` (domyślnie `<KOBIAN_STATE_DIR>/secrets`). Format: `{"backup":{"user","password"|"privateKeyFile","passphrase"},"maintenance":{…}}`. Rola Backup i Maintenance osobno, brak wpisu = odmowa (KB-2102), bez przejścia na drugą rolę. `privateKeyFile` to nazwa pliku w tym samym katalogu.
- Zabezpieczenia: plik ≤ 64 KiB; poza Windows odmowa, gdy plik dostępny dla grupy/innych, jest dowiązaniem albo katalog zapisywalny przez innych; komunikaty błędów bez wartości sekretów i ścieżek. Na Windows ACL katalogu sekretów to BL-17 (kolejka W-14).
- Alternatywa: magazyn DPAPI (Windows, BL-24) / SecretRef do magazynu poświadczeń — docelowo; wymaga Windows do testów.
- Koszt zmiany zdania: mały — inna implementacja `ISftpCredentialProvider` + zmiana jednej linii w `AgentProviders.Create`; format `SecretRef` z prefiksem pozwala dodać `dpapi:`.
- Uwaga: sekrety w jawnych plikach to ustępstwo na czas budowy; do wydania 1.0 zastąpić DPAPI.
