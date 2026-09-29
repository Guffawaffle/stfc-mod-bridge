using System.Text.Json;
using System.Security.Cryptography;
using System.Text;

namespace STFCCommunityMod.Launcher.Core;

public sealed record LauncherProfile(string Id, string Name, string GameDirectory);

public sealed record LauncherProfilesSnapshot(
    string? SelectedProfileId,
    IReadOnlyList<LauncherProfile> Profiles)
{
    public static LauncherProfilesSnapshot Empty { get; } = new(null, []);

    public LauncherProfile? SelectedProfile =>
        Profiles.FirstOrDefault(profile => profile.Id == SelectedProfileId);
}

public enum LauncherProfilesLoadState
{
    Missing,
    Loaded,
    Invalid,
}

public sealed record LauncherProfilesLoadResult(
    LauncherProfilesLoadState State,
    LauncherProfilesSnapshot? Snapshot,
    string? Error,
    string? Revision = null);

public static class LauncherProfiles
{
    public const int MaximumProfiles = 24;

    public static LauncherProfilesSnapshot Add(
        LauncherProfilesSnapshot snapshot,
        string name,
        string gameDirectory,
        string? defaultGameDirectory,
        string? existingProfileId = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Profiles.Count >= MaximumProfiles)
        {
            throw new InvalidOperationException($"At most {MaximumProfiles} profiles are supported.");
        }

