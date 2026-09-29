# Fingerprint cache and snapshot skipping — 0.25.0

From the activity log of a "hello" sent on September 29, 2026 to a room on a 3,277-file, 134 MB workspace: Codex
connected in 0.5 s and called `submit_message` at 7 s, and that call returned 27 s later. The time was the first
workspace fingerprint after the app restarted, since 0.17.0's incremental hash cache lived only in memory. And the
message was a greeting, which needs no snapshot at all.

| Change | Effect | Where |
| --- | --- | --- |
| Persisted hash map | Each workspace's per-file map (length, write time, NTFS change time, hash) is written to `fingerprints-<id>.json` in the data folder's `fingerprints` directory whenever a capture hashed anything or the file set changed, and loaded on the first capture after a launch. Files with unchanged length, write time and change time reuse their hash; a corrupt or foreign cache file is ignored. The fingerprint is identical either way. | `ProjectSnapshot.CacheDirectory`, `LoadPersisted`, `Persist`; the desktop sets the directory |
| No snapshot for status-only messages | `submit_message` captures a snapshot only for a review request or result, a message carrying findings, or an `assignment_complete` while the task has findings. Other status messages (a greeting's reply, `no_further_contribution`, a progress note, a completion with no findings on record) are stored without a snapshot reference. Evidence capture, review completion and the evidence page are unchanged. | `CollaborationStore.Call`, `ValidateEvidenceAndReview` |

## Verification

- Build: zero warnings, zero errors.
- Full regression suite: **244 tests passed**: the 242 of 0.24.0 plus two cases: a six-file workspace is hashed once,
  its map persists, an in-memory reset followed by a capture rehashes nothing and yields the same fingerprint, one
  changed file costs one rehash and changes the fingerprint, and a corrupted cache file is ignored; a progress status
  and a completion with no findings take no snapshot and store no snapshot reference, while a review request on the next
  dispatch takes exactly one. The lock-release test now submits a review request, the case that still captures.
- Package `artifacts\fingerprint-cache-025-release` (file version 0.25.0.0) passed `tests\Local-Diagnostics-Smoke.ps1`
  and `tests\Conversation-Management-Smoke.ps1` (all four conversation-management cases).
- PENDING: clean checkout verification, installation, and a timing check of the first message after the restart.
