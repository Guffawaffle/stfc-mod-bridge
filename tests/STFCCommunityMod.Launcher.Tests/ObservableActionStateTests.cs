using STFCCommunityMod.Launcher.ViewModels;
using STFCCommunityMod.Launcher.Core;
using STFCCommunityMod.Launcher.Controls;

namespace STFCCommunityMod.Launcher.Tests;

[TestClass]
public sealed class ObservableActionStateTests
{
    [TestMethod]
    public void CommandAvailabilityIsIndependentFromWorkingAndDuplicateActivationIsRejected()
    {
        var state = new ObservableActionState();

        Assert.AreEqual(ObservableActionStatus.Idle, state.Status);
        Assert.IsTrue(state.IsCommandAvailable);
        Assert.IsTrue(state.TryBegin("Refresh accepted."));
        Assert.AreEqual(ObservableActionStatus.Working, state.Status);
        Assert.IsTrue(state.IsCommandAvailable);
        Assert.IsFalse(state.TryBegin("Duplicate refresh accepted."));

        state.Complete(true, "Status changed.");

        Assert.AreEqual(ObservableActionStatus.CompletedChanged, state.Status);
        Assert.AreEqual("Status changed.", state.StatusText);
        Assert.AreEqual(state.StatusText, state.AutomationAnnouncement);
    }

    [TestMethod]
    public void UnchangedFailureAndUnavailableRemainTextuallyDistinct()
    {
        var state = new ObservableActionState();
        Assert.IsTrue(state.TryBegin("Checking."));
        state.Complete(false, "Already current.");
        Assert.AreEqual(ObservableActionStatus.CompletedUnchanged, state.Status);
        Assert.AreEqual("Already current.", state.StatusText);

        Assert.IsTrue(state.TryBegin("Retry accepted."));
        state.Fail("The service could not be reached. Retry when online.");
        Assert.AreEqual(ObservableActionStatus.Failed, state.Status);
        StringAssert.Contains(state.AutomationAnnouncement, "Retry");

        state.SetAvailability(false, "Select a game folder before installing the mod.");
        Assert.AreEqual(ObservableActionStatus.Unavailable, state.Status);
        Assert.IsFalse(state.IsCommandAvailable);
        Assert.IsFalse(state.TryBegin("Should not start."));

        state.SetAvailability(true, string.Empty);
        Assert.AreEqual(ObservableActionStatus.Idle, state.Status);
        Assert.IsTrue(state.IsCommandAvailable);
        Assert.IsFalse(state.HasStatus);
    }

    [TestMethod]
    public async Task ObservableCommandKeepsCanExecuteTrueWhileSuppressingDuplicateExecution()
    {
        var state = new ObservableActionState();
        var completion = new TaskCompletionSource<ObservableActionResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var invocationCount = 0;
        var command = new ObservableActionCommand(
            state,
            "Refresh accepted.",
            () =>
            {
                invocationCount++;
                return completion.Task;
            });

        command.Execute(null);
        command.Execute(null);

        Assert.AreEqual(1, invocationCount);
        Assert.IsTrue(command.CanExecute(null));
        Assert.AreEqual(ObservableActionStatus.Working, state.Status);

        completion.SetResult(ObservableActionResult.Unchanged("No changes were found."));
        await WaitUntilAsync(() => state.Status == ObservableActionStatus.CompletedUnchanged);

        Assert.AreEqual("No changes were found.", state.AutomationAnnouncement);
    }

    [TestMethod]
    public async Task ObservableCommandProjectsExceptionsAsDiscoverableFailure()
    {
        var state = new ObservableActionState();
        var command = new ObservableActionCommand(
            state,
            "Work accepted.",
            () => Task.FromException<ObservableActionResult>(new IOException("offline")),
            exception => $"Refresh failed: {exception.Message}. Retry when online.");

        command.Execute(null);
        await WaitUntilAsync(() => state.Status == ObservableActionStatus.Failed);

        Assert.AreEqual("Refresh failed: offline. Retry when online.", state.StatusText);
    }

    [TestMethod]
    public void ObservableCommandRaisesAvailabilityWithoutTreatingWorkingAsUnavailable()
    {
        var state = new ObservableActionState();
        var availabilityChanges = 0;
        var command = new ObservableActionCommand(
            state,
            "Work accepted.",
            () => Task.FromResult(ObservableActionResult.Unchanged("Current.")));
        command.CanExecuteChanged += (_, _) => availabilityChanges++;

        Assert.IsTrue(state.TryBegin("Work accepted."));
        Assert.AreEqual(0, availabilityChanges);
        Assert.IsTrue(command.CanExecute(null));
        state.Complete(false, "Current.");

        state.SetAvailability(false, "Select a game folder first.");

        Assert.AreEqual(1, availabilityChanges);
        Assert.IsFalse(command.CanExecute(null));
    }

