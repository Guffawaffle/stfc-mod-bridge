# Settings and Data Sync

The real App mounts schema-driven Settings and Data Sync over one facade and
one retained draft. The backend schema supplies field types, categories, search
terms, defaults, platform restrictions, sensitivity, apply timing and Data Sync
roles. The frontend has no separate TOML feature list.

Direct field changes display their supplied field timing. Structural Data Sync
changes (destination creation/removal, feeds and proxy preference) follow the
current closed Rust protocol policy, `next_launch`. Sensitive role fields do not
define different timing for those structural operations.

| Criterion | Behavior and evidence |
| --- | --- |
| BR19-01 | Boolean, enum, integer, unsigned integer, decimal, text and protected fields render from the supplied schema. Categories and search use schema metadata and preserve edits while filtering. |
| BR19-02 | Public edits are sparse overrides. Removing an override restores the provider default. Incomplete numeric input stays attached to the draft and blocks Save instead of becoming zero or disappearing. Validation and apply timing remain visible. |
| BR19-03 | Protected entry uses the native/headless input port and returns an opaque reference. The renderer never accepts the protected value. Data Sync creation, endpoints, secrets, feeds and custom proxies follow declared field roles and modes. Hidden and display-only definitions cannot create destinations. |
| BR19-04 | Settings and Data Sync share staged edits and the App's Save/Discard/Stay decision. Save reviews the complete captured draft. Admission does not clear it; only the matching completed operation permits baseline reopening. An uncertain submission retains its exact replay. Stay and confirmed Discard preserve the intended target and restore focus. |
| BR19-05 | External document, schema, target or host changes preserve the draft and stop unsafe edits. Failed Save, unavailable entry and stale observations show feedback without reporting success. A reopened baseline refreshes displayed saved values. |
| BR19-06 | The separate browser preview mounts the real App and shared review controls. Pinned-browser journeys check labels, keyboard focus, modal controls, themes, reduced motion and enlarged text at desktop and compact widths. Production dependency inspection excludes preview, fixture and test modules. |

`ui/settings-preview/` is a separate development entry. Its bounded fixture
session validates closed request and reply DTOs, preserves shared golden source
provenance, and binds generated acknowledgements and reviews to the exact
synthetic draft and document. This fixture composition is development behavior;
it cannot resolve physical installation identity, approve provider policy,
capture real protected values or write configuration.

The source and browser drivers retain their actual commands, observations and
input hashes. The final suite combines both with a production build after the
candidate is frozen. Tests with synthetic ports do not qualify the native
configuration owner, installed webviews, Narrator, VoiceOver, an Apple Silicon
host or a release. Those boundaries require their own native evidence.
