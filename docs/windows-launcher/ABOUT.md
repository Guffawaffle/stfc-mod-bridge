# About, attribution, and notice ownership

The About page is STFC Mod Control's durable identity and provenance surface.
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

The runtime loader fails closed for duplicate IDs, missing required notices,
and inventory references to unknown notices. Production `PackageReference`
coverage and the generated document are checked with:

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
- Extraction provenance and git history ground the named community-mod
  credits. Provider recognition is explicitly not presented as approval of
  this standalone application. Bundled product artwork remains marked
  `review-pending` until the final issue #30 asset review.

These records support product implementation; they do not assert legal
clearance. Final wording, packaged notice completeness, trademarks, artwork,
minimum-width behavior, keyboard behavior, and screenshots remain release
evidence under issue #30.
