# STFC Mod Bridge product identity

Issue #27 adopts **STFC Mod Bridge** as the public name of the installed
per-user Windows application. Its descriptor is **Install · Configure · Diagnose ·
Run**. In prose, use **Mod Bridge** when referring to the product, lowercase
`launch` only for starting a process, and **Scopely launcher** for the external
official application.

## Identity inventory

| Surface | Classification | v1 disposition |
|---|---|---|
| Window title, accessibility name, dialogs, diagnostics filename, UI copy | Public identity | Renamed to STFC Mod Bridge / Mod Bridge |
| Setup error title, Start menu and desktop shortcuts, Add/Remove Programs entry | Public identity | Named STFC Mod Bridge from the first public build |
| Assembly product, title, description and signed-file description | Public identity | Named STFC Mod Bridge |
| README, release title/copy, provider schema title, portfolio artwork | Public identity | Renamed |
| `%LOCALAPPDATA%\\Programs\\STFC Mod Bridge` | Program location | Canonical greenfield install path |
| `%LOCALAPPDATA%\\STFC Mod Bridge` | Persisted state | Canonical greenfield state, logs, journal, rollback, and preferences path |
| `STFCModBridge.exe`, `STFCModBridge.Updater.exe`, and `STFCModBridge.Setup.exe` | Signed executable identity | Canonical filenames and process identity |
| .NET namespaces and solution/project directory names | Internal implementation identity | Retained because they are not player-visible or compatibility contracts |
| `stfc-mod-bridge-win-x64.zip` and `stfc-mod-bridge-release-manifest.json` | Machine-consumed release identity | Canonical pre-v1 artifact names |
| `Guffawaffle/stfc-mod-bridge` update endpoint and trust metadata | Repository/update compatibility | Canonical greenfield repository coordinate |
| Historical architecture paths, provenance links and issue references | Historical record | Retained or annotated; history is not rewritten |

`ModBridgeProductIdentity` is the code authority for public product language
and install/process/artifact identifiers. The WPF namespace and classes may
continue to contain `Launcher`; those are implementation names, not display
copy.

## Greenfield behavior

Pre-v1 release candidates use the canonical product identity from their first
public artifact. Setup installs directly into the canonical program and state
directories, creates only the **STFC Mod Bridge** shortcuts, and writes one
per-user Add/Remove Programs entry. Uninstall removes the canonical program
directory, shortcuts, and registration. State is preserved unless the explicit
removal option is selected.

Setup must present the exact version, verified publisher, program location, and
state location before mutation. It labels a greenfield install, newer-version
update, or same/older-version repair truthfully and launches the product only
after an explicit visible choice. Windows Installed Apps invokes the signed
application's `--uninstall` confirmation. That confirmation preserves state by
default, offers explicit state removal, and states that Community Mod and game
files are outside the application-uninstall boundary.

Product identity does not read or write mod TOML. Self-update artifacts,
publisher verification, rollback paths, and process detection all use the
canonical identity from the first public build, so there is no speculative
migration path to maintain or test.

## Repository coordinate

`Guffawaffle/stfc-mod-bridge` is the canonical repository, self-update
authority, provider-schema origin, and signing-subject coordinate. Runtime and
release metadata use that coordinate directly rather than relying on a GitHub
redirect from the pre-release development name.

## Portfolio provenance

This is LexRunner Portfolio Project 001 under the SmarterGPT brand. The active
v1 lifecycle uses **The Lex Toolchain · In Practice**. **Proven in Practice** is
reserved for the completed evidence-backed release. This provenance is
secondary to the STFC Mod Bridge product identity.
