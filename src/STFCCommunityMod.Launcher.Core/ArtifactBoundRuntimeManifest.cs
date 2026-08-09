using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace STFCCommunityMod.Launcher.Core;

internal sealed record ParsedRuntimeManifest(
    string DistributionId,
    string SourceRevision,
    string Sha256,
    LauncherRuntimeProfile RuntimeProfile);

public sealed class ReviewedRuntimeActivation
{
    internal ReviewedRuntimeActivation(
        string evidenceSourceSha256,
        LauncherRuntimeProfile runtimeProfile,
        LauncherActivationPlan activationPlan)
    {
        EvidenceSourceSha256 = evidenceSourceSha256;
        RuntimeProfile = runtimeProfile;
        ActivationPlan = activationPlan;
    }

    public string EvidenceSourceSha256 { get; }

    public LauncherRuntimeProfile RuntimeProfile { get; }

    public LauncherActivationPlan ActivationPlan { get; }
}

/// <summary>
/// Strictly validates bounded descriptive runtime compatibility evidence and
/// its exact DLL binding. This parser does not establish activation authority.
/// </summary>
internal static partial class ArtifactBoundRuntimeManifestParser
{
    public const string ManagedFileName = "stfc-runtime-manifest.json";
    public const int MaximumManifestBytes = 256 * 1024;
    private const int MaximumCapabilities = 64;
    private const int MaximumRuntimeCapabilities = 32;
    private const int MaximumPayloadKinds = 16;
    private const int MaximumStringLength = 160;

