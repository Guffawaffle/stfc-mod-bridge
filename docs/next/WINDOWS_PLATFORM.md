# Windows platform services

Backend services retain native file/process resources on their owning thread.
The shared DTOs describe observations and refusals; a capture grants no catalog
authority, operational permission or namespace exclusion.

| Criterion | Native fixture proof |
| --- | --- |
| BR06-01 | Volume/file identity, full process creation FILETIME, executable identity/architecture, retained handles, hardlink aliases, replaced files and exited/changed-generation processes. |
| BR06-02 | Bounds/device/stream refusal, final and ancestor junction refusal, write/delete exclusion and directory rename exclusion. Unsupported fixture creation fails the gate. |
| BR06-03 | Staging flush, retained backup, changed-destination refusal before ReplaceFileW and ambiguity after a failed call. The owning service supplies namespace exclusion and recovery journaling. |
| BR06-04 | Synthetic purpose/version-bound current-user DPAPI, separate Mod/OfficialGame/Bridge signature labels, cache-only primary signer observation and secondary-index refusal, exact private-window focus outcome, explicit private shortcut destination and literal arguments. |

`node scripts/next/windows-platform.mjs` compiles current native x64 artifacts,
checks twenty-five exact unit tests and five compile-fail ownership examples, then
invokes fourteen native cases separately by exact name. The re-executed child
helper is orchestration, never a fifteenth criterion or a skipped success.
Every fixture remains under a fresh `artifacts/next/windows-platform/` directory;
junction fixtures are retained without recursive deletion. Owned child processes
are released/waited through their Rust Child handles, with bounded cleanup.

The first controlled native attempt, directory
`4a10ede0-ce4c-4507-8c35-d8fc46cba854`, passed four file/reparse cases then
failed the directory rename exclusion assertion. Its original records remain
unchanged. The admitted handle had requested only attributes. The correction
requests directory listing access with no delete sharing; metadata-only capture
still observes without exclusion. [CreateFileW documents attribute sharing and
delete/rename access](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-createfilew).
The native regression now proves capture allows rename, admission denies rename,
and releasing admission restores it. Actual refreshed native proof is required;
source review and compilation alone do not close this runtime failure.

After three independent correction readings, the fresh complete fourteen-case
run passed at `artifacts/next/windows-platform/64298f25-5c25-4113-9209-585fba60344b/windows-platform.json`,
SHA256 `2a83c3622b3234d789c99fa71293e493b9bb97a22acce02ed9333de54c142068`.
The exact regression establishes the intended directory sharing behavior in
those fixtures. Primary signatures were OS-trusted in each separately labeled
domain, with the pinned certificate; index 1 was refused. The visible owned
window's foreground request was denied by OS policy and recorded as `denied`,
with distinct `no_window` and `process_exited` observations. This closes the
confirmed rename defect for the tested artifact/environment, while retaining
the application-service and release boundaries below.

The signed fixture is a byte-for-byte private copy of the reviewed Windows Node
executable. Its version, digest and independently extracted embedded certificate
digest are pinned in `dependencies/next-windows-signature-fixture.json`.
[Microsoft's certificate extraction API](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.x509certificates.x509certificate.createfromsignedfile?view=net-9.0)
supplies an embedded certificate; it does not establish trust or publisher policy.
The native test must separately observe cache-only OS trust and that exact
certificate. It never executes the copy, searches for a replacement subject or
approves an application publisher. A different Node installation fails this
fixture pin and requires explicit refreshed evidence.

Read-only route checks and retained parent handles do not create an atomic
root-relative namespace lock. ReplaceFileW remains path based and requires the
owning operation's actual exclusion; flush completion is not a promise about
crash durability. HWND identity is rechecked around the focus request, but the
OS call cannot atomically retain a window handle against recycling. A denied
foreground request remains a recorded policy outcome. These services alone do
not qualify a game installation, actual catalog, engine integration, macOS,
assistive technology, signing, distribution or release.

The new retained private journal owner and its unit/ownership tests are described
in [PRIVATE_JOURNAL_STORAGE.md](PRIVATE_JOURNAL_STORAGE.md). Its native
ACL/sharing/namespace-flush and process-interruption fixtures remain pending;
this platform suite does not qualify that owner for production bootstrap.
