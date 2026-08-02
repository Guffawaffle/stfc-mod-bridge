# Windows Release Manifest

## Purpose

Every tagged release publishes
`stfc-community-mod-release-manifest.json`. It is the canonical machine-readable
index for Windows launcher and mod artifacts. The release workflow generates
artifact sizes and SHA-256 values from the signed, packaged files and then
validates the completed manifest against those same files before upload.

The manifest is not handwritten. The checked-in
`scripts/windows_release_manifest_spec.json` declares stable artifact identity,
media type, platform, architecture, source path, and expected authenticity
mechanism. `scripts/generate_release_manifest.py` supplies release identity,
size, and digest facts.

The older `launcher-spike-manifest.json` beside local launcher packages
describes only the WL-001 self-contained packaging experiment. It is not a
release discovery contract and must not be consumed as one.

## Schema v1

```json
{
  "schemaVersion": 1,
  "releaseVersion": "2.1.0-guffa.8",
  "tag": "v2.1.0-guffa.8",
  "channel": "stable",
  "releaseState": "active",
  "minimumLauncherVersion": "0.1.0",
  "source": {
    "repository": "Guffawaffle/stfc-mod",
    "targetCommit": "0123456789abcdef0123456789abcdef01234567"
  },
  "manifestAuthenticity": {
    "scheme": "none"
  },
  "artifacts": [
    {
      "id": "windows-mod-dll-x64",
      "kind": "windows-mod",
      "platform": "windows",
      "architecture": "x64",
      "fileName": "version.dll",
      "mediaType": "application/vnd.microsoft.portable-executable",
      "size": 123,
      "sha256": "<64 lowercase hexadecimal characters>",
      "authenticity": {
        "scheme": "authenticode",
        "scope": "artifact"
      }
    }
  ]
}
```

Schema v1 fields are closed: producers reject unknown spec fields, and
consumers must fail closed on an unknown `schemaVersion`.

### Release identity

- `releaseVersion` is the supported tag without its leading `v`.
- `tag` is the exact immutable Git tag.
- `source.repository` uses `owner/name`.
- `source.targetCommit` is the exact 40-character lowercase commit ID built by
  the tagged workflow.
- `minimumLauncherVersion` is the oldest launcher allowed to interpret and act
  on the release.

### Channels

The generator derives the channel from the tag instead of accepting a
potentially contradictory workflow input:

| Tag form | Channel | GitHub release behavior |
|---|---|---|
| `vX.Y.Z` | `stable` | normal release |
| `vX.Y.Z-guffa.N` | `stable` | normal fork release |
| `vX.Y.Z-guffa.rcN` | `preview` | prerelease |
| `vX.Y.Z.alpha.N` | `preview` | prerelease |
| `vX.Y.Z.beta.N` | `preview` | prerelease |

Unknown tag forms fail generation. Stable is the launcher default. Selecting
preview releases requires explicit user intent and must never silently change
the configured channel.

### Artifact identity

Each artifact has a stable `id` and a semantic `kind`. Schema v1 publishes:

| ID | Kind | Purpose |
|---|---|---|
| `windows-mod-dll-x64` | `windows-mod` | Direct signed `version.dll` used by the transactional installer and retained for manual installation. |
| `windows-mod-archive-x64` | `windows-mod-archive` | Existing Windows archive containing the signed mod DLL. |
| `windows-launcher-archive-x64` | `windows-launcher` | Machine-consumed self-update archive containing the signed launcher and replace-on-exit helper executables; this is not an install artifact. |
| `windows-launcher-setup-x64` | `windows-launcher-setup` | User-facing signed single executable that embeds, installs, and starts the signed launcher archive. |

Future independently installed components receive distinct IDs and kinds.
Their size, checksum, and authenticity metadata must not be borrowed from
another artifact.

