namespace STFCCommunityMod.Launcher.Core;

public enum GameProcessInspectionState
{
    NotRunning,
    RunningTarget,
    Unattributable,
}

public interface IGameProcessInspector
{
    GameProcessInspectionState Inspect(string gameDirectory);
}

public interface IGameProcessIdentityInspector : IGameProcessInspector
{
    IReadOnlyList<int>? CaptureTargetProcessIds(string gameDirectory);
}
