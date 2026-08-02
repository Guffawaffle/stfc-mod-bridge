using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace STFCCommunityMod.Launcher.Core;

public enum LauncherProviderCapabilityStatus
{
    Unknown,
    Unsupported,
    Supported,
}

public static class LauncherProviderCapabilityIds
{
    public const string ConfigurationCatalog = "settings.catalog";
    public const string RuntimeManifest = "runtime.manifest";
    public const string ReleaseDiscovery = "mod.release-discovery";
    public const string ArtifactTrust = "mod.artifact-trust";
    public const string WithdrawalPolicy = "release.withdrawal";
    public const string ConfigurationMigration = "config.migration";

    public static IReadOnlyList<string> ContractCapabilities { get; } =
    [
        ConfigurationCatalog,
        RuntimeManifest,
        ReleaseDiscovery,
        ArtifactTrust,
        WithdrawalPolicy,
        ConfigurationMigration,
    ];
}

public enum LauncherProviderReleaseDiscoveryKind
{
    ReleaseManifest,
    GitHubReleaseAsset,
}

public sealed record LauncherProviderReleaseChannel(
    string Id,
    string DisplayName,
    string Repository,
    LauncherProviderReleaseDiscoveryKind DiscoveryKind,
    string? ManifestAssetName,
    string? ArtifactAssetName);

public sealed record LauncherProviderResource(
    LauncherProviderCapabilityStatus Status,
    string? ResourceName);

public sealed record LauncherProviderArtifactPolicy(
    LauncherProviderCapabilityStatus Status,
    bool RequireSha256,
    string? WindowsPublisher);

public sealed record LauncherProviderWithdrawalPolicy(
    LauncherProviderCapabilityStatus Status,
    string? Mode);

public sealed record LauncherProviderMigrationPolicy(
    LauncherProviderCapabilityStatus Status,
    string ConfigurationFormat,
    bool PreserveUnknownToml,
    IReadOnlySet<string> CompatibleProviderIds);

public sealed class LauncherDistributionProvider
{
    private readonly ReadOnlyDictionary<string, LauncherProviderReleaseChannel> releaseChannels;
    private readonly ReadOnlyDictionary<string, LauncherProviderCapabilityStatus> capabilities;

    internal LauncherDistributionProvider(
        string id,
        string displayName,
        string description,
        string runtimeDistributionId,
        string defaultReleaseChannelId,
        IEnumerable<LauncherProviderReleaseChannel> releaseChannels,
        LauncherProviderResource configurationSchema,
        LauncherProviderResource runtimeManifest,
        IEnumerable<KeyValuePair<string, LauncherProviderCapabilityStatus>> capabilities,
        LauncherProviderArtifactPolicy artifactPolicy,
        LauncherProviderWithdrawalPolicy withdrawalPolicy,
        LauncherProviderMigrationPolicy migration)
    {
        Id = id;
        DisplayName = displayName;
        Description = description;
        RuntimeDistributionId = runtimeDistributionId;
        DefaultReleaseChannelId = defaultReleaseChannelId;
        this.releaseChannels = new(
            releaseChannels.ToDictionary(channel => channel.Id, StringComparer.Ordinal));
        ConfigurationSchema = configurationSchema;
        RuntimeManifest = runtimeManifest;
        this.capabilities = new(
            capabilities.ToDictionary(capability => capability.Key, capability => capability.Value, StringComparer.Ordinal));
        ArtifactPolicy = artifactPolicy;
        WithdrawalPolicy = withdrawalPolicy;
        Migration = migration;
    }

    public string Id { get; }

    public string DisplayName { get; }

    public string Description { get; }

    public string RuntimeDistributionId { get; }

    public string DefaultReleaseChannelId { get; }

    public IReadOnlyDictionary<string, LauncherProviderReleaseChannel> ReleaseChannels => releaseChannels;

    public LauncherProviderReleaseChannel DefaultReleaseChannel => releaseChannels[DefaultReleaseChannelId];

    public LauncherProviderResource ConfigurationSchema { get; }

    public LauncherProviderResource RuntimeManifest { get; }

    public IReadOnlyDictionary<string, LauncherProviderCapabilityStatus> Capabilities => capabilities;

    public LauncherProviderArtifactPolicy ArtifactPolicy { get; }

