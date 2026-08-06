# Diagnostics workspace

Diagnostics is the canonical Mod Bridge support surface. Its default view is a
set of readable, stable-ID checks; the machine-readable JSON is secondary and
collapsed. The workspace does not infer native hook health from a DLL, log, or
process. Missing authoritative evidence is shown as **Unknown**.

Issue #45 protected configuration-backup restore/delete remains a follow-up;
the current Diagnostics implementation must not treat prototype plaintext
provider-switch copies as an accepted recovery surface.

## Evidence contributors

| Contributor | Evidence | Failure behavior |
| --- | --- | --- |
| Environment probe | selected game target and game process | Attention or Unknown |
| Scopely launcher service | supported per-user installation availability | Attention |
| Mod deployment service | persisted journal, allowlisted target, installed identity | Unknown; never guess ownership |
| Launcher-local health (#7) | provider compatibility, bounded update observation, native contract | explicit Unknown when unproven |
| Configuration analyzer (#29/#8) | revision-bound, exact provider/catalog diagnosis plus catalog-authorized cleanup eligibility | Unknown for unsupported provider or syntax |
| Mod log tail | at most 64 KiB / 200 lines from `community_patch.log` | Unknown or Attention |

Opening or refreshing Configuration diagnosis reads a snapshot and never
mutates it. The separate **Review cleanup** action may build a pure plan only
for eligible alias moves/removals identified by the exact diagnosis. Its
preview contains paths, lines, and value-free summaries. Apply requires a
second confirmation, rechecks the source/provider/catalog binding, creates and
verifies a protected provider-scoped backup, commits atomically, and rescans.
Provider behavior is selected by stable provider identity and catalog support,
never display-name matching.

## Privacy contract

The structured report is generated locally and is never uploaded by Mod
Control. Copy and export use immutable strings from the same
`LauncherDiagnosticPreview` that the user reviews:

- **Copy diagnostic summary** copies the displayed redacted summary.
- **Export report** writes the exact redacted JSON shown under Technical report.
- **Export effective configuration** is deliberately outside support export. It
  warns that its local JSON is unredacted and may include credentials, private
  endpoints, and paths; nothing uploads it automatically.
- Known user/game paths, credential assignments, bearer tokens, cookies, and
  HTTP endpoints are scrubbed before either string is exposed.
- Configuration findings structurally omit values and private paths before
  serialization.
- Recent raw log lines are bounded and scrubbed as defense in depth. Their
  redaction is explicitly described as best effort, not perfect.
- Protected configuration-backup roots and envelope extensions are denied
  before collection. Payload bytes, decrypted content, absolute restore paths,
  backup IDs, and full integrity hashes never enter preview/export/support
  evidence. Future backup health may expose only aggregate count, age band,
  protection health, and recovery-required state.

## Action and ownership boundaries

Folder actions are routed through the Windows folder application service; WPF
does not mutate game files. Recovery and removal reuse the single existing
`ModManagementCoordinator` / `ModDeploymentService` transaction owner:

- journaled and allowlisted targets only;
- the game-running exclusion remains enforced;
- provider ownership continues to fail closed;
- removal requires the existing confirmation dialog;
- removal affects the managed `version.dll`, or restores an explicitly adopted
  predecessor, while preserving configuration, logs, and Mod Bridge state.

Launcher self-update remains a separate trust and transaction domain from mod
repair/removal. Automatic self-update residue recovery remains setup/helper
owned; Diagnostics does not invent a second recovery implementation.

An explicit protected-TOML restore will also reuse the common mutation lease,
game-running exclusion, exact-target confirmation, and durable recovery
boundary. It does not belong to source-selection WPF or to the ordinary
configuration editor save path.

## Deferred evidence

Final packaged screenshots, scaling, file-picker dogfood, and a safe recovery
exercise are consolidated under v1 qualification issue #30. The implementation
gate includes packaged UI Automation of cleanup review/cancel/focus return on a
disposable TOML and proves its SHA-256 remains unchanged.
