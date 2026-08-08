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
