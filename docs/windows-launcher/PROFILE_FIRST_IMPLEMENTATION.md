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
