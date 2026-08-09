using System.Collections.Frozen;
using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace STFCCommunityMod.Launcher.Core;

public static class LauncherCapabilityIds
{
    public const string PrincipalSettingsTaxonomyV1 =
        "settings.principal-taxonomy.v1";

    public const string SidecarIngestV1 =
        "ingest.stfc-sidecar.v1";

    public const string BattleCaptureV1 =
        "battle.capture.v1";

    public const string FleetRuntimeSnapshotV1 =
        "fleet.runtime-snapshot.v1";
}

public static class LauncherFeatureIds
{
    public const string SemanticSettingsGrouping =
        "settings.semantic-grouping";

    public const string BattleCollection =
        "battle.collection";

    public const string FleetCollection =
        "fleet.collection";
}

public static class LauncherFeatureImplementations
{
    public const string PrincipalCatalogSettingsLayout =
        "principal-catalog-settings-layout";

    public const string AlphabeticalSettingsLayout =
        "alphabetical-settings-layout";

    public const string NativeBattleCollectionShell =
        "native-battle-collection-shell";

    public const string NoBattleCollection =
        "no-battle-collection";

    public const string NativeFleetCollectionShell =
        "native-fleet-collection-shell";

    public const string NoFleetCollection =
        "no-fleet-collection";
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
    string FallbackImplementation,
    bool ActiveImplementationAvailable = true,
    bool RequiresPlayerPreference = false);

[JsonConverter(typeof(LauncherFeaturePolicyDispositionJsonConverter))]
public enum LauncherFeaturePolicyDisposition
{
    CatalogDefaultEnabled = 1,
    CatalogDefaultDisabled = 2,
    CheckedInOverrideEnabled = 3,
    CheckedInOverrideDisabled = 4,
}

public sealed class LauncherFeaturePolicyDispositionJsonConverter :
    JsonConverter<LauncherFeaturePolicyDisposition>
{
    public override LauncherFeaturePolicyDisposition Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException("Feature policy disposition must be a canonical string.");
        }
        return reader.GetString() switch
        {
            "catalog-default-enabled" => LauncherFeaturePolicyDisposition.CatalogDefaultEnabled,
            "catalog-default-disabled" => LauncherFeaturePolicyDisposition.CatalogDefaultDisabled,
            "checked-in-override-enabled" => LauncherFeaturePolicyDisposition.CheckedInOverrideEnabled,
            "checked-in-override-disabled" => LauncherFeaturePolicyDisposition.CheckedInOverrideDisabled,
            _ => throw new JsonException("Feature policy disposition is unsupported."),
        };
    }

    public override void Write(
        Utf8JsonWriter writer,
        LauncherFeaturePolicyDisposition value,
        JsonSerializerOptions options) =>
        writer.WriteStringValue(LauncherFeaturePolicyDispositionContract.ToWireValue(value));
}

internal static class LauncherFeaturePolicyDispositionContract
{
    public static string ToWireValue(LauncherFeaturePolicyDisposition value) =>
        value switch
        {
            LauncherFeaturePolicyDisposition.CatalogDefaultEnabled => "catalog-default-enabled",
            LauncherFeaturePolicyDisposition.CatalogDefaultDisabled => "catalog-default-disabled",
            LauncherFeaturePolicyDisposition.CheckedInOverrideEnabled => "checked-in-override-enabled",
            LauncherFeaturePolicyDisposition.CheckedInOverrideDisabled => "checked-in-override-disabled",
            _ => throw new JsonException("Feature policy disposition is unsupported."),
        };
}

[JsonConverter(typeof(LauncherFeatureReasonCodeJsonConverter))]
public enum LauncherFeatureReasonCode
{
    Active = 1,
    MissingCapability = 2,
    PolicyDenied = 3,
    MissingDependency = 4,
    UnavailableImplementation = 5,
    Fallback = 6,
}