    [TestMethod]
    public void AutomationAnnouncementChangesForAcceptedAndCompletedStatesOnlyOncePerTransition()
    {
        var state = new ObservableActionState();
        var announcements = new List<string>();
        state.PropertyChanged += (_, eventArgs) =>
        {
            if (eventArgs.PropertyName == nameof(ObservableActionState.AutomationAnnouncement))
            {
                announcements.Add(state.AutomationAnnouncement);
            }
        };

        Assert.IsTrue(state.TryBegin("Refresh accepted."));
        Assert.IsFalse(state.TryBegin("Duplicate."));
        state.Complete(false, "Status is up to date.");

        Assert.AreEqual(2, announcements.Count);
        Assert.AreEqual("Refresh accepted.", announcements[0]);
        Assert.AreEqual("Status is up to date.", announcements[1]);
    }

    [TestMethod]
    public void LauncherUpdateAndModFeedbackHaveIndependentAvailabilityAndStatus()
    {
        var channels = new LauncherActionFeedbackChannels();

        channels.Mod.SetAvailability(false, "Select a game folder first.");
        Assert.IsTrue(channels.LauncherUpdate.TryBegin("Checking for a launcher update…"));
        channels.LauncherUpdate.Complete(false, "The launcher is current.");

        Assert.AreEqual(ObservableActionStatus.Unavailable, channels.Mod.Status);
        Assert.IsFalse(channels.Mod.IsCommandAvailable);
        Assert.AreEqual(ObservableActionStatus.CompletedUnchanged, channels.LauncherUpdate.Status);
        Assert.IsTrue(channels.LauncherUpdate.IsCommandAvailable);
    }

    [TestMethod]
    public void LaunchFeedbackDoesNotOverwriteModOrLauncherUpdateFeedback()
    {
        var channels = new LauncherActionFeedbackChannels();
        channels.Mod.Fail("Mod update failed.");
        channels.LauncherUpdate.Complete(false, "Launcher is current.");

        Assert.IsTrue(channels.Launch.TryBegin("Launch accepted."));
        channels.Launch.Complete(true, "prime.exe started.");

        Assert.AreEqual("prime.exe started.", channels.Launch.StatusText);
        Assert.AreEqual("Mod update failed.", channels.Mod.StatusText);
        Assert.AreEqual("Launcher is current.", channels.LauncherUpdate.StatusText);
    }

    [TestMethod]
    public void HomeFeedbackUsesActiveAndMostRecentActionInsteadOfPermanentLaunchPriority()
    {
        var channels = new LauncherActionFeedbackChannels();
        var arbiter = new HomeActionFeedbackArbiter(channels.Mod, channels.Launch);
        var visibleTransitions = new List<string>();
        arbiter.PropertyChanged += (_, eventArgs) =>
        {
            if (eventArgs.PropertyName == nameof(HomeActionFeedbackArbiter.Text))
            {
                visibleTransitions.Add(arbiter.Text);
            }
        };
        channels.Launch.Complete(true, "prime.exe started.");
        Assert.AreEqual("prime.exe started.", arbiter.Text);

        Assert.IsTrue(channels.Mod.TryBegin("Mod update accepted."));
        Assert.AreEqual("Mod update accepted.", arbiter.Text);
        channels.Mod.Complete(false, "The mod is current.");

        Assert.AreEqual("The mod is current.", arbiter.Text);
        Assert.IsTrue(arbiter.HasFeedback);
        CollectionAssert.Contains(visibleTransitions, "prime.exe started.");
        CollectionAssert.Contains(visibleTransitions, "Mod update accepted.");
        CollectionAssert.Contains(visibleTransitions, "The mod is current.");
    }

    [TestMethod]
    public void ActiveLaunchTemporarilyWinsThenReturnsToNewestCompletion()
    {
        var channels = new LauncherActionFeedbackChannels();
        var arbiter = new HomeActionFeedbackArbiter(channels.Mod, channels.Launch);
        channels.Mod.Fail("Mod failed.");
        Assert.IsTrue(channels.Launch.TryBegin("Opening Scopely launcher…"));

        Assert.AreEqual("Opening Scopely launcher…", arbiter.Text);
        channels.Launch.Complete(false, "Scopely was already running.");

        Assert.AreEqual("Scopely was already running.", arbiter.Text);
    }

