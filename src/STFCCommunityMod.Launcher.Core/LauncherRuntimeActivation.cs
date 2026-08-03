using System.Collections.Frozen;
using System.Collections.ObjectModel;
using System.Text.Json;

namespace STFCCommunityMod.Launcher.Core;

public static class LauncherCapabilityIds
{
    public const string PrincipalSettingsTaxonomyV1 =
        "settings.principal-taxonomy.v1";
}

public static class LauncherFeatureIds
{
    public const string SemanticSettingsGrouping =
        "settings.semantic-grouping";
}

public static class LauncherFeatureImplementations
{
    public const string PrincipalCatalogSettingsLayout =
        "principal-catalog-settings-layout";

    public const string AlphabeticalSettingsLayout =
        "alphabetical-settings-layout";
}

public sealed record LauncherRuntimeDetectionEvidence(
    string Source,
    string Detail);

public sealed record LauncherSettingsCatalogIdentity(
    int SchemaVersion,
    string Revision);

public sealed class LauncherRuntimeProfile
{
    private readonly FrozenSet<string> capabilities;
    private readonly ReadOnlyCollection<LauncherRuntimeDetectionEvidence> evidence;

    public LauncherRuntimeProfile(
        string distributionId,
        Version? runtimeVersion,
        string? sourceRevision,
        LauncherSettingsCatalogIdentity? settingsCatalog,
        IEnumerable<string> capabilities,
        IEnumerable<LauncherRuntimeDetectionEvidence> evidence)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(distributionId);
        ArgumentNullException.ThrowIfNull(capabilities);
        ArgumentNullException.ThrowIfNull(evidence);

        DistributionId = distributionId;
        RuntimeVersion = runtimeVersion;
        SourceRevision = sourceRevision;
        SettingsCatalog = settingsCatalog;
        this.capabilities = capabilities.ToFrozenSet(StringComparer.Ordinal);
        this.evidence = Array.AsReadOnly(evidence.ToArray());
    }

    public string DistributionId { get; }

    public string DistributionDisplayName =>
        DistributionId switch
        {
            LauncherRuntimeManifestDetector.GuffawaffleDistributionId => "Guffawaffle",
            LauncherRuntimeManifestDetector.NetnivDistributionId => "NetniV",
            LauncherRuntimeManifestDetector.UnknownDistributionId => "Unknown",
            _ => DistributionId,
        };

    public Version? RuntimeVersion { get; }

    public string? SourceRevision { get; }

    public LauncherSettingsCatalogIdentity? SettingsCatalog { get; }

    public IReadOnlySet<string> Capabilities => capabilities;

    public IReadOnlyList<LauncherRuntimeDetectionEvidence> Evidence => evidence;

    public bool HasCapability(string capabilityId) =>
        capabilities.Contains(capabilityId);

    public static LauncherRuntimeProfile Unknown(
        string source,
        string reason) =>
        new(
            LauncherRuntimeManifestDetector.UnknownDistributionId,
            null,
            null,
            null,
            [],
            [new(source, reason)]);
}

public static class LauncherRuntimeManifestDetector
{
    public const string GuffawaffleDistributionId =
        "guffawaffle.stfc-community-mod";

    public const string NetnivDistributionId =
        "netniv.stfc-community-mod";

    public const string UnknownDistributionId = "unknown";

    public const int SupportedManifestSchema = 1;

    public const int SupportedSettingsCatalogSchema = 1;

    public static LauncherRuntimeProfile Detect(
        Stream? manifest,
        string evidenceSource)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(evidenceSource);
        if (manifest is null)
        {
            return LauncherRuntimeProfile.Unknown(
                evidenceSource,
                "No runtime manifest was available. Capabilities were not inferred.");
        }

        if (!manifest.CanRead)
        {
            return LauncherRuntimeProfile.Unknown(
                evidenceSource,
                "The runtime manifest could not be read. Capabilities were not inferred.");
        }

