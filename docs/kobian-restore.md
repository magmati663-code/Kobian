# kobian-restore — odtwarzanie offline (US-43, D8.3)

Stan: it. 126. Samodzielne narzędzie (bez Agenta i katalogu): `list`, `restore`, `check`.

## Cel lokalny
`kobian-restore list|restore|check --repo <katalog> --key-file <plik.key> --password-file <plik> [--repo-header <kobian.repo>] [--snapshot <id|latest>] [--path <wzorzec>] --to <katalog>`

## Cel zdalny (it. 126, BL-44) — SFTP i FTPS
`kobian-restore restore --dest-scheme sftp --host <host> [--port <n>] --hostkey SHA256:<odcisk> --secrets-dir <katalog> --secret-ref file:<nazwa> --repo-header <lokalna kopia kobian.repo> --repo <ścieżka na serwerze> --key-file … --password-file … --to <katalog>`

- FTPS: `--spki SHA256:<pin>[,…]` zamiast `--hostkey` (brak pinu = łańcuch systemowy), opcjonalnie `--tls implicit`.
- SFTP wymaga `--hostkey` (pinning, Qb15); tożsamość serwera pochodzi wyłącznie z argumentów — narzędzie nie ma katalogu tożsamości (D4.11).
- Poświadczenia: plik JSON w katalogu `--secrets-dir` w formacie Agenta (`file:<nazwa>` → `<nazwa>.json`, rola Backup; ADR-0007). Hasła nie podaje się w argumentach.
- Nagłówek `kobian.repo` musi być lokalną kopią (`--repo-header`); narzędzie nie pobiera go z serwera.
- Nieobsługiwane w narzędziu samodzielnym: jawny FTP (D4.3) i SMB (wymaga konektora Windows) → kod wyjścia 64. SMB: podłącz udział jako dysk i użyj celu lokalnego.
- Sesja z rolą Backup — bez usuwania. Kody wyjścia: 0 OK, 1 Warning, 2 Failed, 64 składnia.

Implementacja: `OfflineRestore.RunAsync(…, remote)`, `StandaloneRemoteProviders` (Providers.Storage/Secrets); pliki `File*CredentialProvider` przeniesiono z Agenta do `Kobian.Providers.Storage.Secrets`. Niezweryfikowane na prawdziwym serwerze — W-52.
