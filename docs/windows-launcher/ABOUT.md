# About, attribution, and notice ownership

The About page is STFC Mod Bridge's durable identity and provenance surface.
It owns product/build information, active provider/runtime context, credits,
game/publisher acknowledgement, and readable third-party notices. It does not
own repair, update, removal, game/log folder, diagnostic export, or other
operational commands; those belong to Diagnostics. Raw TOML belongs to the
Advanced configuration surface.

## Maintainable content

[`about-content.v1.json`](about-content.v1.json) is the reviewed source for:

- contributor credits and acknowledgements;
- the game/publisher compatibility and non-endorsement statement;
- dependency and asset attribution inventory;
- third-party notice text and authoritative links.

The runtime loader fails closed for duplicate IDs, unsupported evidence or
attribution states, missing required notices, and inventory references to
unknown notices. The generator compares the catalog in both directions with:

- runtime-bearing NuGet packages resolved in each production
  `obj/project.assets.json`;
- self-contained runtime-pack download inputs resolved by the SDK; and
- every explicit production project `Resource`, `Content`,
  `EmbeddedResource`, `ApplicationIcon`, and `ApplicationManifest` input.

An added input fails the check until it is classified, and a stale inventory
entry fails until it is updated or removed. Run the coverage and generated
document check with:

```powershell
./scripts/generate-third-party-notices.ps1 -Check
```

Run the script without `-Check` after an intentional catalog change. It
regenerates the repository-root `THIRD-PARTY-NOTICES.md`; that generated file
must not be edited directly.

## Attribution evidence and boundary

- FluentIcons package metadata identifies `davidxuang/FluentIcons`, version
  2.1.333, under MIT and records its upstream commit.
- FluentIcons documents its glyph source as Microsoft Fluent UI System Icons;
  that upstream repository publishes an MIT license.
- Microsoft's .NET license information states that Windows product
  distributions use the .NET Library License and that runtime third-party
  notices also apply. The app therefore links to Microsoft's authoritative
  information rather than simplifying the self-contained Windows runtime to
  an unsupported blanket MIT claim.
- The automated runtime-pack inventory identifies the exact SDK-resolved pack
  versions, but it does not prove component-level notice completeness for the
  resulting self-contained executable. That evidence remains
  `review-pending` under issue #30.
- Extraction provenance and git history ground the named community-mod
  credits. Provider recognition is explicitly not presented as approval of
  this standalone application. Every explicitly bundled artwork input is
  classified by project identity and remains `review-pending` until the final
  issue #30 asset review.

These records support product implementation; they do not assert legal
clearance or complete coverage of files introduced internally by the .NET
single-file publisher. Final wording, packaged component-notice completeness,
trademarks, artwork, minimum-width behavior, keyboard behavior, and screenshots
remain release evidence under issue #30.
