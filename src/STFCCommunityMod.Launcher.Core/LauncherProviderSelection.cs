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
    Warning,
}

public sealed record LauncherProviderCompatibilityConcern(
    string CapabilityId,
    LauncherProviderCompatibilityKind Kind,
    string Message);

public enum LauncherProviderSwitchConfigurationKind
{
    None,
    PreserveCurrent,
    RestoreProviderHistory,
}

public sealed record LauncherProviderSwitchConfigurationAnalysis(
    LauncherProviderSelection Selection,
    ConfigurationDiagnosisBinding Binding,
    LauncherProviderCapabilityStatus CatalogStatus,
    LauncherConfigurationCatalogIdentity? CatalogIdentity,
    IReadOnlyDictionary<string, int> FindingCounts,
    int AttentionFindingCount,
    IReadOnlyList<string> BlockingFindingCodes);

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
    LauncherProviderSwitchConfigurationKind ConfigurationKind,
    string? TargetConfigurationBackupId,
    string? TargetConfigurationSha256,
    string ConfirmationText,
    LauncherProviderSwitchConfigurationAnalysis? SourceConfigurationAnalysis = null,
    LauncherProviderSwitchConfigurationAnalysis? TargetConfigurationAnalysis = null)
{
    public bool HasCompatibilityLoss =>
        Concerns.Any(concern => concern.Kind == LauncherProviderCompatibilityKind.Loss);

    public bool HasUnknownCompatibility =>
        Concerns.Any(concern => concern.Kind == LauncherProviderCompatibilityKind.Unknown);
}

public sealed record LauncherProviderSwitchResult(
    LauncherProviderSelection Selection,
    ConfigurationBackupReceipt? ConfigurationBackup,
    string Message);

internal sealed record PreparedLauncherProviderSwitch(
    LauncherProviderSwitchPreview Preview,
    ConfigurationBackupReceipt? ConfigurationBackup,
    byte[]? TargetConfiguration);

public sealed class LauncherProviderSourceSwitchService
{
    private const long MaximumConfigurationBytes = 8 * 1024 * 1024;
    private readonly LauncherDistributionProviderCatalog catalog;
    private readonly ILauncherProviderSelectionStore selectionStore;
    private readonly ProviderScopedConfigurationBackupStore backupStore;
    private readonly Action<ConfigurationBackupReceipt?>? backupCompleted;
    private readonly Func<LauncherProviderSelection, LauncherConfigurationDiagnosisEvidence>?
        configurationEvidenceResolver;

    public LauncherProviderSourceSwitchService(
        LauncherDistributionProviderCatalog catalog,
        ILauncherProviderSelectionStore selectionStore,
        string stateDirectory,
        Func<LauncherProviderSelection, LauncherConfigurationDiagnosisEvidence>?
            configurationEvidenceResolver = null)
        : this(
            catalog,
            selectionStore,
            new ProviderScopedConfigurationBackupStore(stateDirectory),
            null,
            configurationEvidenceResolver)
    {
    }

