using System.IO;
using STFCCommunityMod.Launcher.Core;
using STFCCommunityMod.Launcher.ViewModels;

namespace STFCCommunityMod.Launcher.Tests;

[TestClass]
public sealed class MainWindowProfileActionTests
{
    private const string DefaultId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string OtherId = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    [TestMethod]
    public void SelectedProfilesNextLaunchInstallationOverridesUnrelatedHomeSelection()
    {
        var selected = new LauncherProfile(OtherId, "Other", @"C:\next-game", ConfigPath: @"C:\profiles\other\settings.toml");
        var load = Load(selected);
        Assert.AreEqual(selected.GameDirectory,
            MainWindowViewModel.ResolveProfileGameDirectory(load, @"C:\home-game", @"C:\confirmed-home"));
        Assert.AreEqual(selected.ConfigPath,
            MainWindowViewModel.ResolveConfigurationFilePath(selected, selected.GameDirectory));
    }

    [TestMethod]
    public void DefaultUsesOrdinaryInstallationSettingsWhileDisplayNameNeverChoosesStorageKind()
    {
        var ordinary = DefaultProfile() with { Name = "Windows setup", ConfigPath = @"C:\wrong-isolated\settings.toml" };
        var directory = MainWindowViewModel.ResolveProfileGameDirectory(Load(ordinary), @"C:\home-game", null);
        Assert.AreEqual(@"C:\home-game", directory);
        Assert.AreEqual(Path.Combine(directory!, "community_patch_settings.toml"),
            MainWindowViewModel.ResolveConfigurationFilePath(ordinary, directory));

        var isolated = new LauncherProfile(OtherId, "Default", @"C:\isolated-game",
            ConfigPath: @"C:\profiles\isolated\settings.toml");
        Assert.AreEqual(isolated.ConfigPath,
            MainWindowViewModel.ResolveConfigurationFilePath(isolated, isolated.GameDirectory));
        Assert.IsNull(MainWindowViewModel.ResolveProfileGameDirectory(Load(isolated with { GameDirectory = "" }), @"C:\home-game", null));
    }

    [TestMethod]
    public void MissingNativeDefaultOrSelectedProfileNeverFallsBackToOrdinaryAccount()
    {
        var unavailable = new LauncherProfilesLoadResult(LauncherProfilesLoadState.Invalid, null, "Catalog unavailable");
        Assert.IsNull(MainWindowViewModel.ResolveProfileGameDirectory(unavailable, @"C:\home-game", @"C:\confirmed-home"));
        var missing = new LauncherProfilesLoadResult(LauncherProfilesLoadState.Loaded,
            new(OtherId, [DefaultProfile()]), "Selected profile missing");
        Assert.IsNull(MainWindowViewModel.ResolveProfileGameDirectory(missing, @"C:\home-game", null));
        Assert.IsNull(MainWindowViewModel.ResolveConfigurationFilePath(null, @"C:\home-game"));
    }

    [TestMethod]
    public void UnknownRegistrationDoesNotBecomeAReplacementSoftwareOrDefaultConfigurationTarget()
    {
        var selected = DefaultProfile() with { GameDirectory = @"C:\replaced-game",
            PreferredInstallationId = OtherId, InstallationState = "unknown" };
        var target = MainWindowViewModel.ResolveProfileGameDirectory(Load(selected), @"C:\other-game", null);
        Assert.IsNull(target);
        Assert.IsNull(MainWindowViewModel.ResolveConfigurationFilePath(selected, target));
        Assert.AreEqual(selected.GameDirectory,
            MainWindowViewModel.ResolveProfileGameDirectory(Load(selected with { InstallationState = "available" }), null, null));
    }

    [TestMethod]
    public void DefaultSavedTargetRemainsAvailableForRecoveryWhenExecutableIsAbsent()
    {
        var ordinary = DefaultProfile();
        Assert.AreEqual(@"C:\confirmed-home",
            MainWindowViewModel.ResolveProfileGameDirectory(Load(ordinary), null, @"C:\confirmed-home"));
        ordinary = ordinary with { GameDirectory = @"C:\preferred-missing-game" };
        Assert.AreEqual(ordinary.GameDirectory,
            MainWindowViewModel.ResolveProfileGameDirectory(Load(ordinary), @"C:\home-game", @"C:\confirmed-home"));
    }

    [TestMethod]
    public void RunningCardUsesExactSessionSnapshotWithoutRetargetingNextLaunch()
    {
        var profile = new LauncherProfile(OtherId, "Other", @"C:\next-game");
        var session = new ProfileSession(OtherId, 321, "ready", @"C:\running-game", 1234, @"C:\running-game\prime.exe");
        var card = MainWindowViewModel.ProjectProfileCard(profile, profile.GameDirectory, Choice(canExecute: false), [session], true);
        Assert.AreEqual("Focus Other", card.ActionLabel);
        Assert.AreEqual("Running", card.Status);
        Assert.IsTrue(card.CanLaunch, "Focusing an observed session does not mutate an installation.");
        Assert.AreEqual(profile.Id, card.Id);
        Assert.AreEqual(@"C:\next-game", card.GameDirectory);
        Assert.AreEqual(@"C:\running-game", card.RunningSessions[0].GameDirectory);
        Assert.AreEqual(1234L, card.RunningSessions[0].ProcessStartUtcTicks);
        StringAssert.Contains(card.SessionSummary, "321");
    }

