using System.Text.Json;

namespace STFCCommunityMod.Launcher.Core;

public enum GameInstallSelectionState
{
    Missing,
    Loaded,
    Invalid,
}

public sealed record GameInstallSelection(string GameDirectory, DateTimeOffset ConfirmedAtUtc);

public sealed record GameInstallSelectionLoadResult(
    GameInstallSelectionState State,
    GameInstallSelection? Selection,
    string? Error)
{
    public static GameInstallSelectionLoadResult Missing() => new(GameInstallSelectionState.Missing, null, null);

    public static GameInstallSelectionLoadResult Loaded(GameInstallSelection selection) =>
        new(GameInstallSelectionState.Loaded, selection, null);

    public static GameInstallSelectionLoadResult Invalid(string error) =>
        new(GameInstallSelectionState.Invalid, null, error);
}

public interface IGameInstallSelectionStore
{
    GameInstallSelectionLoadResult Load();

    void Save(string gameDirectory);
}

public sealed class JsonGameInstallSelectionStore(
    string stateDirectory,
    TimeProvider? timeProvider = null) : IGameInstallSelectionStore
{
    private const int CurrentSchemaVersion = 1;
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly string selectionPath = Path.Combine(
        Path.GetFullPath(stateDirectory),
        "install-selection.json");

    public GameInstallSelectionLoadResult Load()
    {
        if (!File.Exists(selectionPath))
        {
            return GameInstallSelectionLoadResult.Missing();
        }

        try
        {
            var contents = File.ReadAllText(selectionPath);
            var document = JsonSerializer.Deserialize<SelectionDocument>(contents, SerializerOptions);
            if (document is null
                || document.SchemaVersion != CurrentSchemaVersion
                || string.IsNullOrWhiteSpace(document.GameDirectory))
            {
                return GameInstallSelectionLoadResult.Invalid(
                    "The saved installation selection has an unsupported or incomplete schema.");
            }

            return GameInstallSelectionLoadResult.Loaded(
                new(document.GameDirectory, document.ConfirmedAtUtc));
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or JsonException
                or NotSupportedException)
        {
            return GameInstallSelectionLoadResult.Invalid(
                $"The saved installation selection could not be read: {exception.Message}");
        }
    }

    public void Save(string gameDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameDirectory);

        var normalizedDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(gameDirectory));
        var parentDirectory = Path.GetDirectoryName(selectionPath)
            ?? throw new InvalidOperationException("The selection file has no parent directory.");
        Directory.CreateDirectory(parentDirectory);

        var temporaryPath = Path.Combine(
            parentDirectory,
            $".install-selection.{Guid.NewGuid():N}.tmp");
        var document = new SelectionDocument(
            CurrentSchemaVersion,
            normalizedDirectory,
            clock.GetUtcNow());

        try
        {
            File.WriteAllText(
                temporaryPath,
                JsonSerializer.Serialize(document, SerializerOptions));
            if (File.Exists(selectionPath))
            {
                File.Replace(temporaryPath, selectionPath, null, true);
            }
            else
            {
                File.Move(temporaryPath, selectionPath);
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

    private sealed record SelectionDocument(
        int SchemaVersion,
        string GameDirectory,
        DateTimeOffset ConfirmedAtUtc);
}
