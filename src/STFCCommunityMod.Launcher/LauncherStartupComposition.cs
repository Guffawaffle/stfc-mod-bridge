using STFCCommunityMod.Launcher.Core;

namespace STFCCommunityMod.Launcher;

internal sealed record LauncherStartupComposition(
    LauncherRuntimeProfile RuntimeProfile,
    LauncherActivationPlan ActivationPlan,
    LauncherBattleFeatureSnapshot BattleFeatures,
    ILauncherSettingsLayoutProvider SettingsLayout,
    LauncherSettingsActivationDiagnostics SettingsDiagnostics)
{
    public static LauncherStartupComposition Create(
        LauncherDistributionProvider provider,
        LauncherProviderReleaseChannel releaseChannel,
        ReviewedRuntimeActivation? reviewedRuntimeActivation = null,
        LauncherBattlePreferences? battlePreferences = null,
        LauncherConfigurationCatalog? configurationCatalog = null)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(releaseChannel);
        if (!provider.ReleaseChannels.TryGetValue(releaseChannel.Id, out var registeredChannel)
            || registeredChannel != releaseChannel)
        {
            throw new ArgumentException(
                $"Release channel '{releaseChannel.Id}' does not belong to provider '{provider.Id}'.",
                nameof(releaseChannel));
        }
        if (configurationCatalog is not null
            && !string.Equals(
                configurationCatalog.Source.StableId,
                provider.Id,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Configuration catalog '{configurationCatalog.Source.StableId}' does not belong "
                + $"to provider '{provider.Id}'.",
                nameof(configurationCatalog));
        }
        var manifestResourceName = provider.GetCapabilityStatus(
                LauncherProviderCapabilityIds.RuntimeManifest)
                == LauncherProviderCapabilityStatus.Supported
            && provider.RuntimeManifest.Status == LauncherProviderCapabilityStatus.Supported
            ? provider.RuntimeManifest.ResourceName
            : null;
        using var manifest = manifestResourceName is null
            ? null
            : typeof(LauncherStartupComposition)
                .Assembly
                .GetManifestResourceStream(manifestResourceName);
        var runtimeProfile = reviewedRuntimeActivation?.RuntimeProfile
            ?? LauncherRuntimeManifestDetector.Detect(
                manifest,
                manifestResourceName is null
                    ? $"provider:{provider.Id}:runtime-manifest-unknown"
                    : $"embedded:{manifestResourceName}");
        var activationPlan = reviewedRuntimeActivation?.ActivationPlan
            ?? LauncherFeatureResolver.Resolve(
                runtimeProfile,
                LauncherFeatureCatalog.All);
        var battleFeatures = LauncherBattleFeatureComposer.Compose(
            activationPlan,
            battlePreferences);
        var settingsLayout = LauncherSettingsLayoutComposer.Select(
            activationPlan,
            configurationCatalog);
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
            settingsLayout.DisplayName,
            provider.Id,
            provider.DisplayName,
            releaseChannel.DisplayName,
            releaseChannel.Repository);
        return new(
            runtimeProfile,
            activationPlan,
            battleFeatures,
            settingsLayout,
            settingsDiagnostics);
    }
}