The launcher consumer uses a closed schema and rejects unknown properties. For
mod deployment it selects exactly one `windows-mod-dll-x64` artifact and
requires `windows` / `x64`, the direct PE media type, `version.dll`, bounded
size, lowercase SHA-256, and `authenticode` / `artifact` authenticity. The
manifest repository must be exactly `Guffawaffle/stfc-mod`, the release must be
active and match the user-selected channel, and `minimumLauncherVersion` must
not exceed the running launcher. Asset URLs are derived from the immutable tag
and basename under that repository; manifest-provided arbitrary URLs or paths
are not accepted.

`GitHubWindowsReleaseClient` discovers bounded GitHub release metadata, skips
drafts and the opposite channel, and considers only releases that publish the
canonical manifest asset at its exact immutable-tag URL. GitHub and manifest
tags must match. Release-list and manifest responses are size-bounded and
non-success HTTP responses remain actionable instead of being interpreted as
"no update." An already healthy local installation does not depend on this
online discovery path to remain launchable.

Windows file-version comparison is numeric. The consumer deterministically
maps the release identity (for example, `2.1.0-guffa.8`) to the embedded DLL
file version (`2.1.0.8`) and rejects release forms it cannot map.

`fileName` is a release-asset base name, never a local path or URL. Consumers
resolve it only against the selected GitHub release. Redirects are allowed only
over HTTPS.

### Integrity and authenticity

`size` and `sha256` describe the exact bytes of the published release asset.
They detect truncation or mutation only after the launcher has obtained trusted
release metadata.

Artifact authenticity is independent:

- `scheme: authenticode, scope: artifact` means the downloaded PE file itself
  must pass Authenticode verification.
- `scheme: authenticode, scope: contents` names the signed files that must pass
  Authenticode verification after safe extraction.
- `scheme: none, scope: none` would explicitly declare that no independent
  artifact signature is available.

The release workflow signs and verifies Windows PEs before packaging and
hashing. A checksum is not a signature.

`manifestAuthenticity.scheme` is deliberately `none` in schema v1. GitHub
release transport and repository controls distribute the JSON, but v1 has no
detached manifest signature or replay protection. Consumers and release notes
must not describe the manifest checksum as publisher authentication. A future
signed-manifest design requires a new reviewed contract.

### Withdrawal

The normal generator emits `releaseState: active`. A release is newly eligible
only while:

1. its GitHub release still exists and is not draft;
2. its release/prerelease state matches the selected launcher channel;
3. the repository withdrawal procedure has not yanked it; and
4. its manifest validates.

Withdrawals use `scripts/axf/release-withdrawal.ps1` and the durable
`docs/release-withdrawals/release-withdrawals.jsonl` ledger. Yanked releases are
not newly offered, even if a client has cached their former active manifest.
A cached, already-installed healthy mod may continue to launch offline; no
withdrawal silently removes local files. Replacement, downgrade, or removal
requires explicit policy and user-visible action.

Schema v1 does not rewrite an immutable historical manifest after withdrawal.
A future authenticated release index may add live withdrawal state and replay
protection.

## Generation and validation

The tagged release workflow runs:

```text
build -> sign inner executables -> verify -> package -> embed signed package
      -> sign and verify setup -> generate manifest -> validate manifest
      -> hash manifest -> publish
```

The generator and validator use the same explicit release identity and checked-
in artifact spec. Validation reconstructs the entire expected document from the
packaged files; a filename, size, checksum, channel, commit, repository, or
metadata mismatch fails the release.

Fixture validation:

```powershell
py -3 -m unittest tests.test_generate_release_manifest -v
```

Linux CI uses the equivalent `python -m unittest` command.

## Consumer failure behavior

- Unknown schema, tag, channel, artifact kind, platform, or architecture fails
  closed for install/update.
- Missing, duplicate, empty, or mismatched artifacts fail closed.
- HTTP failure, size mismatch, checksum mismatch, unsafe extraction, or failed
  Authenticode validation must leave the existing installation untouched.
- Manifest/network failure must not prevent launching an already healthy
  installation.
- Downgrade and preview-channel selection always require explicit user intent.