public sealed class LauncherFeatureReasonCodeJsonConverter :
    JsonConverter<LauncherFeatureReasonCode>
{
    public override LauncherFeatureReasonCode Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException("Feature reason code must be a canonical string.");
        }
        return reader.GetString() switch
        {
            "active" => LauncherFeatureReasonCode.Active,
            "missing-capability" => LauncherFeatureReasonCode.MissingCapability,
            "policy-denied" => LauncherFeatureReasonCode.PolicyDenied,
            "missing-dependency" => LauncherFeatureReasonCode.MissingDependency,
            "unavailable-implementation" => LauncherFeatureReasonCode.UnavailableImplementation,
            "fallback" => LauncherFeatureReasonCode.Fallback,
            _ => throw new JsonException("Feature reason code is unsupported."),
        };
    }

    public override void Write(
        Utf8JsonWriter writer,
        LauncherFeatureReasonCode value,
        JsonSerializerOptions options) =>
        writer.WriteStringValue(value switch
        {
            LauncherFeatureReasonCode.Active => "active",
            LauncherFeatureReasonCode.MissingCapability => "missing-capability",
            LauncherFeatureReasonCode.PolicyDenied => "policy-denied",
            LauncherFeatureReasonCode.MissingDependency => "missing-dependency",
            LauncherFeatureReasonCode.UnavailableImplementation => "unavailable-implementation",
            LauncherFeatureReasonCode.Fallback => "fallback",
            _ => throw new JsonException("Feature reason code is unsupported."),
        });
}

public sealed class LauncherFeatureDecisionEvidence :
    IEquatable<LauncherFeatureDecisionEvidence>
{
    private readonly ReadOnlyCollection<string> subjects;

    [JsonConstructor]
    public LauncherFeatureDecisionEvidence(
        LauncherFeatureReasonCode code,
        IReadOnlyList<string> subjects,
        string context = "")
    {
        ArgumentNullException.ThrowIfNull(subjects);
        ArgumentNullException.ThrowIfNull(context);
        if (!Enum.IsDefined(code))
        {
            throw new ArgumentOutOfRangeException(nameof(code));
        }
        var minimumSubjects = code == LauncherFeatureReasonCode.Active ? 0 : 1;
        var maximumSubjects = code is LauncherFeatureReasonCode.PolicyDenied
            or LauncherFeatureReasonCode.UnavailableImplementation
            or LauncherFeatureReasonCode.Fallback
                ? 1
                : 256;
        if (subjects.Count < minimumSubjects || subjects.Count > maximumSubjects)
        {
            throw new ArgumentException("Reason subjects have invalid cardinality.", nameof(subjects));
        }
        var copiedSubjects = subjects.ToArray();
        foreach (var subject in copiedSubjects)
        {
            LauncherFeatureContractText.Require(subject, nameof(subjects), 160);
        }
        if (!copiedSubjects.SequenceEqual(
                copiedSubjects.OrderBy(subject => subject, StringComparer.Ordinal),
                StringComparer.Ordinal)
            || copiedSubjects.Distinct(StringComparer.Ordinal).Count() != copiedSubjects.Length)
        {
            throw new ArgumentException("Reason subjects must be unique and ordinally sorted.", nameof(subjects));
        }
        if (!string.IsNullOrEmpty(context))
        {
            LauncherFeatureContractText.RequireDisplayText(context, nameof(context), 160);
        }
        Code = code;
        this.subjects = Array.AsReadOnly(copiedSubjects);
        Context = context;
    }

    public LauncherFeatureReasonCode Code { get; }

    public IReadOnlyList<string> Subjects => subjects;

    public string Context { get; }

    public bool Equals(LauncherFeatureDecisionEvidence? other) =>
        other is not null
        && Code == other.Code
        && Context == other.Context
        && subjects.SequenceEqual(other.subjects, StringComparer.Ordinal);

    public override bool Equals(object? obj) =>
        Equals(obj as LauncherFeatureDecisionEvidence);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Code);
        hash.Add(Context, StringComparer.Ordinal);
        foreach (var subject in subjects)
        {
            hash.Add(subject, StringComparer.Ordinal);
        }
        return hash.ToHashCode();
    }
}

public sealed record LauncherFeatureSourceIdentity
{
    [JsonConstructor]
    public LauncherFeatureSourceIdentity(string id, string version)
    {
        LauncherFeatureContractText.Require(id, nameof(id), 256);
        LauncherFeatureContractText.Require(version, nameof(version), 64);
        Id = id;
        Version = version;
    }

    public string Id { get; }

    public string Version { get; }
}