    public LauncherProviderWithdrawalPolicy WithdrawalPolicy { get; }

    public LauncherProviderMigrationPolicy Migration { get; }

    public LauncherProviderCapabilityStatus GetCapabilityStatus(string capabilityId) =>
        capabilities.GetValueOrDefault(capabilityId, LauncherProviderCapabilityStatus.Unknown);

    public bool CanUseManifestReleaseDiscovery =>
        CanUseManifestReleaseDiscoveryFor(DefaultReleaseChannel);

    public bool CanUseManifestReleaseDiscoveryFor(LauncherProviderReleaseChannel releaseChannel) =>
        GetCapabilityStatus(LauncherProviderCapabilityIds.ReleaseDiscovery)
            == LauncherProviderCapabilityStatus.Supported
        && releaseChannel.DiscoveryKind == LauncherProviderReleaseDiscoveryKind.ReleaseManifest
        && !string.IsNullOrWhiteSpace(releaseChannel.ManifestAssetName);

    public bool CanAuthenticateWindowsArtifact =>
        GetCapabilityStatus(LauncherProviderCapabilityIds.ArtifactTrust)
            == LauncherProviderCapabilityStatus.Supported
        && ArtifactPolicy.RequireSha256
        && !string.IsNullOrWhiteSpace(ArtifactPolicy.WindowsPublisher);

    public string CapabilitySummary =>
        string.Join(
            ", ",
            LauncherProviderCapabilityIds.ContractCapabilities.Select(
                capability => $"{capability}: {GetCapabilityStatus(capability).ToString().ToLowerInvariant()}"));
}

public sealed class LauncherDistributionProviderCatalog
{
    private readonly ReadOnlyDictionary<string, LauncherDistributionProvider> providers;

    internal LauncherDistributionProviderCatalog(
        string defaultProviderId,
        IEnumerable<LauncherDistributionProvider> providers)
    {
        DefaultProviderId = defaultProviderId;
        this.providers = new ReadOnlyDictionary<string, LauncherDistributionProvider>(
            providers.ToDictionary(provider => provider.Id, StringComparer.Ordinal));
    }

    public string DefaultProviderId { get; }

    public IReadOnlyDictionary<string, LauncherDistributionProvider> Providers => providers;

    public LauncherDistributionProvider DefaultProvider => providers[DefaultProviderId];

    public LauncherDistributionProvider GetProvider(string providerId) =>
        providers.TryGetValue(providerId, out var provider)
            ? provider
            : throw new KeyNotFoundException($"Distribution provider '{providerId}' is not registered.");

    public bool TryGetProvider(string providerId, out LauncherDistributionProvider? provider) =>
        providers.TryGetValue(providerId, out provider);
}

public static partial class LauncherDistributionProviderCatalogLoader
{
    private const int SupportedSchemaVersion = 1;
    private const int MaximumProviders = 16;
    private static readonly HashSet<string> IndexProperties =
    [
        "schemaVersion",
        "defaultProviderId",
        "packResourceNames",
    ];
    private static readonly HashSet<string> PackProperties =
    [
        "schemaVersion",
        "provider",
    ];
    private static readonly HashSet<string> ProviderProperties =
    [
        "id",
        "displayName",
        "description",
        "runtimeDistributionId",
        "defaultReleaseChannelId",
        "releaseChannels",
        "configurationSchema",
        "runtimeManifest",
        "capabilities",
        "artifactPolicy",
        "withdrawalPolicy",
        "migration",
    ];
    private static readonly HashSet<string> ReleaseChannelProperties =
    [
        "id",
        "displayName",
        "repository",
        "discoveryKind",
        "manifestAssetName",
        "artifactAssetName",
    ];
    private static readonly HashSet<string> ResourceProperties = ["status", "resourceName"];
    private static readonly HashSet<string> CapabilityProperties = ["id", "status"];
    private static readonly HashSet<string> ArtifactPolicyProperties =
        ["status", "requireSha256", "windowsPublisher"];
    private static readonly HashSet<string> WithdrawalPolicyProperties = ["status", "mode"];
    private static readonly HashSet<string> MigrationProperties =
        ["status", "configurationFormat", "preserveUnknownToml", "compatibleProviderIds"];