    [TestMethod]
    public void DisabledLaunchExplanationDoesNotBecomePersistentHomeStatus()
    {
        var channels = new LauncherActionFeedbackChannels();
        var arbiter = new HomeActionFeedbackArbiter(channels.Mod, channels.Launch);

        channels.Launch.SetAvailability(false, "The game is already running.");

        Assert.IsFalse(arbiter.HasFeedback);
        Assert.AreEqual(string.Empty, arbiter.Text);
    }

    [TestMethod]
    public void LaunchProjectionPreservesChangedAndNoChangeSemantics()
    {
        var presentation = new GameLaunchPresentation(
            "Official launcher available",
            LauncherHomeTone.Success,
            "Open Scopely launcher",
            true,
            "Open Scopely launcher",
            LauncherLaunchTarget.ScopelyLauncher,
            "Available.",
            LauncherLaunchRecoveryAction.None);

        var changed = MainWindowViewModel.ProjectLaunchResult(
            new GameLaunchHandoffResult(
                GameLaunchHandoffState.Completed,
                "Started.",
                presentation,
                Changed: true));
        var unchanged = MainWindowViewModel.ProjectLaunchResult(
            new GameLaunchHandoffResult(
                GameLaunchHandoffState.Completed,
                "Already running.",
                presentation,
                Changed: false));

        Assert.AreEqual(ObservableActionResultKind.Changed, changed.Kind);
        Assert.AreEqual(ObservableActionResultKind.Unchanged, unchanged.Kind);
    }

    [TestMethod]
    public void SuccessfulMaintenanceNoOpIsReportedAsUnchanged()
    {
        var channels = new LauncherActionFeedbackChannels();
        Assert.IsTrue(channels.Mod.TryBegin("Checking recovery state…"));

        channels.CompleteModDeployment(
            new(
                ModDeploymentResultState.Succeeded,
                "No incomplete mod transaction was found.",
                Changed: false));

        Assert.AreEqual(ObservableActionStatus.CompletedUnchanged, channels.Mod.Status);
        Assert.AreEqual("No incomplete mod transaction was found.", channels.Mod.StatusText);
    }

    [TestMethod]
    public void MaintenanceEntryPointsAreGatedWhileModWorkIsActive()
    {
        var channels = new LauncherActionFeedbackChannels();
        Assert.IsTrue(channels.CanStartModMaintenance(externallyAvailable: true, conflictingWork: false));
        Assert.IsTrue(channels.Mod.TryBegin("Installing…"));

        Assert.IsTrue(channels.Mod.IsCommandAvailable, "The focused primary command remains available.");
        Assert.IsFalse(channels.CanStartModMaintenance(externallyAvailable: true, conflictingWork: false));
    }

    [TestMethod]
    public void ComputedNotificationsAreNotRepeatedWhenTheirValuesDoNotChange()
    {
        var state = new ObservableActionState();
        var changes = new List<string?>();
        state.PropertyChanged += (_, eventArgs) => changes.Add(eventArgs.PropertyName);

        Assert.IsTrue(state.TryBegin("Accepted."));
        state.Complete(false, "Current.");
        state.Fail("Failed.");

        Assert.AreEqual(2, changes.Count(name => name == nameof(ObservableActionState.IsWorking)));
        Assert.AreEqual(1, changes.Count(name => name == nameof(ObservableActionState.HasStatus)));
        Assert.AreEqual(3, changes.Count(name => name == nameof(ObservableActionState.AutomationAnnouncement)));
    }

    [TestMethod]
    public void LiveRegionAnnouncementRecognizesAcceptedTransitionAndIgnoresDuplicates()
    {
        const string accepted = "Refresh accepted. Checking launcher status…";

        Assert.IsTrue(LiveRegionBehavior.IsAnnouncementTransition(string.Empty, accepted));
        Assert.IsFalse(LiveRegionBehavior.IsAnnouncementTransition(accepted, accepted));
        Assert.IsFalse(LiveRegionBehavior.IsAnnouncementTransition(accepted, string.Empty));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 100 && !condition(); ++attempt)
        {
            await Task.Delay(10);
        }

        Assert.IsTrue(condition(), "The observable action did not reach its expected state.");
    }
}
