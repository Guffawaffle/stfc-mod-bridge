namespace STFCCommunityMod.Launcher.Services;

internal interface IGameProcessStateMonitor : IDisposable
{
    event EventHandler? StateChanged;

    bool TryStart(IntPtr windowHandle);
}
