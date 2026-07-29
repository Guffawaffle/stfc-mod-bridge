using STFCCommunityMod.Launcher.Core;

namespace STFCCommunityMod.Launcher;

internal sealed record LauncherStartupComposition(
    LauncherRuntimeProfile RuntimeProfile,
    LauncherActivationPlan ActivationPlan,
    ILauncherSettingsLayoutProvider SettingsLayout,
    LauncherSettingsActivationDiagnostics SettingsDiagnostics)
{
    private const string GuffawaffleRuntimeManifestResource =
        "STFCCommunityMod.Launcher.RuntimeManifests.Guffawaffle.v1.json";

    public static LauncherStartupComposition CreateDefault()
    {
        using var manifest = typeof(LauncherStartupComposition)
            .Assembly
            .GetManifestResourceStream(GuffawaffleRuntimeManifestResource);
        var runtimeProfile = LauncherRuntimeManifestDetector.Detect(
            manifest,
            $"embedded:{GuffawaffleRuntimeManifestResource}");
        var activationPlan = LauncherFeatureResolver.Resolve(
            runtimeProfile,
            LauncherFeatureCatalog.All);
        var settingsLayout = LauncherSettingsLayoutComposer.Select(
            activationPlan);
        var semanticGrouping = activationPlan.GetDecision(
            LauncherFeatureIds.SemanticSettingsGrouping);
        var detectedRuntime = runtimeProfile.RuntimeVersion is null
            ? runtimeProfile.DistributionDisplayName
            : $"{runtimeProfile.DistributionDisplayName} {runtimeProfile.RuntimeVersion}";
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