        try
        {
            using var document = JsonDocument.Parse(
                manifest,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 16,
                });
            var root = document.RootElement;
            RequireKind(root, JsonValueKind.Object, "runtime manifest");

            var manifestSchema = ReadRequiredInt32(root, "manifestSchema", "runtime manifest");
            if (manifestSchema != SupportedManifestSchema)
            {
                return LauncherRuntimeProfile.Unknown(
                    evidenceSource,
                    $"Runtime manifest schema {manifestSchema} is unsupported. "
                    + $"Mod Bridge supports schema {SupportedManifestSchema}.");
            }

            var distributionId = ReadRequiredString(root, "distributionId", "runtime manifest");
            var runtimeVersionText = ReadRequiredString(root, "runtimeVersion", "runtime manifest");
            if (!Version.TryParse(runtimeVersionText, out var runtimeVersion))
            {
                throw new InvalidDataException(
                    $"runtime manifest property 'runtimeVersion' is not a numeric version.");
            }

            var sourceRevision = ReadRequiredString(root, "sourceRevision", "runtime manifest");
            var declaredCapabilities = ReadCapabilities(root);
            var settingsCatalog = ReadSettingsCatalog(root);
            var acceptedCapabilities = new HashSet<string>(
                declaredCapabilities,
                StringComparer.Ordinal);
            var evidence = new List<LauncherRuntimeDetectionEvidence>
            {
                new(
                    evidenceSource,
                    $"Positively identified distribution '{distributionId}' "
                    + $"from runtime manifest schema {manifestSchema}."),
            };

            if (settingsCatalog.SchemaVersion != SupportedSettingsCatalogSchema)
            {
                acceptedCapabilities.Remove(
                    LauncherCapabilityIds.PrincipalSettingsTaxonomyV1);
                evidence.Add(
                    new(
                        evidenceSource,
                        $"Settings catalog schema {settingsCatalog.SchemaVersion} is unsupported; "
                        + $"{LauncherCapabilityIds.PrincipalSettingsTaxonomyV1} was withheld."));
            }

            return new(
                distributionId,
                runtimeVersion,
                sourceRevision,
                settingsCatalog,
                acceptedCapabilities,
                evidence);
        }
        catch (Exception exception) when (
            exception is JsonException
                or InvalidDataException
                or OverflowException)
        {
            return LauncherRuntimeProfile.Unknown(
                evidenceSource,
                $"The runtime manifest was invalid: {exception.Message}");
        }
    }

    private static List<string> ReadCapabilities(JsonElement root)
    {
        var element = ReadRequiredProperty(root, "capabilities", "runtime manifest");
        RequireKind(element, JsonValueKind.Array, "runtime manifest capabilities");

        var capabilities = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(item.GetString()))
            {
                throw new InvalidDataException(
                    "runtime manifest capabilities must be non-empty strings.");
            }

            var capability = item.GetString()!;
            if (!seen.Add(capability))
            {
                throw new InvalidDataException(
                    $"runtime manifest capability '{capability}' is duplicated.");
            }

            capabilities.Add(capability);
        }

        return capabilities;
    }

    private static LauncherSettingsCatalogIdentity ReadSettingsCatalog(
        JsonElement root)
    {
        var element = ReadRequiredProperty(root, "settingsCatalog", "runtime manifest");
        RequireKind(element, JsonValueKind.Object, "runtime manifest settingsCatalog");
        return new(
            ReadRequiredInt32(element, "schemaVersion", "runtime manifest settingsCatalog"),
            ReadRequiredString(element, "revision", "runtime manifest settingsCatalog"));
    }

    private static JsonElement ReadRequiredProperty(
        JsonElement element,
        string propertyName,
        string context)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            throw new InvalidDataException(
                $"{context} is missing required property '{propertyName}'.");
        }

        return property;
    }

    private static string ReadRequiredString(
        JsonElement element,
        string propertyName,
        string context)
    {
        var property = ReadRequiredProperty(element, propertyName, context);
        if (property.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(property.GetString()))
        {
            throw new InvalidDataException(
                $"{context} property '{propertyName}' must be a non-empty string.");
        }

        return property.GetString()!;
    }

    private static int ReadRequiredInt32(
        JsonElement element,
        string propertyName,
        string context)
    {
        var property = ReadRequiredProperty(element, propertyName, context);
        if (property.ValueKind != JsonValueKind.Number
            || !property.TryGetInt32(out var value)
            || value < 1)
        {
            throw new InvalidDataException(
                $"{context} property '{propertyName}' must be a positive integer.");
        }

        return value;
    }

    private static void RequireKind(
        JsonElement element,
        JsonValueKind expected,
        string context)
    {
        if (element.ValueKind != expected)
        {
            throw new InvalidDataException(
                $"{context} must be a JSON {expected.ToString().ToLowerInvariant()}.");
        }
    }
}

