using STFCCommunityMod.Launcher.Core;

namespace STFCCommunityMod.Launcher.ViewModels;

internal sealed record HomeHealthProjection(
    string InstallationStatus,
    string ProviderCompatibilityStatus,
    string UpdateAvailabilityStatus,
    string GameCompatibilityStatus,
    string RuntimeActivationStatus,
    string NativeSupportStatus)
{
    public static HomeHealthProjection FromSnapshot(LauncherHealthSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return new(
            snapshot.ModManagement.Status,
            snapshot.ProviderCompatibility switch
            {
                LauncherProviderCompatibilityState.MatchesSelectedProvider => "Matches selected provider",
                LauncherProviderCompatibilityState.DifferentProvider => "Different provider",
                LauncherProviderCompatibilityState.Unattributed => "Unattributed",
                LauncherProviderCompatibilityState.NotApplicable => "Not applicable",
                _ => "Unknown",
            },
            snapshot.UpdateAvailability switch
            {
                ModUpdateEvidenceState.UpToDate => "Current",
                ModUpdateEvidenceState.UpdateAvailable => "Update available",
                ModUpdateEvidenceState.NotApplicable => "Not applicable",
                _ => "Unknown",
            },
            NativeStatus(snapshot.GameCompatibility),
            NativeStatus(snapshot.RuntimeActivation),
            NativeStatus(snapshot.NativeSupport));
    }

    private static string NativeStatus(LauncherNativeEvidenceState state) => state switch
    {
        LauncherNativeEvidenceState.Healthy => "Healthy",
        LauncherNativeEvidenceState.Degraded => "Degraded",
        LauncherNativeEvidenceState.Incompatible => "Incompatible",
        LauncherNativeEvidenceState.NotApplicable => "Not applicable",
        _ => "Unknown",
    };
}
