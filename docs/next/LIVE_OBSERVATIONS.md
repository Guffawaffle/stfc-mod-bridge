# First live desktop observation flow

The first connected Rust desktop milestone reads registered installations and
running game processes. Windows x64 is the locally exercised host. Multiple
launches remain future work; collection-shaped contracts and exact session
identity are retained. Multiple observed processes are reported, never selected
implicitly. No launch, focus, configuration, catalog or update mutation is admitted.

## Data and ownership

`bridge-app::observations::ReadOnlyApplication` is the shared CLI/desktop v1
dispatcher. It runs inside the existing engine owner channel and retains the
native consumer on its owning thread. Its only implemented command is safe host
close. It creates no operation journal because it cannot admit transactions.
The future mutation host keeps its separate durable journal/admission obligations.

The observation source reads the canonical Profiles API. It does not parse a
private copy of the catalog or scan alternative installation roots. Native
registration IDs remain canonical; OS adapters observe directory identity,
process creation identity, executable identity and actual architecture. Paths
and OS-user identifiers do not enter the UI projection. A live process does not
establish its account/profile association or isolated readiness.

The frontend uses the existing `BridgeClient` and `TauriTransport`. Browser
entries remain unavailable unless their development host injects a fixture
facade. Native bootstrap supplies the document epoch; every later invocation
must match the native current document, main window and permitted local origin.
Navigation retires the old document. A separate delivery worker services bounded
controller settlement and expiry; normal exit waits for the read owner to finish.

The native window refreshes snapshots every five seconds. Unchanged observations
keep their stream/domain revision while evidence time advances. Changed facts
invalidate the stream; missing events require a new snapshot. Incomplete native
reads remain explicit unavailable/partial observations, not successful empty lists.

## Run the development candidate

Build using the pinned repository wrapper:

```powershell
node scripts/next/desktop.mjs build --no-bundle --ci
```

Launch the resulting `target/x86_64-pc-windows-msvc/release/bridge-desktop.exe`
with `--native-root` set to this checkout's absolute path. This startup argument
is native-host input; a renderer cannot supply it. Without it, the application
looks for native inputs under its executable directory and reports unavailable
when those inputs are absent.

The read-only CLI exercises the same dispatcher:

```powershell
node scripts/next/cargo.mjs run -p bridge-app --example observe -- <absolute-checkout-root>
```

The compiled development pins come from `dependencies/next-native-inputs.json`.
The module hash, native host, originating exports, API version and receipt hash
are checked. These existing historical ABI-probe inputs are not qualified
release inputs. Packaging a production native distribution is still open.
Apple Silicon code paths are shared, but its native Profiles pin is currently
absent; an unavailable observation is expected until that input is qualified.

## Validation boundary

Rust tests cover unavailable reads, mutation refusal, domain revisions, stream
retention, generation-bound session IDs and close. Frontend tests cover native
bootstrap/readiness and browser isolation. Existing scripted UI tests remain
separate from actual native-window observation.

Retain exact source/diff and executable hashes with live smoke evidence. An
installed native window showing a real PID is useful evidence for this flow;
it does not qualify game launch, profile isolation, writer custody, accessibility,
the entire br-21 package, either release pipeline or Apple Silicon execution.
