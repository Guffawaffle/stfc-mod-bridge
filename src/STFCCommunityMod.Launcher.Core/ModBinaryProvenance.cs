using System.Diagnostics;
using System.Text.Json;

namespace STFCCommunityMod.Launcher.Core;

public enum ModBuildIdentityParseState
{
    Unmarked,
    Valid,
    Malformed,
}

public sealed record ModBuildIdentity(
    int SchemaVersion,
    string DistributionId,
    string SourceStateId,
    string BaseCommit,
    string BuildInvocationId,
    string BuildMode,
    string BuildChannel);

public sealed record ModBuildIdentityParseResult(
    ModBuildIdentityParseState State,
    ModBuildIdentity? Identity = null,
    string Detail = "");

public sealed record ModBinaryVersionMetadata(
    string? FileVersion,
    string? ProductVersion,
    string? Comments);

public interface IModBinaryVersionMetadataReader
{
    ModBinaryVersionMetadata Read(string path);
}

public sealed class WindowsModBinaryVersionMetadataReader : IModBinaryVersionMetadataReader
{
    public ModBinaryVersionMetadata Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var info = FileVersionInfo.GetVersionInfo(path);
        return new(info.FileVersion, info.ProductVersion, info.Comments);
    }
}

public static class ModBuildIdentityCommentParser
{
    private const string Prefix = "stfc-identity-v1";
    private const int MaximumCommentLength = 2048;
    private const int MaximumValueLength = 160;
    private static readonly string[] RequiredKeys =
        ["distribution", "source", "base", "build", "mode", "channel"];

    public static ModBuildIdentityParseResult Parse(string? comment)
    {
        if (string.IsNullOrEmpty(comment) || !comment.StartsWith("stfc-identity-", StringComparison.Ordinal))
        {
            return new(ModBuildIdentityParseState.Unmarked);
        }
        if (comment.Length > MaximumCommentLength)
        {
            return Malformed($"The DLL identity comment exceeds {MaximumCommentLength} characters.");
        }
        if (!comment.StartsWith($"{Prefix};", StringComparison.Ordinal))
        {
            return Malformed("The DLL identity schema is unsupported.");
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var field in comment[(Prefix.Length + 1)..].Split(';'))
        {
            var separator = field.IndexOf('=');
            if (separator <= 0 || separator == field.Length - 1)
            {
                return Malformed("The DLL identity contains a malformed field.");
            }
            var key = field[..separator];
            var value = field[(separator + 1)..];
            if (!RequiredKeys.Contains(key, StringComparer.Ordinal))
            {
                return Malformed($"The DLL identity contains unsupported field '{key}'.");
            }
            if (!values.TryAdd(key, value))
            {
                return Malformed($"The DLL identity repeats field '{key}'.");
            }
            if (value.Length > MaximumValueLength || !value.All(IsSafeIdentityCharacter))
            {
                return Malformed($"The DLL identity field '{key}' is invalid.");
            }
        }
        var missing = RequiredKeys.FirstOrDefault(key => !values.ContainsKey(key));
        if (missing is not null)
        {
            return Malformed($"The DLL identity is missing field '{missing}'.");
        }

        return new(
            ModBuildIdentityParseState.Valid,
            new(
                1,
                values["distribution"],
                values["source"],
                values["base"],
                values["build"],
                values["mode"],
                values["channel"]));
    }

    private static bool IsSafeIdentityCharacter(char value) =>
        char.IsAsciiLetterOrDigit(value) || value is '.' or '_' or ':' or '+' or '/' or '-';

    private static ModBuildIdentityParseResult Malformed(string detail) =>
        new(ModBuildIdentityParseState.Malformed, Detail: detail);
}

public sealed record KnownModArtifactIdentity(
    string ProviderId,
    string RuntimeDistributionId,
    string TrackId,
    string Version,
    long Size,
    string Sha256,
    string SourceReference,
    DateTimeOffset ObservedAtUtc);

public sealed class KnownModArtifactCatalog
{
    private readonly Dictionary<string, KnownModArtifactIdentity> artifactsByHash;

