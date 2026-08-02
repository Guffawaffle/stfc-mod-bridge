using STFCCommunityMod.Launcher.Core;
using STFCCommunityMod.Launcher.ViewModels;

namespace STFCCommunityMod.Launcher.Tests;

[TestClass]
public sealed class HomeHealthProjectionTests
{
    [TestMethod]
    public void ProjectionKeepsUnknownAndActionableStatesExplicit()
    {
        var snapshot = new LauncherHealthSnapshot(
            new(ModInstallationEvidenceState.ManagedVerified, true, "2.1.0.8"),
            LauncherProviderCompatibilityState.MatchesSelectedProvider,
            ModUpdateEvidenceState.UpdateAvailable,
            LauncherNativeEvidenceState.Incompatible,
            LauncherNativeEvidenceState.Unknown,
            LauncherNativeEvidenceState.Degraded,
            [],
            new(
                "Running degraded",
                LauncherHomeTone.Warning,
                "Unavailable",
                ModManagementActionKind.None,
                false,
                "Open Diagnostics."));

        var projection = HomeHealthProjection.FromSnapshot(snapshot);

        Assert.AreEqual("Running degraded", projection.InstallationStatus);
        Assert.AreEqual("Matches selected provider", projection.ProviderCompatibilityStatus);
        Assert.AreEqual("Update available", projection.UpdateAvailabilityStatus);
        Assert.AreEqual("Incompatible", projection.GameCompatibilityStatus);
        Assert.AreEqual("Unknown", projection.RuntimeActivationStatus);
        Assert.AreEqual("Degraded", projection.NativeSupportStatus);
    }
}
