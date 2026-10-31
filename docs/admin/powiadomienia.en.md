# Webhook notifications (D9.10) — PROVISIONAL Qb58

After every finished job run the Agent sends a `pl.kobian.run.finished.v1` event (CloudEvents JSON). The event is written to the `core_outbox` queue in the same transaction as the run result, so a service restart loses nothing.

## Enabling
| Setting | Meaning |
|---|---|
| `KOBIAN_WEBHOOK_URL` | receiver URL; `https://` only, public host (private, loopback and link-local addresses are rejected — also IPv6 addresses embedding a private IPv4 (NAT64 `64:ff9b::/96`, 6to4 `2002::/16`), Teredo and host names invalid under IDNA; the check is repeated after DNS resolution on every connection) |
| `<secrets>\webhook.secret` | HMAC secret: at least 64 hex characters (32 bytes); readable only by the service account and Administrators |
| `KOBIAN_WEBHOOK_MINIMAL=1` | minimal mode: no job name (identifiers, result and times only) |

Invalid configuration (bad URL, missing or bad secret) stops the service at start with exit code 64 and KB-6302 — it is never silently disabled.

## Request
`POST` with headers `Kobian-Webhook-Id` (stable across retries — deduplicate on it), `Kobian-Webhook-Timestamp` (Unix seconds) and `Kobian-Webhook-Signature: v1=<hex HMAC-SHA256(secret, timestamp + "." + body)>`. Receivers should reject timestamps older than 300 s. The event contains no file paths or customer data.

## Delivery
2xx = delivered; 410 = receiver opted out (no retries); other codes, redirects (not followed) and network errors = retry after 1 min, 5 min, 30 min, 2 h, 6 h, then `dead`. Limits: connect 10 s, total 30 s, response read up to 64 KiB. User proxy settings are not used.

## Daily report (D9.11) — PROVISIONAL Qb59
With the webhook or e-mail enabled, every day at 07:00 local time (after the 06:55 audit anchor) the Agent sends a `pl.kobian.report.ready.v1` event summarising the previous day: run counts by result, ids of failed jobs (up to 20, no names), the number of undeliverable notifications and the latest audit-chain anchor `auditAnchor.text` as `seq:sha256`. Keep it at the receiver: `kobian audit verify --anchor <seq:sha256>` detects a replaced audit log on this machine. After service downtime one overdue report is sent, not several.

A notification that could not be delivered after all retries leaves an audit-log entry with code KB-6301.

# E-mail notifications (SMTP, BL-45) — PROVISIONAL Qb64

The Agent e-mails finished runs and the daily report through the same durable `core_outbox` queue as the webhook (a separate entry for the "email" channel). The channels are independent: enable one or both. Sending uses MailKit/MimeKit 4.18.1 (fix for CVE-2026-30227).

## Enabling
| Setting | Meaning |
|---|---|
| `KOBIAN_SMTP_HOST` | SMTP server (DNS name or IP address); unset = e-mail disabled |
| `KOBIAN_SMTP_PORT` | port; default 587 (STARTTLS), 465 for `tls`, 25 for `none` |
| `KOBIAN_SMTP_TLS` | `starttls` (default; STARTTLS is **required** — a server without it is an error, never a plain-text fallback), `tls` (TLS from the start, port 465), `none` (plain SMTP — see below) |
| `KOBIAN_SMTP_INSECURE=1` | required for `KOBIAN_SMTP_TLS=none`; a KB-6207 warning is printed at start and written to the audit log. Plain SMTP cannot be combined with the credentials file (the password is never sent unencrypted) — only an unauthenticated LAN relay |
| `KOBIAN_SMTP_FROM` | sender address, e.g. `kobian@company.com` |
| `KOBIAN_SMTP_TO` | recipients separated by `,` or `;`, at most 10 |
| `KOBIAN_SMTP_LANG` | `pl` (default) or `en` — subject and body language |
| `KOBIAN_SMTP_NOTIFY` | `problems` (default: e-mail only for "warning" or "failed" results) or `all` (every run) |
| `KOBIAN_SMTP_MINIMAL=1` | minimal mode: result, KB code, computer and job id only — no job name or statistics |
| `<secrets>\smtp.credentials` | optional: line 1 — login, line 2 — password (only in this file, never in environment variables); readable only by the service account and Administrators; no file = no authentication |

Addresses must be plain `name@domain` (ASCII only, no display name, no control characters; give IDN domains in punycode). Invalid configuration stops the service at start with exit code 64 and a message with KB-6204 (settings), KB-6205 (address) or KB-6207 (plain SMTP without the flag) — the message names the variable, never its value.

Microsoft 365: Basic Auth for SMTP is being disabled in Exchange Online (reply `550 5.7.30` → KB-6201). OAuth2 for M365 is not available in this version yet — use your organisation's SMTP relay.

