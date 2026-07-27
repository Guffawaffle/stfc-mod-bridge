using STFCCommunityMod.Launcher.Core;

namespace STFCCommunityMod.Launcher.Core.Tests;

[TestClass]
public sealed class GameInstallDiscoveryTests
{
    [TestMethod]
    public void DiscoverMergesDuplicateCandidatesAndRetainsProvenance()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        TemporaryDirectory.CreateFile(temporaryDirectory.Path, "prime.exe");
        var provider = new BoundedGameInstallCandidateProvider(
            [
                Seed(
                    temporaryDirectory.Path,
                    GameInstallCandidateSource.ConventionalLocation,
                    GameInstallConfidence.Conventional),
                Seed(
                    temporaryDirectory.Path + Path.DirectorySeparatorChar,
                    GameInstallCandidateSource.EnvironmentOverride,
                    GameInstallConfidence.EnvironmentProvided),
            ]);
        var discovery = new GameInstallDiscovery(new MemorySelectionStore(), [provider]);

        var result = discovery.Discover();

        Assert.AreEqual(1, result.Candidates.Count);
        Assert.AreEqual(2, result.Candidates[0].Evidence.Count);
        Assert.AreEqual(GameInstallConfidence.EnvironmentProvided, result.Candidates[0].Confidence);
        Assert.IsTrue(result.Candidates[0].Validation.IsValid);
    }

    [TestMethod]
    public void DiscoverNeverRecursesBelowProvidedCandidate()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var nestedGameDirectory = temporaryDirectory.CreateDirectory("unexpected", "deep", "game");
        TemporaryDirectory.CreateFile(nestedGameDirectory, "prime.exe");
        var provider = new BoundedGameInstallCandidateProvider(
            [
                Seed(
                    temporaryDirectory.Path,
                    GameInstallCandidateSource.ConventionalLocation,
                    GameInstallConfidence.Conventional),
            ]);
        var discovery = new GameInstallDiscovery(new MemorySelectionStore(), [provider]);

        var result = discovery.Discover();

        Assert.AreEqual(1, result.Candidates.Count);
        Assert.IsFalse(result.Candidates[0].Validation.IsValid);
        Assert.AreEqual(temporaryDirectory.Path, result.Candidates[0].GameDirectory);
    }

    [TestMethod]
    public void DiscoverHonorsCancellationBetweenBoundedCandidates()
    {
        var provider = new BoundedGameInstallCandidateProvider(
            [
                Seed(
                    "C:\\one",
                    GameInstallCandidateSource.ConventionalLocation,
                    GameInstallConfidence.Conventional),
            ]);
        var discovery = new GameInstallDiscovery(new MemorySelectionStore(), [provider]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.ThrowsException<OperationCanceledException>(
            () => discovery.Discover(cancellation.Token));
    }

    [TestMethod]
    public void ConfirmManualSelectionPersistsOnlyValidTarget()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var validDirectory = temporaryDirectory.CreateDirectory("valid");
        TemporaryDirectory.CreateFile(validDirectory, "prime.exe");
        var store = new MemorySelectionStore();
        var discovery = new GameInstallDiscovery(store, []);

        var invalid = discovery.ConfirmManualSelection(
            Path.Combine(temporaryDirectory.Path, "missing"));
        var valid = discovery.ConfirmManualSelection(validDirectory);

        Assert.IsFalse(invalid.Validation.IsValid);
        Assert.IsTrue(valid.Validation.IsValid);
        Assert.AreEqual(validDirectory, store.SavedDirectory);
        Assert.AreEqual(1, store.SaveCount);
    }

    [TestMethod]
    public void DiscoverRevalidatesPersistedSelection()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var removedDirectory = Path.Combine(temporaryDirectory.Path, "removed");
        var store = new MemorySelectionStore(
            GameInstallSelectionLoadResult.Loaded(
                new(removedDirectory, DateTimeOffset.UtcNow)));
        var discovery = new GameInstallDiscovery(store, []);

        var result = discovery.Discover();

        Assert.AreEqual(1, result.Candidates.Count);
        Assert.AreEqual(
            GameInstallValidationCode.DirectoryMissing,
            result.Candidates[0].Validation.Code);
        Assert.AreEqual(
            GameInstallCandidateSource.PersistedSelection,
            result.Candidates[0].Evidence[0].Source);
    }

    private static GameInstallCandidateSeed Seed(
        string path,
        GameInstallCandidateSource source,
        GameInstallConfidence confidence)
    {
        return new(path, [new(source, confidence, source.ToString())]);
    }

    private sealed class MemorySelectionStore(
        GameInstallSelectionLoadResult? loadResult = null) : IGameInstallSelectionStore
    {
        private readonly GameInstallSelectionLoadResult result =
            loadResult ?? GameInstallSelectionLoadResult.Missing();

        public string? SavedDirectory { get; private set; }

        public int SaveCount { get; private set; }

        public GameInstallSelectionLoadResult Load() => result;

        public void Save(string gameDirectory)
        {
            SavedDirectory = gameDirectory;
            SaveCount++;
        }
    }
}
