using System.Text.Json;

namespace STFCCommunityMod.Launcher.Core;

public sealed partial class ModDeploymentService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private static bool IsStateReadFailure(Exception exception) =>
        exception is IOException
            or UnauthorizedAccessException
            or JsonException
            or NotSupportedException
            or ArgumentException
            or InvalidDataException;

    private void ValidatePersistedJournal(ModDeploymentJournal journal)
    {
        if (journal.SchemaVersion != SchemaVersion
            || !Guid.TryParseExact(journal.TransactionId, "N", out _)
            || !Enum.IsDefined(journal.Operation)
            || !Enum.IsDefined(journal.Phase)
            || !Path.IsPathFullyQualified(journal.GameDirectory))
        {
            throw new InvalidDataException("The deployment journal identity is invalid or unsupported.");
        }

        var gameDirectory = Path.GetFullPath(journal.GameDirectory);
        var expectedStagePath = Path.Combine(
            gameDirectory,
            $".{ManagedFileName}.{journal.TransactionId}.stage");
        var expectedSameVolumeBackupPath = Path.Combine(
            gameDirectory,
            $".{ManagedFileName}.{journal.TransactionId}.rollback");
        var expectedDurableBackupPath = Path.Combine(
            stateDirectory,
            "rollback",
            journal.TransactionId,
            ManagedFileName);
        if (!PathEquals(journal.StagePath, expectedStagePath)
            || !PathEquals(journal.SameVolumeBackupPath, expectedSameVolumeBackupPath)
            || !PathEquals(journal.DurableBackupPath, expectedDurableBackupPath)
            || !string.Equals(journal.Artifact.FileName, ManagedFileName, StringComparison.OrdinalIgnoreCase)
            || journal.Artifact.Size <= 0
            || journal.Artifact.Size > MaximumArtifactSize
            || !TryNormalizeSha256(journal.Artifact.Sha256, out _))
        {
            throw new InvalidDataException("The deployment journal contains an unsafe artifact or recovery path.");
        }

        if (journal.PreviousInstalledState is not null)
        {
            ValidatePersistedInstalledState(journal.PreviousInstalledState);
        }
    }

    private void ValidatePersistedInstalledState(ModInstalledArtifactState state)
    {
        var attributionValues = new[]
        {
            state.ProviderId,
            state.ReleaseChannelId,
            state.RuntimeDistributionId,
        };
        var attributedValueCount = attributionValues.Count(value => !string.IsNullOrWhiteSpace(value));
        if (state.SchemaVersion != SchemaVersion
            || !Path.IsPathFullyQualified(state.GameDirectory)
            || !string.Equals(state.FileName, ManagedFileName, StringComparison.OrdinalIgnoreCase)
            || state.Size <= 0
            || state.Size > MaximumArtifactSize
            || string.IsNullOrWhiteSpace(state.Version)
            || !TryNormalizeSha256(state.Sha256, out _)
            || attributedValueCount is not (0 or 3)
            || attributionValues.Where(value => value is not null).Any(value => !IsStableIdentity(value!)))
        {
            throw new InvalidDataException("The installed-mod state is invalid or unsupported.");
        }

        if (!string.IsNullOrWhiteSpace(state.PreviousArtifactBackupPath)
            && (!Path.IsPathFullyQualified(state.PreviousArtifactBackupPath)
                || !IsContainedBy(Path.Combine(stateDirectory, "rollback"), state.PreviousArtifactBackupPath)))
        {
            throw new InvalidDataException("The installed-mod rollback path escapes Mod Control-owned state.");
        }
    }

    private static bool IsStableIdentity(string value) =>
        value.Length is > 0 and <= 96
        && (char.IsAsciiDigit(value[0]) || char.IsAsciiLetterLower(value[0]))
        && value.All(character =>
            char.IsAsciiDigit(character)
            || char.IsAsciiLetterLower(character)
            || character is '-' or '_' or '.');

    private static bool PathEquals(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static bool IsContainedBy(string root, string candidate)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(candidate));
        return !Path.IsPathFullyQualified(relative)
            && !string.Equals(relative, "..", StringComparison.Ordinal)
            && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            && !relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal);
    }

    private static T? ReadJson<T>(string path)
    {
        if (!File.Exists(path))
        {
            return default;
        }
        return JsonSerializer.Deserialize<T>(File.ReadAllBytes(path), JsonOptions);
    }

    private static void WriteJsonAtomically<T>(string path, T value)
    {
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, value, JsonOptions);
                stream.Flush(true);
            }
            File.Move(temporaryPath, path, true);
        }
        finally
        {
            DeleteIfExists(temporaryPath);
        }
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

}