## Message
Subject (≤ 120 characters), e.g. `[Kobian] Backup failed KB-2104: Invoices (SERVER-01)`. Body text/plain + HTML: computer, result, KB code, job, times and counters. No paths, file names, database names or passwords. `Message-ID` and `X-Kobian-Event-Id` stay the same across retries (mailbox-side deduplication); `Auto-Submitted: auto-generated` suppresses auto-replies. SPF/DKIM for the sender domain is recommended.

## Delivery
The server certificate is always validated (trusted chain, matching host name); TLS 1.2/1.3. Limits: 30 s per SMTP operation, 90 s per attempt. Transient errors (4xx replies, network, timeout, TLS/certificate failure — KB-6206) are retried after 1 min, 5 min, 30 min, 2 h, 6 h, then `dead`. Permanent errors are not retried: rejected authentication (KB-6201), 5xx server replies such as an unknown recipient (KB-6203). A `dead` entry leaves an audit-log record (`notify.email`) with the cause code.

## Daily report
The 07:00 report (see above) goes to every enabled channel, including e-mail — with the audit anchor in the body ("Audit anchor: seq:sha256" and a ready `kobian audit verify --anchor …` command). Keep these messages outside this computer.

# Alerts (D9.7, D9.8, BL-82)

Every minute the Agent evaluates alert rules A01–A17 against the catalog and stores their state (`alert_alerts` table). An alert has a deduplication key `code:scope` (e.g. `A01:job:<id>`) — while the condition persists there is one alert and it is notified **once**. Notifications come only from transitions:

| Transition | Event | When |
|---|---|---|
| opened | `pl.kobian.alert.opened.v1` | the rule condition is met (quiet hours would delay warnings only — not configurable yet, PROVISIONAL) |
| reminder | `pl.kobian.alert.reminder.v1` | every 24 h while unresolved (A13, A14, A16 — every 7 days) |
| escalation | `pl.kobian.alert.reminder.v1` with `"escalated": true` | critical/security unresolved for 24 h; goes to the same channels (a second recipient comes later) |
| resolved | `pl.kobian.alert.resolved.v1` | the condition has cleared |

Events go to every enabled channel (webhook, e-mail) through the same durable outbox as run results, in the same transaction as the alert state change — a service restart neither loses nor duplicates notifications. Without configured channels alerts are shown on the dashboard only (`GetStatus` → list of active alerts). `KOBIAN_SMTP_NOTIFY` applies to run results only — alerts are always e-mailed.

**Content:** rule code, severity, KB code, scope identifier (job/destination/repository/computer), times, counters. Job name only outside minimal mode (`KOBIAN_WEBHOOK_MINIMAL=1`, `KOBIAN_SMTP_MINIMAL=1`). Never: paths, file names, server addresses, passwords.

## Rules active in this version

| Code | Condition | Data source |
|---|---|---|
| A01 | the job's last run `Failed` after retries are exhausted (attempt ≥ 3 or 2 min without a new attempt) | run history |
| A02 | last run `Warning` — **dashboard only**, no immediate notification (the run result reaches the channels anyway) | run history |
| A03 | no successful backup for longer than 1.5 × the schedule cycle (min. 26 h for daily jobs) — scheduled jobs only | snapshots, schedule |
| A04 | repository damaged: state `corrupt`, a backup with a damage assessment (`check --repair`) or the job's latest backup is suspect (V2/V3) | snapshot catalog |
| A09 | a new or changed destination server key (SFTP/FTPS) awaits Administrator approval | server identities |
| A14 | the Agent runs on an unpatched .NET (KB-7013) | startup check |
| A15 | ≥ 5 API access denials within an hour | audit log |
| A17 | retention held after a clock jump (KB-6117) — until released by an Administrator | retention hold |

**Not active yet** (the Agent does not collect the data): A05 (restore test), A06 (destination free space), A07 (reconciliation), A08 (retention/schedule changes from audit), A10 (destination capability probe), A11 (ransomware heuristics), A12 (heartbeat — by design evaluated by an external service), A13 (certificate/secret expiry), A16 (OS/SQL patch level).

## Example event (webhook)
```json
{"specversion":"1.0","id":"…","source":"urn:kobian:host:…","type":"pl.kobian.alert.opened.v1","time":"2026-10-17T10:00:00Z",
 "datacontenttype":"application/json","subject":"job:7a2e…",
 "data":{"alertId":"…","code":"A01","severity":"critical","category":"availability","kb":"KB-6101","transition":"opened","escalated":false,
  "reminders":0,"scope":{"kind":"job","id":"7a2e…"},"openedUtc":"2026-10-17T10:00:00Z","messageKey":"Alert.A01.Title","job":{"id":"7a2e…","name":"ERP-noc"}}}
```
E-mail: subject e.g. `[Kobian] Alert A03: No successful backup within the allowed gap (RPO) — ERP-noc (SERVER-01)`, body with severity, KB code and recommended action; reminder, escalation and "Resolved" have their own subject prefix.
