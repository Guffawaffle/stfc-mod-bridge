# STFC Mod Bridge pre-release testing

This pre-release is intended for technically comfortable testers. Participation
is not an endorsement that Mod Bridge is ready for a stable or general v1
release.

## Choose the build

Install only a GitHub release whose notes explicitly classify it as
**Closed-alpha approved** or **Public canary — qualification is still in
progress**. For a public canary, read every listed open check before deciding to
test it. Do not infer either classification from a version number, the newest
tag, or the GitHub "Latest" label. `v0.1.0-rc.3` is rejected because of a
provider-state projection regression.

Download only `STFCModBridge.Setup.exe`. The ZIP, release manifest, SBOM, and
attestation bundle are machine-consumed release inputs and are not alternate
installers. The supported alpha environment is Windows 10 or 11 x64, a
per-user Scopely STFC install, and the bundled Guffawaffle or NetniV stable
provider.

Setup is per-user and includes the required .NET runtime. Before running it,
verify that Windows shows the publisher as **Joseph Gustavson**. Windows may
still identify a new certificate as uncommon while reputation develops.

## Protect your installation

Before testing a change:

- Close STFC for every install, update, repair, remove, or provider-switch
  operation.
- Keep an independent copy of any irreplaceable manually installed DLL or TOML
  file. Mod Bridge has transactional rollback, but it is pre-release software.
- Confirm that Mod Bridge selected the intended game installation before a
  mutating operation.
- Do not attach tokens, cookies, raw credential-bearing TOML, or unreviewed
  filesystem contents to an issue.

## Suggested test lanes

Start with the lowest-risk lane that is useful to you.

1. **Install and inspect:** install Mod Bridge, confirm its identity and chosen
   game directory, browse configuration, and export diagnostics. Diagnostics
   remain local until you explicitly attach them; inspect the export first.
2. **One provider:** check for updates, install with the game closed, launch,
   then exercise repair and remove. Confirm unrelated game files are retained.
3. **Provider round-trip:** this higher-risk lane requires distinct TOML state
   for both providers. Switch Guffawaffle → NetniV → Guffawaffle without
   restarting Mod Bridge. Confirm every surface reflects the active provider,
   exact provider-scoped TOML is restored, and cancellation changes nothing.
4. **Delivery and recovery:** test self-update, interrupted-update recovery, or
   uninstall/reinstall only when the invitation specifically requests it.

## Known pre-v1 boundaries

The following work remains intentionally visible rather than being implied as
complete: configuration cleanup and migration (#8), native authenticated
release selection (#71), separate integrity/runtime/freshness feedback (#73),
the compromise-response guide (#74), and provider-switch review polish (#75).

Signatures, exact hashes, and attestations establish publisher or build origin
and byte integrity. They do not establish that the software or its dependencies
are safe or free of malicious behavior. NetniV mod artifacts remain governed by
their reviewed exact-hash policy until that upstream trust path changes.

## Report useful evidence

Use the repository's structured bug or usability form. Include the exact Mod
Bridge version, Windows version, display scaling/theme/input method, selected
provider, game version, action attempted, whether STFC was open, expected
result, and observed result. Attach only diagnostics you have reviewed and
redacted. Diagnostics are generated locally and are never uploaded
automatically.

Potential security vulnerabilities belong in private reporting; see
[SECURITY.md](SECURITY.md).