public sealed record LauncherFeatureDecision
{
    [JsonConstructor]
    public LauncherFeatureDecision(
        string id,
        LauncherFeatureActivationState state,
        LauncherFeatureDecisionEvidence eligibilityEvidence,
        LauncherFeatureDecisionEvidence selectionEvidence,
        string selectedImplementation,
        LauncherFeaturePolicyDisposition policyDisposition =
            LauncherFeaturePolicyDisposition.CatalogDefaultEnabled)
    {
        LauncherFeatureContractText.Require(id, nameof(id), 160);
        ArgumentNullException.ThrowIfNull(eligibilityEvidence);
        ArgumentNullException.ThrowIfNull(selectionEvidence);
        LauncherFeatureContractText.Require(selectedImplementation, nameof(selectedImplementation), 160);
        var coherent = state switch
        {
            LauncherFeatureActivationState.Active =>
                eligibilityEvidence.Code == LauncherFeatureReasonCode.Active
                && selectionEvidence.Code == LauncherFeatureReasonCode.Active,
            LauncherFeatureActivationState.Inactive =>
                eligibilityEvidence.Code is LauncherFeatureReasonCode.MissingCapability
                    or LauncherFeatureReasonCode.PolicyDenied
                    or LauncherFeatureReasonCode.MissingDependency
                    or LauncherFeatureReasonCode.UnavailableImplementation
                && selectionEvidence.Code == LauncherFeatureReasonCode.Fallback,
            _ => false,
        };
        if (!coherent
            || selectionEvidence.Subjects.Count != 1
            || selectionEvidence.Subjects[0] != selectedImplementation)
        {
            throw new ArgumentException("Feature decision evidence is contradictory.");
        }
        if (eligibilityEvidence.Code == LauncherFeatureReasonCode.MissingCapability
            ? string.IsNullOrEmpty(eligibilityEvidence.Context)
            : !string.IsNullOrEmpty(eligibilityEvidence.Context))
        {
            throw new ArgumentException("Feature decision evidence context is invalid.");
        }
        if (!Enum.IsDefined(policyDisposition))
        {
            throw new ArgumentOutOfRangeException(nameof(policyDisposition));
        }
        var policyEnabled = policyDisposition is
            LauncherFeaturePolicyDisposition.CatalogDefaultEnabled
            or LauncherFeaturePolicyDisposition.CheckedInOverrideEnabled;
        if (eligibilityEvidence.Code == LauncherFeatureReasonCode.PolicyDenied
            ? policyEnabled
            : !policyEnabled)
        {
            throw new ArgumentException("Feature policy disposition contradicts its eligibility evidence.");
        }
        Id = id;
        State = state;
        EligibilityEvidence = eligibilityEvidence;
        SelectionEvidence = selectionEvidence;
        SelectedImplementation = selectedImplementation;
        PolicyDisposition = policyDisposition;
    }

    public string Id { get; }

    public LauncherFeatureActivationState State { get; }

    public LauncherFeatureDecisionEvidence EligibilityEvidence { get; }

    public LauncherFeatureDecisionEvidence SelectionEvidence { get; }

    public string SelectedImplementation { get; }

    public LauncherFeaturePolicyDisposition PolicyDisposition { get; }

    public bool IsActive => State == LauncherFeatureActivationState.Active;

    public string Reason => LauncherFeatureDecisionPresenter.Present(this);
}

internal static class LauncherFeatureContractText
{
    private const string Punctuation = "._:/#+-@";

    public static void Require(string value, string parameterName, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > maximumLength
            || value.Any(character =>
                !char.IsAsciiLetterOrDigit(character)
                && !Punctuation.Contains(character)))
        {
            throw new ArgumentException("Feature contract value is empty, oversized, or unsafe.", parameterName);
        }
    }

    public static void RequireDisplayText(string value, string parameterName, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > maximumLength
            || value.Any(char.IsControl))
        {
            throw new ArgumentException("Feature display context is empty, oversized, or unsafe.", parameterName);
        }
    }
}

public sealed class LauncherFeaturePolicy
{
    private readonly FrozenDictionary<string, bool> overrides;

    public LauncherFeaturePolicy(
        IEnumerable<KeyValuePair<string, bool>>? overrides = null,
        LauncherFeatureSourceIdentity? source = null)
    {
        var entries = (overrides ?? []).ToArray();
        if (entries.Length > 0 && source is null)
        {
            throw new ArgumentException(
                "Feature policy overrides must provide their exact source identity and version.",
                nameof(source));
        }
        Source = source ?? DefaultSource;
        this.overrides = entries
            .ToFrozenDictionary(
                pair => pair.Key,
                pair => pair.Value,
                StringComparer.Ordinal);
    }

    public static LauncherFeatureSourceIdentity DefaultSource { get; } =
        new(
            "src/STFCCommunityMod.Launcher.Core/LauncherRuntimeActivation.cs#LauncherFeaturePolicy",
            "1");