public enum LauncherFeatureKind
{
    CompatibilityGate,
    ExperimentalFeature,
    ReleaseFlag,
}

public enum LauncherFeatureActivationMode
{
    StartupLatched,
}

public enum LauncherFeatureDefault
{
    EnabledWhenEligible,
    Disabled,
}

public enum LauncherFeatureActivationState
{
    Active,
    Inactive,
}

public sealed record LauncherFeatureDefinition(
    string Id,
    LauncherFeatureKind Kind,
    LauncherFeatureActivationMode ActivationMode,
    IReadOnlySet<string> RequiredCapabilities,
    IReadOnlySet<string> Dependencies,
    LauncherFeatureDefault Default,
    string ActiveImplementation,
    string FallbackImplementation);

public sealed record LauncherFeatureDecision(
    string Id,
    LauncherFeatureActivationState State,
    string Reason,
    string SelectedImplementation)
{
    public bool IsActive => State == LauncherFeatureActivationState.Active;
}

public sealed class LauncherFeaturePolicy
{
    private readonly FrozenDictionary<string, bool> overrides;

    public LauncherFeaturePolicy(
        IEnumerable<KeyValuePair<string, bool>>? overrides = null)
    {
        this.overrides = (overrides ?? [])
            .ToFrozenDictionary(
                pair => pair.Key,
                pair => pair.Value,
                StringComparer.Ordinal);
    }

    public bool IsEnabled(LauncherFeatureDefinition feature) =>
        overrides.TryGetValue(feature.Id, out var enabled)
            ? enabled
            : feature.Default == LauncherFeatureDefault.EnabledWhenEligible;

    public static LauncherFeaturePolicy Default { get; } = new();
}

public sealed class LauncherActivationPlan
{
    private readonly FrozenDictionary<string, LauncherFeatureDecision> features;

    internal LauncherActivationPlan(
        LauncherRuntimeProfile runtime,
        IEnumerable<LauncherFeatureDecision> features)
    {
        Runtime = runtime;
        this.features = features.ToFrozenDictionary(
            feature => feature.Id,
            StringComparer.Ordinal);
    }

    public LauncherRuntimeProfile Runtime { get; }

    public IReadOnlyDictionary<string, LauncherFeatureDecision> Features =>
        features;

    public LauncherFeatureDecision GetDecision(string featureId) =>
        features.TryGetValue(featureId, out var decision)
            ? decision
            : throw new KeyNotFoundException(
                $"Feature '{featureId}' is not present in the activation plan.");

    public bool IsActive(string featureId) =>
        GetDecision(featureId).IsActive;
}

public static class LauncherFeatureCatalog
{
    private static readonly IReadOnlySet<string> PrincipalTaxonomyRequirement =
        new[] { LauncherCapabilityIds.PrincipalSettingsTaxonomyV1 }
            .ToFrozenSet(StringComparer.Ordinal);

    private static readonly IReadOnlySet<string> NoDependencies =
        Array.Empty<string>().ToFrozenSet(StringComparer.Ordinal);

