# Restore, destinations, settings and audit log — administrator guide

State: iteration 89 (round 6). Applies to the Kobian service (Agent), the Kobian app (GUI) and the `kobian` CLI. Polish: `odtwarzanie-i-ustawienia.md`.

## 1. Restoring files
| Way | How |
|---|---|
| GUI | **History** → pick a backup → **Restore…** (SCR-06): pick files/folders, target folder, result with KB code |
| CLI | `kobian snapshots --job <job>` → `kobian restore --snapshot <id> [--path <path in backup>] --to <folder>` |

Rules (D8.9): restore **never overwrites** existing files (KB-5105) and never follows links in the target folder (KB-5104); a file with a mismatching hash is removed and reported (KB-5201). The target folder must exist.

`--snapshot` takes a full id, an unambiguous prefix (≥ 8 hex chars) or `latest`. **Note:** plain `latest` means the newest backup in the whole service catalog (all jobs) — with several jobs add `--job <job>` (`--snapshot latest --job Files` = newest backup of job “Files”; BL-70) or pass the id from `kobian snapshots --job <job>`. With an id, `--job` changes nothing. The GUI always sends an id. Missing/ambiguous → KB-5002.

**Preview before restoring (it. 98, R2.8.1):** in the GUI “Restore” first checks the backup and the target folder — number of files and size, free space, files that already exist and will be skipped (then a second button “Restore anyway”); blocking: no write permission (KB-5001), not enough space (KB-5106), blocks missing from the indexes (KB-5202), path not in the backup (KB-5003). CLI: `kobian restore … --dry-run [--json]` — writes nothing; exit code 0 = nothing skipped, 1 = files will be skipped, 2 = blocked. Since it. 101 the service lets an Operator restore (and preview) only into a folder where **their own account** may add files and folders (AccessCheck with the client token, SEC-6.3) — otherwise KB-5001 before the backup is read; Administrators are not restricted.

Without the service (server failure): `kobian-restore` reads the repository directly (`kobian-restore restore … --password-file <file>`), see `docs/check.md` and `docs/format-spec/`.

## 2. Restoring a SQL Server database (SCR-08)
| Way | How |
|---|---|
| GUI | **History** → backup tagged "database backup" → **Restore as new database…** → (optional) name → result |
| CLI | `kobian restore-db --snapshot <id> [--as <name>]` |

- Always restored as a **new** database, default `<source>_restore_YYYYMMDD`. An existing database with that name → KB-3501 (no `REPLACE`; replacing production is a deliberate DBA action).
- Before restoring: `RESTORE VERIFYONLY … WITH CHECKSUM`; files go to the server's default data/log directories (unknown → KB-3504); `TRUSTWORTHY OFF`.
- The service account needs `CREATE ANY DATABASE` / `dbcreator` (KB-3502). FILESTREAM and full-text are not supported (KB-3503, restore manually in SSMS).
- The `.bak` file is first downloaded to the service staging folder (`KOBIAN_DB_STAGING`); not enough space → KB-3102. The folder is always cleaned up, also after an error.
- The SQL Server must be allowed (section 5).

## 3. Destinations (SCR-09)
The **Destinations** screen lists destinations used by jobs (scheme, path/host without secrets, SFTP host key fingerprint, jobs using it). **Check space** connects on demand (read-only session) and shows usage; "low space" is stated in text, not colour only. Requires the Operator role.

## 4. Settings (SCR-11)
- Theme (light/dark/system) applies immediately; language PL/EN after restarting the app.
- Per-user file: `%APPDATA%\Kobian\ui-settings.json` (≤ 4 KiB; a damaged or oversized file = defaults). Contains no secrets.
- **Service** section: version, current user's role, .NET patch level; **Verify log** button (section 6).
- **KB-7013** (banner on the dashboard and in Settings): the service runs on an unpatched .NET (below 10.0.13). Action: install the .NET 10 update and restart the service.

## 5. Service environment variables
| Variable | Default | Meaning |
|---|---|---|
| `KOBIAN_CATALOG` | — (required) | path of the SQLite catalog FILE `catalog.db` (not a folder or drive root — since it. 105 a configuration error, exit code 64); missing → the service does not start |
| `KOBIAN_STATE_DIR` | folder of `catalog.db` | service state: repo key copies, `.cer` certificates |
| `KOBIAN_SECRETS_DIR` | `<state>\secrets` | `file:<name>` secrets (PROVISIONAL, ADR-0007; DPAPI planned) |
| `KOBIAN_DB_STAGING` / `KOBIAN_DB_SQL_STAGING` | `<state>\staging` | `.bak` staging (service path / path as seen by SQL Server) |
| `KOBIAN_SQL_ALLOWED_SERVERS` | local server only | `;`-separated allowed SQL servers (e.g. `sql01;sql02\ERP`); others → KB-3009 |
| `KOBIAN_SCHEDULER` | on | `0` disables the scheduler |
| `KOBIAN_VERIFY_SCHEDULE` | on | `0` disables periodic V3 verification (`admin/weryfikacja-okresowa.en.md`) |
| `KOBIAN_AUTO_RETENTION` | on | `0` disables retention after a successful backup |
| `KOBIAN_PARALLEL_UPLOADS` | on | `0` = backup files (packs) are uploaded one at a time. By default the service opens as many connections to the destination as the provider recommends (local disk 2, SMB 4, SFTP 3), lowers the number after a server refusal (KB-2112) and remembers it for 7 days (PROVISIONAL Qb63). Each connection adds one in-memory pack buffer (16 MiB pack → about 20.6 MiB buffer); buffers are reused and their total (being uploaded + being built) is capped at 192 MiB (PROVISIONAL, RD-75) — with large packs (`packTargetSize`) the service uploads fewer packs at once, always at least one |

Changing a variable requires a service restart.

## 6. Audit log
Every modifying API operation (jobs, schedule, restore, retention, certificate approval) and every access denial append to the audit chain (D9.9). Verify integrity:
- GUI: **Settings → Verify log**,
- CLI: `kobian audit verify [--json]` → `ok: <n> audit entries, chain intact` or `failed <KB> seq <n>` (first broken entry). Non-zero exit code when broken.

**Anchors (it. 99, D9.9):** every day at 06:55 (server local time; after downtime — at start) the service records an anchor “seq:sha256” of the last entry. `kobian audit verify` shows the last anchor — **store it off the server** (e.g. in a ticket); later `kobian audit verify --anchor <seq>:<sha256>` checks the log against it independently of the catalog (detects history rewritten with a recomputed chain and a truncated log). Anchors in the e-mail report and in the repository (`journal/`, format v1.1) — later. Codes: KB-6501 chain, KB-6502 audit table schema changed (e.g. trigger removed), KB-6503 anchor mismatch.

A broken chain may mean tampering with the catalog: keep a copy of the catalog file and report an incident; do not repair by hand.

## 7. KB codes in this guide
KB-3009, KB-3102, KB-3501…3504, KB-5001, KB-5002, KB-5003, KB-5104, KB-5105, KB-5106, KB-5201, KB-5202, KB-6501, KB-6502, KB-6503, KB-7013 — full descriptions: `docs/kb-codes.md`.

## Retention of auxiliary data (it. 116) — PROVISIONAL Qb61
The restore resume journal (target paths and names of restored files) is removed from the Agent catalog 30 days after the restore finished; restores in progress are never touched. Sent and undeliverable notifications are removed after 30 days. Run history (no file names) and the audit log are kept.
