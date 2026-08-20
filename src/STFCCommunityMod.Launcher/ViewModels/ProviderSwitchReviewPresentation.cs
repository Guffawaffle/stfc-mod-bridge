using STFCCommunityMod.Launcher.Core;

namespace STFCCommunityMod.Launcher.ViewModels;

internal sealed record ProviderSwitchReviewPresentation(
    string Summary,
    bool IsIntroductoryReview,
    bool HasFocusedWarning,
    bool IsBlocked = false)
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
        if (!preview.CanExecute)
        {
            return new(
                $"{configuration.SourceDisplayName} → {configuration.TargetDisplayName} · {targetChannelDisplayName}"
                + Environment.NewLine + Environment.NewLine
                + (preview.BlockedMessage
                    ?? "This provider switch is blocked until the release evidence is repaired."),
                IsIntroductoryReview: false,
                HasFocusedWarning: false,
                IsBlocked: true);
        }
        var warnings = configuration.Concerns
            .Where(concern => concern.Kind is
                LauncherProviderCompatibilityKind.Warning
                or LauncherProviderCompatibilityKind.Loss)
            .Select(concern => concern.Message)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var artifact = preview.Artifact is null
            ? preview.SourceInstallation.State == ModInstallationEvidenceState.ManualInstallation
                ? "The existing manual DLL will remain unchanged; only the preferred source and TOML profile will change."
                : "No managed DLL is installed; only the preferred source and TOML profile will change."
            : $"The managed DLL will change to release {preview.Artifact.ReleaseVersion}. STFC must remain closed until the switch completes.";
        var configurationSummary = configuration.ConfigurationPath is null
            ? "No TOML file is selected."
            : configuration.ConfigurationExisted == false
                ? configuration.ConfigurationKind == LauncherProviderSwitchConfigurationKind.RestoreProviderHistory
                    ? $"No TOML exists now at {configuration.ConfigurationPath}. The latest verified TOML for the selected source will be restored at that exact path."
                    : $"No TOML exists now at {configuration.ConfigurationPath}. Mod Bridge will recheck that exact path before switching."
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
