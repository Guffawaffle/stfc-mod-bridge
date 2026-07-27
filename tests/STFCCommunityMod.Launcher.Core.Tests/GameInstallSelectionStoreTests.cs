using STFCCommunityMod.Launcher.Core;

namespace STFCCommunityMod.Launcher.Core.Tests;

[TestClass]
public sealed class GameInstallSelectionStoreTests
{
    [TestMethod]
    public void SaveAndLoadRoundTripNonAsciiPath()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var stateDirectory = temporaryDirectory.CreateDirectory("state");
        var gameDirectory = temporaryDirectory.CreateDirectory("ゲーム", "default", "game");
        var timestamp = new DateTimeOffset(2026, 7, 27, 5, 0, 0, TimeSpan.Zero);
        var store = new JsonGameInstallSelectionStore(
            stateDirectory,
            new FixedTimeProvider(timestamp));

        store.Save(gameDirectory);
        var result = store.Load();

        Assert.AreEqual(GameInstallSelectionState.Loaded, result.State);
        Assert.IsNotNull(result.Selection);
        Assert.AreEqual(gameDirectory, result.Selection.GameDirectory);
        Assert.AreEqual(timestamp, result.Selection.ConfirmedAtUtc);
    }

    [TestMethod]
    public void LoadInvalidJsonFailsClosed()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var stateDirectory = temporaryDirectory.CreateDirectory("state");
        File.WriteAllText(Path.Combine(stateDirectory, "install-selection.json"), "{ definitely not json");
        var store = new JsonGameInstallSelectionStore(stateDirectory);

        var result = store.Load();

        Assert.AreEqual(GameInstallSelectionState.Invalid, result.State);
        Assert.IsNull(result.Selection);
        StringAssert.Contains(result.Error, "could not be read");
    }

    [TestMethod]
    public void SaveReplacesExistingSelection()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var stateDirectory = temporaryDirectory.CreateDirectory("state");
        var firstDirectory = temporaryDirectory.CreateDirectory("first");
        var secondDirectory = temporaryDirectory.CreateDirectory("second");
        var store = new JsonGameInstallSelectionStore(stateDirectory);

        store.Save(firstDirectory);
        store.Save(secondDirectory);
        var result = store.Load();

        Assert.AreEqual(GameInstallSelectionState.Loaded, result.State);
        Assert.IsNotNull(result.Selection);
        Assert.AreEqual(secondDirectory, result.Selection.GameDirectory);
    }

    private sealed class FixedTimeProvider(DateTimeOffset timestamp) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => timestamp;
    }
}
