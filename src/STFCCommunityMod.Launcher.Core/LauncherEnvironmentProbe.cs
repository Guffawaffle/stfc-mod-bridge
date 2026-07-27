namespace STFCCommunityMod.Launcher.Core;

public sealed class LauncherEnvironmentProbe(IGameProcessInspector processInspector, PerUserInstallLayout installLayout)
{
    public LauncherEnvironmentSnapshot Capture()
    {
        var gameRunning = processInspector.IsGameRunning();
        return gameRunning
            ? new(
                LauncherHealthCode.GameRunning,
                "GAME CLIENT DETECTED",
                "STFC is running. Read-only checks remain available; deployment changes will be blocked.",
                true,
                installLayout)
            : new(
                LauncherHealthCode.ReadyForDiscovery,
                "READY FOR DISCOVERY",
                "No STFC process is running. The next delivery node can perform bounded installation discovery.",
                false,
                installLayout);
    }
}
