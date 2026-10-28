# Periodic backup verification (V3) — PROVISIONAL Qb101

The Agent periodically checks on its own that backup data on the destination can be restored: it downloads part of the repository data files (packs) and checks their SHA-256 sum, the footer, and the authentication (AEAD) and identifier (HMAC) of every block. Successive cycles check successive parts of the repository, so after a number of cycles the whole repository has been checked.

## Defaults
| Setting | Default | Meaning |
|---|---|---|
| `enabled` | `true` | periodic verification on |
| `intervalDays` | `7` | interval since the last completed cycle (1–366 days); the first cycle runs a week after the first backup |
| `samplePercent` | `5` | share of packs checked in one cycle (1–100%); everything in ⌈100 / %⌉ cycles — 20 weeks by default |

Per job — the `verify` section of the job definition (e.g. in a `kobian-export v1` file):

```json
"verify": {"enabled": true, "intervalDays": 7, "samplePercent": 10}
```

Turning it off for the whole Agent: service environment variable `KOBIAN_VERIFY_SCHEDULE=0` (manual `kobian check` keeps working).

## Cost
A cycle downloads about `samplePercent`% of the repository data from the destination. For remote destinations (SFTP, FTPS, SMB over WAN) this is real traffic — for large repositories and slow links lower `samplePercent` or raise `intervalDays`. Downloaded bytes are shown in the run history (`kobian runs`, bytes read; kind "check").

## Backups and verification do not collide
- Verification does not start while a run of the same job is active or queued, and never runs alongside a backup to the same repository — a backup arriving meanwhile waits in the queue until verification ends.
- On the destination, verification takes a shared lock (like a backup), so maintenance (prune) from another computer cannot delete data underneath it.
- If the destination is unreachable or the service was stopped, the cycle is retried after one hour at the earliest — the same part of the repository, nothing is skipped.

## Result
| Result | What happens |
|---|---|
| OK | backups whose data has all been checked get the **V3** badge; the Agent status API (GetStatus) reports the last verification date, its code and the next due date (shown in `kobian status` and the dashboard in a later version) |
| **KB-1510** — damaged data | the run is `Failed`, affected backups are marked suspect, **the next backup of the job is full**, a `run.finished` event is sent (notification / alert A04) |
| **KB-1511** — backup missing on destination | a backup recorded in the catalog does not exist on the destination (e.g. a different USB disk is attached or data was deleted) — as above |
| KB-1501…1503 | missing pack, wrong size, block outside the index (V2 runs in every cycle) |
| KB-1504 (warning) | unreferenced objects — usually after an interrupted backup |

Verification never deletes or repairs anything. After an error: check the medium/server, run `kobian check <job> --repair` (Administrator role) to see the extent of the damage, and restore affected files from another copy (replica, USB).
