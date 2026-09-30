using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using STFCCommunityMod.Launcher.Core;

namespace STFCCommunityMod.Launcher;

/// <summary>Explicit disposable development fixture; never part of normal profile startup.</summary>
internal static class ProfileCatalogPackageQualification
{
    internal const string Argument = "--profiles-package-qualification";
    internal const string Schema = "stfc.mod-bridge.profiles-package-qualification.v1";
    internal const string ReceiptFile = "receipt.json";
    internal const string ReadyFile = "msix-ready.json";
    internal const string VerifiedFile = "standalone-verified.json";
    internal const string ReleasedFile = "msix-released.json";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly byte[] FakeExecutable = Encoding.UTF8.GetBytes("STFC Bridge synthetic qualification: never execute.\n");
    private static readonly byte[] FakeVersion = Encoding.UTF8.GetBytes("&game=221\n");
    private static readonly string[] FakeGameFiles = [".version", "prime.exe"];

    internal sealed record Options(string Mode, string Nonce, string Fixture);
    internal sealed record Receipt(string Schema, string Nonce, string Fixture, string CatalogRoot,
        string Id, string Name, string Revision, string State, string BuildIdentity, string OutputRoot);
    internal sealed record Evidence(string Schema, string Nonce, string Id, string Revision,
        string BuildIdentity, string? PackageFullName, string Status, string? Stage = null);
    internal sealed record PhysicalFile(string FinalPath, byte[] Bytes);
    internal sealed record Host(IProfileCatalogTransport Catalog, IProfileCatalogLeaseTransport DataLeases,
        IProfileInstallationLeaseTransport InstallationLeases, Func<string?> PackageIdentity,
        string BuildIdentity, TimeSpan VerificationTimeout, string? StandaloneOutputRoot,
        IReadOnlyList<string> PhysicalAppDataDirectories, Func<string, PhysicalFile> PhysicalFileReader);

