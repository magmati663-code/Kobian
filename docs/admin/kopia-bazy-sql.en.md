# SQL Server database backup — administrator guide

> Build status (it. 74, before release 0.1). Describes behaviour implemented and tested on Linux against a stand-in server;
> items marked **(requires Windows)** await verification in `budowa/do-uruchomienia-na-windows.md`. Polish: `kopia-bazy-sql.md`.

## 1. What Kobian does
- Connects to SQL Server as the Agent service account (default `NT SERVICE\KobianAgent`) or with a SQL login from the Agent secret store.
- Runs `BACKUP DATABASE … TO DISK … WITH COPY_ONLY, CHECKSUM` into a staging folder, checks the `msdb` backup set and `RESTORE VERIFYONLY`, encrypts and uploads the file to the repository, and always removes the staging file.
- `COPY_ONLY` does not break the backup chain kept by other tools (Maintenance Plans, ERP agents).
- Supported: SQL Server 2017–2025; 2014/2016 conditionally (warning KB-3401); older versions are refused.

## 2. Preparing the SQL Server
| Step | Who | How |
|---|---|---|
| TLS encryption | SQL administrator | SQL Server Configuration Manager → Protocols → Certificate; "Force Encryption" recommended. Without TLS the wizard reports KB-3006. |
| Permissions | SQL administrator | script from the wizard ("Show permission script", minimal variant D5.7): service-account login + `db_backupoperator` in the chosen databases. The wizard never runs it. |
| Staging | system administrator | folder with room for a full backup of the largest database; `KOBIAN_DB_STAGING` (default `<state>\staging`), `KOBIAN_DB_SQL_STAGING` when SQL Server sees the folder under another path. ACL: Agent and SQL service account only **(requires Windows)**. |
| SQL login (optional) | system administrator | file `<secrets>\<name>.json` with `{"backup":{"user":"…","password":"…"}}`, readable by the Agent account only; the wizard only takes `<name>`. |

## 3. Wizard — "SQL Server database" step
1. Enter the server: `SERVER`, `SERVER\INSTANCE` (port from SQL Browser) or `server,1433`. `np:`/`admin:` prefixes are rejected.
2. Choose sign-in: service account or SQL login (credential name).
3. **Server certificate** — click "Get server certificate". The Agent reads the certificate from the TLS handshake without logging in and shows:
   - the SHA-256 fingerprint in groups of 4 characters,
   - subject, issuer, expiry, "self-signed" or "trusted by the system — approval optional".
4. Compare the fingerprint with the SQL administrator (on the SQL Server in PowerShell: `$c = Get-Item Cert:\LocalMachine\My\<SHA-1 thumbprint>; [BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash($c.RawData)) -replace '-'` — the `certlm.msc` dialog shows only the SHA-1 thumbprint). **Approve only if it matches.** The Agent fetches the certificate again, compares it and stores it in `<state>\certs`; the file name appears in the certificate field.
5. "Show databases" — list with size, recovery model and warnings (TDE without certificate backup, growing log, missing permission). A database without backup permission blocks "Next" and expands the permission script.

## 4. Messages
| Code | Meaning | Action |
|---|---|---|
| KB-3001 | cannot connect / timeout | SQL service, firewall (TCP 1433 or instance port, UDP 1434 for SQL Browser) |
| KB-3003 | BACKUP DATABASE missing | permission script from the wizard |
| KB-3004 | certificate differs from the approved one | certificate replaced on the server or someone impersonates it — check with the SQL administrator, approve the new one under a new name |
| KB-3006 | server presented no TLS certificate | enable encryption on the server; check the address points to SQL Server |
| KB-3007 | certificate changed between display and approval | fetch again and compare; repeated changes = suspected attack |
| KB-3008 | certificate file name taken | approve under a new name (the suggested name contains part of the fingerprint) |
| KB-3401 | version conditionally supported / unsupported | upgrade SQL Server |

## 5. Renewing the SQL Server certificate
After the certificate is replaced, backups report KB-3004 (the job will not connect to an unapproved server). Open the job in the wizard, fetch and approve the new certificate (new file name), save the job. Old `.cer` files in `<state>\certs` are removed by the administrator.

## 6. Security
- The wizard and CLI never see SQL passwords; the job definition holds only a `file:<name>` reference.
- Fetching the certificate never sends credentials; the pin is written by the Agent from the certificate it received itself (the client only sends the fingerprint to compare).
- Database and server names are not written to logs or events (NFR-11).
- NTLM: when the service account signs in to a server outside a trusted domain, consider restricting the server list (RD-39, planned).

## SQL Server on a different computer than the Kobian service
By default the service connects only to a SQL Server on the same computer. To back up a database from another server, the administrator sets the service environment variable `KOBIAN_SQL_ALLOWED_SERVERS` (e.g. `sql01;sql02\ERP`) and restarts the service. Otherwise the wizard shows KB-3009.
