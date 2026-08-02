using System.Text.Json;

namespace STFCCommunityMod.Launcher.Core;

public enum LauncherColorMode
{
    System,
    Light,
    Dark,
}

public sealed record LauncherUiPreferences(
    bool SettingsSearchVisible,
    LauncherColorMode ColorMode = LauncherColorMode.System,
    LauncherLaunchTarget LaunchTarget = LauncherLaunchTarget.ScopelyLauncher)
{
    public static LauncherUiPreferences Default { get; } =
        new(false, LauncherColorMode.System, LauncherLaunchTarget.ScopelyLauncher);
}

public interface ILauncherUiPreferencesStore
{
    LauncherUiPreferences Load();

    void Save(LauncherUiPreferences preferences);
}

public sealed class JsonLauncherUiPreferencesStore(string stateDirectory) : ILauncherUiPreferencesStore
{
    private const int CurrentSchemaVersion = 3;
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly string preferencesPath = Path.Combine(
        Path.GetFullPath(stateDirectory),
        "ui-preferences.json");

    public LauncherUiPreferences Load()
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

            if (document.SchemaVersion is not (2 or CurrentSchemaVersion))
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
            return new(document.SettingsSearchVisible, colorMode, launchTarget);
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

        var parentDirectory = Path.GetDirectoryName(preferencesPath)
            ?? throw new InvalidOperationException("The Mod Control preferences file has no parent directory.");
        Directory.CreateDirectory(parentDirectory);

        var temporaryPath = Path.Combine(
            parentDirectory,
            $".ui-preferences.{Guid.NewGuid():N}.tmp");
        var document = new PreferencesDocument(
            CurrentSchemaVersion,
            preferences.SettingsSearchVisible,
            preferences.ColorMode.ToString(),
            preferences.LaunchTarget.ToString());

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
        string? LaunchTarget = null);

    private static LauncherLaunchTarget ParseLaunchTarget(string? value) =>
        Enum.TryParse<LauncherLaunchTarget>(value, ignoreCase: true, out var parsed)
        && Enum.IsDefined(parsed)
            ? parsed
            : LauncherLaunchTarget.ScopelyLauncher;
}