    public KnownModArtifactCatalog(IEnumerable<KnownModArtifactIdentity> artifacts)
    {
        ArgumentNullException.ThrowIfNull(artifacts);
        var resolved = new Dictionary<string, KnownModArtifactIdentity>(StringComparer.OrdinalIgnoreCase);
        foreach (var artifact in artifacts)
        {
            ArgumentNullException.ThrowIfNull(artifact);
            if (string.IsNullOrWhiteSpace(artifact.ProviderId)
                || string.IsNullOrWhiteSpace(artifact.RuntimeDistributionId)
                || string.IsNullOrWhiteSpace(artifact.TrackId)
                || string.IsNullOrWhiteSpace(artifact.Version)
                || string.IsNullOrWhiteSpace(artifact.SourceReference)
                || !IsStableId(artifact.ProviderId)
                || !IsStableId(artifact.RuntimeDistributionId)
                || !IsStableId(artifact.TrackId)
                || artifact.Version.Length > 64
                || artifact.SourceReference.Length > 256
                || !artifact.SourceReference.All(IsSafeReferenceCharacter)
                || artifact.Size <= 0
                || !IsSha256(artifact.Sha256))
            {
                throw new InvalidDataException("Known mod artifact identity is incomplete or invalid.");
            }
            if (!resolved.TryAdd(artifact.Sha256, artifact with { Sha256 = artifact.Sha256.ToUpperInvariant() }))
            {
                throw new InvalidDataException($"Known mod artifact SHA-256 '{artifact.Sha256}' is duplicated.");
            }
        }
        artifactsByHash = resolved;
    }

    public static KnownModArtifactCatalog Empty { get; } = new([]);

    public int Count => artifactsByHash.Count;

    public KnownModArtifactIdentity? Find(string sha256, long size) =>
        artifactsByHash.TryGetValue(sha256, out var artifact) && artifact.Size == size
            ? artifact
            : null;

    private static bool IsSha256(string value) =>
        value.Length == 64 && value.All(Uri.IsHexDigit);

    private static bool IsStableId(string value) =>
        value.Length <= 96
        && (char.IsAsciiDigit(value[0]) || char.IsAsciiLetterLower(value[0]))
        && value.All(character =>
            char.IsAsciiDigit(character)
            || char.IsAsciiLetterLower(character)
            || character is '-' or '_' or '.');

    private static bool IsSafeReferenceCharacter(char value) =>
        char.IsAsciiLetterOrDigit(value) || value is '.' or '_' or ':' or '+' or '/' or '@' or '-';
}

public static class KnownModArtifactCatalogLoader
{
    private const int SupportedSchemaVersion = 1;
    private const int MaximumArtifacts = 64;
    private static readonly HashSet<string> RootProperties = ["schemaVersion", "artifacts"];
    private static readonly HashSet<string> ArtifactProperties =
    [
        "providerId",
        "runtimeDistributionId",
        "trackId",
        "version",
        "size",
        "sha256",
        "sourceReference",
        "observedAtUtc",
    ];

    public static KnownModArtifactCatalog Load(
        Stream stream,
        LauncherDistributionProviderCatalog providerCatalog)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(providerCatalog);
        try
        {
            using var document = JsonDocument.Parse(stream, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 8,
            });
            var root = document.RootElement;
            RequireObject(root, "known-artifact catalog");
            RejectUnknown(root, RootProperties, "known-artifact catalog");
            if (!root.TryGetProperty("schemaVersion", out var schema)
                || schema.ValueKind != JsonValueKind.Number
                || !schema.TryGetInt32(out var schemaVersion)
                || schemaVersion != SupportedSchemaVersion)
            {
                throw new InvalidDataException("Known-artifact catalog schema is unsupported.");
            }
            if (!root.TryGetProperty("artifacts", out var artifactArray)
                || artifactArray.ValueKind != JsonValueKind.Array
                || artifactArray.GetArrayLength() > MaximumArtifacts)
            {
                throw new InvalidDataException(
                    $"Known-artifact catalog must contain at most {MaximumArtifacts} artifacts.");
            }

