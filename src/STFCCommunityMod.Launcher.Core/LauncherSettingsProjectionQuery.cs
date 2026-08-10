using System.Collections.ObjectModel;

namespace STFCCommunityMod.Launcher.Core;

public abstract record LauncherSettingsProjectionItem;

public sealed record LauncherSettingsGroupHeaderProjection(string Label) :
    LauncherSettingsProjectionItem;

public sealed record LauncherSettingsFamilyHeaderProjection(
    string Id,
    string Label,
    string Description) :
    LauncherSettingsProjectionItem;

public sealed record LauncherSettingRowProjection(
    LauncherConfigurationSetting Setting,
    LauncherSettingsPlacement Placement) :
    LauncherSettingsProjectionItem;

public sealed class LauncherSettingsProjectionQuery
{
    private readonly ILauncherSettingsLayoutProvider layoutProvider;
    private readonly ReadOnlyDictionary<string, LauncherSettingsPlacement> placementsByPath;
    private readonly IReadOnlyList<LauncherConfigurationSetting> orderedSettings;

    public LauncherSettingsProjectionQuery(
        LauncherConfigurationCatalog catalog,
        ILauncherSettingsLayoutProvider layoutProvider)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        this.layoutProvider =
            layoutProvider ?? throw new ArgumentNullException(nameof(layoutProvider));

        var declaredSections = layoutProvider.Sections
            .Select(section => section.Id)
            .ToHashSet();
        var placements = new Dictionary<string, LauncherSettingsPlacement>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var setting in catalog.VisibleSettings)
        {
            var placement = layoutProvider.Place(setting);
            if (!declaredSections.Contains(placement.Section))
            {
                throw new InvalidOperationException(
                    $"Settings layout '{layoutProvider.Id}' placed '{setting.Path}' "
                    + $"in undeclared section '{placement.Section}'.");
            }

            placements.Add(setting.Path, placement);
        }

        placementsByPath = new(placements);
        orderedSettings = Array.AsReadOnly(
            catalog.VisibleSettings
                .OrderBy(setting => placements[setting.Path].Section)
                .ThenBy(setting => placements[setting.Path].GroupOrder)
                .ThenBy(setting => placements[setting.Path].Group, StringComparer.OrdinalIgnoreCase)
                .ThenBy(setting => placements[setting.Path].FamilyOrder)
                .ThenBy(setting => placements[setting.Path].FamilyId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(setting => placements[setting.Path].MemberOrder)
                .ThenBy(setting => placements[setting.Path].SortKey, StringComparer.OrdinalIgnoreCase)
                .ToArray());
    }

    public IReadOnlyDictionary<string, LauncherSettingsPlacement> PlacementsByPath =>
        placementsByPath;

    public IReadOnlyList<LauncherSettingsProjectionItem> Project(
        LauncherSettingsSection section,
        string? searchText)
    {
        var normalizedSearch = searchText?.Trim() ?? string.Empty;
        var isSearchActive = normalizedSearch.Length > 0;
        var matchingSettings = orderedSettings.Where(
            setting =>
                (isSearchActive || placementsByPath[setting.Path].Section == section)
                && (!isSearchActive
                    || placementsByPath[setting.Path].Section != LauncherSettingsSection.DataSync)
                && (!isSearchActive || Matches(setting, placementsByPath[setting.Path], normalizedSearch)));

        var projection = new List<LauncherSettingsProjectionItem>();
        var seenFamilies = new HashSet<string>(StringComparer.Ordinal);
        (LauncherSettingsSection Section, string Group)? previousGroup = null;
        foreach (var setting in matchingSettings)
        {
            var placement = placementsByPath[setting.Path];
            var groupIdentity = (placement.Section, placement.Group);
            if (layoutProvider.ShowGroupHeadings
                && previousGroup != groupIdentity)
            {
                projection.Add(new LauncherSettingsGroupHeaderProjection(placement.Group));
                previousGroup = groupIdentity;
            }

            var family = setting.Presentation.Family;
            if (family is not null && seenFamilies.Add(family.Id))
            {
                projection.Add(
                    new LauncherSettingsFamilyHeaderProjection(
                        family.Id,
                        family.Label,
                        family.Help ?? string.Empty));
            }

            projection.Add(new LauncherSettingRowProjection(setting, placement));
        }

        return projection.AsReadOnly();
    }

    private static bool Matches(
        LauncherConfigurationSetting setting,
        LauncherSettingsPlacement placement,
        string searchText) =>
        Contains(setting.Path, searchText)
        || Contains(setting.Presentation.Label, searchText)
        || Contains(setting.Presentation.Help, searchText)
        || Contains(placement.Group, searchText)
        || Contains(setting.Category, searchText)
        || Contains(setting.Control.ToString(), searchText)
        || Contains(setting.ValueKind.ToString(), searchText)
        || setting.Aliases.Any(alias => Contains(alias.Path, searchText))
        || setting.Presentation.SearchTerms.Any(term => Contains(term, searchText));

    private static bool Contains(string? candidate, string searchText) =>
        candidate?.Contains(searchText, StringComparison.OrdinalIgnoreCase) == true;
}
