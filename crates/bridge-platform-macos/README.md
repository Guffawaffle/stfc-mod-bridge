# Native Apple Silicon platform source

This crate implements backend services using macOS APIs and the existing
`bridge_domain::platform` observations. It is source authored on Windows.
Portable tests and explicit Apple Silicon cross typechecking have separate
candidate-bound receipts. Cross typechecking does not link Apple frameworks or
run native code. No native macOS linking, fixture, Keychain, focus, signing or
installed game test has run for this source. It is not br-07 acceptance or a
release claim.
The native implementation is compiled only for `macos` + `aarch64`; other hosts,
including Intel macOS, return `UnsupportedHost` from `require_macos_arm64`.
An arm64 host does not grant support to an observed translated x86_64 target.

Root registers `bridge-domain.workspace = true`, `sha2.workspace = true`, and
target-only `libc = "=0.2.190"`. There are no frontend, Tauri, shell, ObjC wrapper,
catalog, process lifecycle or application-policy dependencies. CoreFoundation,
Security and ApplicationServices are linked natively. The selected Security
network flag requires macOS 11.3 or later; the actual supported deployment floor
must be set and tested by packaging, not inferred from this document.

## File and bundle observations

`capture_directory` and `capture_file` walk every exact absolute component using
retained `openat` descriptors with `O_NOFOLLOW`. Dot, parent, repeated separator,
trailing separator, NUL and oversized forms refuse rather than normalize.
Every ancestor remains retained; relative-name `fstatat` revalidation checks its
physical device/inode and kind. A path prefix never proves ancestry. FinderInfo
alias flags refuse. Alias resolution and symlink traversal are not fallback
routes. Typical lexical `/var` or `/tmp` symlinks therefore need an explicitly
selected physical path such as the observed `/private/...` location.

`F_GETPATH` records the descriptor's physical filesystem path, including native
case and Unicode spelling. No lowercase or Unicode-normalized identity is
invented. Volume capability bits record `Some(true)`, `Some(false)` or unknown
for case sensitivity, case preservation and atomic swap. Real APFS case-sensitive
and insensitive volumes must qualify independently. Unsupported attributes and
network/union filesystem behavior do not gain local APFS semantics.

Hashes read through a retained descriptor with a 256 MiB ceiling. Descriptor
identity, length, mode/link count, mtime/ctime and physical path are checked
around the read. A hash observes current disk bytes, never an executable's mapped
image. A descriptor does not deny writes, unlink, renames or namespace changes.
These guards are neither a mandatory OS lock nor a Profiles lifetime exclusion.
Same-user adversarial write-and-restore between observations cannot be ruled
out by two hashes; the owning transaction must hold its reviewed exclusion.

`capture_bundle` uses CFBundle's executable URL and then independently captures
the executable through the filesystem adapter. The executable must have the
retained bundle directory in its descriptor ancestry. This grants no bundle-ID,
publisher, manifest, profile or launch policy. Bundle resources and deeply nested
mutable content are not snapshotted by retaining the root and executable.

## Staging, exchange and recovery

`StagedReplacement::prepare` consumes an explicitly selected parent guard and
requires observed native swap support. It exclusively creates one nonce-named
stage in that parent with owner-only mode, a 64 MiB ceiling, complete writes,
`fsync` and `F_FULLFSYNC`. The caller explicitly selects `PrivateData` (0600) or
`OwnerExecutable` (0700). Original ACLs, xattrs, resource forks, ownership and
mode are not silently copied. Metadata-bearing package updates need their own
reviewed artifact materialization route; this API is not a directory updater.

Before `prepare` can create a file, the owning engine must retain its exclusion
and durably record the exact parent/nonce/stage-name intent. A failed preparation
can leave a partial stage, so journaling only the successful returned observation
would lose recovery custody after a forced exit. After preparation succeeds, the
engine records the prepared identity/hash before calling
`replace_under_owner_exclusion`. That method checks the stage's retained
descriptor and current name, requires an exact hashed regular-file destination,
and refuses stage/destination aliasing and multiple hard links. Both names are
relative to one retained parent. `renameatx_np(RENAME_SWAP)` atomically exchanges
them, retaining the old destination at the stage path. It flushes the parent and
verifies both resulting physical identities and hashes. No path is removed on
drop; failed preparation files and backups remain for the owning recovery flow.

`RefusedBeforeCall` means no exchange call was entered. Every error after entering
the exchange call returns `AmbiguousAfterCall`, including a native refusal or
failed post-call validation/parent flush. It never retries, deletes a backup or
claims unchanged destination after a failed call. A successful observation
records staged flush completion. It does not prove hardware flush compliance,
atomic hardware crash recovery, a durable engine journal, uninterrupted work
after forced process death or an application transaction's commit. Those need
real-host interruption/fault tests and the engine's separate recovery protocol.

