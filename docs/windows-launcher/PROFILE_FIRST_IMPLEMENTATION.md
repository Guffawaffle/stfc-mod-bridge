# Shuttle Bay and Engineering implementation

Guff accepted this direction on 2026-10-02. Shuttle Bay contains named profile
launch/focus actions; Engineering contains profiles, settings, installations,
game updates and runtimes. Profile, Installation and Session remain literal
domain terms. Select installation is the ordinary chooser label.

## Delivery order

1. Add a typed, catalog-owned Default descriptor in STFC Profiles. It has a
   persistent immutable ID and uses the current Windows user's existing setup.
   Creating its metadata does not read, copy or redirect account data. Existing
   isolated IDs and stores retain their identity. Versioned projections prevent
   older consumers from treating Default as an empty isolated profile.
2. Consume that identity in Bridge. Launch, configuration and maintenance bind
   to the visible selected profile and its chosen installation. Default launches
   in ordinary mode; isolated launches retain exact isolation admission.
3. Introduce Shuttle Bay and Engineering over one selection/session/draft model.
   Remember presentation preference. View switching does not retarget or save;
   profile changes use Save, Discard or Stay. Remove the separate hidden launch
   selection step. Separate create and copy-Windows-setup routes.
4. Scope observation/navigation guards to their actual resources. Preserve
   native storage custody, exact process exclusion and update recovery. Runtime
   operations act on installation configuration, not profile configuration.
5. Represent Profiles only alongside NetniV and Guffawaffle. Qualification and
   exact artifact availability remain explicit. NetniV ordinary launch works;
   isolated launch requires actual adapter capability. One installation has one
   bootstrap DLL, and switching preserves isolated stores and ordinary setup.
6. Run native/managed focused and full checks, independent Bridge review, package
   validation and a fresh desktop test build. Publish dependency pins only after
   the exact producer source is reviewed and available. Live account import,
   multi-account persistence, packaged host and macOS qualification are separate
   evidence, not inferred from unit tests.

This work uses the existing checkouts and branches. It creates no real account,
imports no preferences and deploys no game DLL as an incidental UI change.
The design study in the Windows control workspace records the wider installation
registration and producer integration contract; completed behavior and remaining
work must be recorded separately at handoff.

## Review corrections

Default metadata edits omit a name field, since the native descriptor cannot be
renamed. Game status, checking, updating, progress polling and recovery carry
the captured registration ID. Changing that ID invalidates checked-version
evidence even when the path is unchanged. A stale registration cannot authorize
shared runtime or Default configuration targets. Prepared runtime work carries
its original registration in the preparation record across provider composition,
then acquires native directory custody and rechecks that registration before
execution. The lease spans the complete download/commit await. Retired provider
composition cannot display a late preparation result. Journal recovery still
reaches an incomplete image through registration-bound native recovery.
Provider switching carries the same captured ID. Runtime deployment, switching,
uninstalling and recovery retain native directory custody through their awaited
operation. Durable transaction journals and installed ownership receipts retain
the original installation ID, physical identity and canonical directory. Recovery
revalidates that saved binding before inspecting dependencies or restoring files;
an incomplete image is recoverable, but a replacement physical directory is not.
Missing recovery bindings preserve the backups for review rather than deriving a
new expectation. Detaching management changes only Bridge metadata and does not
register or modify the game directory.

Unbound Default launches resolve valid installation aliases to a canonical
registration for that operation without changing the saved Default profile.
Asynchronous preparation and provider-switch results publish only into their
original active UI generation; retired results cannot clear newer work.

Startup, refresh and launch decisions observe profile sessions. Default can
coexist with verified isolated sessions, but an unknown running process cannot
be claimed or authorize an extra ordinary launch. Settings navigation restores
its workspace after provider recomposition. Profiles-only composition has an
explicit empty community-settings catalog with its own source identity.

Save/Discard/Stay protects Settings and Data Sync drafts on target transitions.
Profile metadata and import-form draft preservation needs separate UX validation;
the settings guard does not establish that behavior. Actual desktop startup,
Focus/foreground behavior and the account-import/reverse-restart journey remain
live qualification work. The old Home-oriented smoke script is not evidence for
this shell.
