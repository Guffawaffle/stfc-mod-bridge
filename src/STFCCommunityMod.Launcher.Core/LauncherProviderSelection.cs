using System.Security.Cryptography;
using System.Text.Json;

namespace STFCCommunityMod.Launcher.Core;

public sealed record LauncherProviderSelection(string ProviderId, string ReleaseChannelId);

public interface ILauncherProviderSelectionStore
{
    LauncherProviderSelection? Load();

    void Save(LauncherProviderSelection selection);

    void Clear();
}

public sealed class JsonLauncherProviderSelectionStore(string stateDirectory)
    : ILauncherProviderSelectionStore
{
    private const int CurrentSchemaVersion = 1;
    private static readonly HashSet<string> DocumentProperties =
        ["schemaVersion", "providerId", "releaseChannelId"];
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };
    private readonly string selectionPath = Path.Combine(
        Path.GetFullPath(stateDirectory),
        "provider-selection.json");

    public LauncherProviderSelection? Load()
    {
        if (!File.Exists(selectionPath))
        {
            return null;
        }
        using var stream = File.OpenRead(selectionPath);
        using var document = JsonDocument.Parse(
            stream,
            new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 8,
            });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Provider selection must be a JSON object.");
        }
        foreach (var property in root.EnumerateObject())
        {
            if (!DocumentProperties.Contains(property.Name))
            {
                throw new InvalidDataException(
                    $"Provider selection contains unknown property '{property.Name}'.");
            }
        }
        var schemaVersion = ReadInt32(root, "schemaVersion");
        if (schemaVersion != CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"Provider selection schema {schemaVersion} is unsupported.");
        }
        return new(
            ReadString(root, "providerId"),
            ReadString(root, "releaseChannelId"));
    }

    public void Save(LauncherProviderSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        var parent = Path.GetDirectoryName(selectionPath)
            ?? throw new InvalidOperationException("Provider selection path has no parent directory.");
        Directory.CreateDirectory(parent);
        var temporaryPath = Path.Combine(parent, $".provider-selection.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(
                temporaryPath,
                JsonSerializer.Serialize(
                    new SelectionDocument(
                        CurrentSchemaVersion,
                        selection.ProviderId,
                        selection.ReleaseChannelId),
                    SerializerOptions));
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

    public void Clear()
    {
        if (File.Exists(selectionPath))
        {
            File.Delete(selectionPath);
        }
    }

    private static int ReadInt32(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.Number
            || !property.TryGetInt32(out var value))
        {
            throw new InvalidDataException(
                $"Provider selection property '{propertyName}' must be an integer.");
        }
        return value;
    }

    private static string ReadString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(property.GetString()))
        {
            throw new InvalidDataException(
                $"Provider selection property '{propertyName}' must be a non-empty string.");
        }
        return property.GetString()!;
    }

    private sealed record SelectionDocument(
        int SchemaVersion,
        string ProviderId,
        string ReleaseChannelId);
}

public enum LauncherProviderSelectionResolutionState
{
    Defaulted,
    Selected,
    UnknownProvider,
    UnknownReleaseChannel,
}

public sealed record LauncherProviderSelectionResolution(
    LauncherProviderSelectionResolutionState State,
    LauncherProviderSelection Selection,
    LauncherDistributionProvider? Provider,
    LauncherProviderReleaseChannel? ReleaseChannel,
    string Message)
{
    public bool IsResolved => Provider is not null && ReleaseChannel is not null;
}

public static class LauncherProviderSelectionResolver
{
    public static LauncherProviderSelectionResolution Resolve(
        LauncherDistributionProviderCatalog catalog,
        LauncherProviderSelection? persistedSelection)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var selection = persistedSelection
            ?? new(
                catalog.DefaultProviderId,
                catalog.DefaultProvider.DefaultReleaseChannelId);
        if (!catalog.TryGetProvider(selection.ProviderId, out var provider) || provider is null)
        {
            return new(
                LauncherProviderSelectionResolutionState.UnknownProvider,
                selection,
                null,
                null,
                $"Selected provider '{selection.ProviderId}' is not present in the resolved catalog.");
        }
        if (!provider.ReleaseChannels.TryGetValue(selection.ReleaseChannelId, out var channel))
        {
            return new(
                LauncherProviderSelectionResolutionState.UnknownReleaseChannel,
                selection,
                provider,
                null,
                $"Selected release channel '{selection.ReleaseChannelId}' is unknown for provider '{provider.Id}'.");
        }
        return new(
            persistedSelection is null
                ? LauncherProviderSelectionResolutionState.Defaulted
                : LauncherProviderSelectionResolutionState.Selected,
            selection,
            provider,
            channel,
            persistedSelection is null
                ? $"Using default provider '{provider.DisplayName}'."
                : $"Using selected provider '{provider.DisplayName}'.");
    }
}

