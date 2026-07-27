namespace STFCCommunityMod.Launcher.Core;

public sealed record GameInstallDiscoverySnapshot(
    IReadOnlyList<GameInstallCandidate> Candidates,
    GameInstallSelectionLoadResult PersistedSelection)
{
    public IReadOnlyList<GameInstallCandidate> ValidCandidates =>
        Candidates.Where(candidate => candidate.Validation.IsValid).ToArray();
}

public sealed class GameInstallDiscovery(
    IGameInstallSelectionStore selectionStore,
    IReadOnlyList<IGameInstallCandidateProvider> providers)
{
    public GameInstallDiscoverySnapshot Discover(CancellationToken cancellationToken = default)
    {
        var selection = selectionStore.Load();
        var seeds = new List<GameInstallCandidateSeed>();
        if (selection is { State: GameInstallSelectionState.Loaded, Selection: not null })
        {
            seeds.Add(
                new(
                    selection.Selection.GameDirectory,
                    [
                        new(
                            GameInstallCandidateSource.PersistedSelection,
                            GameInstallConfidence.UserConfirmed,
                            $"User confirmed at {selection.Selection.ConfirmedAtUtc:u}."),
                    ]));
        }

        foreach (var provider in providers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            seeds.AddRange(provider.GetCandidates(cancellationToken));
        }

        var candidates = seeds
            .GroupBy(NormalizeForGrouping, StringComparer.OrdinalIgnoreCase)
            .Select(CreateCandidate)
            .OrderByDescending(candidate => candidate.Validation.IsValid)
            .ThenByDescending(candidate => candidate.Confidence)
            .ThenBy(candidate => candidate.GameDirectory, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new(candidates, selection);
    }

    public GameInstallCandidate ConfirmManualSelection(string gameDirectory)
    {
        var seed = new GameInstallCandidateSeed(
            gameDirectory,
            [
                new(
                    GameInstallCandidateSource.ManualSelection,
                    GameInstallConfidence.UserConfirmed,
                    "User selected this exact folder."),
            ]);
        var candidate = CreateCandidate([seed]);
        if (candidate.Validation.IsValid)
        {
            selectionStore.Save(candidate.GameDirectory);
        }

        return candidate;
    }

    private GameInstallCandidate CreateCandidate(IEnumerable<GameInstallCandidateSeed> groupedSeeds)
    {
        var seeds = groupedSeeds.ToArray();
        var validation = GameInstallValidator.Validate(seeds[0].GameDirectory);
        var evidence = seeds
            .SelectMany(seed => seed.Evidence)
            .Distinct()
            .OrderByDescending(item => item.Confidence)
            .ThenBy(item => item.Source)
            .ToArray();
        var confidence = evidence.Length == 0
            ? GameInstallConfidence.Conventional
            : evidence.Max(item => item.Confidence);

        return new(
            validation.GameDirectory,
            confidence,
            evidence,
            validation);
    }

    private static string NormalizeForGrouping(GameInstallCandidateSeed seed)
    {
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(seed.GameDirectory));
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or NotSupportedException
                or PathTooLongException)
        {
            return seed.GameDirectory;
        }
    }
}
