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
        if (!File.Exists(preferencesPath))
        {
            return LauncherUiPreferences.Default;
        }

        try
        {
            var contents = File.ReadAllText(preferencesPath);
            var document = JsonSerializer.Deserialize<PreferencesDocument>(contents, SerializerOptions);
            if (document is null)
            {
                return LauncherUiPreferences.Default;
            }

            if (document.SchemaVersion == 1)
            {
                return new(document.SettingsSearchVisible, LauncherColorMode.System);
            }

            if (document.SchemaVersion is not (2 or 3 or 4 or CurrentSchemaVersion))
            {
                return LauncherUiPreferences.Default;
            }

            var colorMode = Enum.TryParse<LauncherColorMode>(
                    document.ColorMode,
                    ignoreCase: true,
                    out var parsedColorMode)
                && Enum.IsDefined(parsedColorMode)
                    ? parsedColorMode
                    : LauncherColorMode.System;
            var launchTarget = document.SchemaVersion == 2
                ? LauncherLaunchTarget.ScopelyLauncher
                : ParseLaunchTarget(document.LaunchTarget);
            return new(
                document.SettingsSearchVisible,
                colorMode,
                launchTarget,
                (document.SchemaVersion is 4 or CurrentSchemaVersion)
                && document.ProviderSwitchReviewAcknowledged,
                document.SchemaVersion == CurrentSchemaVersion
                    ? new(
                        ParseFeaturePreference(document.BattleCollectionPreference),
                        ParseFeaturePreference(document.FleetCollectionPreference))
                    : LauncherBattlePreferences.Default);
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or JsonException
                or NotSupportedException)
        {
            return LauncherUiPreferences.Default;
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
            var current = LoadCore();
            if (current.EffectiveBattlePreferences != expected)
            {
                return false;
            }
            SaveCore(current with { BattlePreferences = updated });
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
            File.WriteAllText(
                temporaryPath,
                JsonSerializer.Serialize(document, SerializerOptions));
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

    private static LauncherLaunchTarget ParseLaunchTarget(string? value) =>
        Enum.TryParse<LauncherLaunchTarget>(value, ignoreCase: true, out var parsed)
        && Enum.IsDefined(parsed)
            ? parsed
            : LauncherLaunchTarget.ScopelyLauncher;

    private static LauncherPlayerFeaturePreference ParseFeaturePreference(string? value) =>
        Enum.TryParse<LauncherPlayerFeaturePreference>(value, ignoreCase: false, out var parsed)
        && Enum.IsDefined(parsed)
            ? parsed
            : LauncherPlayerFeaturePreference.Unset;

    private static void RequireFeaturePreference(LauncherPlayerFeaturePreference value)
    {
        if (!Enum.IsDefined(value))
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }
    }
}
