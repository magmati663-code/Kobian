# Destination server identity — approving the SSH host key and TLS SPKI (BL-35, D4.11, SEC-14.5)

Applies to **SFTP** destinations (SSH host key) and **FTPS** destinations (certificate public key, SPKI). Kobian never trusts a new server key
"on first use": every new or changed key waits for an **Administrator** decision, and until then backups to that destination stop with
**KB-2104** (SFTP) or **KB-2109** (FTPS). The credential (password/key) is never sent to a server whose key is not approved.

## Where the Agent takes the pin from
- Approved keys live in the Agent catalog (table `repo_dest_identities`), per destination (scheme + host + port + directory).
- The `hostKey` field of a job definition (`--host-key` in `kobian init`) is migrated **once** into the catalog as approved (at Agent start and on
  the first connection), only when the destination has no pin yet. Existing jobs keep working unchanged.
- Changing `hostKey` in a job later does **not** change the pin — only an Administrator can change the server key (below). An Operator account
  (or malware in its session) cannot swap the trusted server by editing a job.

## Key states
| State | Meaning | Connection |
|---|---|---|
| `pending` | the server presented a key nobody approved (first contact or key change) | refused, KB-2104/KB-2109 |
| `expired` | pending for more than 24 h — cannot be approved; the next connection renews it | refused |
| `pinned` | approved (Administrator, migrated from the job definition, or — FTPS with a valid CA — informational record "(CA)") | allowed |
| `rejected` | rejected by an Administrator — stays rejected on later connections | refused |
| `retired` | replaced by a newer key; if it comes back it must be approved again | refused |

On a key change the **old pin stays** until a decision is made — the backup stops (KB-2104/KB-2109, alert) instead of silently moving to the new key.

## How to approve a new server key
1. List pending keys (Operator role or higher):
   ```text
   kobian destinations identity list
   kobian destinations identity list --destination dest-0123456789abcdef --json
   ```
   Columns: destination id, `scheme://host:port`, kind (`ssh-hostkey`/`tls-spki`), state, `SHA256:…` fingerprint, first seen (UTC).
2. **Compare the fingerprint out of band** — on the server itself, not over the same network path:
   - SFTP (OpenSSH): `ssh-keygen -lf /etc/ssh/ssh_host_ed25519_key.pub` → `256 SHA256:… (ED25519)`;
   - FTPS: `openssl x509 -in cert.pem -pubkey -noout | openssl pkey -pubin -outform der | openssl dgst -sha256 -binary | openssl base64`
     (prefix with `SHA256:`, drop trailing `=`).
   If it does not match — **do not approve** (possible man-in-the-middle); reject it and investigate the network.
3. Approve (**Administrator** role), copying the fingerprint **exactly** from the list:
   ```text
   kobian destinations identity approve dest-0123456789abcdef SHA256:<fingerprint> --replace
   kobian destinations identity approve dest-0123456789abcdef SHA256:<fingerprint>
   ```
   - `--replace` — the server changed its key (reinstall, new certificate with a new key): previous pins of the destination become `retired`.
   - without `--replace` (add) — the server legitimately has several keys, e.g. two FTPS nodes behind a load balancer (AC-12.2); at most 4 pins (KB-2143).
4. Reject: `kobian destinations identity reject dest-0123456789abcdef SHA256:<fingerprint>`.

Only a fingerprint that **the server itself presented** and that is pending for this destination can be approved — any other (e.g. mistyped)
fails with **KB-2141**; a request older than 24 h fails with **KB-2142** (connect again: run the job or the destination check in the wizard).
Every decision and every denial is written to the audit log (`kobian audit verify`): destination, fingerprint, decision, Administrator SID.

## FTPS with a certificate from a trusted CA and "high-risk" destinations (SEC-14.5)
- A certificate valid against the Windows trust store with a matching host name is accepted without a pin. A key change on renewal
  (e.g. Let's Encrypt every 90 days) is an **informational event**: the SPKI is recorded as `pinned (CA)` (actor `system:ca-chain`), backups continue.
  Such a record is not a pin: a certificate with that key that is revoked or expired is still refused.
- Mark exposed destinations (e.g. FTPS over the Internet) as "high risk" — a valid CA chain is then not enough and every SPKI change
  waits for approval like an SSH key:
  ```text
  kobian destinations identity require-pin dest-0123456789abcdef on
  kobian destinations identity require-pin dest-0123456789abcdef off
  ```
  After switching it on, the first connection ends with KB-2109 and the current SPKI appears as `pending` — approve it as above.

## Limitations (build status)
- The identity confirmation (step-up, SEC-4.1) required by the plan for approvals is not implemented yet — protection is the Administrator role
  and the audit log. The SCR-09 screen in the app is a separate stage; use the CLI until then.
- SSH key rotation via `hostkeys-00@openssh.com` is not used — a new key always needs approval.
- Behaviour against real OpenSSH/vsftpd servers is confirmed in test queue W-20/SP-04.