    [TestMethod]
    public void MultipleDefaultSessionsRequireChoiceAndUnknownOrdinaryProcessIsNotClaimed()
    {
        var profile = DefaultProfile();
        var sessions = new[] { new ProfileSession(DefaultId, 11, "ordinary", @"C:\one"),
            new ProfileSession(DefaultId, 22, "ordinary", @"C:\two") };
        var known = MainWindowViewModel.ProjectProfileCard(profile, @"C:\one", Choice(false), sessions, false);
        Assert.AreEqual("Choose session for Default", known.ActionLabel);
        var unknown = MainWindowViewModel.ProjectProfileCard(profile, @"C:\one",
            Choice(false) with { NextAction = LauncherLaunchRecoveryAction.CloseRunningGame }, [], false);
        Assert.AreEqual("Running · inspect session", unknown.Status);
        Assert.AreEqual("Launch Default", unknown.ActionLabel);
        Assert.IsFalse(unknown.CanLaunch);
        Assert.AreEqual(0, unknown.RunningSessions.Count);
    }

    [TestMethod]
    public void MissingIsolationSupportExposesExactProfilesSetupActionWithoutPermittingOrdinaryLaunch()
    {
        var profile = new LauncherProfile(OtherId, "Other", @"C:\next-game");
        var card = MainWindowViewModel.ProjectProfileCard(profile, profile.GameDirectory,
            Choice(false) with { NextAction = LauncherLaunchRecoveryAction.SetUpProfileSupport }, [], false);
        Assert.IsFalse(card.CanLaunch);
        Assert.IsTrue(card.NeedsSetup);
        Assert.IsTrue(card.CanAct);
        Assert.AreEqual("Set up profile support for Other", card.ActionLabel);
        Assert.AreEqual(OtherId, card.Id);
        Assert.AreEqual(@"C:\next-game", card.GameDirectory);
        var missingInstallation = MainWindowViewModel.ProjectProfileCard(profile, profile.GameDirectory,
            Choice(false) with { NextAction = LauncherLaunchRecoveryAction.SelectGameFolder }, [], false);
        Assert.IsTrue(missingInstallation.NeedsSetup);
        Assert.IsTrue(missingInstallation.CanAct);
        Assert.IsFalse(missingInstallation.CanLaunch);
        Assert.AreEqual("Select installation for Other", missingInstallation.ActionLabel);
    }

    [TestMethod]
    public void IntactRecoveryStaysOnItsInstallationAndUnknownTargetBlocksMutationConservatively()
    {
        var configuration = Path.Combine(@"C:\game-a", "community_patch_settings.toml");
        Assert.IsTrue(MainWindowViewModel.IsRecoveryForInstallation(configuration, @"C:\game-a"));
        Assert.IsFalse(MainWindowViewModel.IsRecoveryForInstallation(configuration, @"C:\game-b"));
        Assert.IsTrue(MainWindowViewModel.IsRecoveryForInstallation(null, @"C:\game-b"));
        Assert.IsTrue(MainWindowViewModel.IsRecoveryForInstallation("relative.toml", @"C:\game-b"));
        Assert.IsTrue(MainWindowViewModel.IsRecoveryForInstallation(@"C:\game-a\foreign.toml", @"C:\game-b"));
        Assert.IsTrue(MainWindowViewModel.IsRecoveryForInstallation(configuration, null));
    }

    [TestMethod]
    public void RuntimeRemovalDependsOnOwnedBytesAndProcessSafetyRatherThanUpdateHealth()
    {
        var installed = new ModInstallationEvidence(ModInstallationEvidenceState.ManagedVerified, false,
            InstalledProviderId: "guffawaffle");
        Assert.IsTrue(MainWindowViewModel.ResolveUninstallAvailability(installed, "guffawaffle", false));
        Assert.IsFalse(MainWindowViewModel.ResolveUninstallAvailability(installed with { IsGameRunning = true }, "guffawaffle", false));
        Assert.IsFalse(MainWindowViewModel.ResolveUninstallAvailability(installed, "netniv", false));
        Assert.IsFalse(MainWindowViewModel.ResolveUninstallAvailability(installed, "guffawaffle", true));
        Assert.IsFalse(MainWindowViewModel.ResolveUninstallAvailability(installed with { State = ModInstallationEvidenceState.ManagedChanged }, "guffawaffle", false));
    }

    private static LauncherProfile DefaultProfile() => new(DefaultId, "Default", "",
        Kind: "windows-user", OwnerUserId: "S-1-5-21-synthetic", BuiltIn: true);
    private static LauncherProfilesLoadResult Load(LauncherProfile profile) => new(LauncherProfilesLoadState.Loaded,
        new(profile.Id, [profile]), null);
    private static GameLaunchPresentation Choice(bool canExecute) => new("Ready", LauncherHomeTone.Success, "Launch",
        canExecute, "Launch", LauncherLaunchTarget.PrimeExecutable, "Synthetic capability observation", LauncherLaunchRecoveryAction.None);
}
