using STFCCommunityMod.Launcher.Core;

namespace STFCCommunityMod.Launcher.ViewModels;

internal sealed record ProviderSwitchReviewPresentation(
    string Summary,
    bool IsIntroductoryReview,
    bool HasFocusedWarning)
{
    public bool RequiresReview => IsIntroductoryReview || HasFocusedWarning;

    public static ProviderSwitchReviewPresentation From(
        LauncherProviderAtomicSwitchPreview preview,
        string targetChannelDisplayName,
        bool introductoryReviewAcknowledged)
    {
        ArgumentNullException.ThrowIfNull(preview);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetChannelDisplayName);

        var configuration = preview.Configuration;
        var warnings = configuration.Concerns
            .Where(concern => concern.Kind == LauncherProviderCompatibilityKind.Loss)
            .Select(concern => concern.Message)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var artifact = preview.Artifact is null
            ? "No managed DLL is installed; only the preferred source and TOML profile will change."
            : $"The managed DLL will change to release {preview.Artifact.ReleaseVersion}. STFC must remain closed until the switch completes.";
        var configurationSummary = configuration.ConfigurationPath is null
            ? "No TOML file is selected."
            : configuration.ConfigurationKind == LauncherProviderSwitchConfigurationKind.RestoreProviderHistory
                ? "The current TOML is preserved, then the latest verified TOML for the selected source is restored."
                : "The current TOML is preserved exactly for future restoration.";
        var warningSummary = warnings.Length == 0
            ? string.Empty
            : Environment.NewLine + Environment.NewLine
                + "Warning:" + Environment.NewLine
                + string.Join(Environment.NewLine, warnings.Select(warning => $"• {warning}"));

        return new(
            $"{configuration.SourceDisplayName} → {configuration.TargetDisplayName} · {targetChannelDisplayName}"
            + Environment.NewLine + Environment.NewLine
            + artifact
            + Environment.NewLine
            + configurationSummary
            + warningSummary,
            IsIntroductoryReview: !introductoryReviewAcknowledged,
            HasFocusedWarning: warnings.Length > 0);
    }
}
