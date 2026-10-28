# FTPS destination (FTP server with TLS) — administrator guide (BL-34)

Status: the "ftps" provider (and "ftp" behind a flag) is implemented on FluentFTP 55.0.0 and tested against an in-memory server and a scripted
FTPS control server on 127.0.0.1 (real TLS, SPKI pinning, login). **Transfers against real servers are not yet verified** — vsftpd with
`require_ssl_reuse=YES`, FileZilla Server, IIS FTP, ProFTPD: queue SP-04/SP-21. Until then treat FTPS as "early access".

```text
kobian init Office --source C:\Data --dest /backup/office --dest-scheme ftps --host nas.company.local --secret-ref file:nas-ftp --password-file C:\secret\pw.txt
kobian init Office --source C:\Data --dest /backup/office --dest-scheme ftps --host 192.168.1.20 --port 990 --host-key SHA256:<SPKI fingerprint> --secret-ref file:nas-ftp --password-file C:\secret\pw.txt
```

## Connection modes (D4.3)
| Mode | How | Notes |
|---|---|---|
| Explicit FTPS (`AUTH TLS`, port 21) | default | recommended; TLS 1.2/1.3, data channel encrypted too (`PROT P`) |
| Implicit FTPS (port 990) | `--port 990` (PROVISIONAL: port 990 = implicit mode) | older NAS devices |
| Plain FTP | **not available for jobs** (KB-2115) | the password would travel in clear text; the "ftp" provider requires `insecure=1`, job definition v1 has no such field |

Kobian **never** silently falls back from FTPS to plain FTP: a server refusing `AUTH TLS` = KB-2110. The data channel is always passive; the address
in the `PASV` reply is ignored (data connects to the control address — protects against FTP bounce and broken NAT). IPv6-only servers need EPSV
(provider option `passive=epsv`, not yet exposed in the job definition).

## Wizard in the app (SCR-03, step 2)
Destination type **“FTPS server”**: server, port and “Connection encryption” — *Standard (recommended) · port 21* = Explicit, *Older NAS servers · port 990* = Implicit
(the mode follows the port, PROVISIONAL Qb68), the credential entry name (as above — the wizard never accepts the password) and a folder starting with “/”. After
“Next” the wizard checks the connection; if the system does not trust the certificate, it shows the SPKI fingerprint to compare and, once approved, stores it in the
job (`hostKey` field). Plain FTP is not offered in the wizard.

## Server identity — certificate and SPKI pin (D4.11)
1. Certificate from a trusted CA (Windows system store) and a name matching `--host` → accepted, no pin needed. A key change with a valid chain is not an error. Revocation (CRL/OCSP) is checked too — without access to the CRL the chain is treated as invalid and a pin is needed (item 2).
2. Self-signed certificate, invalid chain or name mismatch (e.g. connecting by IP) → an **SPKI pin** is required: `SHA256:<base64>` — SHA-256 of the public key (SubjectPublicKeyInfo), not of the whole certificate, so it **survives certificate renewal with the same key**.
3. No pin or a different key → **KB-2109**, backups stop. The wizard / ProbeDestination shows the fingerprint presented by the server — compare it out of band (on the server) before approving.
4. Since BL-35 pins live in the Agent catalog and are approved by an Administrator (`kobian destinations identity approve`); a "high-risk" destination requires a pin even with a valid CA — see `tozsamosc-serwera.en.md`.

Fingerprint on the server (OpenSSL):

```sh
openssl x509 -in /etc/ssl/certs/vsftpd.pem -pubkey -noout | openssl pkey -pubin -outform der | openssl dgst -sha256 -binary | openssl base64
```

Prefix the result with `SHA256:` and strip trailing `=`. The certificate is checked before `USER`/`PASS` are sent — a rejected server never receives
the password. There is no "accept any certificate" option.

Known limitation (FluentFTP 55.0.0): the **data channel** certificate is not verified by the library (the control channel is, and the data connection
always goes to the same address). Repository data is encrypted and authenticated (AEAD) and every write is verified by size and read-back — tampering
is detected but not prevented. To be resolved in SP-04.

## Credentials (PROVISIONAL Qb16)
`SecretRef` = `file:<name>` → `<secrets dir>\<name>.json` (service account only; DPAPI later — W-21), same format as SMB/SFTP:

```json
{"backup":{"user":"kobian-backup","password":"<password>"},"maintenance":{"user":"kobian-maint","password":"<password>"}}
```

Recommendation (D4.1, SEC-1.3): the "backup" account may write and create directories but **not** `DELE`/`RMD` or overwrite (e.g. vsftpd
`cmds_denied=DELE,RMD`; FileZilla Server: no "Delete"); the "maintenance" account is used by retention only. A Kobian backup session never deletes anyway.

## Writes without overwrite — how and the limitation (D4.7)
- New object: `STOR` to a temporary `<name>.tmp-<random>`, size check with `SIZE` in binary mode (`TYPE I`, up to 3 tries 1 s apart), then `RNFR`/`RNTO`.
- FTP has no "rename but do not replace"; on Unix servers `RNTO` and `STOR` overwrite an existing file. Kobian checks that the target does not exist
  (`MLST`) **right before** `RNTO`/`STOR` — a short **race window** remains: if another program creates the same file between the check and `RNTO`,
  it is overwritten. In practice object names are unique (content hashes) and a repository has a single writer; IIS and FileZilla Server reject `RNTO`
  onto an existing file — then KB-2301. Full protection requires an account without overwrite rights (level P2, D4.12).
- Interrupted transfer: resumed with `APPE` from the size confirmed by `SIZE`; a size mismatch abandons the object (KB-2401).
- Leftover `.tmp-*` files are cleaned up by retention (maintenance account).

## Server requirements
`FEAT` with `MLST`/`MLSD` (recommended; otherwise `LIST`), `SIZE`, `REST STREAM` (range reads), optionally `HASH` SHA-256 (server-side verification)
and `AVBL` (free space). vsftpd: `ssl_enable=YES`, `force_local_logins_ssl=YES`, `force_local_data_ssl=YES`, `ssl_tlsv1_2=YES`; `require_ssl_reuse=YES`
may break the data channel with the .NET client — to be confirmed in SP-04 (workaround: `require_ssl_reuse=NO`). Kobian opens up to 2 parallel
sessions per destination (e.g. `max_per_ip` ≥ 3 leaves headroom); on 421 refusals parallelism drops (KB-2112).

## Messages
| Code | When | Action |
|---|---|---|
| KB-2109 | untrusted certificate without pin / different SPKI key | compare the fingerprint out of band, approve the pin |
| KB-2110 | server refuses `AUTH TLS` | enable FTPS on the server or use SFTP |
| KB-2115 | plain FTP without confirmation | use FTPS or SFTP |
| KB-2102 | 530 — login rejected (no retries) | fix the credential |
| KB-2105 | 550 on write / `MKD` | grant directory permissions |
| KB-2106 | repository directory does not exist | create it |
| KB-2201 | 452 / 552 — disk full or quota | free space |
| KB-2301 | object already exists (never overwritten) | report a bug |
| KB-2302 | 553 — name not allowed | report a bug |
| KB-2303 | path contains a symlink | remove the link from the repository |
| KB-2401 | size after write differs | none — retried; if persistent check the server (ASCII mode?) |
| KB-2112 | 421 "too many connections" | none — lower parallelism |
| KB-2001 | 421/425/426/450/451, connection reset, timeout | none — retried |
