# SMB destination (network share) — administrator guide (BL-73, it. 92)

Status: the "smb" provider is implemented and tested against a fake connector (Linux). The native Windows connection (`WNetAddConnection2`) is
**not yet verified on Windows** — queue item W-35. Since it. 93 SMB works in job definitions, `kobian init --dest-scheme smb --secret-ref file:<name>` and the Agent wizard RPC (the SMB field in the wizard view comes next).
The UNC root and the reference are sent to the Agent unchanged and validated before the repository is created (missing reference = KB-2102, nothing is written to the share).

- Root: `\\server\share\folder` (must exist). Connection without a drive letter, in the service logon session, only for the duration of the job (AC-04.3); parallel sessions share one connection.
- Kobian never changes SMB client settings (signing, encryption, NTLM).
- Objects are never overwritten (KB-2301); deletion only with the "maintenance" credential (D4.1, D4.6).
- Rejected roots: `\\?\`, `\\.\`, drive letters, ADS (`:`), `.`/`..`, device names, trailing dot/space, administrative shares (`C$`, `ADMIN$`, `IPC$`, `PRINT$`), IPv6.
- Credentials (PROVISIONAL Qb16/Qb42): `SecretRef` = `file:<name>` → `<secrets dir>\<name>.json` with `backup` and `maintenance` entries (`user`, `password`). Two different accounts to the same server at once are refused immediately (KB-2101, Windows error 1219).
- Codes: KB-2101 (1219), KB-2102 (1326/86/2202, no retries), KB-2103 (locked/disabled/expired), KB-2105 (5/1385), KB-2106 (53/67/1203), KB-2112 (1238), KB-2001 (network, retried), KB-2114 (3 failed session opens to the destination within an hour — attempts paused for 10 min; it. 97).

## Wizard, queue and connect timeout (it. 94)
- **Wizard (SCR-03, step S2):** "Network share (SMB)" — UNC folder and the credential **entry name** (`file:<name>`). The wizard never accepts the password; the secret file is created by an administrator. SMB has no server-identity step (that is SFTP, D4.11).
- **Queue (D7.4/D7.10, PROVISIONAL Qb44):** scheduled jobs to the same server with **different credential entries** wait for each other (state Queued) instead of failing with KB-2101; jobs with the same entry run in parallel (service-wide limit). If one entry has different `backup` and `maintenance` accounts, two parallel jobs using it can still hit KB-2101 during retention. Since it. 96 manual "Run now" waits in the same queue and since it. 97 it is shown as "Queued" in the job list and history.
- **Session-open throttling (D4.16 item 3, it. 97, PROVISIONAL Qb47):** for SMB and SFTP destinations at most 1 session open per 2 s per destination (2 immediately); after 3 failed opens within an hour (including a wrong password) attempts are paused for 10 min with KB-2114 — protects the account from lockout and the address from NAS/fail2ban auto-blocking. In-memory only (a service restart clears it).
- **Connect timeout (D4.13):** one connection attempt takes at most 30 s, then KB-2001 (transient, retried). A late system connection is closed when Windows returns it; the next attempt to that server first waits for it (at most 30 s), otherwise KB-2001. Windows check: W-36.
