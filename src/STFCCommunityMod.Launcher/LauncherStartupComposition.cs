using STFCCommunityMod.Launcher.Core;

namespace STFCCommunityMod.Launcher;

internal sealed record LauncherStartupComposition(
    LauncherRuntimeProfile RuntimeProfile,
    LauncherActivationPlan ActivationPlan,
    ILauncherSettingsLayoutProvider SettingsLayout,
    LauncherSettingsActivationDiagnostics SettingsDiagnostics)
{
    public static LauncherStartupComposition Create(
        LauncherDistributionProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        using var manifest = typeof(LauncherStartupComposition)
            .Assembly
            .GetManifestResourceStream(provider.RuntimeManifestResourceName);
        var runtimeProfile = LauncherRuntimeManifestDetector.Detect(
            manifest,
            $"embedded:{provider.RuntimeManifestResourceName}");
        var activationPlan = LauncherFeatureResolver.Resolve(
            runtimeProfile,
            LauncherFeatureCatalog.All);
        var settingsLayout = LauncherSettingsLayoutComposer.Select(
            activationPlan);
        var semanticGrouping = activationPlan.GetDecision(
            LauncherFeatureIds.SemanticSettingsGrouping);
        var detectedDistributionName = string.Equals(
            runtimeProfile.DistributionId,
            provider.RuntimeDistributionId,
            StringComparison.Ordinal)
            ? provider.DisplayName
            : runtimeProfile.DistributionDisplayName;
        var detectedRuntime = runtimeProfile.RuntimeVersion is null
            ? detectedDistributionName
            : $"{detectedDistributionName} {runtimeProfile.RuntimeVersion}";
        var settingsDiagnostics = new LauncherSettingsActivationDiagnostics(
            detectedRuntime,
            semanticGrouping.IsActive ? "Active" : "Inactive",
            semanticGrouping.Reason,
            settingsLayout.DisplayName);
        return new(
            runtimeProfile,
            activationPlan,
            settingsLayout,
            settingsDiagnostics);
    }
}
