using System.Collections.ObjectModel;

namespace STFCCommunityMod.Launcher.Core;

public enum LauncherSettingsSection
{
    General,
    Interface,
    Graphics,
    Notifications,
    Hotkeys,
    DataSync,
    Advanced,
    About,
}

public sealed record LauncherSettingsSectionDefinition(
    LauncherSettingsSection Id,
    string Title,
    string Description,
    string AutomationName);

public sealed record LauncherSettingsPlacement(
    LauncherSettingsSection Section,
    string Group,
    int GroupOrder,
    string FamilyId,
    int FamilyOrder,
    int MemberOrder,
    string SortKey,
    bool IsUncategorized = false);

public sealed record LauncherSettingsActivationDiagnostics(
    string DetectedRuntime,
    string SemanticGroupingStatus,
    string SemanticGroupingReason,
    string SettingsLayoutName,
    string? ProviderId = null,
    string? ProviderDisplayName = null,
    string? ReleaseChannelDisplayName = null,
    string? ReleaseRepository = null);

public interface ILauncherSettingsLayoutProvider
{
    string Id { get; }

    string DisplayName { get; }

    IReadOnlyList<LauncherSettingsSectionDefinition> Sections { get; }

    bool ShowGroupHeadings { get; }

    LauncherSettingsPlacement Place(LauncherConfigurationSetting setting);
}

public sealed class PrincipalCatalogSettingsLayoutProvider :
    ILauncherSettingsLayoutProvider
{
    private static readonly ReadOnlyCollection<LauncherSettingsSectionDefinition>
        SectionDefinitions = Array.AsReadOnly<LauncherSettingsSectionDefinition>(
        [
            new(
                LauncherSettingsSection.General,
                "General",
                "Core mod behavior and ordinary preferences.",
                "General settings"),
            new(
                LauncherSettingsSection.Interface,
                "Interface",
                "Game interface behavior and quality-of-life controls.",
                "Interface settings"),
            new(
                LauncherSettingsSection.Graphics,
                "Graphics",
                "Display, scaling, loading, and zoom behavior.",
                "Graphics settings"),
            new(
                LauncherSettingsSection.Notifications,
                "Notifications",
                "Choose which events alert you and how.",
                "Notification settings"),
            new(
                LauncherSettingsSection.Hotkeys,
                "Hotkeys",
                "Capture keyboard and mouse shortcuts with runtime-aware conflict checks.",
                "Hotkey settings"),
            new(
                LauncherSettingsSection.DataSync,
                "Data Sync",
                "Control supported sync feeds and destination behavior.",
                "Data Sync settings"),
            new(
                LauncherSettingsSection.Advanced,
                "Advanced",
                "Experimental, patch, diagnostic, and support-directed controls.",
                "Advanced settings"),
        ]);

    public string Id =>
        LauncherFeatureImplementations.PrincipalCatalogSettingsLayout;

    public string DisplayName => "Semantic";

    public IReadOnlyList<LauncherSettingsSectionDefinition> Sections =>
        SectionDefinitions;

    public bool ShowGroupHeadings => true;

    public LauncherSettingsPlacement Place(
        LauncherConfigurationSetting setting)
    {
        ArgumentNullException.ThrowIfNull(setting);
        var group = string.IsNullOrWhiteSpace(setting.Presentation.Group)
            ? "Uncategorized"
            : setting.Presentation.Group;
        return new(
            ResolveSection(setting),
            group,
            GroupOrder(group),
            setting.Presentation.Family?.Id ?? string.Empty,
            setting.Presentation.Family?.DisplayOrder ?? 500_000,
            setting.Presentation.Family?.MemberOrder ?? 0,
            setting.Presentation.Label,
            string.Equals(group, "Uncategorized", StringComparison.Ordinal));
    }

    private static int GroupOrder(string group) =>
        group switch
        {
            "Fleet" => 10,
            "Ships" => 20,
            "Navigation" => 30,
            "Camera" => 40,
            "Panels" => 50,
            "Chat" => 60,
            "Interface" => 70,
            "System" => 80,
            "Diagnostics" => 90,
            "Uncategorized" => 10_000,
            _ => 1_000,
        };

    private static LauncherSettingsSection ResolveSection(
        LauncherConfigurationSetting setting)
    {
        if (setting.Control == LauncherConfigurationControl.NotificationPolicy
            || string.Equals(
                setting.Category,
                "notifications",
                StringComparison.OrdinalIgnoreCase))
        {
            return LauncherSettingsSection.Notifications;
        }

        if (setting.Control == LauncherConfigurationControl.Keybinding
            || string.Equals(
                setting.Category,
                "input",
                StringComparison.OrdinalIgnoreCase))
        {
            return LauncherSettingsSection.Hotkeys;
        }

        return setting.Category.ToLowerInvariant() switch
        {
            "graphics" => LauncherSettingsSection.Graphics,
            "ui" or "buffs" => LauncherSettingsSection.Interface,
            "sync" or "sidecar" => LauncherSettingsSection.DataSync,
            "advanced" or "patches" or "battle_log_decoder" =>
                LauncherSettingsSection.Advanced,
            _ => setting.Stability is LauncherConfigurationStability.Advanced
                    or LauncherConfigurationStability.Experimental
                ? LauncherSettingsSection.Advanced
                : LauncherSettingsSection.General,
        };
    }
}

public sealed class AlphabeticalSettingsLayoutProvider :
    ILauncherSettingsLayoutProvider
{
    private static readonly ReadOnlyCollection<LauncherSettingsSectionDefinition>
        SectionDefinitions = Array.AsReadOnly<LauncherSettingsSectionDefinition>(
        [
            new(
                LauncherSettingsSection.General,
                "Settings",
                "All available settings, listed alphabetically.",
                "Alphabetical settings"),
        ]);

    public string Id =>
        LauncherFeatureImplementations.AlphabeticalSettingsLayout;

    public string DisplayName => "Alphabetical";

    public IReadOnlyList<LauncherSettingsSectionDefinition> Sections =>
        SectionDefinitions;

    public bool ShowGroupHeadings => false;

    public LauncherSettingsPlacement Place(
        LauncherConfigurationSetting setting)
    {
        ArgumentNullException.ThrowIfNull(setting);
        return new(
            LauncherSettingsSection.General,
            string.Empty,
            0,
            string.Empty,
            0,
            0,
            setting.Presentation.Label);
    }
}

public static class LauncherSettingsLayoutComposer
{
    public static ILauncherSettingsLayoutProvider Select(
        LauncherActivationPlan activationPlan,
        LauncherConfigurationCatalog? configurationCatalog = null)
    {
        ArgumentNullException.ThrowIfNull(activationPlan);
        var decision = activationPlan.GetDecision(
            LauncherFeatureIds.SemanticSettingsGrouping);
        var implementation = configurationCatalog?.ReviewedSettingsLayoutId
            ?? decision.SelectedImplementation;
        return implementation switch
        {
            LauncherFeatureImplementations.PrincipalCatalogSettingsLayout =>
                new PrincipalCatalogSettingsLayoutProvider(),
            LauncherFeatureImplementations.AlphabeticalSettingsLayout =>
                new AlphabeticalSettingsLayoutProvider(),
            _ => throw new InvalidOperationException(
                $"Settings presentation selected unknown implementation "
                + $"'{implementation}'."),
        };
    }
}
