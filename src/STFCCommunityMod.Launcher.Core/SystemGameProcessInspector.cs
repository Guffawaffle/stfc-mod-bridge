using System.Diagnostics;

namespace STFCCommunityMod.Launcher.Core;

public sealed class SystemGameProcessInspector : IGameProcessInspector
{
    private const string PrimeProcessName = "prime";

    public bool IsGameRunning()
    {
        var processes = Process.GetProcessesByName(PrimeProcessName);
        try
        {
            return processes.Length > 0;
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }
}