        var profile = new LauncherProfile(
            existingProfileId ?? Guid.NewGuid().ToString("N"),
            NormalizeName(name),
            ValidateGameDirectory(gameDirectory));
        ValidateDistinct(snapshot.Profiles, profile, defaultGameDirectory);
        return snapshot with { Profiles = [.. snapshot.Profiles, profile] };
    }

    public static LauncherProfilesSnapshot Edit(
        LauncherProfilesSnapshot snapshot,
        string profileId,
        string name,
        string gameDirectory,
        string? defaultGameDirectory)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var existing = snapshot.Profiles.FirstOrDefault(profile => profile.Id == profileId)
            ?? throw new InvalidOperationException("The profile no longer exists.");
        var validatedDirectory = ValidateGameDirectory(gameDirectory);
        if (!GameDirectoryIdentity.SameLocation(existing.GameDirectory, validatedDirectory))
        {
            throw new InvalidOperationException(
                "An enrolled profile keeps its game folder. Add or adopt a profile for another installation.");
        }
        var updated = existing with
        {
            Name = NormalizeName(name),
            GameDirectory = validatedDirectory,
        };
        ValidateDistinct(snapshot.Profiles.Where(profile => profile.Id != profileId), updated, defaultGameDirectory);
        return snapshot with
        {
            Profiles = snapshot.Profiles.Select(profile => profile.Id == profileId ? updated : profile).ToArray(),
        };
    }

    public static LauncherProfilesSnapshot Remove(LauncherProfilesSnapshot snapshot, string profileId)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!snapshot.Profiles.Any(profile => profile.Id == profileId))
        {
            throw new InvalidOperationException("The profile no longer exists.");
        }
        return new(
            snapshot.SelectedProfileId == profileId ? null : snapshot.SelectedProfileId,
            snapshot.Profiles.Where(profile => profile.Id != profileId).ToArray());
    }

    public static LauncherProfilesSnapshot Select(LauncherProfilesSnapshot snapshot, string? profileId)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (profileId is not null && !snapshot.Profiles.Any(profile => profile.Id == profileId))
        {
            throw new InvalidOperationException("The profile no longer exists.");
        }
        return snapshot with { SelectedProfileId = profileId };
    }

    public static string GameConfigPath(LauncherProfile profile) =>
        Path.Combine(profile.GameDirectory, "stfc-mod", profile.Id, $"{profile.Id}.toml");

    public static string UnityLogPath(LauncherProfile profile, string localApplicationData) =>
        Path.Combine(Path.GetFullPath(localApplicationData), "STFC Community Mod", "Profiles", profile.Id, "Player.log");

    public static bool IsNamedProfileFolder(string gameDirectory, LauncherProfilesSnapshot snapshot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameDirectory);
        ArgumentNullException.ThrowIfNull(snapshot);
        return File.Exists(Path.Combine(gameDirectory, "stfc_community_mod.profile"))
            || snapshot.Profiles.Any(profile =>
                GameDirectoryIdentity.SameLocation(profile.GameDirectory, gameDirectory));
    }

    internal static void ValidateStored(LauncherProfilesSnapshot snapshot)
    {
        if (snapshot.Profiles is null || snapshot.Profiles.Count > MaximumProfiles)
        {
            throw new InvalidDataException("The profile registry has an invalid profile count.");
        }
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var profile in snapshot.Profiles)
        {
            if (profile is null || !ValidId(profile.Id)
                || profile.Name != NormalizeName(profile.Name)
                || profile.GameDirectory != NormalizeDirectory(profile.GameDirectory)
                || !ids.Add(profile.Id)
                || !names.Add(profile.Name)
                || !directories.Add(profile.GameDirectory))
            {
                throw new InvalidDataException("The profile registry contains invalid or duplicate entries.");
            }
        }
        if (snapshot.SelectedProfileId is not null
            && !snapshot.Profiles.Any(profile => profile.Id == snapshot.SelectedProfileId))
        {
            throw new InvalidDataException("The selected profile is not in the registry.");
        }
    }

    private static string NormalizeName(string name)
    {
        if (name is null)
        {
            throw new ArgumentException("Enter a profile name.", nameof(name));
        }
        var normalized = name.Trim();
        if (normalized.Length is < 1 or > 48 || normalized.Any(char.IsControl))
        {
            throw new ArgumentException("Profile names must be 1 to 48 visible characters.", nameof(name));
        }
        return normalized;
    }

    private static string NormalizeDirectory(string gameDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameDirectory);
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(gameDirectory));
    }

    private static string ValidateGameDirectory(string gameDirectory)
    {
        var validation = GameInstallValidator.Validate(gameDirectory);
        if (!validation.IsValid)
        {
            throw new ArgumentException(validation.Message, nameof(gameDirectory));
        }
        return validation.GameDirectory!;
    }

    private static void ValidateDistinct(
        IEnumerable<LauncherProfile> profiles,
        LauncherProfile candidate,
        string? defaultGameDirectory)
    {
        if (!ValidId(candidate.Id))
        {
            throw new ArgumentException("Profile keys must be 1 to 32 lowercase ASCII letters, digits, '-' or '_', excluding Windows device names.");
        }
        if (profiles.Any(profile => string.Equals(profile.Id, candidate.Id, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("That profile key is already in use.");
        }
        if (profiles.Any(profile => string.Equals(profile.Name, candidate.Name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("That profile name is already in use.");
        }
        if (profiles.Any(profile => PathEquals(profile.GameDirectory, candidate.GameDirectory))
            || (!string.IsNullOrWhiteSpace(defaultGameDirectory)
                && PathEquals(defaultGameDirectory, candidate.GameDirectory)))
        {
            throw new InvalidOperationException("Choose a game folder not used by another profile or Default.");
        }
    }

    public static bool ValidId(string? id)
    {
        if (id is not { Length: >= 1 and <= 32 }
            || !id.All(ch => ch is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_')
            || id is "con" or "prn" or "aux" or "nul")
        {
            return false;
        }
        return id.Length != 4 || id[3] is < '1' or > '9'
            || (id[..3] is not ("com" or "lpt"));
    }

    private static bool PathEquals(string left, string right) => GameDirectoryIdentity.SameLocation(left, right);
}

public sealed class JsonLauncherProfilesStore(
    string stateDirectory,
    IGameProcessInspector? gameProcessInspector = null)
{
    private const int SchemaVersion = 1;
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly string path = Path.Combine(Path.GetFullPath(stateDirectory), "launch-profiles.json");
    private readonly LauncherOperationLock operationLock = new(stateDirectory);
    private readonly IGameProcessInspector gameProcessInspector = gameProcessInspector ?? new SystemGameProcessInspector();
    private readonly JsonGameInstallSelectionStore installSelectionStore = new(stateDirectory);

    public LauncherProfilesLoadResult Load()
    {
        if (!File.Exists(path))
        {
            return new(LauncherProfilesLoadState.Missing, LauncherProfilesSnapshot.Empty, null, "missing");
        }
        try
        {
            var bytes = File.ReadAllBytes(path);
            var revision = Convert.ToHexString(SHA256.HashData(bytes));
            var document = JsonSerializer.Deserialize<Document>(bytes, SerializerOptions);
            if (document is null || document.SchemaVersion != SchemaVersion || document.Profiles is null)
            {
                return new(LauncherProfilesLoadState.Invalid, null, "The profile registry schema is incomplete or unsupported.");
            }
            var snapshot = new LauncherProfilesSnapshot(document.SelectedProfileId, document.Profiles);
            LauncherProfiles.ValidateStored(snapshot);
            return new(LauncherProfilesLoadState.Loaded, snapshot, null, revision);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or JsonException or InvalidDataException or ArgumentException or NotSupportedException)
        {
            return new(LauncherProfilesLoadState.Invalid, null, $"The profile registry could not be read: {exception.Message}");
        }
    }

    public async Task<string> SaveAsync(LauncherProfilesSnapshot snapshot, string expectedRevision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedRevision);
        LauncherProfiles.ValidateStored(snapshot);
        await using var lease = await operationLock.TryAcquireAsync(cancellationToken);
        if (lease is null)
        {
            throw new InvalidOperationException("Another Mod Bridge operation is active. Try the profile change again.");
        }
        RequireRevision(expectedRevision);
        return SaveCore(snapshot);
    }

    public async Task<(LauncherProfilesSnapshot Snapshot, string Revision)> CreateNewAsync(
        string name, string gameDirectory, string? defaultGameDirectory, string expectedRevision,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedRevision);
        await using var lease = await operationLock.TryAcquireAsync(cancellationToken);
        if (lease is null)
        {
            throw new InvalidOperationException("Another Mod Bridge operation is active. Try the profile change again.");
        }
        var current = RequireRevision(expectedRevision);
        var currentDefault = RequireCurrentDefault(defaultGameDirectory);
        var updated = LauncherProfiles.Add(current, name, gameDirectory, currentDefault);
        var profile = updated.Profiles[^1];
        var capability = LauncherProfileLaunchContract.InspectCapableDll(profile.GameDirectory);
        if (!capability.IsValid)
        {
            throw new InvalidOperationException(capability.Message);
        }
        var markerPath = Path.Combine(profile.GameDirectory, "stfc_community_mod.profile");
        if (File.Exists(markerPath))
        {
            throw new InvalidOperationException("This game folder already has a profile marker. Use Adopt existing profile.");
        }
        switch (gameProcessInspector.Inspect(profile.GameDirectory))
        {
            case GameProcessInspectionState.NotRunning:
                break;
            case GameProcessInspectionState.RunningTarget:
                throw new InvalidOperationException(
                    "Close Star Trek Fleet Command in this installation before creating a profile marker.");
            default:
                throw new InvalidOperationException(
                    "A prime.exe process is running but could not be attributed safely. Close it before creating a profile marker.");
        }
        var temporaryMarker = Path.Combine(profile.GameDirectory, $".stfc-profile.{Guid.NewGuid():N}.tmp");
        try
        {
            var bytes = Encoding.ASCII.GetBytes($"v1:{profile.Id}\n");
            using (var file = new FileStream(temporaryMarker, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.WriteThrough))
            {
                file.Write(bytes);
                file.Flush(flushToDisk: true);
            }
            File.Move(temporaryMarker, markerPath);
        }
        finally
        {
            if (File.Exists(temporaryMarker))
            {
                File.Delete(temporaryMarker);
            }
        }
        try
        {
            return (updated, SaveCore(updated));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            throw new InvalidOperationException(
                "The marker was created, but Bridge could not save its profile entry. Reopen Profiles and adopt the marked install.",
                exception);
        }
    }

    public async Task<(LauncherProfilesSnapshot Snapshot, string Revision)> AdoptExistingAsync(
        string name, string gameDirectory, string? defaultGameDirectory, string expectedRevision,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedRevision);
        await using var lease = await operationLock.TryAcquireAsync(cancellationToken);
        if (lease is null)
        {
            throw new InvalidOperationException("Another Mod Bridge operation is active. Try the profile change again.");
        }
        var current = RequireRevision(expectedRevision);
        var currentDefault = RequireCurrentDefault(defaultGameDirectory);
        var contract = LauncherProfileLaunchContract.Inspect(gameDirectory);
        if (!contract.IsValid || contract.ProfileId is null)
        {
            throw new InvalidOperationException(contract.Message);
        }
        var updated = LauncherProfiles.Add(current, name, gameDirectory, currentDefault, contract.ProfileId);
        return (updated, SaveCore(updated));
    }

    public async Task<(LauncherProfilesSnapshot Snapshot, string Revision)> SelectAsync(
        string? profileId, string? defaultGameDirectory, string expectedRevision,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedRevision);
        await using var lease = await operationLock.TryAcquireAsync(cancellationToken);
        if (lease is null)
        {
            throw new InvalidOperationException("Another Mod Bridge operation is active. Try the profile change again.");
        }
        var current = RequireRevision(expectedRevision);
        var currentDefault = RequireCurrentDefault(defaultGameDirectory);
        var updated = LauncherProfiles.Select(current, profileId);
        if (updated.SelectedProfile is { } profile)
        {
            var contract = LauncherProfileLaunchContract.Inspect(profile.GameDirectory, profile.Id);
            if (!contract.IsValid)
            {
                throw new InvalidOperationException(contract.Message);
            }
            if (currentDefault is not null
                && GameDirectoryIdentity.SameLocation(currentDefault, profile.GameDirectory))
            {
                throw new InvalidOperationException("The named profile must use a separate game folder from Default.");
            }
        }
        else if (currentDefault is not null
            && LauncherProfiles.IsNamedProfileFolder(currentDefault, current))
        {
            throw new InvalidOperationException("Default must use an unmarked game folder distinct from named profiles.");
        }
        return (updated, SaveCore(updated));
    }

    private LauncherProfilesSnapshot RequireRevision(string expectedRevision)
    {
        var current = Load();
        if (current.State == LauncherProfilesLoadState.Invalid)
        {
            throw new InvalidOperationException("The existing profile registry is invalid; it was not replaced.");
        }
        if (current.Revision != expectedRevision)
        {
            throw new InvalidOperationException("The profile registry changed in another window. Reopen Profiles before saving.");
        }
        return current.Snapshot!;
    }

    private string? RequireCurrentDefault(string? displayedDirectory)
    {
        var selection = installSelectionStore.Load();
        if (selection.State == GameInstallSelectionState.Invalid)
        {
            throw new InvalidOperationException(selection.Error ?? "The Default game selection could not be read.");
        }
        if (selection.State == GameInstallSelectionState.Missing)
        {
            return displayedDirectory;
        }
        var currentDirectory = selection.Selection!.GameDirectory;
        if (displayedDirectory is null
            || !GameDirectoryIdentity.SameLocation(displayedDirectory, currentDirectory))
        {
            throw new InvalidOperationException(
                "The Default game folder changed in another window. Refresh Bridge before saving profiles.");
        }
        return currentDirectory;
    }

    private string SaveCore(LauncherProfilesSnapshot snapshot)
    {
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".launch-profiles.{Guid.NewGuid():N}.tmp");
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(
                new Document(SchemaVersion, snapshot.SelectedProfileId, snapshot.Profiles), SerializerOptions);
            File.WriteAllBytes(temporaryPath, bytes);
            if (File.Exists(path))
            {
                File.Replace(temporaryPath, path, null, true);
            }
            else
            {
                File.Move(temporaryPath, path);
            }
            return Convert.ToHexString(SHA256.HashData(bytes));
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private sealed record Document(
        int SchemaVersion,
        string? SelectedProfileId,
        IReadOnlyList<LauncherProfile> Profiles);
}
