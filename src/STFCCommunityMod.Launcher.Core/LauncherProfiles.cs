using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace STFCCommunityMod.Launcher.Core;

public sealed record LauncherProfile(
    string Id, string Name, string GameDirectory,
    string Directory = "", string ConfigPath = "", string LogPath = "",
    string Revision = "", string State = "active", bool PreferencesInitialized = false,
    string Kind = "isolated", string OwnerUserId = "", bool BuiltIn = false,
    string PreferredInstallationId = "", string InstallationState = "")
{
    public bool IsDefault => Kind == "windows-user" && BuiltIn;
}

public sealed record ProfileCatalogIssue(string? Id, string Path, string Code, string Message);
public sealed record ProfileSession(string Id, int ProcessId, string Readiness, string GameDirectory,
    long ProcessStartUtcTicks = 0, string ExecutablePath = "");
public sealed record RegisteredGameInstallation(string Id, string Name, string GameDirectory,
    string PhysicalIdentity, string State, string Revision);

public sealed record LauncherProfilesSnapshot(
    string? SelectedProfileId, IReadOnlyList<LauncherProfile> Profiles,
    IReadOnlyList<ProfileCatalogIssue>? Issues = null)
{
    public static LauncherProfilesSnapshot Empty { get; } = new(null, []);
    public LauncherProfile? SelectedProfile => Profiles.FirstOrDefault(profile => profile.Id == SelectedProfileId
        && profile.State == "active");
}

public enum LauncherProfilesLoadState { Missing, Loaded, Invalid }
public sealed record LauncherProfilesLoadResult(
    LauncherProfilesLoadState State, LauncherProfilesSnapshot? Snapshot,
    string? Error, string? Revision = null);

public static class LauncherProfiles
{
    public static bool ValidId(string? id) => id is { Length: 32 }
        && id.All(ch => ch is >= 'a' and <= 'f' or >= '0' and <= '9');
}

public sealed record ProfileCatalogRequest(
    string Operation, string? Root = null, string? Id = null, string? Name = null,
    string? GameDirectory = null, string? ExpectedRevision = null,
    bool Archived = false, int ApiVersion = 2, int? ExpectedVersion = null, bool Permanent = false,
    string? SourceUserSid = null, string? ExpectedDestinationSid = null, bool AllowElevation = false,
    string? InstallationId = null, string? PreferredInstallationId = null,
    string? ExpectedInstallationRevision = null);
public sealed record ProfileImportUser(string Sid, string Name, bool CurrentUser = false);
public sealed record ProfileUserImportPlan(
    string SourceUserSid, string SourceUserName, string DestinationUserSid, string DestinationUserName,
    string Name, string GameDirectory, bool RequiresElevation, string Reason,
    string PreferredInstallationId = "", string InstallationRevision = "");
public sealed record ProfileImportSources(IReadOnlyList<ProfileImportUser> Users, ProfileImportUser DestinationUser,
    bool RequiresElevation = false, int UnavailableUsers = 0);

public sealed record ProfileCatalogError(string Code, string Message);
public sealed record ProfileCatalogResponse(
    bool Ok, ProfileCatalogError? Error = null,
    IReadOnlyList<LauncherProfile>? Profiles = null,
    IReadOnlyList<ProfileCatalogIssue>? Issues = null,
    LauncherProfile? Profile = null,
    IReadOnlyList<ProfileSession>? Sessions = null,
    string? Revision = null, int? ProcessId = null, string? Readiness = null,
    string? Message = null, GameInstallationSnapshot? Installation = null,
    string? CatalogRoot = null, IReadOnlyList<ProfileImportUser>? Users = null,
    ProfileImportUser? DestinationUser = null, ProfileUserImportPlan? ImportPlan = null,
    bool? RequiresElevation = null, int? UnavailableUsers = null,
    IReadOnlyList<RegisteredGameInstallation>? Installations = null,
    RegisteredGameInstallation? RegisteredInstallation = null);

public interface IProfileCatalogTransport
{
    ProfileCatalogResponse Request(ProfileCatalogRequest request);
}

public interface IProfileInstallationLeaseTransport
{
    IDisposable AcquireInstallationLease(ProfileCatalogRequest request);
}