    [GeneratedRegex("^[a-z0-9]+(?:[.-][a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex StableIdPattern();

    [GeneratedRegex("^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex GitHubRepositoryPattern();

    public static LauncherDistributionProviderCatalog Load(
        Stream indexStream,
        Func<string, Stream?> openPackResource)
    {
        ArgumentNullException.ThrowIfNull(indexStream);
        ArgumentNullException.ThrowIfNull(openPackResource);
        using var document = Parse(indexStream);
        var root = document.RootElement;
        RequireKind(root, JsonValueKind.Object, "provider catalog index");
        RejectUnknownProperties(root, IndexProperties, "provider catalog index");
        RequireSchemaVersion(root, "provider catalog index");
        var defaultProviderId = ReadStableId(root, "defaultProviderId", "provider catalog index");
        var resourceNames = ReadRequiredProperty(root, "packResourceNames", "provider catalog index");
        RequireKind(resourceNames, JsonValueKind.Array, "provider pack resource names");

        var providers = new List<LauncherDistributionProvider>();
        var seenResources = new HashSet<string>(StringComparer.Ordinal);
        foreach (var resourceElement in resourceNames.EnumerateArray())
        {
            if (providers.Count == MaximumProviders)
            {
                throw new InvalidDataException($"Provider catalog exceeds the {MaximumProviders}-provider limit.");
            }
            if (resourceElement.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(resourceElement.GetString()))
            {
                throw new InvalidDataException("Provider pack resource names must be non-empty strings.");
            }
            var resourceName = resourceElement.GetString()!;
            if (!seenResources.Add(resourceName))
            {
                throw new InvalidDataException($"Provider pack resource '{resourceName}' is duplicated.");
            }
            using var packStream = openPackResource(resourceName)
                ?? throw new InvalidDataException($"Provider pack resource '{resourceName}' is missing.");
            providers.Add(LoadPack(packStream));
        }

        if (providers.Count == 0)
        {
            throw new InvalidDataException("Provider catalog must contain at least one provider pack.");
        }
        var duplicateId = providers.GroupBy(provider => provider.Id, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1)?.Key;
        if (duplicateId is not null)
        {
            throw new InvalidDataException($"Provider catalog contains duplicate ID '{duplicateId}'.");
        }
        if (providers.All(provider => !string.Equals(provider.Id, defaultProviderId, StringComparison.Ordinal)))
        {
            throw new InvalidDataException($"Default provider '{defaultProviderId}' is not present in the catalog.");
        }
        return new(defaultProviderId, providers);
    }

    public static LauncherDistributionProvider LoadPack(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var document = Parse(stream);
        var root = document.RootElement;
        RequireKind(root, JsonValueKind.Object, "provider pack");
        RejectUnknownProperties(root, PackProperties, "provider pack");
        RequireSchemaVersion(root, "provider pack");
        var providerElement = ReadRequiredProperty(root, "provider", "provider pack");
        RequireKind(providerElement, JsonValueKind.Object, "provider definition");
        RejectUnknownProperties(providerElement, ProviderProperties, "provider definition");
        return ReadProvider(providerElement);
    }

    private static LauncherDistributionProvider ReadProvider(JsonElement element)
    {
        var id = ReadStableId(element, "id", "provider definition");
        var channelsElement = ReadRequiredProperty(element, "releaseChannels", $"provider '{id}'");
        RequireKind(channelsElement, JsonValueKind.Array, $"provider '{id}' release channels");
        var channels = channelsElement.EnumerateArray()
            .Select(channel => ReadReleaseChannel(channel, id))
            .ToArray();
        if (channels.Length == 0)
        {
            throw new InvalidDataException($"Provider '{id}' must declare a release channel.");
        }
        var duplicateChannel = channels.GroupBy(channel => channel.Id, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1)?.Key;
        if (duplicateChannel is not null)
        {
            throw new InvalidDataException($"Provider '{id}' contains duplicate release channel '{duplicateChannel}'.");
        }
        var defaultChannelId = ReadStableId(element, "defaultReleaseChannelId", $"provider '{id}'");
        if (channels.All(channel => !string.Equals(channel.Id, defaultChannelId, StringComparison.Ordinal)))
        {
            throw new InvalidDataException($"Provider '{id}' default release channel '{defaultChannelId}' is missing.");
        }

        var capabilitiesElement = ReadRequiredProperty(element, "capabilities", $"provider '{id}'");
        RequireKind(capabilitiesElement, JsonValueKind.Array, $"provider '{id}' capabilities");
        var capabilities = capabilitiesElement.EnumerateArray()
            .Select(capability => ReadCapability(capability, id))
            .ToArray();
        var duplicateCapability = capabilities.GroupBy(capability => capability.Key, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1)?.Key;
        if (duplicateCapability is not null)
        {
            throw new InvalidDataException($"Provider '{id}' contains duplicate capability '{duplicateCapability}'.");
        }

        var configurationSchema = ReadResource(element, "configurationSchema", id);
        var runtimeManifest = ReadResource(element, "runtimeManifest", id);
        var artifactPolicy = ReadArtifactPolicy(element, id);
        var withdrawalPolicy = ReadWithdrawalPolicy(element, id);
        var migration = ReadMigrationPolicy(element, id);
        var provider = new LauncherDistributionProvider(
            id,
            ReadRequiredString(element, "displayName", $"provider '{id}'"),
            ReadRequiredString(element, "description", $"provider '{id}'"),
            ReadStableId(element, "runtimeDistributionId", $"provider '{id}'"),
            defaultChannelId,
            channels,
            configurationSchema,
            runtimeManifest,
            capabilities,
            artifactPolicy,
            withdrawalPolicy,
            migration);
        ValidateCapabilityEvidence(provider);
        return provider;
    }

    private static LauncherProviderReleaseChannel ReadReleaseChannel(JsonElement element, string providerId)
    {
        RequireKind(element, JsonValueKind.Object, $"provider '{providerId}' release channel");
        RejectUnknownProperties(element, ReleaseChannelProperties, $"provider '{providerId}' release channel");
        var repository = ReadRequiredString(element, "repository", $"provider '{providerId}' release channel");
        if (!GitHubRepositoryPattern().IsMatch(repository))
        {
            throw new InvalidDataException($"Provider '{providerId}' has invalid GitHub repository '{repository}'.");
        }
        var kind = ReadEnum<LauncherProviderReleaseDiscoveryKind>(
            element,
            "discoveryKind",
            $"provider '{providerId}' release channel");
        var manifestAssetName = ReadOptionalFileName(element, "manifestAssetName", providerId);
        var artifactAssetName = ReadOptionalFileName(element, "artifactAssetName", providerId);
        if (kind == LauncherProviderReleaseDiscoveryKind.ReleaseManifest && manifestAssetName is null)
        {
            throw new InvalidDataException($"Provider '{providerId}' manifest release channel requires manifestAssetName.");
        }
        if (kind == LauncherProviderReleaseDiscoveryKind.GitHubReleaseAsset && artifactAssetName is null)
        {
            throw new InvalidDataException($"Provider '{providerId}' GitHub asset channel requires artifactAssetName.");
        }
        return new(
            ReadStableId(element, "id", $"provider '{providerId}' release channel"),
            ReadRequiredString(element, "displayName", $"provider '{providerId}' release channel"),
            repository,
            kind,
            manifestAssetName,
            artifactAssetName);
    }

    private static KeyValuePair<string, LauncherProviderCapabilityStatus> ReadCapability(
        JsonElement element,
        string providerId)
    {
        RequireKind(element, JsonValueKind.Object, $"provider '{providerId}' capability");
        RejectUnknownProperties(element, CapabilityProperties, $"provider '{providerId}' capability");
        return new(
            ReadStableId(element, "id", $"provider '{providerId}' capability"),
            ReadEnum<LauncherProviderCapabilityStatus>(element, "status", $"provider '{providerId}' capability"));
    }

    private static LauncherProviderResource ReadResource(JsonElement provider, string propertyName, string providerId)
    {
        var element = ReadRequiredProperty(provider, propertyName, $"provider '{providerId}'");
        RequireKind(element, JsonValueKind.Object, $"provider '{providerId}' {propertyName}");
        RejectUnknownProperties(element, ResourceProperties, $"provider '{providerId}' {propertyName}");
        var status = ReadEnum<LauncherProviderCapabilityStatus>(element, "status", $"provider '{providerId}' {propertyName}");
        var resourceName = ReadOptionalString(element, "resourceName");
        if (status == LauncherProviderCapabilityStatus.Supported && resourceName is null)
        {
            throw new InvalidDataException($"Provider '{providerId}' supported {propertyName} requires resourceName.");
        }
        return new(status, resourceName);
    }

    private static LauncherProviderArtifactPolicy ReadArtifactPolicy(JsonElement provider, string providerId)
    {
        var element = ReadRequiredProperty(provider, "artifactPolicy", $"provider '{providerId}'");
        RequireKind(element, JsonValueKind.Object, $"provider '{providerId}' artifact policy");
        RejectUnknownProperties(element, ArtifactPolicyProperties, $"provider '{providerId}' artifact policy");
        return new(
            ReadEnum<LauncherProviderCapabilityStatus>(element, "status", $"provider '{providerId}' artifact policy"),
            ReadRequiredBoolean(element, "requireSha256", $"provider '{providerId}' artifact policy"),
            ReadOptionalString(element, "windowsPublisher"));
    }

    private static LauncherProviderWithdrawalPolicy ReadWithdrawalPolicy(JsonElement provider, string providerId)
    {
        var element = ReadRequiredProperty(provider, "withdrawalPolicy", $"provider '{providerId}'");
        RequireKind(element, JsonValueKind.Object, $"provider '{providerId}' withdrawal policy");
        RejectUnknownProperties(element, WithdrawalPolicyProperties, $"provider '{providerId}' withdrawal policy");
        return new(
            ReadEnum<LauncherProviderCapabilityStatus>(element, "status", $"provider '{providerId}' withdrawal policy"),
            ReadOptionalString(element, "mode"));
    }

    private static LauncherProviderMigrationPolicy ReadMigrationPolicy(JsonElement provider, string providerId)
    {
        var element = ReadRequiredProperty(provider, "migration", $"provider '{providerId}'");
        RequireKind(element, JsonValueKind.Object, $"provider '{providerId}' migration policy");
        RejectUnknownProperties(element, MigrationProperties, $"provider '{providerId}' migration policy");
        var compatibleIds = ReadRequiredProperty(element, "compatibleProviderIds", $"provider '{providerId}' migration policy");
        RequireKind(compatibleIds, JsonValueKind.Array, $"provider '{providerId}' compatible provider IDs");
        var ids = compatibleIds.EnumerateArray()
            .Select(id =>
            {
                if (id.ValueKind != JsonValueKind.String || !StableIdPattern().IsMatch(id.GetString() ?? string.Empty))
                {
                    throw new InvalidDataException($"Provider '{providerId}' migration compatibility contains an invalid ID.");
                }
                return id.GetString()!;
            })
            .ToHashSet(StringComparer.Ordinal);
        return new(
            ReadEnum<LauncherProviderCapabilityStatus>(element, "status", $"provider '{providerId}' migration policy"),
            ReadRequiredString(element, "configurationFormat", $"provider '{providerId}' migration policy"),
            ReadRequiredBoolean(element, "preserveUnknownToml", $"provider '{providerId}' migration policy"),
            ids);
    }

    private static void ValidateCapabilityEvidence(LauncherDistributionProvider provider)
    {
        foreach (var capabilityId in LauncherProviderCapabilityIds.ContractCapabilities)
        {
            if (!provider.Capabilities.ContainsKey(capabilityId))
            {
                continue;
            }
            var status = provider.GetCapabilityStatus(capabilityId);
            var evidenceIsSupported = capabilityId switch
            {
                LauncherProviderCapabilityIds.ConfigurationCatalog =>
                    provider.ConfigurationSchema.Status == LauncherProviderCapabilityStatus.Supported,
                LauncherProviderCapabilityIds.RuntimeManifest =>
                    provider.RuntimeManifest.Status == LauncherProviderCapabilityStatus.Supported,
                LauncherProviderCapabilityIds.ReleaseDiscovery => provider.CanUseManifestReleaseDiscovery,
                LauncherProviderCapabilityIds.ArtifactTrust =>
                    provider.ArtifactPolicy.Status == LauncherProviderCapabilityStatus.Supported
                    && provider.ArtifactPolicy.RequireSha256
                    && !string.IsNullOrWhiteSpace(provider.ArtifactPolicy.WindowsPublisher),
                LauncherProviderCapabilityIds.WithdrawalPolicy =>
                    provider.WithdrawalPolicy.Status == LauncherProviderCapabilityStatus.Supported
                    && !string.IsNullOrWhiteSpace(provider.WithdrawalPolicy.Mode),
                LauncherProviderCapabilityIds.ConfigurationMigration =>
                    provider.Migration.Status == LauncherProviderCapabilityStatus.Supported
                    && provider.Migration.PreserveUnknownToml,
                _ => false,
            };
            if (status == LauncherProviderCapabilityStatus.Supported && !evidenceIsSupported)
            {
                throw new InvalidDataException(
                    $"Provider '{provider.Id}' marks '{capabilityId}' supported without required evidence.");
            }
        }
    }

    private static JsonDocument Parse(Stream stream) =>
        JsonDocument.Parse(
            stream,
            new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 24,
            });

    private static void RequireSchemaVersion(JsonElement root, string context)
    {
        var schemaVersion = ReadRequiredInt32(root, "schemaVersion", context);
        if (schemaVersion != SupportedSchemaVersion)
        {
            throw new InvalidDataException(
                $"{context} schema {schemaVersion} is unsupported; expected {SupportedSchemaVersion}.");
        }
    }

    private static string? ReadOptionalFileName(JsonElement element, string propertyName, string providerId)
    {
        var value = ReadOptionalString(element, propertyName);
        if (value is not null
            && (!string.Equals(Path.GetFileName(value), value, StringComparison.Ordinal)
                || value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
        {
            throw new InvalidDataException($"Provider '{providerId}' {propertyName} must be a file name, not a path.");
        }
        return value;
    }

    private static string ReadStableId(JsonElement element, string propertyName, string context)
    {
        var value = ReadRequiredString(element, propertyName, context);
        if (!StableIdPattern().IsMatch(value))
        {
            throw new InvalidDataException($"{context} property '{propertyName}' is not a stable lowercase ID.");
        }
        return value;
    }

    private static TEnum ReadEnum<TEnum>(JsonElement element, string propertyName, string context)
        where TEnum : struct, Enum
    {
        var token = ReadRequiredString(element, propertyName, context);
        var normalized = token.Replace("-", string.Empty, StringComparison.Ordinal);
        if (!Enum.TryParse<TEnum>(normalized, true, out var value) || !Enum.IsDefined(value))
        {
            throw new InvalidDataException($"{context} property '{propertyName}' has unsupported value '{token}'.");
        }
        return value;
    }

    private static JsonElement ReadRequiredProperty(JsonElement element, string propertyName, string context)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            throw new InvalidDataException($"{context} is missing required property '{propertyName}'.");
        }
        return property;
    }

    private static string ReadRequiredString(JsonElement element, string propertyName, string context)
    {
        var property = ReadRequiredProperty(element, propertyName, context);
        if (property.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(property.GetString()))
        {
            throw new InvalidDataException($"{context} property '{propertyName}' must be a non-empty string.");
        }
        return property.GetString()!;
    }

    private static string? ReadOptionalString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        if (property.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(property.GetString()))
        {
            throw new InvalidDataException($"Property '{propertyName}' must be null or a non-empty string.");
        }
        return property.GetString();
    }

    private static int ReadRequiredInt32(JsonElement element, string propertyName, string context)
    {
        var property = ReadRequiredProperty(element, propertyName, context);
        if (property.ValueKind != JsonValueKind.Number || !property.TryGetInt32(out var value))
        {
            throw new InvalidDataException($"{context} property '{propertyName}' must be an integer.");
        }
        return value;
    }

    private static bool ReadRequiredBoolean(JsonElement element, string propertyName, string context)
    {
        var property = ReadRequiredProperty(element, propertyName, context);
        if (property.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new InvalidDataException($"{context} property '{propertyName}' must be a boolean.");
        }
        return property.GetBoolean();
    }

    private static void RequireKind(JsonElement element, JsonValueKind expected, string context)
    {
        if (element.ValueKind != expected)
        {
            throw new InvalidDataException($"{context} must be a JSON {expected.ToString().ToLowerInvariant()}.");
        }
    }

    private static void RejectUnknownProperties(JsonElement element, HashSet<string> allowed, string context)
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
