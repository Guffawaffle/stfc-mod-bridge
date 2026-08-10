using System.Collections.Concurrent;
using System.Text.Json;

namespace STFCCommunityMod.Launcher.Core;

public enum LauncherColorMode
{
    System,
    Light,
    Dark,
}

public enum LauncherPlayerFeaturePreference
{
    Unset,
    Enabled,
    Disabled,
}

public sealed record LauncherBattlePreferences(
    LauncherPlayerFeaturePreference BattleCollection,
    LauncherPlayerFeaturePreference FleetCollection)
{
    public static LauncherBattlePreferences Default { get; } = new(
        LauncherPlayerFeaturePreference.Unset,
        LauncherPlayerFeaturePreference.Unset);
}

public sealed record LauncherUiPreferences(
    bool SettingsSearchVisible,
    LauncherColorMode ColorMode = LauncherColorMode.System,
    LauncherLaunchTarget LaunchTarget = LauncherLaunchTarget.ScopelyLauncher,
    bool ProviderSwitchReviewAcknowledged = false,
    LauncherBattlePreferences? BattlePreferences = null)
{
    public static LauncherUiPreferences Default { get; } =
        new(
            false,
            LauncherColorMode.System,
            LauncherLaunchTarget.ScopelyLauncher,
            false,
            LauncherBattlePreferences.Default);

    public LauncherBattlePreferences EffectiveBattlePreferences =>
        BattlePreferences ?? LauncherBattlePreferences.Default;
}

public interface ILauncherUiPreferencesStore
{
    LauncherUiPreferences Load();

    void Save(LauncherUiPreferences preferences);
}

public interface ILauncherBattlePreferencesCommitter
{
    bool TryLoadBattlePreferences(out LauncherBattlePreferences preferences);

    bool TrySaveBattlePreferences(
        LauncherBattlePreferences expected,
        LauncherBattlePreferences updated);
}

