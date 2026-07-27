namespace STFCCommunityMod.Launcher.Core;

public interface IGameInstallCandidateProvider
{
    IEnumerable<GameInstallCandidateSeed> GetCandidates(CancellationToken cancellationToken);
}