    private static readonly HashSet<string> RootProperties =
    [
        "manifestSchema", "distributionId", "runtimeVersion", "sourceRevision",
        "capabilities", "settingsCatalog", "producerContract",
    ];
    private static readonly HashSet<string> SettingsCatalogProperties = ["schemaVersion", "revision"];
    private static readonly HashSet<string> ProducerContractProperties =
    [
        "schema", "capabilityEvidencePin", "runtimeCapabilities", "artifact",
        "compatibilityEvidenceOnly", "operationalActivation",
    ];
    private static readonly HashSet<string> EvidencePinProperties = ["schema", "sha256"];
    private static readonly HashSet<string> RuntimeCapabilityProperties =
    ["id", "schema", "evidenceStatus", "payloadKinds", "envelopeKind"];
    private static readonly HashSet<string> ArtifactProperties = ["fileName", "size", "sha256"];
    private static readonly Dictionary<string, (string Schema, string[]? PayloadKinds, string? EnvelopeKind)>
        SupportedRuntimeCapabilities = new Dictionary<string, (string, string[]?, string?)>(StringComparer.Ordinal)
        {
            [LauncherCapabilityIds.SidecarIngestV1] = (
                "stfc.sidecar.ingest.v1",
                ["battle.events", "fleet.runtime", "transport.chunk"],
                null),
            [LauncherCapabilityIds.BattleCaptureV1] = ("stfc.battle.capture.v1", null, "battle.events"),
            [LauncherCapabilityIds.FleetRuntimeSnapshotV1] = (
                "stfc.fleet.runtime_snapshot.v1", null, "fleet.runtime"),
        };

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,159}$", RegexOptions.CultureInvariant)]
    private static partial Regex StableIdPattern();

    [GeneratedRegex("^[0-9a-f]{40}$", RegexOptions.CultureInvariant)]
    private static partial Regex CommitPattern();

    [GeneratedRegex("^[0-9A-Fa-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Pattern();

    [GeneratedRegex("^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex RepositoryPattern();

    public static ParsedRuntimeManifest Parse(
        ReadOnlyMemory<byte> bytes,
        ModReleaseArtifact dll,
        ModRuntimeManifestArtifact discoveryMetadata,
        string expectedDistributionId)
    {
        ArgumentNullException.ThrowIfNull(dll);
        ArgumentNullException.ThrowIfNull(discoveryMetadata);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedDistributionId);
        ValidateDiscoveryMetadata(discoveryMetadata);
        if (bytes.Length is <= 0 or > MaximumManifestBytes || bytes.Length != discoveryMetadata.Size)
        {
            throw new InvalidDataException("The runtime manifest size does not match its release discovery metadata.");
        }
        var manifestSha256 = Convert.ToHexString(SHA256.HashData(bytes.Span)).ToLowerInvariant();
        if (!FixedTimeEquals(manifestSha256, discoveryMetadata.Sha256))
        {
            throw new InvalidDataException("The runtime manifest SHA-256 does not match its release discovery metadata.");
        }

        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 16,
            });
            RejectDuplicateProperties(document.RootElement, "runtime manifest");
            var root = RequireObject(document.RootElement, "runtime manifest", RootProperties);
            RequireInt32(root, "manifestSchema", 1, "runtime manifest");
            var distributionId = RequireStableId(root, "distributionId", "runtime manifest");
            if (!string.Equals(distributionId, expectedDistributionId, StringComparison.Ordinal))
            {
                throw new InvalidDataException("The runtime manifest distribution does not match the selected provider endpoint.");
            }
            var runtimeVersion = RequireString(root, "runtimeVersion", "runtime manifest");
            if (!Version.TryParse(runtimeVersion, out var parsedVersion)
                || !Version.TryParse(dll.ExpectedVersion, out var expectedVersion)
                || parsedVersion != expectedVersion)
            {
                throw new InvalidDataException("The runtime manifest version does not match the reviewed DLL version.");
            }
            var sourceRevision = RequireString(root, "sourceRevision", "runtime manifest");
            if (!CommitPattern().IsMatch(sourceRevision)
                || !string.Equals(sourceRevision, discoveryMetadata.ExpectedSourceRevision, StringComparison.Ordinal))
            {
                throw new InvalidDataException("The runtime manifest source revision does not match the reviewed release.");
            }

            var declaredCapabilities = ReadStringArray(
                root, "capabilities", "runtime manifest", MaximumCapabilities, StableIdPattern());
            var settingsCatalog = RequireObject(
                RequireProperty(root, "settingsCatalog", "runtime manifest"),
                "runtime manifest settingsCatalog",
                SettingsCatalogProperties);
            RequireInt32(settingsCatalog, "schemaVersion", 1, "runtime manifest settingsCatalog");
            _ = RequireStableId(settingsCatalog, "revision", "runtime manifest settingsCatalog");

            var producer = RequireObject(
                RequireProperty(root, "producerContract", "runtime manifest"),
                "runtime manifest producerContract",
                ProducerContractProperties);
            if (RequireString(producer, "schema", "runtime manifest producerContract")
                != "stfc.battle-bridge.producer-capabilities.v1")
            {
                throw new InvalidDataException("The runtime producer contract schema is unsupported.");
            }
            ReadEvidencePin(producer);
            var runtimeCapabilityIds = ReadRuntimeCapabilities(producer);
            var expectedCapabilities = runtimeCapabilityIds
                .Append(LauncherCapabilityIds.PrincipalSettingsTaxonomyV1)
                .ToHashSet(StringComparer.Ordinal);
            if (!expectedCapabilities.SetEquals(declaredCapabilities))
            {
                throw new InvalidDataException(
                    "The manifest capability set does not exactly match its producer declarations and supported base capability.");
            }
            if (!RequireBoolean(producer, "compatibilityEvidenceOnly", "runtime manifest producerContract")
                || RequireString(producer, "operationalActivation", "runtime manifest producerContract")
                    != "requires-bridge-transactional-binding")
            {
                throw new InvalidDataException("The runtime producer contract does not declare the supported evidence boundary.");
            }
            ReadAndBindDllArtifact(producer, dll);

            using var detectorStream = new MemoryStream(bytes.ToArray(), writable: false);
            var evidenceSource = $"managed-pair:sha256:{manifestSha256}";
            var profile = LauncherRuntimeManifestDetector.Detect(detectorStream, evidenceSource);
            if (!string.Equals(profile.DistributionId, distributionId, StringComparison.Ordinal)
                || !string.Equals(profile.SourceRevision, sourceRevision, StringComparison.Ordinal))
            {
                throw new InvalidDataException("The runtime manifest could not produce the expected normalized runtime profile.");
            }
            return new(distributionId, sourceRevision, manifestSha256, profile);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The runtime manifest is not valid bounded JSON.", exception);
        }
    }

    public static ReviewedRuntimeActivation? AuthorizeActivation(
        ParsedRuntimeManifest manifest,
        ModReleaseArtifact dll,
        ModRuntimeManifestArtifact discoveryMetadata,
        ReviewedReleaseCertification? reviewedCertification)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(dll);
        ArgumentNullException.ThrowIfNull(discoveryMetadata);
        var reviewedManifest = reviewedCertification?.RuntimeManifest;
        if (reviewedCertification is null
            || reviewedManifest is null
            || reviewedCertification.PayloadFileName != dll.FileName
            || reviewedCertification.PayloadSize != dll.Size
            || !FixedTimeEquals(reviewedCertification.PayloadSha256, dll.Sha256)
            || reviewedCertification.PayloadVersion != dll.ExpectedVersion
            || reviewedManifest.FileName != discoveryMetadata.FileName
            || reviewedManifest.Size != discoveryMetadata.Size
            || !FixedTimeEquals(reviewedManifest.Sha256, discoveryMetadata.Sha256)
            || !FixedTimeEquals(reviewedManifest.Sha256, manifest.Sha256)
            || reviewedCertification.SourceCommit != discoveryMetadata.ExpectedSourceRevision
            || reviewedCertification.SourceCommit != manifest.SourceRevision
            || reviewedCertification.Repository != discoveryMetadata.ExpectedRepository
            || reviewedCertification.Tag != discoveryMetadata.ExpectedTag
            || reviewedCertification.RuntimeDistributionId != manifest.DistributionId)
        {
            return null;
        }
        return new(
            manifest.Sha256,
            manifest.RuntimeProfile,
            LauncherFeatureResolver.Resolve(manifest.RuntimeProfile, LauncherFeatureCatalog.All));
    }

    private static void ValidateDiscoveryMetadata(ModRuntimeManifestArtifact artifact)
    {
        if (artifact.FileName != ManagedFileName
            || artifact.Size is <= 0 or > MaximumManifestBytes
            || !Sha256Pattern().IsMatch(artifact.Sha256)
            || !CommitPattern().IsMatch(artifact.ExpectedSourceRevision)
            || artifact.ExpectedRepository.Length > MaximumStringLength
            || artifact.ExpectedTag.Length > MaximumStringLength
            || !RepositoryPattern().IsMatch(artifact.ExpectedRepository)
            || !artifact.DownloadUri.IsAbsoluteUri
            || artifact.DownloadUri.Scheme != Uri.UriSchemeHttps
            || artifact.DownloadUri != new Uri(
                $"https://github.com/{artifact.ExpectedRepository}/releases/download/"
                + $"{Uri.EscapeDataString(artifact.ExpectedTag)}/{Uri.EscapeDataString(artifact.FileName)}"))
        {
            throw new InvalidDataException("The selected runtime-manifest discovery metadata is invalid.");
        }
    }

    private static void ReadEvidencePin(JsonElement producer)
    {
        var pin = RequireObject(
            RequireProperty(producer, "capabilityEvidencePin", "runtime manifest producerContract"),
            "runtime manifest capabilityEvidencePin",
            EvidencePinProperties);
        _ = RequireStableId(pin, "schema", "runtime manifest capabilityEvidencePin");
        var sha256 = RequireString(pin, "sha256", "runtime manifest capabilityEvidencePin");
        if (!Sha256Pattern().IsMatch(sha256))
        {
            throw new InvalidDataException("The capability evidence pin SHA-256 is invalid.");
        }
    }

    private static string[] ReadRuntimeCapabilities(JsonElement producer)
    {
        var value = RequireProperty(producer, "runtimeCapabilities", "runtime manifest producerContract");
        if (value.ValueKind != JsonValueKind.Array
            || value.GetArrayLength() is <= 0 or > MaximumRuntimeCapabilities)
        {
            throw new InvalidDataException("runtimeCapabilities must be a bounded non-empty array.");
        }
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in value.EnumerateArray())
        {
            var capability = RequireObject(item, "runtime capability", RuntimeCapabilityProperties);
            var id = RequireStableId(capability, "id", "runtime capability");
            if (!ids.Add(id))
            {
                throw new InvalidDataException($"Runtime capability '{id}' is duplicated.");
            }
            if (!SupportedRuntimeCapabilities.TryGetValue(id, out var supported)
                || RequireStableId(capability, "schema", "runtime capability") != supported.Schema)
            {
                throw new InvalidDataException($"Runtime capability '{id}' has an unsupported schema.");
            }
            if (RequireString(capability, "evidenceStatus", "runtime capability") != "payload-fixture-only")
            {
                throw new InvalidDataException($"Runtime capability '{id}' has an unsupported evidence status.");
            }
            var hasPayloadKinds = capability.TryGetProperty("payloadKinds", out _);
            var hasEnvelopeKind = capability.TryGetProperty("envelopeKind", out _);
            if (hasPayloadKinds == hasEnvelopeKind)
            {
                throw new InvalidDataException($"Runtime capability '{id}' must declare one payload shape.");
            }
            if (hasPayloadKinds)
            {
                var payloadKinds = ReadStringArray(
                    capability, "payloadKinds", "runtime capability", MaximumPayloadKinds, StableIdPattern());
                if (supported.PayloadKinds is null
                    || !payloadKinds.ToHashSet(StringComparer.Ordinal).SetEquals(supported.PayloadKinds))
                {
                    throw new InvalidDataException($"Runtime capability '{id}' has an unsupported payload set.");
                }
            }
            else if (RequireStableId(capability, "envelopeKind", "runtime capability") != supported.EnvelopeKind)
            {
                throw new InvalidDataException($"Runtime capability '{id}' has an unsupported envelope kind.");
            }
        }
        return ids.ToArray();
    }

    private static void ReadAndBindDllArtifact(JsonElement producer, ModReleaseArtifact dll)
    {
        var artifact = RequireObject(
            RequireProperty(producer, "artifact", "runtime manifest producerContract"),
            "runtime manifest DLL artifact",
            ArtifactProperties);
        if (RequireString(artifact, "fileName", "runtime manifest DLL artifact") != "version.dll"
            || RequireInt64(artifact, "size", "runtime manifest DLL artifact") != dll.Size
            || !FixedTimeEquals(
                RequireString(artifact, "sha256", "runtime manifest DLL artifact"),
                dll.Sha256))
        {
            throw new InvalidDataException("The runtime manifest is not bound to the exact reviewed DLL.");
        }
    }

    private static JsonElement RequireObject(JsonElement value, string context, HashSet<string> allowed)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"{context} must be an object.");
        }
        foreach (var property in value.EnumerateObject())
        {
            if (!allowed.Contains(property.Name))
            {
                throw new InvalidDataException($"{context} contains unknown property '{property.Name}'.");
            }
        }
        return value;
    }

    private static JsonElement RequireProperty(JsonElement parent, string name, string context) =>
        parent.TryGetProperty(name, out var value)
            ? value
            : throw new InvalidDataException($"{context} is missing '{name}'.");

    private static string RequireString(JsonElement parent, string name, string context)
    {
        var value = RequireProperty(parent, name, context);
        var text = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        return !string.IsNullOrWhiteSpace(text) && text.Length <= MaximumStringLength
            ? text
            : throw new InvalidDataException($"{context}.{name} must be a bounded non-empty string.");
    }

    private static string RequireStableId(JsonElement parent, string name, string context)
    {
        var value = RequireString(parent, name, context);
        return StableIdPattern().IsMatch(value)
            ? value
            : throw new InvalidDataException($"{context}.{name} contains unsupported identity characters.");
    }

    private static int RequireInt32(JsonElement parent, string name, int expected, string context)
    {
        var value = RequireProperty(parent, name, context);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var actual) || actual != expected)
        {
            throw new InvalidDataException($"{context}.{name} must equal {expected}.");
        }
        return actual;
    }

    private static long RequireInt64(JsonElement parent, string name, string context)
    {
        var value = RequireProperty(parent, name, context);
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var actual) && actual > 0
            ? actual
            : throw new InvalidDataException($"{context}.{name} must be a positive integer.");
    }

    private static bool RequireBoolean(JsonElement parent, string name, string context)
    {
        var value = RequireProperty(parent, name, context);
        return value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : throw new InvalidDataException($"{context}.{name} must be a boolean.");
    }

    private static List<string> ReadStringArray(
        JsonElement parent,
        string name,
        string context,
        int maximumCount,
        Regex pattern)
    {
        var value = RequireProperty(parent, name, context);
        if (value.ValueKind != JsonValueKind.Array
            || value.GetArrayLength() is <= 0
            || value.GetArrayLength() > maximumCount)
        {
            throw new InvalidDataException($"{context}.{name} must be a bounded non-empty array.");
        }
        var values = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in value.EnumerateArray())
        {
            var text = item.ValueKind == JsonValueKind.String ? item.GetString() : null;
            if (string.IsNullOrWhiteSpace(text)
                || text.Length > MaximumStringLength
                || !pattern.IsMatch(text)
                || !seen.Add(text))
            {
                throw new InvalidDataException($"{context}.{name} contains an invalid or duplicate value.");
            }
            values.Add(text);
        }
        return values;
    }

    private static void RejectDuplicateProperties(JsonElement value, string context)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!seen.Add(property.Name))
                {
                    throw new InvalidDataException($"{context} contains duplicate property '{property.Name}'.");
                }
                RejectDuplicateProperties(property.Value, context);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                RejectDuplicateProperties(item, context);
            }
        }
    }

    private static bool FixedTimeEquals(string left, string right)
    {
        if (!Sha256Pattern().IsMatch(left) || !Sha256Pattern().IsMatch(right))
        {
            return false;
        }
        return CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(left),
            Convert.FromHexString(right));
    }
}