## Processes and focus

`capture_process` observes an explicit positive PID via libproc BSD info,
`proc_pidpath` and `PROC_PIDARCHINFO`, before and after the physical executable
capture. It records kernel start seconds/microseconds and the target's observed
CPU type. Unknown architecture/start units, partial native structures and a path
byte count that disagrees with libproc's NUL-terminated result refuse. Apple's
libproc header labels these interfaces private and subject to change. Each
supported macOS/SDK route therefore needs its own actual ABI/runtime evidence;
source constants do not establish future OS support or App Store eligibility.
`open_exact_process` binds all exact fields; revalidation detects a changed
generation, executable or architecture. Permission/identity failure is not
reported as a clean exit. The guard is not a Mach task or kernel process lease.
There is no enumeration, first-process selection, launch, kill or game cycle.

Focus runs only on the actual main thread. It captures a ProcessSerialNumber
between exact process revalidations and verifies the serial's PID. It addresses
that native application-instance identity with `SetFrontProcess`, then checks
the native foreground serial and exact process again. A missing GUI serial is
followed by exact process revalidation; it cannot alone prove process exit.
It does not fall back to
PID-only or bundle-ID activation. OS refusal is `Denied`; absent GUI identity is
`NoWindow`. Process Manager APIs are public but deprecated. Actual Apple Silicon
symbol availability, exact-session behavior and desktop permissions must qualify
before the application advertises focus availability. A text process fixture
does not qualify a GUI foreground result or assistive technology behavior.

Native menu registration currently returns `FeatureUnavailable`: AppKit host
main-thread/run-loop and callback custody have not been registered. Shortcut
creation also returns `FeatureUnavailable`: a Finder alias cannot express the
current exact executable/argv/working-directory port. No `.command`, AppleScript,
shell invocation, implicit Desktop path or approximate shortcut is substituted.
The owning application projects these operational feature facts; the renderer
must not branch by OS name or display names.

## Keychain and signature custody

Keychain services reject root and setuid execution, use the ordinary user's Data
Protection Keychain, deny authentication UI, disable synchronization and set
`WhenUnlockedThisDeviceOnly`. The fixed service plus purpose digest and random
nonce selects one generic-password item. Purpose version/domain/context are
bound into both the opaque reference and stored envelope. Unknown versions,
control/NUL contexts, wrong purposes, malformed envelopes and oversized payloads
refuse. References contain no plaintext. There are no broad queries, updates,
access-group discovery, search-list changes, login-Keychain setup or implicit
delete-on-drop. `delete_secret` takes one exact purpose/reference explicitly.

Rust-owned plaintext buffers have no Clone/Debug/Display/serialization and are
volatile overwritten on drop. Caller buffers and internal Security/CFData
buffers are outside that wipe guarantee. Native CF Create/Copy references are
owned and released exactly once; Get/static references remain borrowed. Native
descriptors and plaintext custody are deliberately local and neither Send nor
Sync. Compile-fail examples cover descriptor/process/stage/plaintext custody.
Data Protection Keychain entitlement and locked/denied behavior require an
actual correctly signed ordinary-user host. Source cannot assume that custody.

Signature observation selects the arm64 static code object (index zero; other
indices refuse), performs strict/all-architecture/nested-code/symlink-restricted
validation, and applies the fixed OS `anchor trusted` requirement. Merely passing
cryptographic validity without this requirement would not establish OS trust.
The selected leaf certificate is SHA-256 fingerprinted. A separate SecTrust
object uses code-signing policy and positive OCSP/CRL policy; CacheOnly disables
network and refuses missing positive cache evidence, while Online is an explicit
request for native certificate retrieval. The API does not change trust settings,
certificate stores or Security's returned live trust objects. Synchronous native
trust reads refuse the actual main thread; the owning host captures and retains
the local file descriptor on its worker.

Mod, official-game and Bridge domains remain explicit independent caller facts.
OS trust and a signer fingerprint do not approve a publisher for any domain.
No Gatekeeper/notarization, signing credential, remote artifact or mapped-code
claim is made. Security reads by path and requires the caller's held exclusion;
descriptor/hash checks alone cannot prevent concurrent bundle-resource edits.
Real fixtures must verify unsigned/ad-hoc/untrusted/revoked/offline/known signer
and domain policy behavior before operational qualification.

## Test boundaries and future real-host requirements