    public LauncherFeatureSourceIdentity Source { get; }

    public bool IsEnabled(LauncherFeatureDefinition feature) =>
        GetDisposition(feature) is
            LauncherFeaturePolicyDisposition.CatalogDefaultEnabled
            or LauncherFeaturePolicyDisposition.CheckedInOverrideEnabled;

    public LauncherFeaturePolicyDisposition GetDisposition(LauncherFeatureDefinition feature)
    {
        ArgumentNullException.ThrowIfNull(feature);
        if (overrides.TryGetValue(feature.Id, out var enabled))
        {
            return enabled
                ? LauncherFeaturePolicyDisposition.CheckedInOverrideEnabled
                : LauncherFeaturePolicyDisposition.CheckedInOverrideDisabled;
        }
        return feature.Default == LauncherFeatureDefault.EnabledWhenEligible
            ? LauncherFeaturePolicyDisposition.CatalogDefaultEnabled
            : LauncherFeaturePolicyDisposition.CatalogDefaultDisabled;
    }

    public static LauncherFeaturePolicy Default { get; } = new();
}

public sealed class LauncherActivationPlan
{
    private readonly FrozenDictionary<string, LauncherFeatureDecision> features;

    internal LauncherActivationPlan(
        LauncherRuntimeProfile runtime,
        LauncherFeatureSourceIdentity catalogSource,
        LauncherFeatureSourceIdentity policySource,
        IEnumerable<LauncherFeatureDecision> features)
    {
        Runtime = runtime;
        CatalogSource = catalogSource;
        PolicySource = policySource;
        this.features = features.ToFrozenDictionary(
            feature => feature.Id,
            StringComparer.Ordinal);
    }

    public LauncherRuntimeProfile Runtime { get; }

    public LauncherFeatureSourceIdentity CatalogSource { get; }

    public LauncherFeatureSourceIdentity PolicySource { get; }

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
    public static LauncherFeatureSourceIdentity Source { get; } =
        new(
            "src/STFCCommunityMod.Launcher.Core/LauncherRuntimeActivation.cs#LauncherFeatureCatalog",
            "2");

    private static readonly IReadOnlySet<string> PrincipalTaxonomyRequirement =
        new[] { LauncherCapabilityIds.PrincipalSettingsTaxonomyV1 }
            .ToFrozenSet(StringComparer.Ordinal);

    private static readonly IReadOnlySet<string> BattleCollectionRequirements =
        new[]
        {
            LauncherCapabilityIds.SidecarIngestV1,
            LauncherCapabilityIds.BattleCaptureV1,
        }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly IReadOnlySet<string> FleetCollectionRequirements =
        new[]
        {
            LauncherCapabilityIds.SidecarIngestV1,
            LauncherCapabilityIds.FleetRuntimeSnapshotV1,
        }.ToFrozenSet(StringComparer.Ordinal);

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
            new(
                LauncherFeatureIds.BattleCollection,
                LauncherFeatureKind.CompatibilityGate,
                LauncherFeatureActivationMode.StartupLatched,
                BattleCollectionRequirements,
                NoDependencies,
                LauncherFeatureDefault.EnabledWhenEligible,
                LauncherFeatureImplementations.NativeBattleCollectionShell,
                LauncherFeatureImplementations.NoBattleCollection,
                RequiresPlayerPreference: true),
            new(
                LauncherFeatureIds.FleetCollection,
                LauncherFeatureKind.CompatibilityGate,
                LauncherFeatureActivationMode.StartupLatched,
                FleetCollectionRequirements,
                NoDependencies,
                LauncherFeatureDefault.EnabledWhenEligible,
                LauncherFeatureImplementations.NativeFleetCollectionShell,
                LauncherFeatureImplementations.NoFleetCollection,
                RequiresPlayerPreference: true),
        ]);
}

