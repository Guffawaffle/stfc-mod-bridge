namespace STFCCommunityMod.Launcher.Core;

public interface ILauncherShellRefreshTarget
{
    void RefreshHome();

    void RefreshConfigurationAvailability();

    void ReloadConfigurationDocument();
}

public sealed class LauncherShellLifecycleController
{
    private readonly ILauncherShellRefreshTarget target;

    public LauncherShellLifecycleController(ILauncherShellRefreshTarget target)
    {
        this.target = target ?? throw new ArgumentNullException(nameof(target));
    }

    public void HandleGameProcessChanged()
    {
        target.RefreshHome();
    }

    public void HandleGameInstallationChanged()
    {
        target.RefreshHome();
        target.RefreshConfigurationAvailability();
        target.ReloadConfigurationDocument();
    }
}
