# Apple Silicon release prerequisites

The initial release targets native Apple Silicon macOS alongside Windows x64.
The current campaign has no bound live Mac host or confirmed Bridge signing
identity. The human confirmed an existing Apple developer account used to sign
the Community Mod. Reuse that account; new enrollment is not a prerequisite.
Development and portable qualification can continue; the Mac release cannot be
qualified by Windows, cross-compilation or browser evidence.

For direct distribution, use an active Apple Developer Program membership and
a Developer ID Application signing certificate with its private key available
on the authorized signing host or CI secret store. Apple's account holder can
create the certificate. A Developer ID Installer certificate is additionally
needed if the chosen distribution uses a signed installer package; it is not
required merely to sign an app for a disk image.

Configure a notarization credential through the signing host's supported
Keychain profile or a CI credential reference. Keep secret values outside source
and chat. Record the chosen team ID, signing identity, bundle ID, credential
reference, packaging route and host in the packaging packet before signing.
Bind the Bridge package to that explicit signing identity and verify its own
signature and notarization evidence independently of the Community Mod payload.

The existing Community Mod signing workflow references environment-scoped secrets
`MACOS_CERTIFICATE_P12_BASE64`, `MACOS_CERTIFICATE_PASSWORD` and
`APPLE_APP_SPECIFIC_PASSWORD`, and variables `APPLE_ID`, `APPLE_TEAM_ID` and
`MACOS_SIGNING_IDENTITY`. The workflow selects the `macos-release` environment;
the separate submission observer uses `macos-notary-status`. These names identify
the integration seam without
exposing secret values. The live Bridge environment inventory currently contains
`windows-release` and `copilot`, with no Mac release environment. Confirm
credential availability to the Bridge repository
and its chosen signing workflow before package qualification. A configured
reference does not establish certificate validity or a successful Bridge signature.

Live qualification requires a reachable Apple Silicon Mac with the supported
STFC client installed. Bind its canonical owning checkouts and explicit game
installation/profile/session before native validation. That host exercises the
actual installed app, native modules, Keychain, launch/store ownership, runtime
and configuration routes. A hosted build runner alone does not establish those
live game observations.

Apple's current setup guidance:

- [Developer Program enrollment](https://developer.apple.com/programs/enroll/)
- [Developer ID certificates](https://developer.apple.com/help/account/certificates/create-developer-id-certificates)
- [Notarizing macOS software before distribution](https://developer.apple.com/documentation/security/notarizing-macos-software-before-distribution)

This prerequisite record creates no credentials, purchases membership, registers
an app, selects a publisher identity or qualifies a release.
