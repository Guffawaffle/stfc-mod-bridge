using System.Collections.Frozen;

namespace STFCCommunityMod.Launcher.Core;

public enum LauncherPlayerFeatureState
{
    Unavailable,
    Available,
    Disabled,
    Enabled,
}

public sealed record LauncherPlayerFeatureProjection(
    string FeatureId,
    LauncherFeatureDecision Decision,
    LauncherPlayerFeaturePreference Preference,
    LauncherPlayerFeatureState State)
{
    public bool IsEligible => Decision.IsActive;

    public bool IsRequested => State == LauncherPlayerFeatureState.Enabled;
}

public sealed class LauncherBattleFeatureSnapshot
{
    private readonly FrozenDictionary<string, LauncherPlayerFeatureProjection> features;

    internal LauncherBattleFeatureSnapshot(
        LauncherActivationPlan activationPlan,
        IEnumerable<LauncherPlayerFeatureProjection> features)
    {
        ActivationPlan = activationPlan ?? throw new ArgumentNullException(nameof(activationPlan));
        this.features = features.ToFrozenDictionary(feature => feature.FeatureId, StringComparer.Ordinal);
    }

    public LauncherActivationPlan ActivationPlan { get; }

    public IReadOnlyDictionary<string, LauncherPlayerFeatureProjection> Features => features;

    public LauncherPlayerFeatureProjection BattleCollection =>
        features[LauncherFeatureIds.BattleCollection];

    public LauncherPlayerFeatureProjection FleetCollection =>
        features[LauncherFeatureIds.FleetCollection];

    public LauncherPlayerFeatureProjection GetFeature(string featureId) =>
        features.TryGetValue(featureId, out var feature)
            ? feature
            : throw new KeyNotFoundException($"Battle feature '{featureId}' is not composed.");

    public IReadOnlyList<LauncherDiagnosticFact> BuildDiagnosticFacts() =>
        features.Values
            .OrderBy(feature => feature.FeatureId, StringComparer.Ordinal)
            .Select(BuildDiagnosticFact)
            .ToArray();

    private LauncherDiagnosticFact BuildDiagnosticFact(LauncherPlayerFeatureProjection feature)
    {
        var name = feature.FeatureId switch
        {
            LauncherFeatureIds.BattleCollection => "Battle collection",
            LauncherFeatureIds.FleetCollection => "Fleet collection",
            _ => feature.FeatureId,
        };
        var summary = feature.State switch
        {
            LauncherPlayerFeatureState.Unavailable =>
                $"This feature is unavailable. {feature.Decision.Reason}",
            LauncherPlayerFeatureState.Available =>
                "The exact runtime and product policy make this feature available, but player preference is unset. "
                + "No collection resource is started.",
            LauncherPlayerFeatureState.Disabled =>
                "The exact runtime and product policy make this feature available, but the player disabled it. "
                + "No collection resource is started.",
            LauncherPlayerFeatureState.Enabled =>
                "The feature is eligible and player intent is enabled. Operational collection remains dormant until "
                + "the separately reviewed local IPC and lifecycle boundary is activated.",
            _ => throw new InvalidOperationException("The Battle feature projection state is unsupported."),
        };
        var nextAction = feature.State switch
        {
            LauncherPlayerFeatureState.Unavailable =>
                "Review runtime capability evidence; no provider transition runs passively.",
            LauncherPlayerFeatureState.Available =>
                "No action is exposed until the authenticated local IPC activation gate is accepted.",
            LauncherPlayerFeatureState.Disabled => "No action needed.",
            LauncherPlayerFeatureState.Enabled =>
                "No action is exposed until the authenticated local IPC activation gate is accepted.",
            _ => throw new InvalidOperationException("The Battle feature projection state is unsupported."),
        };
        var technicalDetail = string.Join(
            "; ",
            $"feature={feature.FeatureId}",
            $"state={feature.Decision.State}",
            $"reason={feature.Decision.EligibilityEvidence.Code}",
            $"policyDisposition={LauncherFeaturePolicyDispositionContract.ToWireValue(feature.Decision.PolicyDisposition)}",
            $"selectedImplementation={feature.Decision.SelectedImplementation}",
            $"preference={feature.Preference}",
            $"catalog={ActivationPlan.CatalogSource.Id}@{ActivationPlan.CatalogSource.Version}",
            $"policy={ActivationPlan.PolicySource.Id}@{ActivationPlan.PolicySource.Version}");
        return new(
            name,
            LauncherDiagnosticLevel.Informational,
            summary,
            nextAction,
            $"feature.{feature.FeatureId}",
            "launcher-activation-plan",
            technicalDetail);
    }
}

public static class LauncherBattleFeatureComposer
{
    public static LauncherBattleFeatureSnapshot Compose(
        LauncherActivationPlan activationPlan,
        LauncherBattlePreferences? preferences = null)
    {
        ArgumentNullException.ThrowIfNull(activationPlan);
        preferences ??= LauncherBattlePreferences.Default;
        return new(
            activationPlan,
            [
                ComposeFeature(
                    activationPlan,
                    LauncherFeatureIds.BattleCollection,
                    preferences.BattleCollection),
                ComposeFeature(
                    activationPlan,
                    LauncherFeatureIds.FleetCollection,
                    preferences.FleetCollection),
            ]);
    }

    private static LauncherPlayerFeatureProjection ComposeFeature(
        LauncherActivationPlan activationPlan,
        string featureId,
        LauncherPlayerFeaturePreference preference)
    {
        if (!Enum.IsDefined(preference))
        {
            throw new ArgumentOutOfRangeException(nameof(preference));
        }
        var decision = activationPlan.GetDecision(featureId);
        var state = !decision.IsActive
            ? LauncherPlayerFeatureState.Unavailable
            : preference switch
            {
                LauncherPlayerFeaturePreference.Unset => LauncherPlayerFeatureState.Available,
                LauncherPlayerFeaturePreference.Enabled => LauncherPlayerFeatureState.Enabled,
                LauncherPlayerFeaturePreference.Disabled => LauncherPlayerFeatureState.Disabled,
                _ => throw new ArgumentOutOfRangeException(nameof(preference)),
            };
        return new(featureId, decision, preference, state);
    }
}
