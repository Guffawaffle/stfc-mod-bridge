using System.IO;
using STFCCommunityMod.Launcher.Core;
using STFCCommunityMod.Launcher.ViewModels;

namespace STFCCommunityMod.Launcher.Tests;

[TestClass]
public sealed class GameInstallationViewModelTests
{
    [TestMethod]
    public async Task CheckedVersionAndStoppedInstallationAreBothRequiredBeforeUpdate()
    {
        using var fixture = new Fixture();
        fixture.State = "running";
        await fixture.ViewModel.CheckAsync();
        StringAssert.Contains(fixture.ViewModel.Status, "221 → 267");
        Assert.IsFalse(fixture.ViewModel.CanUpdate);
        fixture.State = "ready";
        await fixture.ViewModel.RefreshStatusAsync();
        Assert.IsTrue(fixture.ViewModel.CanUpdate);
        await fixture.ViewModel.UpdateAsync();
        Assert.AreEqual(267, fixture.LastMutation!.ExpectedVersion);
        Assert.AreEqual(fixture.Directory, fixture.LastMutation.GameDirectory);
        Assert.IsFalse(fixture.ViewModel.CanUpdate);
        Assert.AreEqual(1, fixture.Completed);
    }

    [TestMethod]
    public async Task ChoosingAnotherInstallationInvalidatesThePreviouslyCheckedUpdate()
    {
        using var fixture = new Fixture();
        await fixture.ViewModel.CheckAsync();
        Assert.IsTrue(fixture.ViewModel.CanUpdate);
        fixture.SelectedDirectory = Path.Combine(fixture.Directory, "different-game");
        fixture.ViewModel.SetTarget(fixture.SelectedDirectory);
        Assert.IsFalse(fixture.ViewModel.CanUpdate);
        await fixture.ViewModel.UpdateAsync();
        Assert.IsNull(fixture.LastMutation);
        Assert.AreEqual(fixture.SelectedDirectory, fixture.ViewModel.Target);
    }

    [TestMethod]
    public async Task UnfinishedJournalExposesRecoveryIndependentlyOfModLaunchReadiness()
    {
        using var fixture = new Fixture();
        fixture.State = "recovery-required";
        fixture.RequiresRecovery = true;
        await fixture.ViewModel.RefreshStatusAsync();
        Assert.IsTrue(fixture.ViewModel.CanRecover);
        Assert.IsFalse(fixture.ViewModel.CanUpdate);
        await fixture.ViewModel.RecoverAsync();
        Assert.AreEqual("recover-game-update", fixture.LastMutation!.Operation);
        Assert.AreEqual(fixture.Directory, fixture.LastMutation.GameDirectory);
    }

    [TestMethod]
    public async Task RestartAfterExecutableBackupStillOffersRecoveryAtTheSavedConfirmedInstallation()
    {
        using var fixture = new Fixture();
        var game = Path.Combine(fixture.Directory, "chosen-game");
        System.IO.Directory.CreateDirectory(game);
        File.WriteAllText(Path.Combine(game, "prime.exe"), "synthetic executable");
        var state = Path.Combine(fixture.Directory, "selection");
        new GameInstallDiscovery(new JsonGameInstallSelectionStore(state), []).ConfirmManualSelection(game);
        File.Move(Path.Combine(game, "prime.exe"), Path.Combine(game, "backup.exe"));
        var captured = new LauncherEnvironmentProbe(new StoppedInspector(),
            PerUserInstallLayout.FromLocalApplicationData(fixture.Directory),
            new GameInstallDiscovery(new JsonGameInstallSelectionStore(state), [])).Capture();
        Assert.IsNull(captured.SelectedGameDirectory);
        fixture.SelectedDirectory = captured.ConfirmedGameInstallationDirectory!;
        fixture.State = "recovery-required";
        fixture.RequiresRecovery = true;
        await fixture.ViewModel.RefreshStatusAsync();
        Assert.IsTrue(fixture.ViewModel.CanRecover);
        Assert.IsFalse(fixture.ViewModel.CanUpdate);
        await fixture.ViewModel.RecoverAsync();
        Assert.AreEqual(game, fixture.LastMutation!.GameDirectory);
        Assert.AreEqual("recover-game-update", fixture.LastMutation.Operation);
        Assert.AreEqual(1, fixture.Completed);
        Assert.IsFalse(File.Exists(Path.Combine(game, "prime.exe")), "The transport seam proves Bridge routing, not native journal rollback.");
    }

    private sealed class StoppedInspector : IGameProcessInspector
    {
        public GameProcessInspectionState Inspect(string gameDirectory) => GameProcessInspectionState.NotRunning;
    }

    private sealed class Fixture : IDisposable, IProfileCatalogTransport
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "bridge-game-ui-" + Guid.NewGuid().ToString("N"));
        public string SelectedDirectory { get; set; }
        public string State { get; set; } = "ready";
        public bool RequiresRecovery { get; set; }
        public ProfileCatalogRequest? LastMutation { get; private set; }
        public int Completed { get; private set; }
        public GameInstallationViewModel ViewModel { get; }
        public Fixture()
        {
            SelectedDirectory = Directory;
            ViewModel = new(new GameInstallationCoordinator(Directory, this), () => SelectedDirectory, () => true, () => Completed++);
            ViewModel.SetTarget(SelectedDirectory);
        }
        public ProfileCatalogResponse Request(ProfileCatalogRequest request)
        {
            if (request.Operation is "update-game" or "recover-game-update") LastMutation = request;
            return new(true, Installation: new(request.GameDirectory!, 221, State, "idle", 267, true,
                RequiresRecovery: RequiresRecovery));
        }
        public void Dispose()
        {
            if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, recursive: true);
        }
    }
}