public interface IProfileCatalogLeaseTransport
{
    ProfileCatalogLease AcquireDataLease(ProfileCatalogRequest request);
}

public sealed class ProfileCatalogLease(LauncherProfile profile, IDisposable resource) : IDisposable
{
    public LauncherProfile Profile { get; } = profile;
    public void Dispose() => resource.Dispose();
}

/// <summary>Calls the packaged, byte-paired native catalog; all account operations live there.</summary>
public sealed class NativeProfileCatalogTransport : IProfileCatalogTransport, IProfileCatalogLeaseTransport, IProfileInstallationLeaseTransport
{
    public const string LibraryName = "stfc-profiles-native.dll";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
    private readonly string libraryPath;
    private readonly string expectedSha256;

    public NativeProfileCatalogTransport(string? libraryPath = null, string? expectedSha256 = null)
    {
        this.libraryPath = Path.GetFullPath(libraryPath ?? Path.Combine(AppContext.BaseDirectory, LibraryName));
        this.expectedSha256 = expectedSha256 ?? typeof(NativeProfileCatalogTransport).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .SingleOrDefault(attribute => attribute.Key == "ProfilesNativeSha256")?.Value ?? string.Empty;
    }

    public ProfileCatalogResponse Request(ProfileCatalogRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var module = OpenVerifiedModule();
        return module.Request(request);
    }