`tests/format.rs` contains 16 portable parser/bounds/purpose/identity tests.
They can run on Windows after root registration and a granted Cargo window.
They establish parsing semantics only. `tests/native.rs` contains 11 ignored
real macOS arm64 cases. Ignored is not accepted: the qualification harness must
explicitly discover and execute every required native case, retain commands and
candidate/binary/source/fixture hashes, and fail absent actual receipts.

Native filesystem cases require `BRIDGE_MACOS_NATIVE_FIXTURE_ROOT`, an existing
physical ordinary-user-owned directory with no group/other access. Every test
creates a fresh owner-private child exclusively. Files remain for evidence;
there is no recursive cleanup or arbitrary destination. The owned child fixture
requires a separately compiled `native_fixture_child` example inside that root,
plus exact `BRIDGE_MACOS_OWNED_CHILD` and `BRIDGE_MACOS_OWNED_CHILD_SHA256` bindings.
It dispatches only that pinned helper with fixed role/nonce arguments, exact
private cwd and one stdin quit byte; no arbitrary kill is used. The native test
observes its actual process generation and exit. This does not qualify actual
kernel PID reuse stress or a game session.

Keychain case additionally requires the explicit
`BRIDGE_MACOS_KEYCHAIN_FIXTURE=ephemeral-ordinary-user-data-protection` marker.
Only an ephemeral runner or explicitly provisioned disposable user is suitable;
do not run Profiles' Keychain setup against a developer's login Keychain. It
deletes only the exact newly created item even after an assertion failure. No
fixture reads accounts, app preferences, TOML, catalog or game logs.

Remaining required actual-host qualification includes native compile/link/FFI
layout, all ignored cases, both actual case-volume types, Finder alias creation,
permission/ACL/xattr/locked-Keychain failures, stage collisions/ENOSPC, injected
post-call failures, process death at every recovery boundary, native GUI focus
and PID reuse, signed known-leaf/revoked/offline corpus, concurrency/exclusion
integration, and actual selected game/runtime architecture. Menu/shortcut
availability stays explicit until their host implementation is qualified.
None of these may be replaced with a Windows build or synthetic macOS pass.

## Primary API references

- Apple [libproc ABI](https://github.com/apple-oss-distributions/xnu/blob/main/bsd/sys/proc_info.h).
- Apple [libproc interface declaration](https://github.com/apple-oss-distributions/xnu/blob/main/libsyscall/wrappers/libproc/libproc.h) and [path return semantics](https://github.com/apple-oss-distributions/xnu/blob/main/libsyscall/wrappers/libproc/libproc.c).
- Apple [fcntl/F_GETPATH/F_FULLFSYNC](https://developer.apple.com/library/archive/documentation/System/Conceptual/ManPages_iPhoneOS/man2/fcntl.2.html).
- Apple [APFS safe-save APIs](https://developer.apple.com/library/archive/documentation/FileManagement/Conceptual/APFS_Guide/ToolsandAPIs/ToolsandAPIs.html).
- Apple [Finder flags](https://github.com/apple-oss-distributions/hfs/blob/main/core/hfs_macos_defs.h) and [FinderInfo layout](https://github.com/apple-oss-distributions/hfs/blob/main/core/hfs_format.h).
- Apple [CFBundle executable URL](https://developer.apple.com/documentation/corefoundation/cfbundlecopyexecutableurl(_:)).
- Apple [GetProcessForPID](https://developer.apple.com/documentation/applicationservices/1501069-getprocessforpid), [GetProcessPID](https://developer.apple.com/documentation/applicationservices/1500992-getprocesspid), [SetFrontProcess](https://developer.apple.com/documentation/applicationservices/1501042-setfrontprocess), and [Process Manager serial identity](https://developer.apple.com/library/archive/documentation/mac/pdf/Processes/Intro_to_Procs_Tasks.pdf).
- Apple [Keychain accessibility](https://developer.apple.com/documentation/security/restricting-keychain-item-accessibility).
- Apple [static code validation](https://github.com/apple-oss-distributions/Security/blob/main/OSX/libsecurity_codesigning/lib/SecStaticCode.h), [signing information ownership](https://github.com/apple-oss-distributions/Security/blob/main/OSX/libsecurity_codesigning/lib/SecCode.h), and [trusted requirements](https://developer.apple.com/library/archive/documentation/Security/Conceptual/CodeSigningGuide/RequirementLang/RequirementLang.html).
- Apple [revocation policies](https://github.com/apple-oss-distributions/Security/blob/main/header_symlinks/Security/SecPolicy.h) and [trust evaluation](https://github.com/apple-oss-distributions/Security/blob/main/header_symlinks/Security/SecTrust.h).