public sealed class JsonLauncherUiPreferencesStore(string stateDirectory) :
    ILauncherUiPreferencesStore,
    ILauncherBattlePreferencesCommitter
{
    private const int CurrentSchemaVersion = 5;
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
    };

    private readonly string preferencesPath = Path.Combine(
        Path.GetFullPath(stateDirectory),
        "ui-preferences.json");
    private static readonly ConcurrentDictionary<string, object> PathGates = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    public LauncherUiPreferences Load()
    {
        lock (PathGates.GetOrAdd(preferencesPath, static _ => new()))
        {
            return LoadCore();
        }
    }

    private LauncherUiPreferences LoadCore()
    {
        return TryLoadCore(requireCanonical: false, out var preferences)
            ? preferences
            : LauncherUiPreferences.Default;
    }

    private bool TryLoadCore(bool requireCanonical, out LauncherUiPreferences preferences)
    {
        if (!File.Exists(preferencesPath))
        {
            preferences = LauncherUiPreferences.Default;
            return true;
        }

        try
        {
            var contents = File.ReadAllBytes(preferencesPath);
            RejectDuplicateProperties(contents);
            var document = JsonSerializer.Deserialize<PreferencesDocument>(contents, SerializerOptions);
            if (document is null)
            {
                preferences = LauncherUiPreferences.Default;
                return false;
            }

            if (document.SchemaVersion == 1)
            {
                preferences = new(document.SettingsSearchVisible, LauncherColorMode.System);
                return true;
            }

            if (document.SchemaVersion is not (2 or 3 or 4 or CurrentSchemaVersion))
            {
                preferences = LauncherUiPreferences.Default;
                return false;
            }

            var launchTarget = LauncherLaunchTarget.ScopelyLauncher;
            var battlePreference = LauncherPlayerFeaturePreference.Unset;
            var fleetPreference = LauncherPlayerFeaturePreference.Unset;
            if (!TryParseExact(document.ColorMode, out LauncherColorMode colorMode))
            {
                if (requireCanonical)
                {
                    preferences = LauncherUiPreferences.Default;
                    return false;
                }
                colorMode = LauncherColorMode.System;
            }
            if (document.SchemaVersion is not 2
                && !TryParseExact(document.LaunchTarget, out launchTarget))
            {
                if (requireCanonical)
                {
                    preferences = LauncherUiPreferences.Default;
                    return false;
                }
                launchTarget = LauncherLaunchTarget.ScopelyLauncher;
            }
            if (document.SchemaVersion == CurrentSchemaVersion)
            {
                var battleValid = TryParseExact(
                    document.BattleCollectionPreference,
                    out battlePreference);
                var fleetValid = TryParseExact(
                    document.FleetCollectionPreference,
                    out fleetPreference);
                if (requireCanonical && (!battleValid || !fleetValid))
                {
                    preferences = LauncherUiPreferences.Default;
                    return false;
                }
                if (!battleValid) battlePreference = LauncherPlayerFeaturePreference.Unset;
                if (!fleetValid) fleetPreference = LauncherPlayerFeaturePreference.Unset;
            }
            preferences = new(
                document.SettingsSearchVisible,
                colorMode,
                launchTarget,
                (document.SchemaVersion is 4 or CurrentSchemaVersion)
                && document.ProviderSwitchReviewAcknowledged,
                document.SchemaVersion == CurrentSchemaVersion
                    ? new(
                        battlePreference,
                        fleetPreference)
                    : LauncherBattlePreferences.Default);
            return true;
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or JsonException
                or NotSupportedException)
        {
            preferences = LauncherUiPreferences.Default;
            return false;
        }
    }

    public void Save(LauncherUiPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        RequireFeaturePreference(preferences.EffectiveBattlePreferences.BattleCollection);
        RequireFeaturePreference(preferences.EffectiveBattlePreferences.FleetCollection);
        lock (PathGates.GetOrAdd(preferencesPath, static _ => new()))
        {
            SaveCore(preferences);
        }
    }

    public bool TrySaveBattlePreferences(
        LauncherBattlePreferences expected,
        LauncherBattlePreferences updated)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(updated);
        RequireFeaturePreference(expected.BattleCollection);
        RequireFeaturePreference(expected.FleetCollection);
        RequireFeaturePreference(updated.BattleCollection);
        RequireFeaturePreference(updated.FleetCollection);
        lock (PathGates.GetOrAdd(preferencesPath, static _ => new()))
        {
            if (!TryLoadCore(requireCanonical: true, out var current))
            {
                return false;
            }
            if (current.EffectiveBattlePreferences != expected)
            {
                return false;
            }
            SaveCore(current with { BattlePreferences = updated });
            return true;
        }
    }

    public bool TryLoadBattlePreferences(out LauncherBattlePreferences preferences)
    {
        lock (PathGates.GetOrAdd(preferencesPath, static _ => new()))
        {
            if (!TryLoadCore(requireCanonical: true, out var current))
            {
                preferences = LauncherBattlePreferences.Default;
                return false;
            }
            preferences = current.EffectiveBattlePreferences;
            return true;
        }
    }

    private void SaveCore(LauncherUiPreferences preferences)
    {

        var parentDirectory = Path.GetDirectoryName(preferencesPath)
            ?? throw new InvalidOperationException("The Mod Bridge preferences file has no parent directory.");
        Directory.CreateDirectory(parentDirectory);

        var temporaryPath = Path.Combine(
            parentDirectory,
            $".ui-preferences.{Guid.NewGuid():N}.tmp");
        var document = new PreferencesDocument(
            CurrentSchemaVersion,
            preferences.SettingsSearchVisible,
            preferences.ColorMode.ToString(),
            preferences.LaunchTarget.ToString(),
            preferences.ProviderSwitchReviewAcknowledged,
            preferences.EffectiveBattlePreferences.BattleCollection.ToString(),
            preferences.EffectiveBattlePreferences.FleetCollection.ToString());

        try
        {
            var contents = JsonSerializer.SerializeToUtf8Bytes(document, SerializerOptions);
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       81920,
                       FileOptions.WriteThrough))
            {
                stream.Write(contents);
                stream.Flush(flushToDisk: true);
            }
            if (File.Exists(preferencesPath))
            {
                File.Replace(temporaryPath, preferencesPath, null, true);
            }
            else
            {
                File.Move(temporaryPath, preferencesPath);
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

    private sealed record PreferencesDocument(
        int SchemaVersion,
        bool SettingsSearchVisible,
        string? ColorMode = null,
        string? LaunchTarget = null,
        bool ProviderSwitchReviewAcknowledged = false,
        string? BattleCollectionPreference = null,
        string? FleetCollectionPreference = null);

    private static bool TryParseExact<T>(string? value, out T parsed)
        where T : struct, Enum =>
        Enum.TryParse(value, ignoreCase: false, out parsed)
        && Enum.IsDefined(parsed)
        && string.Equals(value, parsed.ToString(), StringComparison.Ordinal);

    private static void RejectDuplicateProperties(ReadOnlySpan<byte> contents)
    {
        var reader = new Utf8JsonReader(contents, new JsonReaderOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 3,
        });
        var names = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.PropertyName
                && !names.Add(reader.GetString() ?? throw new JsonException()))
            {
                throw new JsonException("The launcher preferences contain duplicate properties.");
            }
        }
    }

    private static void RequireFeaturePreference(LauncherPlayerFeaturePreference value)
    {
        if (!Enum.IsDefined(value))
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }
    }
}
