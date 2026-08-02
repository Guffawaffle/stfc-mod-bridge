using STFCCommunityMod.Launcher.ViewModels;

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

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 100 && !condition(); ++attempt)
        {
            await Task.Delay(10);
        }

        Assert.IsTrue(condition(), "The observable action did not reach its expected state.");
    }
}
