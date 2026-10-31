# Job export and import (kobian-export v1) — administrator guide

State: US-29 (BL-47, AC-29.1–29.2, SEC-6.7). Applies to the Kobian service (Agent) and the `kobian` CLI. Polish: `eksport-import-zadan.md`.

Export moves job configuration to another server (migration, disaster recovery, the same setup at many customers of a service provider — persona P2) and lets you review the configuration. **The file contains no secrets**: passwords and logins stay in the Agent's secret store; the file holds references only.

## 1. Commands

| Command | Effect | Exit codes |
|---|---|---|
| `kobian export [--job <name>]... [--output <file> [--force]] [--json]` | exports all (or the named) “files” and “database” jobs; without `--output` the document goes to standard output | 0 OK, 2 error (KB in JSON / on stderr), 3 service unavailable **or access denied**, 64 usage / file exists (without `--force`) |
| `kobian import <file> --dry-run [--on-conflict skip\|overwrite\|rename] [--json]` | change plan without writing: `new`, `unchanged` (identical), `conflict` (same name, different definition) | 0 import would succeed, 2 rejected (list of errors), 3, 64 |
| `kobian import <file> [--on-conflict skip\|overwrite\|rename] [--json]` | write — **all or nothing** | as above |

Both calls require the **Administrator** role (contract D2.10). Export and every import (including `--dry-run`) are recorded in the audit log (`kobian audit verify`): for import, the SHA-256 of the file, its size and the mode are stored — not its content.

Name conflict (a job with this name exists with a **different** definition or schedule):

| `--on-conflict` | Result |
|---|---|
| (none) | the whole import is rejected: KB-0124 |
| `skip` | the existing job is left unchanged |
| `overwrite` | definition, time zone and schedule are replaced; the job keeps its id and history and **is disabled**. A protected job (SEC-7.6) is never overwritten by import (KB-0124) |
| `rename` | a new job “name (import)”, “name (import 2)”, … |

## 2. What import does and does not do

- **It runs nothing.** New and overwritten jobs are stored **disabled**; the schedule is stored but does not fire until an Administrator enables the job (wizard / `UpsertJob`). Import does not connect to the backup destination.
- **It accepts no scripts or actions** (pre/post, `runSql`, `stopService`, commands): such fields reject the whole file (KB-0123; SEC-6.7, AS-02). Scripts are configured in the program after import.
- **It accepts no secrets:** fields named like `password`, `token`, `privateKey` and values that look like a secret (PEM block, `password=…`, `https://user:password@…`, JWT, long hex string, random-looking string of ≥ 20 characters) = KB-0122. A credential is given only as `secretRef` (`file:<name>`) with an entry in the Agent's secret store.
- **It rejects unknown fields** (they are not ignored): a typo or a field from a newer version = KB-0120 with the exact path.
- **It checks each job with the wizard's rules:** the source must exist and be readable on this server (KB-0101), the destination must be valid for its scheme and supported (KB-0103/KB-21xx), no backup into the source (KB-0102), schedule ≥ 5 min (KB-0201), retention (KB-0401). Key/header/password files must be local paths (not UNC).
- **It prepares the wizard:** each job in the plan carries `needsCredentials` (the `secretRef` has no entry in this server's secret store — the wizard will ask for credentials, AC-29.1) and `missingKeyMaterial` (key/header/password file missing on this server).

File errors are returned as a list: JSON path (e.g. `$.jobs[0].definition.destination.root`), KB code and reason — without field values (AC-29.2). Up to 100 entries.

## 3. Format `kobian-export v1`

See the example and the field table in the Polish guide (section 3) — field names are the same. Summary:

| Field | Meaning |
|---|---|
| `format`, `version` | constants `kobian-export` and `1`; anything else = KB-0120 |
| `programVersion`, `exportedAt` | Agent version and export time (UTC, `yyyy-MM-ddTHH:mm:ssZ`) — informational |
| `jobs[].name`, `type` | name (≤ 200 chars, no control characters, no leading/trailing spaces), type `files` or `database` |
| `jobs[].enabled` | state at export time — **informational**; import always creates disabled jobs |
| `jobs[].timeZone`, `misfire`, `misfireWithinSeconds` | schedule time zone, missed-occurrence policy (`skip`, `runOnceIfMissed`, `runIfMissedWithin` + seconds ≤ 7 days) |
| `jobs[].schedule` | `cron` (5 fields, job time zone) and `enabled`; absent = manual job |
| `jobs[].definition` | job definition v1 (`docs/agent-job-definition.md`) — schema fields only |

**No secrets:** `secretRef` is the name of an entry in the Agent's secret store; `key.file`/`passwordFile`/`repoHeader`/`certificate` are **paths** (never file content). After moving to another server, recreate the secret entries and key files there (or create a new repository with the wizard).

**Canonical form:** object keys sorted ordinally at every level, jobs sorted by name, no `null`, 2-space indentation, LF, UTF-8 without BOM, non-ASCII characters as `\uXXXX`. The same catalog state yields an identical file (except `exportedAt`) — suitable for `diff` and version control. Import also accepts hand-formatted files (other key order, BOM).

**Limits (SEC-3.3):** file ≤ 60 KiB (fits the 64 KiB API message limit — PROVISIONAL; a larger export → KB-0121, export in parts with `--job`), nesting ≤ 8, ≤ 200 jobs, string ≤ 1024 characters, duplicate JSON keys rejected.

## 4. KB codes

| Code | When |
|---|---|
| KB-0120 | file does not match the schema: bad JSON, unknown/required field or wrong type, duplicate key or job name, other `format`/`version` |
| KB-0121 | size, depth, job count or string length exceeded |
| KB-0122 | a field or value that looks like a secret |
| KB-0123 | a script or action in the file |
| KB-0124 | name conflict without `--on-conflict`, or an attempt to overwrite a protected job |
| KB-0101/0102/0103/0201/0401, KB-21xx | job rejected by the wizard's rules (source, destination, schedule, retention) |
| KB-0104 | `export --job` with a name that does not exist |
| KB-7011 | Administrator role missing (CLI: exit code 3) |
