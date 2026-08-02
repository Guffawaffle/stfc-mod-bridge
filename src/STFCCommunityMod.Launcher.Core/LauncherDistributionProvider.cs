using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace STFCCommunityMod.Launcher.Core;

public sealed record LauncherDistributionProvider(
    string Id,
    string DisplayName,
    string RuntimeDistributionId,
    string ModReleaseRepository,
    string ModReleaseManifestAssetName,
    string ConfigurationSchemaResourceName,
    string RuntimeManifestResourceName,
    string WindowsArtifactPublisher);

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
}

public static partial class LauncherDistributionProviderCatalogLoader
{
    private const int SupportedSchemaVersion = 1;
    private const int MaximumProviders = 16;
    private static readonly HashSet<string> RootProperties =
    [
        "schemaVersion",
        "defaultProviderId",
        "providers",
    ];
    private static readonly HashSet<string> ProviderProperties =
    [
        "id",
        "displayName",
        "runtimeDistributionId",
        "modReleaseRepository",
        "modReleaseManifestAssetName",
        "configurationSchemaResourceName",
        "runtimeManifestResourceName",
        "windowsArtifactPublisher",
    ];

    [GeneratedRegex("^[a-z0-9]+(?:[.-][a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex StableIdPattern();

    [GeneratedRegex(
        "^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$",
        RegexOptions.CultureInvariant)]
    private static partial Regex GitHubRepositoryPattern();

    public static LauncherDistributionProviderCatalog Load(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var document = JsonDocument.Parse(
            stream,
            new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 16,
            });
        var root = document.RootElement;
        RequireKind(root, JsonValueKind.Object, "provider catalog");
        RejectUnknownProperties(root, RootProperties, "provider catalog");

        var schemaVersion = ReadRequiredInt32(root, "schemaVersion", "provider catalog");
        if (schemaVersion != SupportedSchemaVersion)
        {
            throw new InvalidDataException(
                $"Provider catalog schema {schemaVersion} is unsupported; expected {SupportedSchemaVersion}.");
        }

        var defaultProviderId = ReadStableId(root, "defaultProviderId", "provider catalog");
        var providersElement = ReadRequiredProperty(root, "providers", "provider catalog");
        RequireKind(providersElement, JsonValueKind.Array, "provider catalog providers");
        var providers = new List<LauncherDistributionProvider>();
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var element in providersElement.EnumerateArray())
        {
            if (providers.Count == MaximumProviders)
            {
                throw new InvalidDataException(
                    $"Provider catalog exceeds the {MaximumProviders}-provider limit.");
            }

            RequireKind(element, JsonValueKind.Object, "provider definition");
            RejectUnknownProperties(element, ProviderProperties, "provider definition");
            var provider = ReadProvider(element);
            if (!seenIds.Add(provider.Id))
            {
                throw new InvalidDataException(
                    $"Provider catalog contains duplicate ID '{provider.Id}'.");
            }
            providers.Add(provider);
        }

        if (providers.Count == 0)
        {
            throw new InvalidDataException("Provider catalog must contain at least one provider.");
        }
        if (!seenIds.Contains(defaultProviderId))
        {
            throw new InvalidDataException(
                $"Default provider '{defaultProviderId}' is not present in the catalog.");
        }

        return new(defaultProviderId, providers);
    }

    private static LauncherDistributionProvider ReadProvider(JsonElement element)
    {
        var id = ReadStableId(element, "id", "provider definition");
        var displayName = ReadRequiredString(element, "displayName", "provider definition");
        var runtimeDistributionId = ReadStableId(
            element,
            "runtimeDistributionId",
            "provider definition");
        var repository = ReadRequiredString(
            element,
            "modReleaseRepository",
            "provider definition");
        if (!GitHubRepositoryPattern().IsMatch(repository))
        {
            throw new InvalidDataException(
                $"Provider '{id}' has invalid GitHub repository '{repository}'.");
        }

        var manifestAssetName = ReadRequiredString(
            element,
            "modReleaseManifestAssetName",
            "provider definition");
        if (!string.Equals(
                Path.GetFileName(manifestAssetName),
                manifestAssetName,
                StringComparison.Ordinal)
            || manifestAssetName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new InvalidDataException(
                $"Provider '{id}' release manifest must be a file name, not a path.");
        }

        return new(
            id,
            displayName,
            runtimeDistributionId,
            repository,
            manifestAssetName,
            ReadRequiredString(element, "configurationSchemaResourceName", "provider definition"),
            ReadRequiredString(element, "runtimeManifestResourceName", "provider definition"),
            ReadRequiredString(element, "windowsArtifactPublisher", "provider definition"));
    }

    private static string ReadStableId(
        JsonElement element,
        string propertyName,
        string context)
    {
        var value = ReadRequiredString(element, propertyName, context);
        if (!StableIdPattern().IsMatch(value))
        {
            throw new InvalidDataException(
                $"{context} property '{propertyName}' is not a stable lowercase ID.");
        }
        return value;
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
        if (property.ValueKind != JsonValueKind.Number || !property.TryGetInt32(out var value))
        {
            throw new InvalidDataException(
                $"{context} property '{propertyName}' must be an integer.");
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

    private static void RejectUnknownProperties(
        JsonElement element,
        HashSet<string> allowed,
        string context)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (!allowed.Contains(property.Name))
            {
                throw new InvalidDataException(
                    $"{context} contains unknown property '{property.Name}'.");
            }
        }
    }
}
