namespace STFCCommunityMod.Launcher.Core;

public enum GameProcessInspectionState
{
    NotRunning,
    RunningTarget,
    Unattributable,
}

public interface IGameProcessInspector
{
    bool IsGameRunning(string gameDirectory);

    GameProcessInspectionState Inspect(string gameDirectory) =>
        IsGameRunning(gameDirectory)
            ? GameProcessInspectionState.RunningTarget
            : GameProcessInspectionState.NotRunning;
}