    public IDisposable AcquireInstallationLease(ProfileCatalogRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.GameDirectory);
        var module = OpenVerifiedModule();
        IntPtr lease = IntPtr.Zero;
        try
        {
            var acquire = Marshal.GetDelegateForFunctionPointer<DataLeaseAcquire>(
                NativeLibrary.GetExport(module.Handle, "stfc_profiles_acquire_installation_lease_v1"));
            var release = Marshal.GetDelegateForFunctionPointer<DataLeaseRelease>(
                NativeLibrary.GetExport(module.Handle, "stfc_profiles_release_installation_lease_v1"));
            var status = acquire(request.Root, request.GameDirectory, out lease, out var error);
            try
            {
                if (status != 0 || lease == IntPtr.Zero)
                    throw new InvalidOperationException(error == IntPtr.Zero
                        ? "The selected game installation is unavailable for launch."
                        : Marshal.PtrToStringUTF8(error));
            }
            finally { if (error != IntPtr.Zero) module.Free(error); }
            var held = lease;
            lease = IntPtr.Zero;
            return new HeldDataLease(module, held, release);
        }
        catch
        {
            if (lease != IntPtr.Zero)
                Marshal.GetDelegateForFunctionPointer<DataLeaseRelease>(
                    NativeLibrary.GetExport(module.Handle, "stfc_profiles_release_installation_lease_v1"))(lease);
            module.Dispose();
            throw;
        }
    }

    public ProfileCatalogLease AcquireDataLease(ProfileCatalogRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Id);
        var module = OpenVerifiedModule();
        IntPtr lease = IntPtr.Zero;
        try
        {
            var acquire = Marshal.GetDelegateForFunctionPointer<DataLeaseAcquire>(
                NativeLibrary.GetExport(module.Handle, "stfc_profiles_acquire_data_lease_v1"));
            var release = Marshal.GetDelegateForFunctionPointer<DataLeaseRelease>(
                NativeLibrary.GetExport(module.Handle, "stfc_profiles_release_data_lease_v1"));
            var status = acquire(request.Root, request.Id, out lease, out var error);
            try
            {
                if (status != 0 || lease == IntPtr.Zero)
                    throw new InvalidOperationException(error == IntPtr.Zero
                        ? "The shared profile data lease is unavailable."
                        : Marshal.PtrToStringUTF8(error));
            }
            finally { if (error != IntPtr.Zero) module.Free(error); }
            var response = module.Request(request with { Operation = "paths" });
            if (!response.Ok || response.Profile is not { State: "active" } profile)
                throw new InvalidOperationException(response.Error?.Message ?? "The profile is no longer active.");
            var held = lease;
            lease = IntPtr.Zero;
            return new(profile, new HeldDataLease(module, held, release));
        }
        catch
        {
            if (lease != IntPtr.Zero)
                Marshal.GetDelegateForFunctionPointer<DataLeaseRelease>(NativeLibrary.GetExport(
                    module.Handle, "stfc_profiles_release_data_lease_v1"))(lease);
            module.Dispose();
            throw;
        }
    }

    private VerifiedModule OpenVerifiedModule()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Mod Bridge requires the Windows shared profile catalog.");
        if (expectedSha256.Length != 64 || expectedSha256.All(ch => ch == '0')
            || !expectedSha256.All(ch => ch is >= 'a' and <= 'f' or >= '0' and <= '9'))
            throw new InvalidOperationException("This Bridge build has no qualified shared profile component. Rebuild or repair Bridge.");
        var file = new FileStream(libraryPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            var actualSha256 = Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant();
            if (actualSha256 != expectedSha256)
                throw new InvalidDataException("The shared profile component differs from the exact component paired with this Bridge build. Repair Bridge.");
            var handle = NativeLibrary.Load(libraryPath, typeof(NativeProfileCatalogTransport).Assembly,
                DllImportSearchPath.UseDllDirectoryForDependencies | DllImportSearchPath.System32);
            return new(handle, file);
        }
        catch { file.Dispose(); throw; }
    }

    private sealed class VerifiedModule(IntPtr handle, FileStream verifiedFile) : IDisposable
    {
        public IntPtr Handle { get; } = handle;
        public void Free(IntPtr value) => Marshal.GetDelegateForFunctionPointer<CatalogFree>(
            NativeLibrary.GetExport(Handle, "stfc_profiles_free_v1"))(value);
        public ProfileCatalogResponse Request(ProfileCatalogRequest request)
        {
            var invoke = Marshal.GetDelegateForFunctionPointer<CatalogRequest>(
                NativeLibrary.GetExport(Handle, "stfc_profiles_catalog_request_v1"));
            var status = invoke(JsonSerializer.Serialize(request, JsonOptions), out var response);
            try
            {
                if (status != 0 || response == IntPtr.Zero)
                    throw new InvalidOperationException($"The shared profile API failed (transport status {status}).");
                var json = Marshal.PtrToStringUTF8(response)
                    ?? throw new InvalidDataException("The shared profile API returned no JSON response.");
                var result = JsonSerializer.Deserialize<ProfileCatalogResponse>(json, JsonOptions)
                    ?? throw new InvalidDataException("The shared profile API returned an incomplete response.");
                if (request.Operation is "register-installation" or "installation-paths" && result.Ok)
                {
                    using var document = JsonDocument.Parse(json);
                    var installation = document.RootElement.GetProperty("installation").Deserialize<RegisteredGameInstallation>(JsonOptions);
                    return result with { Installation = null, RegisteredInstallation = installation };
                }
                return result;
            }
            finally { if (response != IntPtr.Zero) Free(response); }
        }
        public void Dispose() { NativeLibrary.Free(Handle); verifiedFile.Dispose(); }
    }

    private sealed class HeldDataLease(VerifiedModule module, IntPtr lease, DataLeaseRelease release) : IDisposable
    {
        private IntPtr heldLease = lease;
        public void Dispose()
        {
            var held = Interlocked.Exchange(ref heldLease, IntPtr.Zero);
            if (held == IntPtr.Zero) return;
            try { release(held); }
            finally { module.Dispose(); }
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int CatalogRequest([MarshalAs(UnmanagedType.LPUTF8Str)] string request, out IntPtr response);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void CatalogFree(IntPtr response);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int DataLeaseAcquire([MarshalAs(UnmanagedType.LPUTF8Str)] string? root,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string id, out IntPtr lease, out IntPtr error);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void DataLeaseRelease(IntPtr lease);
}

/// <summary>Consumes the shared directory catalog; only the last UI selection is stored by Bridge.</summary>
public sealed class NativeLauncherProfilesStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IProfileCatalogTransport transport;
    private readonly string? root;
    private readonly string selectionPath;
    private readonly LauncherOperationLock operationLock;

    public NativeLauncherProfilesStore(string stateDirectory, IProfileCatalogTransport? transport = null, string? root = null)
    {
        this.transport = transport ?? new NativeProfileCatalogTransport();
        this.root = root is null ? null : Path.GetFullPath(root);
        selectionPath = Path.Combine(Path.GetFullPath(stateDirectory), "profile-ui-selection.json");
        operationLock = new(stateDirectory);
    }

    public LauncherProfilesLoadResult Load(bool archived = false, bool allowSelectionRepair = false)
    {
        string? selectedId = null;
        var selectionRead = false;
        string? selectionError = null;
        try
        {
            try { selectedId = ReadSelectedId(); }
            catch (Exception exception) when (allowSelectionRepair && IsCatalogException(exception))
            { selectionError = $"Bridge's saved selection needs repair: {exception.Message}"; }
            selectionRead = true;
            if (!archived)
            {
                var builtIn = EnsureDefault();
                selectedId ??= builtIn.Id;
            }
            var response = RequireSuccess(new("list", Root: root, Archived: archived));
            var profiles = response.Profiles ?? throw new InvalidDataException("The shared catalog omitted its profile list.");
            if (profiles.Any(profile => !LauncherProfiles.ValidId(profile.Id)
                || profile.Kind is not ("isolated" or "windows-user")
                || profile.Kind == "windows-user" && !profile.IsDefault))
                throw new InvalidDataException("The shared catalog returned an unsupported profile storage kind.");
            var snapshot = new LauncherProfilesSnapshot(selectedId, profiles, response.Issues ?? []);
            if (!archived && selectedId is not null && snapshot.SelectedProfile is null)
                return new(LauncherProfilesLoadState.Loaded, snapshot,
                    "The selected profile is unavailable or archived. Restore it or explicitly choose Default.", response.Revision);
            return new(LauncherProfilesLoadState.Loaded, snapshot, selectionError, response.Revision);
        }
        catch (Exception exception) when (IsCatalogException(exception))
        {
            return new(LauncherProfilesLoadState.Invalid,
                selectionRead ? new LauncherProfilesSnapshot(selectedId, []) : null, exception.Message);
        }
    }

    public string? LoadSelectedId()
        => ReadSelectedId() ?? ResolveDefault().Id;

    private string? ReadSelectedId()
    {
        if (!File.Exists(selectionPath)) return null;
        var selection = JsonSerializer.Deserialize<Selection>(File.ReadAllBytes(selectionPath), JsonOptions);
        if (selection is null || selection.SchemaVersion != 1
            || (selection.SelectedProfileId is not null && !LauncherProfiles.ValidId(selection.SelectedProfileId)))
            throw new InvalidDataException("Bridge's launch selection is invalid. Explicitly choose Default or a profile.");
        return selection.SelectedProfileId;
    }

    public LauncherProfile EnsureDefault() => RequireDefault("ensure-default");
    public LauncherProfile ResolveDefault() => RequireDefault("resolve-default");

    private LauncherProfile RequireDefault(string operation)
    {
        var profile = RequireSuccess(new(operation, Root: root)).Profile;
        if (profile is null || !LauncherProfiles.ValidId(profile.Id) || !profile.IsDefault
            || profile.State != "active" || string.IsNullOrWhiteSpace(profile.OwnerUserId))
            throw new InvalidDataException("The shared Profiles component omitted the current Windows setup identity.");
        return profile;
    }

    public Task<LauncherProfile> CreateNewAsync(string name, string gameDirectory,
        CancellationToken cancellationToken = default) => CreateNewAsync(name, gameDirectory, "", cancellationToken);

    public Task<LauncherProfile> CreateNewAsync(string name, string gameDirectory,
        string preferredInstallationId, CancellationToken cancellationToken = default) => MutateAsync(new("create", Root: root,
            Name: name, GameDirectory: OptionalGameDirectory(gameDirectory), PreferredInstallationId: preferredInstallationId), cancellationToken);

    public Task<ProfileImportSources> ImportSourcesAsync(bool allowElevation = false, string? expectedDestinationSid = null,
        CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            if (allowElevation) ArgumentException.ThrowIfNullOrWhiteSpace(expectedDestinationSid);
            var response = RequireSuccess(new("import-sources", Root: root,
                AllowElevation: allowElevation, ExpectedDestinationSid: expectedDestinationSid));
            if (response.Users is null || response.DestinationUser is not { Sid.Length: > 0, Name.Length: > 0 } destination)
                throw new InvalidDataException("The shared Profiles component omitted the Windows import sources.");
            if (response.RequiresElevation is not bool requiresElevation || response.UnavailableUsers is not int unavailable
                || unavailable is < 0 or > 10000 || (allowElevation && destination.Sid != expectedDestinationSid))
                throw new InvalidDataException("The shared Profiles component omitted or changed the user discovery details.");
            return new ProfileImportSources(response.Users, destination, requiresElevation, unavailable);
        }, cancellationToken);

    public Task<ProfileUserImportPlan> PrepareUserImportAsync(string sourceUserSid, string name, string gameDirectory,
        CancellationToken cancellationToken = default) => PrepareUserImportAsync(sourceUserSid, name, gameDirectory, "", cancellationToken);

    public Task<ProfileUserImportPlan> PrepareUserImportAsync(string sourceUserSid, string name, string gameDirectory,
        string preferredInstallationId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceUserSid);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var game = OptionalGameDirectory(gameDirectory);
        return Task.Run(() =>
        {
            var plan = RequireSuccess(new("prepare-user-import", Root: root, SourceUserSid: sourceUserSid,
                Name: name, GameDirectory: game, PreferredInstallationId: preferredInstallationId)).ImportPlan
                ?? throw new InvalidDataException("The shared Profiles component omitted the import explanation.");
            if (plan.SourceUserSid != sourceUserSid || string.IsNullOrWhiteSpace(plan.SourceUserName)
                || string.IsNullOrWhiteSpace(plan.DestinationUserSid) || string.IsNullOrWhiteSpace(plan.DestinationUserName)
                || string.IsNullOrWhiteSpace(plan.Name) || plan.PreferredInstallationId != preferredInstallationId
                || preferredInstallationId.Length > 0 && string.IsNullOrWhiteSpace(plan.InstallationRevision)
                || (plan.RequiresElevation && string.IsNullOrWhiteSpace(plan.Reason)))
                throw new InvalidDataException("The shared Profiles component returned an incomplete import explanation.");
            return plan;
        }, cancellationToken);
    }

    // The caller presents this plan and confirms it before permitting the native Windows prompt.
    public Task<LauncherProfile> ImportUserAsync(ProfileUserImportPlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return MutateAsync(new("import-user", Root: root, SourceUserSid: plan.SourceUserSid,
            Name: plan.Name, GameDirectory: plan.GameDirectory, ExpectedDestinationSid: plan.DestinationUserSid,
            AllowElevation: plan.RequiresElevation, PreferredInstallationId: plan.PreferredInstallationId,
            ExpectedInstallationRevision: plan.InstallationRevision), cancellationToken);
    }

    public Task<LauncherProfile> EditAsync(LauncherProfile profile, string name, string gameDirectory,
        CancellationToken cancellationToken = default) => EditAsync(profile, name, gameDirectory, "", cancellationToken);

    public Task<LauncherProfile> EditAsync(LauncherProfile profile, string name, string gameDirectory,
        string preferredInstallationId, CancellationToken cancellationToken = default) => MutateAsync(new("edit", Root: root,
            Id: profile.Id, Name: profile.IsDefault ? null : name, GameDirectory: OptionalGameDirectory(gameDirectory),
            ExpectedRevision: profile.Revision, PreferredInstallationId: preferredInstallationId), cancellationToken);

    public Task<LauncherProfile> AssignInstallationAsync(LauncherProfile profile, RegisteredGameInstallation installation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(installation);
        if (installation.State != "available" || !LauncherProfiles.ValidId(installation.Id))
            throw new InvalidOperationException("Select an available game installation before assigning it.");
        return MutateAsync(new("edit", Root: root, Id: profile.Id, Name: profile.IsDefault ? null : profile.Name,
            GameDirectory: installation.GameDirectory, ExpectedRevision: profile.Revision,
            PreferredInstallationId: installation.Id), cancellationToken);
    }

    public IReadOnlyList<RegisteredGameInstallation> Installations()
        => RequireSuccess(new("installations", Root: root)).Installations
            ?? throw new InvalidDataException("The shared catalog omitted its installation registrations.");

    public Task<IReadOnlyList<RegisteredGameInstallation>> InstallationsAsync(CancellationToken cancellationToken = default)
        => Task.Run(Installations, cancellationToken);

    public Task<RegisteredGameInstallation> RegisterInstallationAsync(string name, string gameDirectory,
        CancellationToken cancellationToken = default)
        => Task.Run(() => RequireSuccess(new("register-installation", Root: root, Name: name,
            GameDirectory: OptionalGameDirectory(gameDirectory))).RegisteredInstallation
            ?? throw new InvalidDataException("The shared catalog omitted the registered installation."), cancellationToken);

    public RegisteredGameInstallation InstallationPaths(string id)
        => RequireSuccess(new("installation-paths", Root: root, InstallationId: id)).RegisteredInstallation
            ?? throw new InvalidDataException("The shared catalog omitted its installation identity.");

    public Task<LauncherProfile> ArchiveAsync(LauncherProfile profile, CancellationToken cancellationToken = default) =>
        MutateAsync(new("archive", Root: root, Id: profile.Id, ExpectedRevision: profile.Revision), cancellationToken);
    public Task<LauncherProfile> RestoreAsync(LauncherProfile profile, CancellationToken cancellationToken = default) =>
        MutateAsync(new("restore", Root: root, Id: profile.Id, ExpectedRevision: profile.Revision), cancellationToken);

    public async Task SelectAsync(string? profileId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using var lease = await operationLock.TryAcquireAsync(cancellationToken);
        if (lease is null) throw new InvalidOperationException("Another Bridge operation is active. Try selecting again.");
        profileId ??= EnsureDefault().Id;
        if (profileId is not null)
        {
            var response = RequireSuccess(new("paths", Root: root, Id: profileId));
            if (response.Profile?.State != "active")
                throw new InvalidOperationException("Restore the profile before selecting it for launch.");
        }
        var directory = Path.GetDirectoryName(selectionPath)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".ui-selection.{Guid.NewGuid():N}.tmp");
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new Selection(1, profileId), JsonOptions);
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                output.Write(bytes);
                output.Flush(flushToDisk: true);
            }
            File.Move(temporary, selectionPath, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public IDisposable AcquireInstallationLease(string gameDirectory)
    {
        if (transport is not IProfileInstallationLeaseTransport leases)
            throw new NotSupportedException("The shared Profiles component does not provide installation launch custody.");
        return leases.AcquireInstallationLease(new("installation-status", Root: root, GameDirectory: gameDirectory));
    }

    public ProfileCatalogLease AcquireDataLease(string id)
    {
        if (transport is not IProfileCatalogLeaseTransport leases)
            throw new NotSupportedException("The shared profile component does not provide safe data leases.");
        return leases.AcquireDataLease(new("paths", Root: root, Id: id));
    }

    public Task<ProfileCatalogResponse> LaunchAsync(LauncherProfile profile, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(() => transport.Request(new(profile.IsDefault ? "launch-ordinary" : "launch", Root: root, Id: profile.Id,
            GameDirectory: profile.GameDirectory, ExpectedRevision: profile.Revision,
            InstallationId: string.IsNullOrWhiteSpace(profile.PreferredInstallationId) ? null : profile.PreferredInstallationId)), cancellationToken);
    }

    public IReadOnlyList<ProfileSession> Sessions()
        => RequireSuccess(new("sessions", Root: root)).Sessions
            ?? throw new InvalidDataException("The shared Profiles component omitted its session list.");

    public Task<IReadOnlyList<ProfileSession>> SessionsAsync(CancellationToken cancellationToken = default)
        => Task.Run(Sessions, cancellationToken);

    private Task<LauncherProfile> MutateAsync(ProfileCatalogRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(() => RequireSuccess(request).Profile
            ?? throw new InvalidDataException("The shared catalog omitted the changed profile."), cancellationToken);
    }

    private ProfileCatalogResponse RequireSuccess(ProfileCatalogRequest request)
    {
        var response = transport.Request(request);
        if (!response.Ok) throw new InvalidOperationException(response.Error is { } error
            ? $"{error.Message} ({error.Code})" : "The shared profile operation failed without an explanation.");
        return response;
    }

    private static string OptionalGameDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        var validation = GameInstallValidator.Validate(path);
        if (!validation.IsValid) throw new ArgumentException(validation.Message, nameof(path));
        return validation.GameDirectory;
    }

    private static bool IsCatalogException(Exception exception) => exception is IOException
        or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException
        or InvalidOperationException or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException;
    private sealed record Selection(int SchemaVersion, string? SelectedProfileId);
}
