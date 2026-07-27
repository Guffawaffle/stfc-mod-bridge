namespace STFCCommunityMod.Launcher.Core;

public sealed class BoundedGameInstallCandidateProvider(IReadOnlyList<GameInstallCandidateSeed> candidates)
    : IGameInstallCandidateProvider
{
    public IEnumerable<GameInstallCandidateSeed> GetCandidates(CancellationToken cancellationToken)
    {
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return candidate;
        }
    }

    public static BoundedGameInstallCandidateProvider FromCurrentMachine()
    {
        var candidates = new List<GameInstallCandidateSeed>();
        AddEnvironmentOverride(candidates);
        AddConventionalLocations(candidates);
        return new(candidates);
    }

    private static void AddEnvironmentOverride(List<GameInstallCandidateSeed> candidates)
    {
        var overridePath = Environment.GetEnvironmentVariable("STFC_GAME_DIRECTORY");
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            candidates.Add(
                CreateSeed(
                    overridePath,
                    GameInstallCandidateSource.EnvironmentOverride,
                    GameInstallConfidence.EnvironmentProvided,
                    "STFC_GAME_DIRECTORY explicitly identifies this candidate."));
        }
    }

    private static void AddConventionalLocations(List<GameInstallCandidateSeed> candidates)
    {
        AddSpecialFolderCandidate(candidates, Environment.SpecialFolder.LocalApplicationData);
        AddSpecialFolderCandidate(candidates, Environment.SpecialFolder.ProgramFiles);
        AddSpecialFolderCandidate(candidates, Environment.SpecialFolder.ProgramFilesX86);

        var windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var systemDrive = Path.GetPathRoot(windowsDirectory);
        if (!string.IsNullOrWhiteSpace(systemDrive))
        {
            candidates.Add(
                CreateSeed(
                    Path.Combine(systemDrive, "Games", "Star Trek Fleet Command", "default", "game"),
                    GameInstallCandidateSource.ConventionalLocation,
                    GameInstallConfidence.Conventional,
                    "Bounded conventional C:\\Games installation location."));
        }
    }

    private static void AddSpecialFolderCandidate(
        List<GameInstallCandidateSeed> candidates,
        Environment.SpecialFolder specialFolder)
    {
        var root = Environment.GetFolderPath(specialFolder);
        if (string.IsNullOrWhiteSpace(root))
        {
            return;
        }

        candidates.Add(
            CreateSeed(
                Path.Combine(root, "Star Trek Fleet Command", "default", "game"),
                GameInstallCandidateSource.ConventionalLocation,
                GameInstallConfidence.Conventional,
                $"Bounded {specialFolder} installation location."));
    }

    private static GameInstallCandidateSeed CreateSeed(
        string path,
        GameInstallCandidateSource source,
        GameInstallConfidence confidence,
        string detail)
    {
        return new(path, [new(source, confidence, detail)]);
    }
}
