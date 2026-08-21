# App Installer permissions and least-authority plan

Status: pre-v1 authority baseline and implementation plan

Tracking issue: [#219](https://github.com/Guffawaffle/stfc-mod-bridge/issues/219)

This document explains the broad Windows App Installer warning shown for the
signed STFC Mod Bridge MSIX, records what the current application actually
does, and defines the evidence required before changing its package security
model. Issue #219 and its children are an inserted pre-v1 release gate. They do
not grant a release classification: issue #89 remains the installed-product
qualification owner, and issue #30 remains the final release decision owner.

The current public canary, rc.21, remains the control baseline. A final v1
candidate cannot be classified until #219 closes, and evidence from rc.21 does
not transfer to a changed manifest, executable set, process topology, storage
model, or authority boundary.

## Short answer for testers

Windows describes STFC Mod Bridge as a full-trust desktop application because
the installed WPF process runs with the ordinary authority of the signed-in
Windows user. It does **not** run as administrator and the package does not
request elevation.

That authority is broad because Bridge must safely inspect and change files in
the STFC installation selected by the player, inspect whether that exact game
is running, start the selected game or Scopely launcher, download reviewed mod
releases, and maintain recovery data outside the read-only package.

The current Bridge code:

- reads bounded discovery locations and Scopely launcher metadata to find
  candidate installations;
- writes its documented Bridge state, the selected game/configuration paths,
  and explicit destinations selected by the player for local exports;
- in standalone mode, stages and replaces only its validated per-user program
  layout during an explicitly requested self-update; installed-MSIX servicing
  remains owned by Windows App Installer;
- when the player explicitly invokes the corresponding action, opens product-
  or bundled-catalog-supplied HTTPS links, the package-associated HTTPS
  `.appinstaller` source, the selected TOML and game folder, internally derived
  Bridge-state folders, Windows Settings routes, and validated game/launcher
  targets; current HTTPS activation boundaries validate URI scheme and shape,
  while #220 owns exact typed allowlisting;
- reads one current-user Registry value to follow the Windows light/dark theme;
- does not contain a production Registry-write call;
- does not request administrator elevation, install a service or driver, add a
  scheduled task, or install a browser extension;
- does not claim that signing, App Installer, or this audit proves the
  application harmless.

The warning is a declaration of what a medium-integrity desktop process could
do as the user. It is not a Windows report that Bridge has exercised every
listed resource.

## Two layers of least privilege

The current folder picker is an application policy boundary, not an operating-
system sandbox. Bridge validates the selected installation and deliberately
limits its own actions, but the `mediumIL` process token is technically capable
of accessing other locations available to the user.

That leaves two complementary least-privilege goals:

1. **Application-enforced authority:** every privileged operation accepts its
   narrow typed authority, never an arbitrary destination path, arbitrary
   bytes, or arbitrary executable. Game/mod/TOML/process operations use a
   validated installation identity; Bridge state uses internally derived
   product-owned roots; standalone self-update uses a validated program/update
   layout; exports use one-shot player-approved picker destinations; reviewed
   downloads and external activations use exact allowlisted identities. Each
   domain revalidates its authority and retains the ownership/recovery evidence
   required by that operation.
2. **OS-enforced authority:** AppContainer, Win32 App Isolation, or another
   proven boundary prevents the process from reaching resources the player did
   not grant even if application code is compromised.

The first goal is valuable and achievable while Bridge remains `mediumIL`, but
it is not a substitute for the second. The second materially reduces the
compromise boundary, but only if the replacement preserves Bridge's transaction
and recovery safety.

A game-scoped privileged API must therefore be shaped like:

```text
DeployManagedMod(installationId, reviewedArtifactId)
RemoveManagedMod(installationId)
GetSelectedGameState(installationId)
LaunchSelectedGame(installationId)
```

Other domains need equally narrow operations, such as exporting one specific
already-built report to a destination returned by the active picker or
replacing the exact validated standalone program from a reviewed update. None
may expose general capabilities such as `WriteFile(path, bytes)` or
`LaunchProcess(path)`. This rule applies inside the current process and to any
future broker protocol.

## What the package declares

The reviewed manifest at `packaging/windows/AppxManifest.xml.in` currently
declares:

```xml
<Application ...
             uap10:RuntimeBehavior="win32App"
             uap10:TrustLevel="mediumIL">
...
<rescap:Capability Name="runFullTrust" />
<rescap:Capability Name="unvirtualizedResources" />
```

Microsoft calls a `mediumIL` packaged desktop process a full-trust app. It runs
as the user rather than inside an AppContainer, and Microsoft requires such a
package to declare `runFullTrust`. "Full trust" in this context is not an
administrator token. See [App capability declarations][capabilities].

`unvirtualizedResources` permits a package to declare that selected current-
user Registry or AppData writes should not be virtualized, so those writes can
be visible to other processes and survive package uninstall. Microsoft warns
that this broad mechanism can exceed an application's needs. See
[Flexible virtualization][virtualization].

Bridge declares the capability but does not declare
`RegistryWriteVirtualization`, `FileSystemWriteVirtualization`, or Windows 11
excluded-key/directory elements. Therefore the current manifest does not ask
Windows to apply one of those specific virtualization exclusions. The
capability may instead be a schema/runtime requirement of the current
`win32App` package shape; that must be proven with a package experiment rather
than assumed.

## What App Installer can and cannot explain

Microsoft documents the native App Installer dialog as showing the application
name, icon, signature, publisher, version, source, capabilities, warning, and
install controls. The `.appinstaller` schema carries package/update
association and policy, but neither surface provides arbitrary
publisher-authored text that replaces or annotates each native capability.
See [App Installer user interface][installer-ui] and the
[App Installer file overview][appinstaller].

Microsoft also documents a package-resident
`Msix.AppInstaller.Data/MSIXAppInstallerData.xml` customization, available on
Windows 10 1709 and later. Its `<HyperLink>` element can add an external
**Why these permissions?** explanation link; its separate `AppInformation`
element can vary how the native information surface is presented. See
[Custom App Installer experience][custom-installer-ux]. Bridge must test those
controls independently, keep `AllowUserInteraction="true"`, and reject any
variant that hides the native capability list or warning by default or changes
the expected Install/Cancel semantics. Cosmetic customization must never soften
or obscure Windows' capability text.

The explanation should therefore exist on the release/testing page and, if the
supported package experiment passes, as an accessible link in App Installer
itself. The native dialog remains authoritative for the actual package
declaration.

## Capability-to-operation inventory

This inventory is based on the rc.21-era production source. It describes
operations, not permission guarantees; a medium-integrity process has broader
authority than the paths Bridge intends to use.

| Bridge operation | Current mechanism | Why a lower-authority design is not yet equivalent |
| --- | --- | --- |
| Select and validate an arbitrary Scopely STFC installation | WPF folder selection plus direct path and file inspection | AppContainer would require a brokered folder grant and durable access token whose behavior across updates, reinstall, drive changes, and multiple installations must be proven. |
| Install, update, repair, remove, and adopt the mod | Exact-handle file identity, hashing, no-replace moves, atomic replacement, journals, backups, and recovery in the selected game directory | Storage broker APIs must preserve the same identity, sharing, atomicity, crash, read-only, and external-edit guarantees before they can replace direct Win32 file access. |
| Read and edit provider TOML | Direct sparse-document reads and atomic staged Save/Discard commits | A grant must cover the exact live document and recovery files without silently broadening access or weakening compare-and-swap. |
| Detect whether the selected `prime.exe` is running | Process enumeration and executable-path comparison | AppContainer process visibility is restricted; a trustworthy broker or different game-lifetime signal would be needed. |
| Start the Scopely launcher or selected game target | Shell/process launch | AppContainer activation and desktop process launch have different contracts and consent boundaries. |
| Download reviewed mod and Bridge releases | Desktop network client plus independent authenticity policy | AppContainer network capabilities are feasible, but verifier, staging, handoff, and rollback still need redesign. |
| Share durable state with standalone recovery/fallback components | Nominal `%LOCALAPPDATA%\STFC Mod Bridge` paths visible outside the package | Package-private storage would break the current standalone and cross-process boundary unless state ownership and migration are redesigned. |
| Follow Windows light/dark mode | Read `HKCU` theme preference in `LauncherThemeManager` | This read is not a reason for persistent Registry-write authority and can be replaced if an AppContainer-compatible theme API is required. |

The package also contains a narrowly allowlisted release verifier. Any package
split must preserve the signed executable inventory, update ordering,
attestation, and compromise-response boundaries rather than quietly adding a
general-purpose privileged helper.

## Alternatives

| Option | Actual authority reduction | Windows coverage | Delivery and safety cost | Disposition |
| --- | --- | --- | --- | --- |
| Keep current MSIX and add a pre-install explainer | None | Current Windows 10/11 boundary | Low | Do now for honest tester consent while research continues. |
| Keep `mediumIL` and harden application-level installation grants | No OS sandbox reduction; narrows intended and reachable mutation APIs | Current boundary | Medium; requires a complete call-site and path-derivation audit | Required baseline regardless of the eventual package model. |
| Remove only `unvirtualizedResources` | Potentially removes the Registry/AppData persistence warning; `runFullTrust` and ordinary desktop authority remain | Potentially current boundary | Low to medium if the package shape and shared state still work | First technical spike. Do not promise success until MakeAppx, install, update, state-sharing, uninstall, and recovery tests pass. |
| Change to `packagedClassicApp` while retaining `mediumIL` | Probably no reduction in ordinary user authority; may allow a narrower manifest/virtualization model | Windows 10 2004+ in principle | Medium; runtime and filesystem behavior can change | Include in the first spike as an experimental variant, not an accepted fix. |
| One AppContainer WPF application | Material: access is limited to declared and user-granted resources | Windows 10 2004+ is documented for packaged WPF | High; nearly every file, process, launch, shared-state, verifier, update, and recovery boundary needs proof or redesign | Long-term feasibility spike. |
| AppContainer UI plus a full-trust mutation broker | Material for ordinary UI lifetime; mutation authority is concentrated in a smaller component | Depends on the selected broker/IPC/package design | Very high: two security boundaries, explicit consent, package/signing/update coordination, IPC authentication, rollback, and compromise isolation | Best long-term least-authority candidate if the single-process AppContainer cannot meet transaction safety. |
| Windows 11 Win32 App Isolation | Material, with brokered prompts and AppContainer-style isolation | Windows 11 24H2+ only; documentation and consent model are still evolving | High and cannot replace the Windows 10 route | Future platform-bounded research only. |
| `allAppMods` / ModifiableWindowsApps | None for this game path | Only compatible packaged-game AppMods locations | Not applicable to a Scopely Win32 installation | Reject for Bridge/STFC. |
| Microsoft Store distribution | Does not by itself remove `runFullTrust` or medium-integrity authority | Store-supported systems | Adds Store policy/review and another release channel | May improve publisher/distribution confidence, but is not a permission reduction. |
| MSI, EXE, or ZIP as the primary route | None; the process still runs with ordinary user authority | Broad | Loses or replaces MSIX package integrity, atomic servicing, identity, and clean lifecycle; may add custom installer risk | Reject as a security answer. A different prompt is not least privilege. |

Microsoft documents that a packaged WPF application can run in an
AppContainer, so that direction is technically real rather than hypothetical.
It also documents the resulting rule: the process and child processes can
access only resources specifically granted to them. See
[MSIX AppContainer apps][appcontainer]. The difficulty for Bridge is preserving
its exact transaction and recovery invariants across those grants, not drawing
the window.

Executable optional packages do not make a privileged component disappear.
They introduce related-set and servicing constraints, and Store distribution
requires additional approval. See
[Optional packages with executable code][optional-code]. A split design must be
treated as a new authenticated protocol and release topology.

## Recommended work plan

### Phase 0 — explain the current contract

Publish the short tester explanation from this document wherever the player
chooses the installed product. It must:

- place "not administrator" and "runs with your Windows account's ordinary
  desktop authority" next to each other;
- enumerate Bridge's actual filesystem, process, network, and theme operations;
- say explicitly that current production code does not write the Registry;
- explain that Windows' capability text describes available authority rather
  than observed behavior;
- link the manifest, source, independent verification guide, security policy,
  and private vulnerability-reporting route;
- avoid weakening the warning or telling the player to ignore Windows.

This improves informed consent but does not close #219 because it reduces no
authority.

Build disposable packages containing only the supported
`MSIXAppInstallerData.xml` controls. Record the exact customization XML, test
the external explanation `<HyperLink>` separately from `AppInformation`
`Mode="Normal"` and `Mode="flyout"`, require `AllowUserInteraction="true"`, and
prove:

- the native capability list and warning remain present and visually primary
  by default;
- Install, Cancel, update, and reinstall behavior remain under user control;
- the explanation opens a product-owned, versioned, retention-protected HTTPS
  target; qualification fetches it with redirects disabled and records the
  exact URL, status, MIME type, content digest, hosting immutability control,
  and offline result;
- the link remains advisory and the native warning remains authoritative if the
  explanation is unavailable or its published bytes no longer match evidence;
- keyboard, high-contrast, scaling, screen-reader, and offline behavior are
  understandable;
- package inspection, executable/content allowlists, signing, attestation, and
  App Installer update association remain intact.

Treat this as an evidence input to #222/#223, not as authority reduction.

### Phase 1 — application-level least authority ([#220](https://github.com/Guffawaffle/stfc-mod-bridge/issues/220))

Audit every file mutation, process inspection, process launch, download,
configuration, backup, export, support activation, and recovery entry point.
Require that:

- public/core operations accept the applicable typed authority and semantic
  product operation, not caller-supplied arbitrary output paths;
- game/mod/TOML/process operations require a validated installation identity,
  while Bridge-state, standalone-program/update, one-shot picker/export,
  reviewed-artifact/network, and allowlisted external-activation domains have
  separate narrow authorities;
- one canonical installation resolver derives every game path after validating
  the selected root and installation identity;
- filenames and artifacts are allowlisted by the operation contract;
- provider/catalog data cannot widen filesystem or process authority;
- ownership receipts authorize cleanup only for their exact installation and
  exact live revision;
- UI, command-line, diagnostics, journal, and recovery inputs all cross their
  applicable typed validation boundary;
- tests inject path traversal, alternate roots, junction/reparse paths,
  drive-letter/case aliases, stale selections, forged journals, and arbitrary
  launch targets and prove rejection before mutation.

This phase strengthens Bridge under every package model. Its acceptance must
state explicitly that Windows still does not enforce the selected-folder
boundary while the application remains `mediumIL`.

### Phase 2 — smallest capability-reduction spike ([#221](https://github.com/Guffawaffle/stfc-mod-bridge/issues/221))

Produce disposable, development-signed packages from the same reviewed
executable bytes:

1. current `win32App`/`mediumIL` with both capabilities as the control;
2. current runtime/trust shape without `unvirtualizedResources`;
3. `packagedClassicApp`/`mediumIL` with only the minimum capability set accepted
   by MakeAppx.

For every constructible variant, record:

- exact generated manifest and MakeAppx validation output;
- App Installer capability text on supported Windows 10 and Windows 11,
  including the `Microsoft.DesktopAppInstaller` version/package identity,
  Windows display language/locale, direct `.appinstaller` versus MSIX route,
  and clean-install, update, or reinstall context;
- ordinary launch, single-instance behavior, update, uninstall, and reinstall;
- nominal LocalAppData visibility from packaged Bridge, standalone recovery,
  and a separately running bounded canary;
- Bridge-state and game-file retention across update and uninstall;
- mod install/update/repair/adopt/remove, TOML Save/Discard, interruption,
  recovery, external-edit, process-lock, and residue results;
- package inspection, executable allowlist, signing, and attestation impact.

If a one-capability package passes the complete boundary, publish its exact
manifest, constraints, lifecycle receipts, and required production changes as
the minimum-capability `mediumIL` candidate for #222. If it fails, retain the
exact failure as evidence; do not infer that the broader capability is forever
necessary.

#221 is evidentiary. It does not edit the production manifest, package
inspector, or shipping product. #222 compares the proven `mediumIL` candidate
with the AppContainer and broker evidence, and #223 implements the accepted
architecture once.

### Phase 3 — AppContainer feasibility matrix and ADR ([#222](https://github.com/Guffawaffle/stfc-mod-bridge/issues/222))

Build non-production spikes for the following independent questions before
selecting an architecture:

- folder-picker grant and `FutureAccessList` persistence for every supported
  STFC installation location;
- exact file identity and same-byte external-replacement detection;
- no-replace/atomic promotion, read-only metadata, durable flush, backup,
  journal, crash, and fresh-process recovery semantics;
- selected-game process detection and game-running admission;
- Scopely launcher/game activation;
- the post-v1 startup-at-login contract in issue #118, including a default-off
  Windows `startupTask`, user revocation, and whether its documented full-trust
  entry point is compatible with the candidate AppContainer topology;
- reviewed downloads, Authenticode verification, and Bridge self-update;
- package-private versus externally shared state and migration;
- authenticated IPC and cancellation if any operation moves to a broker;
- clean uninstall, reinstall, revocation, and compromised-broker response.

Folder selection is supported by Windows, and `FutureAccessList` can retain a
user-approved location. That establishes a possible access route, not
transactional equivalence. See [File access permissions][file-access].

Microsoft documents the packaged desktop startup task as user-controllable and
shows `Enabled="false"` for an opt-in route. It also documents
`EntryPoint="Windows.FullTrustApplication"` for the packaged desktop form. That
makes startup a feasibility question rather than a reason to assume full trust
forever. See [StartupTask][startup-task]. Issue #118 remains post-v1 and is not
silently pulled into #219.

### Phase 4 — architecture decision

Choose among the proven medium-integrity reduction, a single AppContainer, or
an AppContainer UI plus mutation broker. The decision must include:

- the threat model and which compromise is actually contained;
- the exact authority and lifetime of every executable;
- player consent before any privileged component is installed or invoked;
- request allowlists, canonical paths, operation leases, authenticated IPC,
  replay resistance, logging/redaction, and update-version compatibility;
- failure, cancellation, rollback, recovery, and uninstall behavior when only
  one component updates or survives;
- Windows 10/11, packaged/standalone, migration, and fallback policy;
- signed-package, clean-machine, accessibility, performance, and independent
  verification gates;
- changes required in #89 and candidate-specific evidence required by #30.

### Phase 5 — implementation and migration ([#223](https://github.com/Guffawaffle/stfc-mod-bridge/issues/223))

Only an accepted Phase 4 decision can authorize production implementation.
Ship authority changes through a new signed canary. Never transfer rc.21 or an
earlier candidate's package, update, recovery, or uninstall evidence to a new
topology.

Issue ordering is authoritative in #219: #220 and #221 establish the two
baseline evidence lanes, #222 accepts the architecture decision, #223 receives
that exact decision as its implementation contract, and #89/#30 qualify and
classify the resulting signed product.

## PM closure boundary

#223 must produce a signed architecture canary and pass the authority-specific
manifest, native permission presentation, migration, transaction, recovery,
and negative checks needed to prove the selected design. #219 closes after
#220–#223 and those architecture-canary receipts are complete and after #89 and
#30 contain the exact final qualification matrix for the selected topology.

#219 does not wait for the complete final-v1 #89/#30 matrix. After #219 closes,
#89 completes installed-product qualification and #30 owns final source freeze,
final candidate qualification, and the release decision. This avoids a cycle
in which #219 waits for #30 while #30 waits for #219.

## Decision guardrails

- A less alarming installer is not evidence of less runtime authority.
- `runFullTrust` is not administrator elevation, but it remains a broad
  compromise boundary and should not be minimized in player copy.
- `unvirtualizedResources` must not remain merely because an old test expects
  it; nor may it be removed without requalifying state and recovery behavior.
- Do not move game mutations into a generic always-running service.
- Do not call a broker "least privilege" unless its protocol, paths,
  operations, lifetime, and update compatibility are narrower and tested.
- Do not trade exact rollback/external-byte preservation for a cosmetically
  smaller capability list.
- Store review, Authenticode, package integrity, and attestations establish
  different facts; none proves the application harmless.

## Sources

- [App capability declarations][capabilities]
- [Flexible virtualization][virtualization]
- [App Installer user interface][installer-ui]
- [Custom App Installer experience][custom-installer-ux]
- [Install and update App Installer][app-installer-update]
- [App Installer file overview][appinstaller]
- [Windows packaging, deployment, and process choices][packaging]
- [Understanding how packaged desktop apps run][desktop-packages]
- [MSIX AppContainer apps][appcontainer]
- [File access permissions and FutureAccessList][file-access]
- [Win32 App Isolation packaging][win32-isolation]
- [Win32 App Isolation supported capabilities][isolation-capabilities]
- [Win32 App Isolation consent][isolation-consent]
- [Packaged desktop StartupTask][startup-task]
- [Optional packages with executable code][optional-code]

[capabilities]: https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/app-capability-declarations
[virtualization]: https://learn.microsoft.com/en-us/windows/msix/desktop/flexible-virtualization
[installer-ui]: https://learn.microsoft.com/en-us/windows/msix/app-installer/app-installer-ui-dialog
[custom-installer-ux]: https://learn.microsoft.com/en-us/windows/msix/app-installer/how-to-create-custom-app-installer-ux
[app-installer-update]: https://learn.microsoft.com/en-us/windows/msix/app-installer/install-update-app-installer
[appinstaller]: https://learn.microsoft.com/en-us/windows/msix/app-installer/app-installer-file-overview
[packaging]: https://learn.microsoft.com/en-us/windows/apps/get-started/intro-pack-dep-proc
[desktop-packages]: https://learn.microsoft.com/en-us/windows/msix/desktop/desktop-to-uwp-behind-the-scenes
[appcontainer]: https://learn.microsoft.com/en-us/windows/msix/msix-container
[file-access]: https://learn.microsoft.com/en-us/windows/uwp/files/file-access-permissions
[win32-isolation]: https://learn.microsoft.com/en-us/windows/win32/secauthz/app-isolation-packaging-with-vs
[isolation-capabilities]: https://learn.microsoft.com/en-us/windows/win32/secauthz/app-isolation-supported-capabilities
[isolation-consent]: https://learn.microsoft.com/en-us/windows/win32/secauthz/app-isolation-app-consent
[startup-task]: https://learn.microsoft.com/en-us/uwp/api/windows.applicationmodel.startuptask
[optional-code]: https://learn.microsoft.com/en-us/windows/msix/package/optional-packages-with-executable-code
