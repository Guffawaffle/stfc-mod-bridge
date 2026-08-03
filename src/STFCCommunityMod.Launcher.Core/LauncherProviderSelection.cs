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
    InvalidSelection,
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

public sealed record LauncherProviderShellAccess(
    bool CanUseProviderBoundModActions,
    bool CanEditProviderSettings,
    bool CanChangeProvider,
    string RestrictionReason)
{
    public static LauncherProviderShellAccess From(
        LauncherProviderSelectionResolution resolution)
    {
        ArgumentNullException.ThrowIfNull(resolution);
        return resolution.IsResolved
            ? new(true, true, true, string.Empty)
            : new(false, false, true, resolution.Message);
    }
}

public static class LauncherProviderSelectionResolver
{
    private static readonly LauncherProviderSelection InvalidPlaceholder =
        new("invalid", "invalid");

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

    public static LauncherProviderSelectionResolution Invalid(string reason) =>
        new(
            LauncherProviderSelectionResolutionState.InvalidSelection,
            InvalidPlaceholder,
            null,
            null,
            $"The persisted provider selection is unreadable: {reason}");
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
    LauncherProviderSelectionResolutionState SourceResolutionState,
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

public sealed class LauncherProviderSourceSwitchService
{
    private const long MaximumConfigurationBytes = 8 * 1024 * 1024;
    private readonly LauncherDistributionProviderCatalog catalog;
    private readonly ILauncherProviderSelectionStore selectionStore;
    private readonly string normalizedStateDirectory;
    private readonly Action<string?>? backupCompleted;

    public LauncherProviderSourceSwitchService(
        LauncherDistributionProviderCatalog catalog,
        ILauncherProviderSelectionStore selectionStore,
        string stateDirectory)
        : this(catalog, selectionStore, stateDirectory, null)
    {
    }

    internal LauncherProviderSourceSwitchService(
        LauncherDistributionProviderCatalog catalog,
        ILauncherProviderSelectionStore selectionStore,
        string stateDirectory,
        Action<string?>? backupCompleted)
    {
        this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        this.selectionStore = selectionStore ?? throw new ArgumentNullException(nameof(selectionStore));
        normalizedStateDirectory = Path.GetFullPath(stateDirectory);
        this.backupCompleted = backupCompleted;
    }

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
        if (current.IsResolved && target == current.Selection)
        {
            throw new InvalidOperationException("The requested provider and release channel are already selected.");
        }

        var normalizedConfigurationPath = NormalizeOptionalConfigurationPath(configurationPath);
        var configurationSha256 = normalizedConfigurationPath is null
            ? null
            : HashConfiguration(normalizedConfigurationPath);
        var concerns = BuildConcerns(current.Provider, targetProvider, current.Message);
        return new(
            Guid.NewGuid().ToString("N"),
            current.State,
            current.Selection,
            target,
            current.Provider?.DisplayName ?? current.Selection.ProviderId,
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
        if (current.State != preview.SourceResolutionState
            || current.Selection != preview.Source)
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
        backupCompleted?.Invoke(backupPath);
        if (preview.ConfigurationPath is not null
            && !string.Equals(
                HashConfiguration(preview.ConfigurationPath),
                preview.ConfigurationSha256,
                StringComparison.Ordinal))
        {
            if (backupPath is not null && File.Exists(backupPath))
            {
                File.Delete(backupPath);
            }
            throw new InvalidOperationException(
                "Configuration changed while its provider-switch backup was being prepared. Review the switch again.");
        }
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
                RestoreSelection(preview.SourceResolutionState, preview.Source);
            }
            catch (Exception rollbackException) when (
                rollbackException is IOException
                    or UnauthorizedAccessException
                    or InvalidDataException
                    or InvalidOperationException)
            {
                throw new InvalidOperationException(
                    "Provider switch failed and Mod Bridge state rollback also failed.",
                    new AggregateException(switchException, rollbackException));
            }
            throw new InvalidOperationException(
                "Provider switch failed; the Mod Bridge selection was rolled back and configuration was not rewritten.",
                switchException);
        }
        return new(
            preview.Target,
            backupPath,
            $"Selected {preview.TargetDisplayName}. Restart Mod Bridge before managing the mod or editing settings.");
    }

    private LauncherProviderSelectionResolution ResolveCurrent()
    {
        try
        {
            return LauncherProviderSelectionResolver.Resolve(catalog, selectionStore.Load());
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidDataException
                or JsonException
                or NotSupportedException)
        {
            return LauncherProviderSelectionResolver.Invalid(exception.Message);
        }
    }

    private static System.Collections.ObjectModel.ReadOnlyCollection<LauncherProviderCompatibilityConcern> BuildConcerns(
        LauncherDistributionProvider? source,
        LauncherDistributionProvider target,
        string sourceResolutionMessage)
    {
        var concerns = new List<LauncherProviderCompatibilityConcern>();
        if (source is null)
        {
            concerns.Add(
                new(
                    LauncherProviderCapabilityIds.ConfigurationMigration,
                    LauncherProviderCompatibilityKind.Unknown,
                    $"The current source cannot be resolved. {sourceResolutionMessage}"));
        }
        foreach (var capabilityId in LauncherProviderCapabilityIds.ContractCapabilities)
        {
            var sourceStatus = source?.GetCapabilityStatus(capabilityId)
                ?? LauncherProviderCapabilityStatus.Unknown;
            var targetStatus = target.GetCapabilityStatus(capabilityId);
            if (targetStatus == LauncherProviderCapabilityStatus.Unknown)
            {
                concerns.Add(
                    new(
                        capabilityId,
                        LauncherProviderCompatibilityKind.Unknown,
                        $"{capabilityId} is unknown for {target.DisplayName}; Mod Bridge will not assume support."));
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
        if (source is null
            || target.Migration.Status == LauncherProviderCapabilityStatus.Unknown
            || !target.Migration.CompatibleProviderIds.Contains(source.Id))
        {
            var sourceName = source?.DisplayName ?? "the unresolved source";
            concerns.Add(
                new(
                    LauncherProviderCapabilityIds.ConfigurationMigration,
                    LauncherProviderCompatibilityKind.Unknown,
                    $"Configuration compatibility from {sourceName} to {target.DisplayName} is unknown; TOML will be preserved without normalization."));
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

    private void RestoreSelection(
        LauncherProviderSelectionResolutionState sourceState,
        LauncherProviderSelection source)
    {
        if (sourceState == LauncherProviderSelectionResolutionState.InvalidSelection)
        {
            // JsonLauncherProviderSelectionStore commits with replace/move. An
            // exception cannot follow a successful replacement, so the corrupt
            // source remains authoritative and must not be approximated.
            return;
        }
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
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }
        var normalized = Path.GetFullPath(path);
        if (!File.Exists(normalized))
        {
            throw new FileNotFoundException(
                "The explicitly selected configuration file does not exist.",
                normalized);
        }
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