public static class LauncherFeatureDecisionPresenter
{
    public static string Present(LauncherFeatureDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        var reason = decision.EligibilityEvidence.Code switch
        {
            LauncherFeatureReasonCode.Active =>
                $"Runtime provides {string.Join(", ", decision.EligibilityEvidence.Subjects)}.",
            LauncherFeatureReasonCode.PolicyDenied =>
                "Product policy disabled this feature.",
            LauncherFeatureReasonCode.MissingCapability =>
                $"Required capability {string.Join(", ", decision.EligibilityEvidence.Subjects)} is unavailable. "
                + $"Detected distribution: {decision.EligibilityEvidence.Context}.",
            LauncherFeatureReasonCode.MissingDependency =>
                $"Required feature {string.Join(", ", decision.EligibilityEvidence.Subjects)} is inactive.",
            LauncherFeatureReasonCode.UnavailableImplementation =>
                $"Required implementation {decision.EligibilityEvidence.Subjects.Single()} is unavailable.",
            _ => throw new InvalidOperationException(
                $"Reason code '{decision.EligibilityEvidence.Code}' cannot establish eligibility."),
        };
        return decision.SelectionEvidence.Code switch
        {
            LauncherFeatureReasonCode.Active => reason,
            LauncherFeatureReasonCode.Fallback =>
                $"{reason} Fallback: {decision.SelectionEvidence.Subjects.Single()}.",
            _ => throw new InvalidOperationException(
                $"Reason code '{decision.SelectionEvidence.Code}' cannot select an implementation."),
        };
    }
}

public static class LauncherFeatureResolver
{
    public static LauncherActivationPlan Resolve(
        LauncherRuntimeProfile runtime,
        IEnumerable<LauncherFeatureDefinition> features,
        LauncherFeaturePolicy? policy = null,
        LauncherFeatureSourceIdentity? catalogSource = null)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(features);
        policy ??= LauncherFeaturePolicy.Default;
        if (catalogSource is null)
        {
            if (!ReferenceEquals(features, LauncherFeatureCatalog.All))
            {
                throw new ArgumentException(
                    "A non-default feature catalog must provide its exact source identity and version.",
                    nameof(catalogSource));
            }
            catalogSource = LauncherFeatureCatalog.Source;
        }
        ValidateSource(catalogSource, nameof(catalogSource));
        ValidateSource(policy.Source, nameof(policy));

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

        return new(runtime, catalogSource, policy.Source, decisions.Values);
    }

    private static LauncherFeatureDecision ResolveDecision(
        LauncherRuntimeProfile runtime,
        LauncherFeatureDefinition definition,
        IReadOnlyDictionary<string, LauncherFeatureDecision> decisions,
        LauncherFeaturePolicy policy)
    {
        var policyDisposition = policy.GetDisposition(definition);
        if (!policy.IsEnabled(definition))
        {
            return Inactive(
                definition,
                new(LauncherFeatureReasonCode.PolicyDenied, [definition.Id]),
                policyDisposition);
        }

        var missingCapabilities = definition.RequiredCapabilities
            .Where(capability => !runtime.HasCapability(capability))
            .OrderBy(capability => capability, StringComparer.Ordinal)
            .ToArray();
        if (missingCapabilities.Length > 0)
        {
            return Inactive(
                definition,
                new(
                    LauncherFeatureReasonCode.MissingCapability,
                    missingCapabilities,
                    runtime.DistributionDisplayName),
                policyDisposition);
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
                new(
                    LauncherFeatureReasonCode.MissingDependency,
                    inactiveDependencies),
                policyDisposition);
        }

        if (!definition.ActiveImplementationAvailable)
        {
            return Inactive(
                definition,
                new(
                    LauncherFeatureReasonCode.UnavailableImplementation,
                    [definition.ActiveImplementation]),
                policyDisposition);
        }

        return new(
            definition.Id,
            LauncherFeatureActivationState.Active,
            new(
                LauncherFeatureReasonCode.Active,
                definition.RequiredCapabilities.OrderBy(
                    capability => capability,
                    StringComparer.Ordinal).ToArray()),
            new(
                LauncherFeatureReasonCode.Active,
                [definition.ActiveImplementation]),
            definition.ActiveImplementation,
            policyDisposition);
    }

    private static LauncherFeatureDecision Inactive(
        LauncherFeatureDefinition definition,
        LauncherFeatureDecisionEvidence evidence,
        LauncherFeaturePolicyDisposition policyDisposition) =>
        new(
            definition.Id,
            LauncherFeatureActivationState.Inactive,
            evidence,
            new(
                LauncherFeatureReasonCode.Fallback,
                [definition.FallbackImplementation]),
            definition.FallbackImplementation,
            policyDisposition);

    private static void ValidateSource(
        LauncherFeatureSourceIdentity source,
        string parameterName)
    {
        if (string.IsNullOrWhiteSpace(source.Id)
            || string.IsNullOrWhiteSpace(source.Version))
        {
            throw new ArgumentException(
                "Feature source identity and version must be non-empty.",
                parameterName);
        }
    }
}