public enum LauncherProviderCompatibilityKind
{
    Compatible,
    Loss,
    Unknown,
}

public sealed record LauncherProviderCompatibilityConcern(
    string CapabilityId,
    LauncherProviderCompatibilityKind Kind,
    string Message);

public sealed record LauncherProviderSwitchPreview(
    string TransactionId,
    LauncherProviderSelection Source,
    LauncherProviderSelection Target,
    string SourceDisplayName,
    string TargetDisplayName,
    IReadOnlyList<LauncherProviderCompatibilityConcern> Concerns,
    string? ConfigurationPath,
    string? ConfigurationSha256,
    string ConfirmationText)
{
    public bool HasCompatibilityLoss =>
        Concerns.Any(concern => concern.Kind == LauncherProviderCompatibilityKind.Loss);

    public bool HasUnknownCompatibility =>
        Concerns.Any(concern => concern.Kind == LauncherProviderCompatibilityKind.Unknown);
}

public sealed record LauncherProviderSwitchResult(
    LauncherProviderSelection Selection,
    string? ConfigurationBackupPath,
    string Message);

public sealed class LauncherProviderSourceSwitchService(
    LauncherDistributionProviderCatalog catalog,
    ILauncherProviderSelectionStore selectionStore,
    string stateDirectory)
{
    private const long MaximumConfigurationBytes = 8 * 1024 * 1024;
    private readonly string normalizedStateDirectory = Path.GetFullPath(stateDirectory);

    public LauncherProviderSwitchPreview Preview(
        string targetProviderId,
        string? targetReleaseChannelId,
        string? configurationPath)
    {
        var current = ResolveCurrent();
        var targetProvider = catalog.GetProvider(targetProviderId);
        var channelId = string.IsNullOrWhiteSpace(targetReleaseChannelId)
            ? targetProvider.DefaultReleaseChannelId
            : targetReleaseChannelId;
        if (!targetProvider.ReleaseChannels.ContainsKey(channelId))
        {
            throw new KeyNotFoundException(
                $"Release channel '{channelId}' is not registered for provider '{targetProvider.Id}'.");
        }
        var target = new LauncherProviderSelection(targetProvider.Id, channelId);
        if (target == current.Selection)
        {
            throw new InvalidOperationException("The requested provider and release channel are already selected.");
        }

        var normalizedConfigurationPath = NormalizeOptionalConfigurationPath(configurationPath);
        var configurationSha256 = normalizedConfigurationPath is null
            ? null
            : HashConfiguration(normalizedConfigurationPath);
        var concerns = BuildConcerns(current.Provider!, targetProvider);
        return new(
            Guid.NewGuid().ToString("N"),
            current.Selection,
            target,
            current.Provider!.DisplayName,
            targetProvider.DisplayName,
            concerns,
            normalizedConfigurationPath,
            configurationSha256,
            targetProvider.Id);
    }

    public LauncherProviderSwitchResult Execute(
        LauncherProviderSwitchPreview preview,
        string confirmationText)
    {
        ArgumentNullException.ThrowIfNull(preview);
        if (!string.Equals(confirmationText, preview.ConfirmationText, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Provider switch requires confirmation text '{preview.ConfirmationText}'.");
        }
        var current = ResolveCurrent();
        if (current.Selection != preview.Source)
        {
            throw new InvalidOperationException(
                "Provider selection changed after the compatibility preview. Review the switch again.");
        }
        _ = catalog.GetProvider(preview.Target.ProviderId).ReleaseChannels[preview.Target.ReleaseChannelId];
        if (preview.ConfigurationPath is not null
            && !string.Equals(
                HashConfiguration(preview.ConfigurationPath),
                preview.ConfigurationSha256,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Configuration changed after the compatibility preview. Review the switch again.");
        }

        var backupPath = BackupConfiguration(preview);
        try
        {
            selectionStore.Save(preview.Target);
        }
        catch (Exception switchException) when (
            switchException is IOException
                or UnauthorizedAccessException
                or InvalidDataException
                or InvalidOperationException)
        {
            try
            {
                RestoreSelection(preview.Source);
            }
            catch (Exception rollbackException) when (
                rollbackException is IOException
                    or UnauthorizedAccessException
                    or InvalidDataException
                    or InvalidOperationException)
            {
                throw new InvalidOperationException(
                    "Provider switch failed and launcher-state rollback also failed.",
                    new AggregateException(switchException, rollbackException));
            }
            throw new InvalidOperationException(
                "Provider switch failed; launcher selection was rolled back and configuration was not rewritten.",
                switchException);
        }
        return new(
            preview.Target,
            backupPath,
            $"Selected {preview.TargetDisplayName}. Restart the launcher before managing the mod or editing settings.");
    }

    private LauncherProviderSelectionResolution ResolveCurrent()
    {
        var resolution = LauncherProviderSelectionResolver.Resolve(catalog, selectionStore.Load());
        if (!resolution.IsResolved)
        {
            throw new InvalidDataException(resolution.Message);
        }
        return resolution;
    }

    private static System.Collections.ObjectModel.ReadOnlyCollection<LauncherProviderCompatibilityConcern> BuildConcerns(
        LauncherDistributionProvider source,
        LauncherDistributionProvider target)
    {
        var concerns = new List<LauncherProviderCompatibilityConcern>();
        foreach (var capabilityId in LauncherProviderCapabilityIds.ContractCapabilities)
        {
            var sourceStatus = source.GetCapabilityStatus(capabilityId);
            var targetStatus = target.GetCapabilityStatus(capabilityId);
            if (targetStatus == LauncherProviderCapabilityStatus.Unknown)
            {
                concerns.Add(
                    new(
                        capabilityId,
                        LauncherProviderCompatibilityKind.Unknown,
                        $"{capabilityId} is unknown for {target.DisplayName}; the launcher will not assume support."));
            }
            else if (sourceStatus == LauncherProviderCapabilityStatus.Supported
                     && targetStatus == LauncherProviderCapabilityStatus.Unsupported)
            {
                concerns.Add(
                    new(
                        capabilityId,
                        LauncherProviderCompatibilityKind.Loss,
                        $"{target.DisplayName} does not support {capabilityId}."));
            }
        }
        if (target.Migration.Status == LauncherProviderCapabilityStatus.Unknown
            || !target.Migration.CompatibleProviderIds.Contains(source.Id))
        {
            concerns.Add(
                new(
                    LauncherProviderCapabilityIds.ConfigurationMigration,
                    LauncherProviderCompatibilityKind.Unknown,
                    $"Configuration compatibility from {source.DisplayName} to {target.DisplayName} is unknown; TOML will be preserved without normalization."));
        }
        if (concerns.Count == 0)
        {
            concerns.Add(
                new(
                    LauncherProviderCapabilityIds.ConfigurationMigration,
                    LauncherProviderCompatibilityKind.Compatible,
                    "The target provider declares this source compatible and preserves unknown TOML."));
        }
        return concerns.AsReadOnly();
    }

    private string? BackupConfiguration(LauncherProviderSwitchPreview preview)
    {
        if (preview.ConfigurationPath is null)
        {
            return null;
        }
        var backupDirectory = Path.Combine(normalizedStateDirectory, "provider-switch-backups");
        Directory.CreateDirectory(backupDirectory);
        var backupPath = Path.Combine(backupDirectory, $"{preview.TransactionId}.toml");
        File.Copy(preview.ConfigurationPath, backupPath, overwrite: false);
        if (!string.Equals(HashConfiguration(backupPath), preview.ConfigurationSha256, StringComparison.Ordinal))
        {
            File.Delete(backupPath);
            throw new IOException("Provider-switch configuration backup verification failed.");
        }
        return backupPath;
    }

    private void RestoreSelection(LauncherProviderSelection source)
    {
        var defaultSelection = new LauncherProviderSelection(
            catalog.DefaultProviderId,
            catalog.DefaultProvider.DefaultReleaseChannelId);
        if (source == defaultSelection)
        {
            selectionStore.Clear();
        }
        else
        {
            selectionStore.Save(source);
        }
    }

    private static string? NormalizeOptionalConfigurationPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }
        var normalized = Path.GetFullPath(path);
        var length = new FileInfo(normalized).Length;
        if (length > MaximumConfigurationBytes)
        {
            throw new InvalidDataException(
                $"Configuration exceeds the {MaximumConfigurationBytes}-byte provider-switch limit.");
        }
        return normalized;
    }

    private static string HashConfiguration(string path)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length > MaximumConfigurationBytes)
        {
            throw new InvalidDataException(
                $"Configuration exceeds the {MaximumConfigurationBytes}-byte provider-switch limit.");
        }
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