    public static bool TryRun(string[] arguments, out int exitCode)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        exitCode = 0;
        if (arguments.Length == 0 || arguments[0] != Argument) return false;
        Options? options = null;
        try
        {
            options = ParseArguments(arguments);
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("Profiles package qualification requires Windows.");
            var transport = new NativeProfileCatalogTransport();
            var build = typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            var identity = LauncherReleaseIdentityParser.Parse(build);
            if (identity.SourceCommit is null || !ValidDigest(identity.ProfilesNativeSha256))
                throw new InvalidOperationException("Profiles qualification requires a source-bound, byte-paired Bridge build.");
            RunAsync(options, new(transport, transport, transport,
                () => WindowsPackageIdentity.CurrentPackageFullName, build!, TimeSpan.FromSeconds(15),
                Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory)), GetPhysicalAppDataDirectories(), ReadPhysicalFile))
                .GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            exitCode = 1;
            var stage = exception is QualificationException failure ? failure.Stage : "arguments-or-build";
            Console.Error.WriteLine($"Profiles package qualification failed at {stage}: {exception.GetBaseException().Message}");
            if (options is not null) TryWriteFailure(options, stage);
        }
        return true;
    }

    internal static Options ParseArguments(string[] arguments)
    {
        if (arguments.Length != 4 || arguments[0] != Argument
            || arguments[1] is not ("prepare" or "msix" or "verify" or "cleanup")
            || !LauncherProfiles.ValidId(arguments[2]))
            throw new ArgumentException("Expected prepare|msix|verify|cleanup, a lowercase 32-hex nonce and a fresh absolute fixture path.");
        var path = arguments[3];
        if (!Path.IsPathFullyQualified(path) || path.Contains('\0'))
            throw new ArgumentException("The fixture path must be fully qualified.");
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!string.Equals(path, full, StringComparison.OrdinalIgnoreCase)
            || Path.GetFileName(full) != $"stfc-mod-bridge-profiles-qualification-{arguments[2]}")
            throw new ArgumentException("The fixture path must be canonical and named for the exact nonce.");

        return new(arguments[1], arguments[2], full);
    }

    internal static async Task RunAsync(Options options, Host host)
    {
        var stage = "package-identity";
        try
        {
            var package = host.PackageIdentity();
            if (options.Mode == "msix") RequirePackageIdentity(package);
            else if (package is not null) throw new InvalidOperationException("This stage requires an unpackaged process.");
            if (host.VerificationTimeout < TimeSpan.FromSeconds(10) || host.VerificationTimeout > TimeSpan.FromSeconds(20))
                throw new ArgumentOutOfRangeException(nameof(host), "Verification must be bounded to 10–20 seconds.");
            stage = "fixture-boundary";
            RequireExternalFixture(options, host);
            RequirePlainAncestors(Path.GetDirectoryName(options.Fixture)!);
            if (options.Mode == "prepare")
            {
                if (Directory.Exists(options.Fixture) || File.Exists(options.Fixture))
                    throw new IOException("Prepare refuses an existing fixture.");
                Directory.CreateDirectory(options.Fixture);
                var game = GameDirectory(options);
                Directory.CreateDirectory(game);
                WriteNew(Path.Combine(game, "prime.exe"), FakeExecutable);
                WriteNew(Path.Combine(game, ".version"), FakeVersion);
                stage = "catalog-location";
                var root = DefaultLocation(host);
                stage = "create-synthetic-profile";
                var profile = Success(host, new("create", Name: PreparedName(options), GameDirectory: game)).Profile
                    ?? throw new InvalidDataException("Create omitted its profile.");
                var receipt = new Receipt(Schema, options.Nonce, options.Fixture, root,
                    profile.Id, PreparedName(options), profile.Revision, "active", host.BuildIdentity, host.StandaloneOutputRoot!);
                // Retain ownership evidence even if subsequent path checks fail.
                WriteNew(Path.Combine(options.Fixture, ReceiptFile), JsonSerializer.SerializeToUtf8Bytes(receipt, JsonOptions));
                ValidateReceipt(options, host, receipt);
                ValidateProfile(host, options, receipt, profile);
                RequireSyntheticContents(host, profile, options, edited: false);
                return;
            }
            stage = "receipt";
            RequirePlainAncestors(options.Fixture);
            var current = Read<Receipt>(Path.Combine(options.Fixture, ReceiptFile));
            ValidateReceipt(options, host, current);
            stage = "catalog-location";
            if (!SamePath(DefaultLocation(host), current.CatalogRoot))
                throw new InvalidDataException("Packaged and standalone default catalogs differ.");
            RequireFakeGame(options);
            var selected = Success(host, new("paths", Id: current.Id, Archived: current.State == "archived")).Profile
                ?? throw new InvalidDataException("Paths omitted its profile.");
            ValidateProfile(host, options, current, selected);
            if (options.Mode == "msix")
            {
                stage = "edit-synthetic-profile";
                if (current.State != "active" || current.Name != PreparedName(options))
                    throw new InvalidDataException("MSIX requires the untouched prepared profile.");
                RequireSyntheticContents(host, selected, options, edited: false);
                selected = Success(host, new("edit", Id: current.Id, Name: EditedName(options),
                    GameDirectory: GameDirectory(options), ExpectedRevision: current.Revision)).Profile
                    ?? throw new InvalidDataException("Edit omitted its profile.");
                current = current with { Name = EditedName(options), Revision = selected.Revision };
                WriteReceipt(options, current);
                ValidateProfile(host, options, current, selected);
                stage = "write-synthetic-config";
                WriteNew(selected.ConfigPath, ConfigBytes(options));
                stage = "native-leases";
                using (var data = host.DataLeases.AcquireDataLease(new("paths", Id: current.Id)))
                using (host.InstallationLeases.AcquireInstallationLease(new("installation-status", GameDirectory: GameDirectory(options))))
                {
                    ValidateProfile(host, options, current, data.Profile);
                    RequireSyntheticContents(host, data.Profile, options, edited: true);
                    var evidence = MakeEvidence(current, package, "ready");
                    WriteNew(Path.Combine(options.Fixture, ReadyFile), JsonSerializer.SerializeToUtf8Bytes(evidence, JsonOptions));
                    stage = "standalone-verification";
                    var timer = Stopwatch.StartNew();
                    while (!File.Exists(Path.Combine(options.Fixture, VerifiedFile)))
                    {
                        if (timer.Elapsed >= host.VerificationTimeout)
                            throw new TimeoutException("Standalone verification did not arrive while both native leases were held.");
                        await Task.Delay(50).ConfigureAwait(false);
                    }
                    ValidateEvidence(current, Read<Evidence>(Path.Combine(options.Fixture, VerifiedFile)), "verified", package);
                }
                stage = "lease-release";
                WriteNew(Path.Combine(options.Fixture, ReleasedFile),
                    JsonSerializer.SerializeToUtf8Bytes(MakeEvidence(current, package, "passed"), JsonOptions));
                return;
            }
            if (options.Mode == "verify")
            {
                stage = "shared-metadata-and-config";
                if (current.State != "active" || current.Name != EditedName(options))
                    throw new InvalidDataException("The standalone host did not observe the packaged metadata edit.");
                var ready = Read<Evidence>(Path.Combine(options.Fixture, ReadyFile));
                RequirePackageIdentity(ready.PackageFullName);
                ValidateEvidence(current, ready, "ready", ready.PackageFullName);
                RequireSyntheticContents(host, selected, options, edited: true);
                stage = "profile-data-lease-exclusion";
                RequireBusy(host.Catalog.Request(new("archive", Id: current.Id, ExpectedRevision: current.Revision)));
                stage = "installation-reader-lease-exclusion";
                // An invalid lease can only reach missing GameAssembly, never the official network plan.
                RequireBusy(host.Catalog.Request(new("update-game", GameDirectory: GameDirectory(options), ExpectedVersion: 267)));
                ValidateProfile(host, options, current, Success(host, new("paths", Id: current.Id)).Profile
                    ?? throw new InvalidDataException("The synthetic profile disappeared during lease verification."));
                RequireFakeGame(options);
                WriteNew(Path.Combine(options.Fixture, VerifiedFile),
                    JsonSerializer.SerializeToUtf8Bytes(MakeEvidence(current, ready.PackageFullName, "verified"), JsonOptions));
                return;
            }
            stage = "cleanup-ownership";
            // Permanent deletion never sees user prefs, extra files or a stale metadata revision.
            RequireSyntheticContents(host, selected, options, current.Name == EditedName(options));
            if (current.State == "active")
            {
                stage = "cleanup-archive";
                selected = Success(host, new("archive", Id: current.Id, ExpectedRevision: current.Revision)).Profile
                    ?? throw new InvalidDataException("Archive omitted its profile.");
                current = current with { State = "archived", Revision = selected.Revision };
                WriteReceipt(options, current);
                ValidateProfile(host, options, current, selected);
                RequireSyntheticContents(host, selected, options, current.Name == EditedName(options));
            }
            stage = "cleanup-delete";
            Success(host, new("delete", Id: current.Id, ExpectedRevision: current.Revision, Archived: true, Permanent: true));
            WriteNew(Path.Combine(options.Fixture, "cleanup-passed.json"),
                JsonSerializer.SerializeToUtf8Bytes(MakeEvidence(current, null, "deleted"), JsonOptions));
        }
        catch (Exception exception) when (exception is not QualificationException)
        {
            throw new QualificationException(stage, exception);
        }
    }

    internal static void RequireExternalFixture(Options options, Host host)
    {
        if (options.Mode != "msix"
            && (host.StandaloneOutputRoot is null || !Path.IsPathFullyQualified(host.StandaloneOutputRoot)
                || !SamePath(Path.GetDirectoryName(options.Fixture)!, host.StandaloneOutputRoot)))
            throw new InvalidDataException("The fixture must be a direct child of the canonical standalone output root.");
        if (host.PhysicalAppDataDirectories.Count < 3)
            throw new InvalidDataException("Physical OS-user AppData facts are unavailable.");
        foreach (var root in host.PhysicalAppDataDirectories)
        {
            if (!Path.IsPathFullyQualified(root)) throw new InvalidDataException("An OS-user AppData fact is invalid.");
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            if (SamePath(options.Fixture, full) || options.Fixture.StartsWith(
                full + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The fixture must remain outside physical OS-user AppData to prevent package write redirection.");
        }
    }

    private static List<string> GetPhysicalAppDataDirectories()
    {
        // SDK KnownFolders.h / ShlObj_core.h: resolve physical user folders without package redirection.
        Guid[] ids = [new("F1B32785-6FBA-4FCF-9D55-7B8E7F157091"),
            new("3EB685DB-65F9-4CF6-A03A-E3EF65729F3D"), new("A520A1A4-1780-4FF6-BD18-167343C5AF16")];
        var roots = new List<string>();
        foreach (var id in ids)
        {
            var result = SHGetKnownFolderPath(in id, 0x00010000, IntPtr.Zero, out var value);
            try
            {
                Marshal.ThrowExceptionForHR(result);
                var path = Marshal.PtrToStringUni(value) ?? throw new InvalidDataException("Windows returned no physical AppData path.");
                roots.Add(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)));
            }
            finally { Marshal.FreeCoTaskMem(value); }
        }
        var parent = Path.GetDirectoryName(roots[0]);
        if (parent is not null && roots.All(root => SamePath(Path.GetDirectoryName(root)!, parent))) roots.Add(parent);
        return roots;
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int SHGetKnownFolderPath(in Guid folderId, uint flags, IntPtr token, out IntPtr path);
    private static PhysicalFile ReadPhysicalFile(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var finalPath = new char[32768];
        var length = GetFinalPathNameByHandleW(file.SafeFileHandle, finalPath, checked((uint)finalPath.Length), 0);
        if (length == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot resolve the qualification file's physical path.");
        if (length >= finalPath.Length) throw new InvalidDataException("The qualification file's final path is oversized.");
        var resolved = new string(finalPath, 0, checked((int)length));
        if (resolved.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) resolved = @"\\" + resolved[8..];
        else if (resolved.StartsWith(@"\\?\", StringComparison.Ordinal)) resolved = resolved[4..];
        if (file.Length > 65536) throw new InvalidDataException("Synthetic profile file is oversized.");
        var bytes = new byte[checked((int)file.Length)];
        file.ReadExactly(bytes);
        return new(resolved, bytes);
    }

    private static byte[] RequirePhysicalFile(Host host, string expectedPath)
    {
        var observed = host.PhysicalFileReader(expectedPath);
        if (!Path.IsPathFullyQualified(observed.FinalPath) || !SamePath(observed.FinalPath, expectedPath))
            throw new InvalidDataException("The actual metadata/config handle resolves outside the receipt-bound physical catalog path.");
        return observed.Bytes;
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file,
        [Out, MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.U2, SizeParamIndex = 2)] char[] path,
        uint pathLength, uint flags);
    private static string PreparedName(Options options) => $"Bridge proof {options.Nonce}";
    private static string EditedName(Options options) => $"Bridge MSIX {options.Nonce}";
    private static string GameDirectory(Options options) => Path.Combine(options.Fixture, "game");
    private static byte[] ConfigBytes(Options options) => Encoding.UTF8.GetBytes(
        $"# Synthetic Bridge package qualification; contains no player credentials.\n[qualification]\nnonce = \"{options.Nonce}\"\nwritten_by = \"msix\"\n");
    private static bool SamePath(string left, string right) => string.Equals(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
        StringComparison.OrdinalIgnoreCase);
    private static bool ValidDigest(string? value) => value is { Length: 64 }
        && value.Any(ch => ch != '0') && value.All(ch => ch is >= 'a' and <= 'f' or >= '0' and <= '9');
    private static string DefaultLocation(Host host)
    {
        var root = Success(host, new("catalog-location")).CatalogRoot;
        if (root is null || !Path.IsPathFullyQualified(root)) throw new InvalidDataException("Native catalog location is invalid.");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
    }
    private static void RequirePackageIdentity(string? package)
    {
        var fields = package?.Split('_');
        if (fields is not { Length: 5 } || fields[0] != BattleNamedPipePackageQualification.PackageIdentityName
            || !Version.TryParse(fields[1], out _) || fields[2] != "x64" || fields[3] != "" || fields[4].Length != 13)
            throw new InvalidOperationException("MSIX qualification lacks the reviewed x64 package identity.");
    }
    private static void ValidateReceipt(Options options, Host host, Receipt receipt)
    {
        if (receipt.Schema != Schema || receipt.Nonce != options.Nonce || !SamePath(receipt.Fixture, options.Fixture)
            || !LauncherProfiles.ValidId(receipt.Id) || !ValidDigest(receipt.Revision)
            || receipt.Name != PreparedName(options) && receipt.Name != EditedName(options)
            || receipt.State is not ("active" or "archived") || receipt.BuildIdentity != host.BuildIdentity
            || !Path.IsPathFullyQualified(receipt.CatalogRoot)
            || !Path.IsPathFullyQualified(receipt.OutputRoot)
            || !SamePath(receipt.OutputRoot, Path.GetDirectoryName(options.Fixture)!))
            throw new InvalidDataException("The fixture receipt is not bound to this nonce, profile and candidate build.");
    }
    private static void ValidateProfile(Host host, Options options, Receipt receipt, LauncherProfile profile)
    {
        var directory = Path.Combine(receipt.CatalogRoot, receipt.State == "archived" ? "archives" : "profiles", receipt.Id);
        if (profile.Id != receipt.Id || profile.Name != receipt.Name || profile.Revision != receipt.Revision
            || profile.State != receipt.State || profile.PreferencesInitialized
            || !SamePath(profile.GameDirectory, GameDirectory(options)) || !SamePath(profile.Directory, directory)
            || !SamePath(profile.ConfigPath, Path.Combine(directory, "config.toml")))
            throw new InvalidDataException("The receipt-owned synthetic profile changed or has unexpected paths.");
        RequirePlainAncestors(directory);
        _ = RequirePhysicalFile(host, Path.Combine(directory, "metadata.json"));
    }
    private static void RequireSyntheticContents(Host host, LauncherProfile profile, Options options, bool edited)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(profile.Directory))
        {
            RequirePlainAncestors(entry);
            var name = Path.GetFileName(entry);
            if (name == "metadata.json" && File.Exists(entry)) continue;
            if (name == "logs" && Directory.Exists(entry) && !Directory.EnumerateFileSystemEntries(entry).Any()) continue;
            if (name == "config.toml" && edited && File.Exists(entry)) continue;
            throw new InvalidDataException("Synthetic profile has external data; retain it for inspection.");
        }
        if (edited && !RequirePhysicalFile(host, profile.ConfigPath).AsSpan().SequenceEqual(ConfigBytes(options)))
            throw new InvalidDataException("Synthetic config was changed externally; retain it for inspection.");
        if (!edited && File.Exists(profile.ConfigPath)) throw new InvalidDataException("Prepared config unexpectedly exists.");
    }
    private static void RequireFakeGame(Options options)
    {
        var game = GameDirectory(options);
        RequirePlainAncestors(game);
        var entries = Directory.EnumerateFileSystemEntries(game).Select(Path.GetFileName).Order().ToArray();
        if (!entries.SequenceEqual(FakeGameFiles)
            || !ReadBytes(Path.Combine(game, "prime.exe")).AsSpan().SequenceEqual(FakeExecutable)
            || !ReadBytes(Path.Combine(game, ".version")).AsSpan().SequenceEqual(FakeVersion))
            throw new InvalidDataException("The fake game fixture has external bytes or unexpected files.");
    }
    private static byte[] ReadBytes(string path) { RequirePlainAncestors(path); return File.ReadAllBytes(path); }
    private static T Read<T>(string path)
    {
        var bytes = ReadBytes(path);
        if (bytes.Length > 16384) throw new InvalidDataException("Fixture evidence is oversized.");
        return JsonSerializer.Deserialize<T>(bytes, JsonOptions) ?? throw new InvalidDataException("Fixture evidence is empty.");
    }
    private static void RequirePlainAncestors(string path)
    {
        for (var cursor = Path.GetFullPath(path); cursor is not null; cursor = Path.GetDirectoryName(cursor))
            if ((File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Qualification refuses reparse points.");
    }
    private static ProfileCatalogResponse Success(Host host, ProfileCatalogRequest request)
    {
        var response = host.Catalog.Request(request);
        if (!response.Ok) throw new InvalidOperationException($"Native {request.Operation}: {response.Error?.Code}: {response.Error?.Message}");
        return response;
    }
    private static void RequireBusy(ProfileCatalogResponse response)
    {
        if (response.Ok || response.Error?.Code != "busy")
            throw new InvalidDataException($"Expected native busy exclusion; got {response.Error?.Code ?? "success"}.");
    }
    private static Evidence MakeEvidence(Receipt receipt, string? package, string status) =>
        new(Schema, receipt.Nonce, receipt.Id, receipt.Revision, receipt.BuildIdentity, package, status);
    private static void ValidateEvidence(Receipt receipt, Evidence evidence, string status, string? package)
    {
        if (evidence != MakeEvidence(receipt, package, status)) throw new InvalidDataException("The cross-host marker is stale or unbound.");
    }
    private static void WriteNew(string path, byte[] bytes)
    {
        RequirePlainAncestors(Path.GetDirectoryName(path)!);
        var temporary = Path.Combine(Path.GetDirectoryName(path)!, $".{Path.GetFileName(path)}-{Guid.NewGuid():N}.tmp");
        using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            output.Write(bytes);
            output.Flush(flushToDisk: true);
        }
        // Publish only complete bytes so the other host never observes a partially written marker.
        File.Move(temporary, path, overwrite: false);
    }
    private static void WriteReceipt(Options options, Receipt receipt)
    {
        var path = Path.Combine(options.Fixture, ReceiptFile);
        RequirePlainAncestors(path);
        var temporary = Path.Combine(options.Fixture, $"receipt-{Guid.NewGuid():N}.tmp");
        WriteNew(temporary, JsonSerializer.SerializeToUtf8Bytes(receipt, JsonOptions));
        File.Move(temporary, path, overwrite: true);
    }
    private static void TryWriteFailure(Options options, string stage)
    {
        try
        {
            if (!Directory.Exists(options.Fixture)) return;
            WriteNew(Path.Combine(options.Fixture, $"failed-{options.Mode}-{Guid.NewGuid():N}.json"),
                JsonSerializer.SerializeToUtf8Bytes(new { schema = Schema, nonce = options.Nonce, status = "failed", stage }, JsonOptions));
        }
        catch (Exception) { /* Missing evidence remains gate failure; never delete the retained receipt. */ }
    }
    internal sealed class QualificationException(string stage, Exception innerException)
        : Exception($"Profiles package qualification failed at {stage}.", innerException)
    {
        public string Stage { get; } = stage;
    }
}
