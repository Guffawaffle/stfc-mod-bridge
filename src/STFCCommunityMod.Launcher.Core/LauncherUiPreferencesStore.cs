using System.Text.Json;

namespace STFCCommunityMod.Launcher.Core;

public sealed record LauncherUiPreferences(bool SettingsSearchVisible)
{
    public static LauncherUiPreferences Default { get; } = new(false);
}

public interface ILauncherUiPreferencesStore
{
    LauncherUiPreferences Load();

    void Save(LauncherUiPreferences preferences);
}

public sealed class JsonLauncherUiPreferencesStore(string stateDirectory) : ILauncherUiPreferencesStore
{
    private const int CurrentSchemaVersion = 1;
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
            if (document is null || document.SchemaVersion != CurrentSchemaVersion)
            {
                return LauncherUiPreferences.Default;
            }

            return new(document.SettingsSearchVisible);
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
            ?? throw new InvalidOperationException("The launcher preferences file has no parent directory.");
        Directory.CreateDirectory(parentDirectory);

        var temporaryPath = Path.Combine(
            parentDirectory,
            $".ui-preferences.{Guid.NewGuid():N}.tmp");
        var document = new PreferencesDocument(
            CurrentSchemaVersion,
            preferences.SettingsSearchVisible);

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
        bool SettingsSearchVisible);
}