    internal LauncherProviderSourceSwitchService(
        LauncherDistributionProviderCatalog catalog,
        ILauncherProviderSelectionStore selectionStore,
        ProviderScopedConfigurationBackupStore backupStore,
        Action<ConfigurationBackupReceipt?>? backupCompleted,
        Func<LauncherProviderSelection, LauncherConfigurationDiagnosisEvidence>?
            configurationEvidenceResolver = null)
    {
        this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        this.selectionStore = selectionStore ?? throw new ArgumentNullException(nameof(selectionStore));
        this.backupStore = backupStore ?? throw new ArgumentNullException(nameof(backupStore));
        this.backupCompleted = backupCompleted;
        this.configurationEvidenceResolver = configurationEvidenceResolver;
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
        var sourceConfiguration = normalizedConfigurationPath is null
            ? null
            : ReadConfiguration(normalizedConfigurationPath);
        var configurationSha256 = sourceConfiguration is null
            ? null
            : ConfigurationDocumentRevision.FromContents(sourceConfiguration).Sha256;
        ConfigurationBackupReceipt? targetBackup = null;
        byte[]? targetConfiguration = null;
        if (normalizedConfigurationPath is not null)
        {
            var targetBackups = backupStore.List(
                Path.GetDirectoryName(normalizedConfigurationPath)!,
                target.ProviderId);
            targetBackup = targetBackups.Count == 0 ? null : targetBackups[0];
            if (targetBackup is not null)
            {
                targetConfiguration = backupStore.Read(
                    Path.GetDirectoryName(normalizedConfigurationPath)!,
                    target.ProviderId,
                    targetBackup.BackupId);
                if (!string.Equals(
                        ConfigurationDocumentRevision.FromContents(targetConfiguration).Sha256,
                        targetBackup.ContentSha256,
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        "The target provider configuration backup no longer matches its protected receipt.");
                }
            }
        }
        var configurationKind = normalizedConfigurationPath is null
            ? LauncherProviderSwitchConfigurationKind.None
            : targetBackup is null
                ? LauncherProviderSwitchConfigurationKind.PreserveCurrent
                : LauncherProviderSwitchConfigurationKind.RestoreProviderHistory;
        LauncherProviderSwitchConfigurationAnalysis? sourceAnalysis = null;
        LauncherProviderSwitchConfigurationAnalysis? targetAnalysis = null;
        if (normalizedConfigurationPath is not null && sourceConfiguration is not null)
        {
            sourceAnalysis = AnalyzeConfiguration(
                current.Selection,
                normalizedConfigurationPath,
                sourceConfiguration);
            targetConfiguration ??= sourceConfiguration;
            EnsureTargetParserSafe(targetConfiguration, isInitialPreview: true);
            targetAnalysis = AnalyzeConfiguration(
                target,
                normalizedConfigurationPath,
                targetConfiguration);
            EnsureTargetCatalogSafe(targetAnalysis, isInitialPreview: true);
        }
        var concerns = BuildConcerns(
            current.Provider,
            targetProvider,
            current.Message,
            targetAnalysis);
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
            configurationKind,
            targetBackup?.BackupId,
            targetBackup?.ContentSha256,
            targetProvider.Id,
            sourceAnalysis,
            targetAnalysis);
    }

    public async Task<LauncherProviderSwitchResult> ExecuteAsync(
        LauncherProviderSwitchPreview preview,
        string confirmationText,
        CancellationToken cancellationToken = default)
    {
        var prepared = await PrepareAsync(
            preview,
            confirmationText,
            cancellationToken).ConfigureAwait(false);
        return await CommitAsync(prepared, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<PreparedLauncherProviderSwitch> PrepareAsync(
        LauncherProviderSwitchPreview preview,
        string confirmationText,
        CancellationToken cancellationToken)
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
        var targetProvider = catalog.GetProvider(preview.Target.ProviderId);
        _ = targetProvider.ReleaseChannels[preview.Target.ReleaseChannelId];
        byte[]? sourceConfiguration = null;
        if (preview.ConfigurationPath is not null)
        {
            sourceConfiguration = ReadConfiguration(preview.ConfigurationPath);
            if (!string.Equals(
                    ConfigurationDocumentRevision.FromContents(sourceConfiguration).Sha256,
                    preview.ConfigurationSha256,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Configuration changed after the compatibility preview. Review the switch again.");
            }
        }

        byte[]? targetConfiguration = null;
        if (preview.ConfigurationKind == LauncherProviderSwitchConfigurationKind.RestoreProviderHistory)
        {
            if (preview.ConfigurationPath is null
                || preview.TargetConfigurationBackupId is null
                || preview.TargetConfigurationSha256 is null)
            {
                throw new InvalidDataException(
                    "The provider-switch configuration proposal is incomplete.");
            }
            targetConfiguration = backupStore.Read(
                Path.GetDirectoryName(preview.ConfigurationPath)!,
                preview.Target.ProviderId,
                preview.TargetConfigurationBackupId);
            if (!string.Equals(
                    Convert.ToHexString(SHA256.HashData(targetConfiguration)),
                    preview.TargetConfigurationSha256,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "The target provider configuration backup changed after review.");
            }
        }

        if (preview.ConfigurationPath is not null && sourceConfiguration is not null)
        {
            var currentSourceAnalysis = AnalyzeConfiguration(
                preview.Source,
                preview.ConfigurationPath,
                sourceConfiguration);
            var proposedTargetConfiguration = targetConfiguration ?? sourceConfiguration;
            EnsureTargetParserSafe(proposedTargetConfiguration, isInitialPreview: false);
            var currentTargetAnalysis = AnalyzeConfiguration(
                preview.Target,
                preview.ConfigurationPath,
                proposedTargetConfiguration);
            EnsureTargetCatalogSafe(currentTargetAnalysis, isInitialPreview: false);
            VerifyAnalysisBinding(
                preview.SourceConfigurationAnalysis,
                currentSourceAnalysis,
                "source");
            VerifyAnalysisBinding(
                preview.TargetConfigurationAnalysis,
                currentTargetAnalysis,
                "target");
            VerifyConcerns(
                preview.Concerns,
                BuildConcerns(current.Provider, targetProvider, current.Message, currentTargetAnalysis));
        }

        var backup = await BackupConfigurationAsync(
            preview,
            sourceConfiguration,
            cancellationToken).ConfigureAwait(false);
        backupCompleted?.Invoke(backup);
        if (preview.ConfigurationPath is not null
            && !string.Equals(
                HashConfiguration(preview.ConfigurationPath),
                preview.ConfigurationSha256,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Configuration changed while its provider-switch backup was being prepared. Review the switch again.");
        }

        return new(preview, backup, targetConfiguration);
    }

    internal async Task<LauncherProviderSwitchResult> CommitAsync(
        PreparedLauncherProviderSwitch prepared,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        var preview = prepared.Preview;
        var current = ResolveCurrent();
        if (current.State != preview.SourceResolutionState
            || current.Selection != preview.Source)
        {
            throw new InvalidOperationException(
                "Provider selection changed after the switch was prepared. The switch was not committed.");
        }
        var targetProvider = catalog.GetProvider(preview.Target.ProviderId);
        _ = targetProvider.ReleaseChannels[preview.Target.ReleaseChannelId];
        byte[]? sourceConfiguration = null;
        if (preview.ConfigurationPath is not null)
        {
            sourceConfiguration = ReadConfiguration(preview.ConfigurationPath);
            if (!string.Equals(
                    ConfigurationDocumentRevision.FromContents(sourceConfiguration).Sha256,
                    preview.ConfigurationSha256,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Configuration changed after the switch was prepared. The switch was not committed.");
            }
            var currentSourceAnalysis = AnalyzeConfiguration(
                preview.Source,
                preview.ConfigurationPath,
                sourceConfiguration);
            var proposedTargetConfiguration = prepared.TargetConfiguration ?? sourceConfiguration;
            EnsureTargetParserSafe(proposedTargetConfiguration, isInitialPreview: false);
            var currentTargetAnalysis = AnalyzeConfiguration(
                preview.Target,
                preview.ConfigurationPath,
                proposedTargetConfiguration);
            EnsureTargetCatalogSafe(currentTargetAnalysis, isInitialPreview: false);
            VerifyAnalysisBinding(
                preview.SourceConfigurationAnalysis,
                currentSourceAnalysis,
                "source");
            VerifyAnalysisBinding(
                preview.TargetConfigurationAnalysis,
                currentTargetAnalysis,
                "target");
            VerifyConcerns(
                preview.Concerns,
                BuildConcerns(current.Provider, targetProvider, current.Message, currentTargetAnalysis));
        }

        if (preview.ConfigurationPath is not null && prepared.TargetConfiguration is not null)
        {
            var configurationWrite = await new AtomicTomlStore(retainAdjacentBackup: false)
                .SaveDocumentAsync(
                    preview.ConfigurationPath,
                    sourceConfiguration!,
                    prepared.TargetConfiguration!,
                    cancellationToken).ConfigureAwait(false);
            if (!configurationWrite.IsSuccess)
            {
                throw new IOException(
                    configurationWrite.Error
                        ?? "The target provider configuration could not be restored.");
            }
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
                await RollBackAsync(prepared, CancellationToken.None).ConfigureAwait(false);
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
            prepared.ConfigurationBackup,
            preview.ConfigurationKind == LauncherProviderSwitchConfigurationKind.RestoreProviderHistory
                ? $"Selected {preview.TargetDisplayName} and restored its protected TOML history."
                : $"Selected {preview.TargetDisplayName}.");
    }

    internal async Task RollBackAsync(
        PreparedLauncherProviderSwitch prepared,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        var preview = prepared.Preview;
        if (preview.ConfigurationPath is not null && prepared.ConfigurationBackup is not null)
        {
            var gameDirectory = Path.GetDirectoryName(preview.ConfigurationPath)
                ?? throw new InvalidDataException("The configuration path has no game directory.");
            var sourceConfiguration = backupStore.Read(
                gameDirectory,
                prepared.ConfigurationBackup.ProviderId,
                prepared.ConfigurationBackup.BackupId);
            var currentConfiguration = await File.ReadAllBytesAsync(
                preview.ConfigurationPath,
                cancellationToken).ConfigureAwait(false);
            if (!currentConfiguration.AsSpan().SequenceEqual(sourceConfiguration))
            {
                var rollback = await new AtomicTomlStore(retainAdjacentBackup: false)
                    .SaveDocumentAsync(
                        preview.ConfigurationPath,
                        currentConfiguration,
                        sourceConfiguration,
                        cancellationToken).ConfigureAwait(false);
                if (!rollback.IsSuccess)
                {
                    throw new IOException(rollback.Error ?? "Configuration rollback failed.");
                }
            }
        }
        RestoreSelection(preview.SourceResolutionState, preview.Source);
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
        string sourceResolutionMessage,
        LauncherProviderSwitchConfigurationAnalysis? targetAnalysis)
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
            if (capabilityId is LauncherProviderCapabilityIds.ConfigurationCatalog
                or LauncherProviderCapabilityIds.ConfigurationMigration)
            {
                continue;
            }
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

        if (targetAnalysis is not null)
        {
            AddConfigurationConcerns(concerns, target, targetAnalysis);
        }
        if (concerns.Count == 0)
        {
            concerns.Add(
                new(
                    LauncherProviderCapabilityIds.ConfigurationMigration,
                    LauncherProviderCompatibilityKind.Compatible,
                    "No selected TOML requires provider-specific compatibility analysis."));
        }
        return concerns.AsReadOnly();
    }

    private static void AddConfigurationConcerns(
        List<LauncherProviderCompatibilityConcern> concerns,
        LauncherDistributionProvider target,
        LauncherProviderSwitchConfigurationAnalysis analysis)
    {
        if (analysis.CatalogStatus != LauncherProviderCapabilityStatus.Supported
            || analysis.CatalogIdentity is null)
        {
            concerns.Add(
                new(
                    LauncherProviderCapabilityIds.ConfigurationCatalog,
                    LauncherProviderCompatibilityKind.Warning,
                    $"No exact {target.DisplayName} configuration catalog is available for "
                    + $"'{analysis.Selection.ReleaseChannelId}'. The proposed TOML bytes will be preserved "
                    + "without guessing provider-specific meaning."));
            return;
        }

        var identity = analysis.CatalogIdentity;
        concerns.Add(
            new(
                LauncherProviderCapabilityIds.ConfigurationCatalog,
                LauncherProviderCompatibilityKind.Compatible,
                $"The proposed TOML revision was analyzed with {identity.CatalogId} "
                + $"v{identity.CatalogVersion} for {analysis.Selection.ProviderId}/"
                + $"{analysis.Selection.ReleaseChannelId}."));

        var unknownContentCount = CountFindings(
            analysis,
            "CONFIG_UNKNOWN_KEY",
            "CONFIG_UNKNOWN_TABLE");
        if (unknownContentCount > 0)
        {
            concerns.Add(
                new(
                    LauncherProviderCapabilityIds.ConfigurationMigration,
                    LauncherProviderCompatibilityKind.Warning,
                    $"The target catalog does not recognize {unknownContentCount} TOML "
                    + "item(s). Their exact bytes will be preserved; Mod Bridge will not normalize or remove them."));
        }

        var runtimeLimitedCount = CountFindings(
            analysis,
            "CONFIG_SETTING_PARSED_UNUSED",
            "CONFIG_SETTING_IGNORED",
            "CONFIG_SETTING_LEGACY",
            "CONFIG_SETTING_REMOVED");
        if (runtimeLimitedCount > 0)
        {
            concerns.Add(
                new(
                    LauncherProviderCapabilityIds.ConfigurationMigration,
                    LauncherProviderCompatibilityKind.Loss,
                    $"The target catalog identifies {runtimeLimitedCount} configured setting(s) "
                    + "that are ignored, removed, legacy, or parsed without runtime effect. Their bytes will remain intact."));
        }

        var otherAttentionCount = Math.Max(
            0,
            analysis.AttentionFindingCount - runtimeLimitedCount);
        if (otherAttentionCount > 0)
        {
            concerns.Add(
                new(
                    LauncherProviderCapabilityIds.ConfigurationMigration,
                    LauncherProviderCompatibilityKind.Warning,
                    $"The target catalog reports {otherAttentionCount} compatibility condition(s). "
                    + "The proposed TOML remains byte-preserving until you explicitly edit it."));
        }
    }

    private static int CountFindings(
        LauncherProviderSwitchConfigurationAnalysis analysis,
        params string[] codes) =>
        codes.Sum(code => analysis.FindingCounts.GetValueOrDefault(code));

    private LauncherProviderSwitchConfigurationAnalysis AnalyzeConfiguration(
        LauncherProviderSelection selection,
        string configurationPath,
        byte[] contents)
    {
        var evidence = ResolveConfigurationEvidence(selection);
        if (!string.Equals(evidence.ProviderId, selection.ProviderId, StringComparison.Ordinal)
            || !string.Equals(evidence.ChannelId, selection.ReleaseChannelId, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Configuration diagnosis evidence is bound to a different provider or release channel.");
        }
        if (evidence.Catalog is not null
            && !string.Equals(
                evidence.Catalog.Identity.TrackId,
                "unversioned",
                StringComparison.Ordinal)
            && !string.Equals(
                evidence.Catalog.Identity.TrackId,
                selection.ReleaseChannelId,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Configuration diagnosis evidence is bound to a different release track.");
        }

        var report = new ConfigurationHealthAnalyzer().Analyze(
            new ConfigurationDocumentSnapshot(configurationPath, contents),
            evidence);
        var findingCounts = report.Findings
            .GroupBy(finding => finding.Code, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var blockingFindingCodes = report.Findings
            .Where(finding =>
                finding.Confidence == ConfigurationDiagnosisConfidence.Established
                && finding.Severity is ConfigurationDiagnosisSeverity.Error
                    or ConfigurationDiagnosisSeverity.Unknown)
            .Select(finding => finding.Code)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(code => code, StringComparer.Ordinal)
            .ToArray();
        return new(
            selection,
            report.Binding,
            evidence.CapabilityStatus,
            evidence.Catalog?.Identity,
            findingCounts,
            report.Findings.Count(finding =>
                finding.Severity == ConfigurationDiagnosisSeverity.Attention),
            blockingFindingCodes);
    }

    private LauncherConfigurationDiagnosisEvidence ResolveConfigurationEvidence(
        LauncherProviderSelection selection)
    {
        if (configurationEvidenceResolver is not null)
        {
            return configurationEvidenceResolver(selection)
                ?? throw new InvalidDataException(
                    "The configuration evidence resolver returned no provider evidence.");
        }

        var capabilityStatus = catalog.TryGetProvider(selection.ProviderId, out var provider)
            && provider is not null
                ? provider.GetCapabilityStatus(LauncherProviderCapabilityIds.ConfigurationCatalog)
                : LauncherProviderCapabilityStatus.Unknown;
        return LauncherConfigurationDiagnosisEvidence.Unavailable(
            selection.ProviderId,
            selection.ReleaseChannelId,
            capabilityStatus == LauncherProviderCapabilityStatus.Unsupported
                ? LauncherProviderCapabilityStatus.Unsupported
                : LauncherProviderCapabilityStatus.Unknown);
    }

    private static void EnsureTargetParserSafe(byte[] contents, bool isInitialPreview)
    {
        var load = SparseTomlDocument.Load(contents, out var document);
        var read = load.IsValid && document is not null
            ? document.ReadOverrides()
            : null;
        if (!load.IsValid || document is null || read is null || !read.IsValid)
        {
            throw new InvalidDataException(
                "The proposed target configuration cannot be read safely by the conservative TOML parser."
                + (isInitialPreview
                    ? " No provider-switch backup, download, or mutation was started."
                    : " The target configuration and provider selection were not committed; review the switch again."));
        }
    }

    private static void EnsureTargetCatalogSafe(
        LauncherProviderSwitchConfigurationAnalysis analysis,
        bool isInitialPreview)
    {
        if (analysis.CatalogStatus != LauncherProviderCapabilityStatus.Supported)
        {
            return;
        }
        var blockingCodes = analysis.BlockingFindingCodes;
        if (blockingCodes.Count == 0)
        {
            return;
        }

        throw new InvalidOperationException(
            $"The proposed target configuration is unsafe for {analysis.Selection.ProviderId}/"
            + $"{analysis.Selection.ReleaseChannelId} under its exact catalog "
            + $"({string.Join(", ", blockingCodes)})."
            + (isInitialPreview
                ? " No provider-switch backup, download, or mutation was started."
                : " The target configuration and provider selection were not committed; review the switch again."));
    }

    private static void VerifyAnalysisBinding(
        LauncherProviderSwitchConfigurationAnalysis? expected,
        LauncherProviderSwitchConfigurationAnalysis actual,
        string role)
    {
        if (expected is null
            || expected.Selection != actual.Selection
            || expected.Binding.Revision != actual.Binding.Revision
            || !string.Equals(
                expected.Binding.ProviderId,
                actual.Binding.ProviderId,
                StringComparison.Ordinal)
            || !string.Equals(
                expected.Binding.ChannelId,
                actual.Binding.ChannelId,
                StringComparison.Ordinal)
            || !string.Equals(
                expected.Binding.CatalogId,
                actual.Binding.CatalogId,
                StringComparison.Ordinal)
            || !string.Equals(
                expected.Binding.CatalogVersion,
                actual.Binding.CatalogVersion,
                StringComparison.Ordinal)
            || expected.CatalogStatus != actual.CatalogStatus
            || expected.CatalogIdentity != actual.CatalogIdentity
            || expected.AttentionFindingCount != actual.AttentionFindingCount
            || !DictionaryEqual(expected.FindingCounts, actual.FindingCounts)
            || !expected.BlockingFindingCodes.SequenceEqual(
                actual.BlockingFindingCodes,
                StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                $"The {role} configuration catalog or TOML revision changed after review. "
                + "Review the provider switch again.");
        }
    }

    private static bool DictionaryEqual(
        IReadOnlyDictionary<string, int> left,
        IReadOnlyDictionary<string, int> right) =>
        left.Count == right.Count
        && left.All(pair =>
            right.TryGetValue(pair.Key, out var value) && value == pair.Value);

    private static void VerifyConcerns(
        IReadOnlyList<LauncherProviderCompatibilityConcern> expected,
        IReadOnlyList<LauncherProviderCompatibilityConcern> actual)
    {
        if (!expected.SequenceEqual(actual))
        {
            throw new InvalidOperationException(
                "The provider-switch compatibility findings changed after review. "
                + "Review the provider switch again.");
        }
    }

    private async Task<ConfigurationBackupReceipt?> BackupConfigurationAsync(
        LauncherProviderSwitchPreview preview,
        byte[]? sourceConfiguration,
        CancellationToken cancellationToken)
    {
        if (preview.ConfigurationPath is null)
        {
            return null;
        }
        var gameDirectory = Path.GetDirectoryName(preview.ConfigurationPath)
            ?? throw new InvalidDataException("The configuration path has no game directory.");
        if (sourceConfiguration is null)
        {
            throw new InvalidDataException(
                "The provider-switch source configuration was not retained for its exact backup.");
        }
        return await backupStore.CreateAsync(
            new(
                gameDirectory,
                preview.Source.ProviderId,
                preview.ConfigurationPath,
                sourceConfiguration,
                "provider-switch",
                preview.Target.ProviderId,
                $"{preview.Source.ProviderId}/{preview.Source.ReleaseChannelId}"),
            cancellationToken).ConfigureAwait(false);
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
        return ConfigurationDocumentRevision.FromContents(ReadConfiguration(path)).Sha256;
    }

    private static byte[] ReadConfiguration(string path)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length > MaximumConfigurationBytes)
        {
            throw new InvalidDataException(
                $"Configuration exceeds the {MaximumConfigurationBytes}-byte provider-switch limit.");
        }
        using var buffer = new MemoryStream((int)stream.Length);
        stream.CopyTo(buffer);
        if (buffer.Length > MaximumConfigurationBytes)
        {
            throw new InvalidDataException(
                $"Configuration exceeds the {MaximumConfigurationBytes}-byte provider-switch limit.");
        }
        return buffer.ToArray();
    }
}
