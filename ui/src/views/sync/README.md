# Data Sync workspace

Data Sync shares the exact Settings draft, target, baseline and schema binding.
`SyncController` stages feeds, proxy preferences, removals and new destinations
as participants in the complete typed edit set. Desired preferences remain
separate from observed effective feed/proxy states.

Modes, creatability, field roles, published feeds and global-proxy inheritance
come from `SyncTypeDefinition`. Hidden modes expose no mutation controls;
existing-only modes permit existing configuration editing and no creation.
New destinations require the explicit mode and exact current-draft protected
endpoint and secret role captures. Creation moves those captures into one typed
participant atomically, preserving unrelated Settings changes. A custom proxy
capture is likewise moved into its destination participant so protected handles
are not aliased between edits.

Staging creates no network connection and activates no dormant sidecar or Battle
flow. Ordinary presentation names destinations by their visible ordinal and mode,
without displaying endpoints, secret IDs or filesystem paths. Full-set Save,
Discard, target-change review and operation outcomes belong to the shared facade
and application composition.