            var artifacts = artifactArray.EnumerateArray()
                .Select(element => ReadArtifact(element, providerCatalog))
                .ToArray();
            return new(artifacts);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Known-artifact catalog is not valid JSON.", exception);
        }
    }

    private static KnownModArtifactIdentity ReadArtifact(
        JsonElement element,
        LauncherDistributionProviderCatalog providerCatalog)
    {
        RequireObject(element, "known artifact");
        RejectUnknown(element, ArtifactProperties, "known artifact");
        var providerId = ReadString(element, "providerId");
        if (!providerCatalog.TryGetProvider(providerId, out var provider) || provider is null)
        {
            throw new InvalidDataException($"Known artifact references unknown provider '{providerId}'.");
        }
        var runtimeDistributionId = ReadString(element, "runtimeDistributionId");
        if (!string.Equals(runtimeDistributionId, provider.RuntimeDistributionId, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Known artifact runtime '{runtimeDistributionId}' does not match provider '{providerId}'.");
        }
        if (!element.TryGetProperty("size", out var sizeElement)
            || sizeElement.ValueKind != JsonValueKind.Number
            || !sizeElement.TryGetInt64(out var size))
        {
            throw new InvalidDataException("Known artifact size must be an integer.");
        }
        var observedAtText = ReadString(element, "observedAtUtc");
        if (!DateTimeOffset.TryParseExact(
                observedAtText,
                "O",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None,
                out var observedAtUtc)
            || observedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new InvalidDataException("Known artifact observedAtUtc must be an ISO-8601 UTC timestamp.");
        }
        return new(
            providerId,
            runtimeDistributionId,
            ReadString(element, "trackId"),
            ReadString(element, "version"),
            size,
            ReadString(element, "sha256"),
            ReadString(element, "sourceReference"),
            observedAtUtc);
    }

    private static string ReadString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(property.GetString()))
        {
            throw new InvalidDataException($"Known artifact property '{propertyName}' must be a non-empty string.");
        }
        return property.GetString()!;
    }

    private static void RequireObject(JsonElement element, string context)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"{context} must be an object.");
        }
    }

    private static void RejectUnknown(JsonElement element, HashSet<string> allowed, string context)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (!allowed.Contains(property.Name))
            {
                throw new InvalidDataException($"{context} contains unknown property '{property.Name}'.");
            }
        }
    }
}

public enum ModBinaryProvenanceState
{
    CustomUnattributed,
    SelfDeclaredLineage,
    KnownProviderArtifact,
    MalformedIdentity,
    MetadataUnavailable,
}

public sealed record ModBinaryProvenance(
    ModBinaryProvenanceState State,
    string Sha256,
    long Size,
    string? FileVersion,
    string? ProductVersion,
    ModBuildIdentity? BuildIdentity = null,
    KnownModArtifactIdentity? KnownArtifact = null,
    string Detail = "")
{
    public string? DetectedProviderId => KnownArtifact?.ProviderId;

    public string? DetectedRuntimeDistributionId =>
        KnownArtifact?.RuntimeDistributionId ?? BuildIdentity?.DistributionId;
}

public sealed class ModBinaryProvenanceResolver(
    IModBinaryVersionMetadataReader metadataReader,
    KnownModArtifactCatalog knownArtifacts)
{
    public ModBinaryProvenance Resolve(string path, string sha256, long size)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(sha256);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(size);

        var knownArtifact = knownArtifacts.Find(sha256, size);
        ModBinaryVersionMetadata metadata;
        try
        {
            metadata = metadataReader.Read(path);
        }
        catch (Exception exception) when (IsMetadataFailure(exception))
        {
            return knownArtifact is null
                ? new(
                    ModBinaryProvenanceState.MetadataUnavailable,
                    sha256,
                    size,
                    null,
                    null,
                    Detail: $"PE version metadata is unavailable: {exception.GetType().Name}.")
                : new(
                    ModBinaryProvenanceState.KnownProviderArtifact,
                    sha256,
                    size,
                    null,
                    null,
                    KnownArtifact: knownArtifact,
                    Detail: "The exact SHA-256 matches a reviewed provider artifact; PE metadata is unavailable.");
        }

        if (knownArtifact is not null)
        {
            return new(
                ModBinaryProvenanceState.KnownProviderArtifact,
                sha256,
                size,
                metadata.FileVersion,
                metadata.ProductVersion,
                KnownArtifact: knownArtifact,
                Detail: "The exact SHA-256 matches a reviewed provider artifact.");
        }

        var parsed = ModBuildIdentityCommentParser.Parse(metadata.Comments);
        return parsed.State switch
        {
            ModBuildIdentityParseState.Valid => new(
                ModBinaryProvenanceState.SelfDeclaredLineage,
                sha256,
                size,
                metadata.FileVersion,
                metadata.ProductVersion,
                BuildIdentity: parsed.Identity,
                Detail: "The DLL self-declares build lineage; this is not official-release authenticity proof."),
            ModBuildIdentityParseState.Malformed => new(
                ModBinaryProvenanceState.MalformedIdentity,
                sha256,
                size,
                metadata.FileVersion,
                metadata.ProductVersion,
                Detail: parsed.Detail),
            _ => new(
                ModBinaryProvenanceState.CustomUnattributed,
                sha256,
                size,
                metadata.FileVersion,
                metadata.ProductVersion,
                Detail: "No recognized embedded identity or reviewed exact hash is available."),
        };
    }

    private static bool IsMetadataFailure(Exception exception) =>
        exception is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException;
}
