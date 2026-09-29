using System.Text.Json;

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
    string? Error);

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
        var updated = existing with
        {
            Name = NormalizeName(name),
            GameDirectory = ValidateGameDirectory(gameDirectory),
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

    private static bool PathEquals(string left, string right) =>
        string.Equals(NormalizeDirectory(left), NormalizeDirectory(right), StringComparison.OrdinalIgnoreCase);
}

public sealed class JsonLauncherProfilesStore(string stateDirectory)
{
    private const int SchemaVersion = 1;
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly string path = Path.Combine(Path.GetFullPath(stateDirectory), "launch-profiles.json");

    public LauncherProfilesLoadResult Load()
    {
        if (!File.Exists(path))
        {
            return new(LauncherProfilesLoadState.Missing, LauncherProfilesSnapshot.Empty, null);
        }
        try
        {
            var document = JsonSerializer.Deserialize<Document>(File.ReadAllText(path), SerializerOptions);
            if (document is null || document.SchemaVersion != SchemaVersion || document.Profiles is null)
            {
                return new(LauncherProfilesLoadState.Invalid, null, "The profile registry schema is incomplete or unsupported.");
            }
            var snapshot = new LauncherProfilesSnapshot(document.SelectedProfileId, document.Profiles);
            LauncherProfiles.ValidateStored(snapshot);
            return new(LauncherProfilesLoadState.Loaded, snapshot, null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or JsonException or InvalidDataException or ArgumentException or NotSupportedException)
        {
            return new(LauncherProfilesLoadState.Invalid, null, $"The profile registry could not be read: {exception.Message}");
        }
    }

    public void Save(LauncherProfilesSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        LauncherProfiles.ValidateStored(snapshot);
        if (File.Exists(path) && Load().State != LauncherProfilesLoadState.Loaded)
        {
            throw new InvalidOperationException("The existing profile registry is invalid; it was not replaced.");
        }
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".launch-profiles.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(
                temporaryPath,
                JsonSerializer.Serialize(new Document(SchemaVersion, snapshot.SelectedProfileId, snapshot.Profiles), SerializerOptions));
            if (File.Exists(path))
            {
                File.Replace(temporaryPath, path, null, true);
            }
            else
            {
                File.Move(temporaryPath, path);
            }
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
