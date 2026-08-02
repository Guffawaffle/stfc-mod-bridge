# STFC Mod Control product identity

Issue #27 adopts **STFC Mod Control** as the public name of the standalone
Windows application. Its descriptor is **Install · Configure · Diagnose ·
Run**. In prose, use **Mod Control** when referring to the product, lowercase
`launch` only for starting a process, and **Scopely launcher** for the external
official application.

## Identity inventory

| Surface | Classification | v1 disposition |
|---|---|---|
| Window title, accessibility name, dialogs, diagnostics filename, UI copy | Public identity | Renamed to STFC Mod Control / Mod Control |
| Setup error title, Start menu and desktop shortcuts, Add/Remove Programs entry | Public identity | Renamed; setup and scripts retire both legacy shortcut locations |
| Assembly product, title, description and signed-file description | Public identity | Renamed without changing assembly identity |
| README, release title/copy, provider schema title, portfolio artwork | Public identity | Renamed |
| `%LOCALAPPDATA%\\Programs\\STFC Community Mod Launcher` | Upgrade compatibility | Retained so setup and self-update replace the existing installation in place |
| `%LOCALAPPDATA%\\STFC Community Mod Launcher` | Persisted-state compatibility | Retained so provider selection, logs, journals, caches and settings survive the rename |
| `STFCCommunityMod.Launcher.exe`, updater/setup filenames and process name | Signed update/process compatibility | Retained; updater allowlists, rollback and running-process detection depend on them |
| .NET namespaces, assembly names and solution/project names | Internal implementation identity | Retained; no user benefit justifies a high-risk source-wide rename before v1 |
| `stfc-community-mod-launcher-win-x64.zip`, manifest asset name and artifact IDs | Signed release compatibility | Retained until a separately versioned manifest migration is designed |
| `Guffawaffle/stfc-mod-launcher` update endpoint and trust metadata | Repository/update compatibility | Retained until the repository-rename checklist below is complete |
| Historical architecture paths, provenance links and issue references | Historical record | Retained or annotated; history is not rewritten |

`ModControlProductIdentity` is the code authority for public product language
and retained install/process identifiers. The WPF namespace and classes may
continue to contain `Launcher`; those are implementation names, not display
copy.

## Upgrade behavior

Setup installs into the legacy program and state directories, replaces the
legacy Start menu shortcut with **STFC Mod Control**, and writes one per-user
Add/Remove Programs entry named **STFC Mod Control**. Uninstall removes both old
and new shortcut names and the single registration. State is still preserved
unless the existing explicit `-RemoveState` option is used.

The rename does not read or write mod TOML. Self-update artifacts, publisher
verification, rollback paths and process detection remain unchanged.

## Deferred repository rename

The intended repository name is `Guffawaffle/stfc-mod-control`. Rename it only
after release workflow URLs, the hard-coded self-update repository, provider
metadata, documentation links, OIDC/signing subjects and external consumers
have an atomic migration plan. GitHub redirects are a convenience, not an
update-trust contract.

## Portfolio provenance

This is LexRunner Portfolio Project 001 under the SmarterGPT brand. The active
v1 lifecycle uses **The Lex Toolchain · In Practice**. **Proven in Practice** is
reserved for the completed evidence-backed release. This provenance is
secondary to the STFC Mod Control product identity.
