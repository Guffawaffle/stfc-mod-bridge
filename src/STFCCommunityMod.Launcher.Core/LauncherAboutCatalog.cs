using System.Collections.ObjectModel;
using System.Text.Json;

namespace STFCCommunityMod.Launcher.Core;

public sealed record LauncherContributor(
    string Name,
    string Contribution,
    string? Url)
{
    public bool HasUrl => !string.IsNullOrWhiteSpace(Url);
}

public sealed record LauncherAcknowledgement(
    string Title,
    string Text,
    string? Url);

public sealed record LauncherThirdPartyNotice(
    string Id,
    string Name,
    string Version,
    string License,
    string SourceUrl,
    string LicenseUrl,
    string NoticeText);

public sealed record LauncherNoticeInventoryItem(
    string Id,
    string? Version,
    string? NoticeId,
    string AttributionStatus,
    string EvidenceKind);

public sealed record LauncherAboutCatalog(
    IReadOnlyList<LauncherContributor> Contributors,
    IReadOnlyList<LauncherAcknowledgement> Acknowledgements,
    IReadOnlyList<LauncherThirdPartyNotice> ThirdPartyNotices,
    IReadOnlyList<LauncherNoticeInventoryItem> DependencyInventory,
    IReadOnlyList<LauncherNoticeInventoryItem> AssetInventory,
    string GameAcknowledgement,
    string NoticeCoverageStatus,
    string LegalReviewStatus);

public static class LauncherAboutCatalogLoader
{
    public const int SupportedSchemaVersion = 1;

    public static LauncherAboutCatalog Load(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;
        var schemaVersion = ReadRequiredInt(root, "schemaVersion");
        if (schemaVersion != SupportedSchemaVersion)
        {
            throw new InvalidDataException(
                $"Unsupported About catalog schema version '{schemaVersion}'.");
        }

        var contributors = ReadArray(
            root,
            "contributors",
            element => new LauncherContributor(
                ReadRequiredString(element, "name"),
                ReadRequiredString(element, "contribution"),
                ReadOptionalString(element, "url")));
        var acknowledgements = ReadArray(
            root,
            "acknowledgements",
            element => new LauncherAcknowledgement(
                ReadRequiredString(element, "title"),
                ReadRequiredString(element, "text"),
                ReadOptionalString(element, "url")));
        var notices = ReadArray(
            root,
            "thirdPartyNotices",
            element => new LauncherThirdPartyNotice(
                ReadRequiredString(element, "id"),
                ReadRequiredString(element, "name"),
                ReadRequiredString(element, "version"),
                ReadRequiredString(element, "license"),
                ReadRequiredString(element, "sourceUrl"),
                ReadRequiredString(element, "licenseUrl"),
                ReadRequiredString(element, "noticeText")));
        var dependencies = ReadInventory(root, "dependencyInventory");
        var assets = ReadInventory(root, "assetInventory");

        RequireUnique(contributors.Select(item => item.Name), "contributor name");
        RequireUnique(notices.Select(item => item.Id), "third-party notice ID");
        RequireUnique(dependencies.Select(item => item.Id), "dependency inventory ID");
        RequireUnique(assets.Select(item => item.Id), "asset inventory ID");
        RequireEvidenceKind(
            dependencies,
            new HashSet<string>(["resolved-package", "runtime-pack"], StringComparer.Ordinal));
        RequireEvidenceKind(
            assets,
            new HashSet<string>(["project-input", "package-transitive"], StringComparer.Ordinal));

        var noticeIds = notices.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var item in dependencies.Concat(assets))
        {
            if (item.AttributionStatus is not ("required" or "review-pending" or "internal-build-input"))
            {
                throw new InvalidDataException(
                    $"Inventory item '{item.Id}' has unsupported attribution status '{item.AttributionStatus}'.");
            }

            if (string.Equals(item.AttributionStatus, "required", StringComparison.Ordinal)
                && string.IsNullOrWhiteSpace(item.NoticeId))
            {
                throw new InvalidDataException(
                    $"Attribution-required inventory item '{item.Id}' has no notice ID.");
            }

            if (item.NoticeId is not null && !noticeIds.Contains(item.NoticeId))
            {
                throw new InvalidDataException(
                    $"Inventory item '{item.Id}' references unknown notice '{item.NoticeId}'.");
            }
        }

        return new(
            contributors,
            acknowledgements,
            notices,
            dependencies,
            assets,
            ReadRequiredString(root, "gameAcknowledgement"),
            ReadRequiredString(root, "noticeCoverageStatus"),
            ReadRequiredString(root, "legalReviewStatus"));
    }

    private static ReadOnlyCollection<LauncherNoticeInventoryItem> ReadInventory(
        JsonElement root,
        string propertyName) =>
        ReadArray(
            root,
            propertyName,
            element => new LauncherNoticeInventoryItem(
                ReadRequiredString(element, "id"),
                ReadOptionalString(element, "version"),
                ReadOptionalString(element, "noticeId"),
                ReadRequiredString(element, "attributionStatus"),
                ReadRequiredString(element, "evidenceKind")));

    private static ReadOnlyCollection<T> ReadArray<T>(
        JsonElement root,
        string propertyName,
        Func<JsonElement, T> factory)
    {
        if (!root.TryGetProperty(propertyName, out var array)
            || array.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException($"About catalog '{propertyName}' must be an array.");
        }

        return Array.AsReadOnly(array.EnumerateArray().Select(factory).ToArray());
    }

    private static void RequireUnique(IEnumerable<string> values, string description)
    {
        var duplicate = values
            .GroupBy(value => value, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidDataException(
                $"About catalog contains duplicate {description} '{duplicate.Key}'.");
        }
    }

    private static void RequireEvidenceKind(
        IEnumerable<LauncherNoticeInventoryItem> items,
        HashSet<string> allowedKinds)
    {
        foreach (var item in items)
        {
            if (!allowedKinds.Contains(item.EvidenceKind))
            {
                throw new InvalidDataException(
                    $"Inventory item '{item.Id}' has unsupported evidence kind '{item.EvidenceKind}'.");
            }
        }
    }

    private static string ReadRequiredString(JsonElement element, string propertyName)
    {
        var value = ReadOptionalString(element, propertyName);
        return string.IsNullOrWhiteSpace(value)
            ? throw new InvalidDataException($"About catalog '{propertyName}' must not be blank.")
            : value;
    }

    private static string? ReadOptionalString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int ReadRequiredInt(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value)
        && value.TryGetInt32(out var integer)
            ? integer
            : throw new InvalidDataException($"About catalog '{propertyName}' must be an integer.");
}
