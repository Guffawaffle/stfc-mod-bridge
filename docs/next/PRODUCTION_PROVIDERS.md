# Production host providers

`bridge-app` contains backend application services shared by CLI and native UI composition. Its clock and identity providers implement the operation kernel’s existing fallible ports and depend on no renderer. They do not create a journal, select a game installation, construct a dispatcher host or grant authority.

## Clock sampling and expiry

`NativeClock` reads the supported host’s native monotonic source before one UTC wall-clock observation. Windows x64 uses GetTickCount64; Apple Silicon macOS uses mach_continuous_time with a checked timebase conversion. Both count suspension. Values have a boot-local origin, accept zero and equal readings, and cannot be reused as a deadline after a new host epoch. The engine detects and latches regression in actual samples.

UTC is evidence and a display projection. Conversion from SystemTime uses checked signed nanoseconds, including fractional times before Unix epoch, then the pinned time library’s fallible constructor and RFC3339 formatter plus the strict protocol timestamp constructor. Unsupported native observations or unrepresentable UTC values refuse; the provider does not replace native monotonic failure with wall time.

A deadline derives only from its supplied ClockReading. Monotonic addition and UTC duration conversion/addition are checked independently. Later wall-clock adjustment does not change the engine’s monotonic expiry decision. No sampling occurs during deadline derivation.

## Identity inputs

`NativeIdentitySource` requests one independent sixteen-byte native cryptographic sample for every host epoch, stream, plan and operation ID. Windows uses the system-preferred BCrypt source; macOS uses Security’s default source. No retry, seed, cache or alternative source substitutes for refusal.

Encoding masks only the UUID version-four and variant bits, preserves the other 122 random bits, emits lowercase canonical hyphenated text, and validates the corresponding strict protocol type. IDs do not establish account, process, installation, permission or publisher identity. Random generation is no guarantee of uniqueness: the engine still rejects reused plan/operation identities and bounds host-lifetime issued-plan custody.

## Evidence boundary

Deterministic provider tests cover call order/count, native refusal, UTC representation boundaries, fractional pre-epoch conversion, deadline rollover/overflow and UUID masking. Supported-host tests call the actual read-only native providers without logging entropy; each receipt belongs to that host and source. A successful clock read does not test system suspend, and synthetically injected faults do not qualify a native persistence or game route.

Application host construction, retained private storage, CLI/Tauri adoption and installed Windows/Apple Silicon journeys remain separate dependencies of br-21. These provider objects alone do not accept that matrix package or qualify a release.