    public static IReadOnlyList<LauncherFeatureDefinition> All { get; } =
        Array.AsReadOnly<LauncherFeatureDefinition>(
        [
            new(
                LauncherFeatureIds.SemanticSettingsGrouping,
                LauncherFeatureKind.CompatibilityGate,
                LauncherFeatureActivationMode.StartupLatched,
                PrincipalTaxonomyRequirement,
                NoDependencies,
                LauncherFeatureDefault.EnabledWhenEligible,
                LauncherFeatureImplementations.PrincipalCatalogSettingsLayout,
                LauncherFeatureImplementations.AlphabeticalSettingsLayout),
        ]);
}

public static class LauncherFeatureResolver
{
    public static LauncherActivationPlan Resolve(
        LauncherRuntimeProfile runtime,
        IEnumerable<LauncherFeatureDefinition> features,
        LauncherFeaturePolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(features);
        policy ??= LauncherFeaturePolicy.Default;

        var definitions = features.ToArray();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var definition in definitions)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(definition.Id);
            if (!ids.Add(definition.Id))
            {
                throw new InvalidOperationException(
                    $"Feature '{definition.Id}' is defined more than once.");
            }
        }

        var decisions = new Dictionary<string, LauncherFeatureDecision>(
            StringComparer.Ordinal);
        var pending = new Queue<LauncherFeatureDefinition>(definitions);
        var stalledCount = 0;
        while (pending.Count > 0)
        {
            var definition = pending.Dequeue();
            var unresolvedDependencies = definition.Dependencies
                .Where(dependency => !decisions.ContainsKey(dependency))
                .ToArray();
            if (unresolvedDependencies.Length > 0)
            {
                if (unresolvedDependencies.Any(dependency => !ids.Contains(dependency)))
                {
                    throw new InvalidOperationException(
                        $"Feature '{definition.Id}' depends on an undefined feature.");
                }

                pending.Enqueue(definition);
                ++stalledCount;
                if (stalledCount >= pending.Count)
                {
                    throw new InvalidOperationException(
                        "Feature dependencies contain a cycle.");
                }

                continue;
            }

            stalledCount = 0;
            decisions.Add(
                definition.Id,
                ResolveDecision(runtime, definition, decisions, policy));
        }

        return new(runtime, decisions.Values);
    }

    private static LauncherFeatureDecision ResolveDecision(
        LauncherRuntimeProfile runtime,
        LauncherFeatureDefinition definition,
        IReadOnlyDictionary<string, LauncherFeatureDecision> decisions,
        LauncherFeaturePolicy policy)
    {
        if (!policy.IsEnabled(definition))
        {
            return Inactive(
                definition,
                "Product policy disabled this feature.");
        }

        var missingCapabilities = definition.RequiredCapabilities
            .Where(capability => !runtime.HasCapability(capability))
            .OrderBy(capability => capability, StringComparer.Ordinal)
            .ToArray();
        if (missingCapabilities.Length > 0)
        {
            return Inactive(
                definition,
                $"Required capability {string.Join(", ", missingCapabilities)} is unavailable. "
                + $"Detected distribution: {runtime.DistributionDisplayName}.");
        }

        var inactiveDependencies = definition.Dependencies
            .Select(dependency => decisions[dependency])
            .Where(decision => !decision.IsActive)
            .Select(decision => decision.Id)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
        if (inactiveDependencies.Length > 0)
        {
            return Inactive(
                definition,
                $"Required feature {string.Join(", ", inactiveDependencies)} is inactive.");
        }

        return new(
            definition.Id,
            LauncherFeatureActivationState.Active,
            $"Runtime provides {string.Join(", ", definition.RequiredCapabilities)}.",
            definition.ActiveImplementation);
    }

    private static LauncherFeatureDecision Inactive(
        LauncherFeatureDefinition definition,
        string reason) =>
        new(
            definition.Id,
            LauncherFeatureActivationState.Inactive,
            $"{reason} Fallback: {definition.FallbackImplementation}.",
            definition.FallbackImplementation);
}
