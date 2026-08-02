# Extraction provenance

The Windows launcher was developed in
[`Guffawaffle/stfc-mod`](https://github.com/Guffawaffle/stfc-mod) before this
standalone repository was created on 2026-08-02.

The initial history was produced with a Git subtree split of
`windows-launcher/` through original source commit
`266ce72731e68c97f5b7010ccea3afcbd21d0553`. Launcher-owned documentation,
fixtures, generated provider data, and artwork that lived elsewhere in the
parent tree were then imported explicitly.

Subtree extraction rewrites commit IDs and therefore cannot retain the
original commit signatures. Historical signatures must be verified against
the original repository. New commits in this repository are expected to be
signed.

The extraction intentionally excludes:

- the C++/IL2CPP mod runtime;
- Windows proxy DLL code and build machinery;
- the macOS launcher, loader, and dylib;
- parent-repository release automation that builds mod artifacts;
- generated binaries, screenshots, traces, and local dogfood artifacts.

Mod repositories remain authoritative producers of runtime manifests,
configuration schemas, and mod release artifacts. This repository consumes
those contracts through provider packs.
