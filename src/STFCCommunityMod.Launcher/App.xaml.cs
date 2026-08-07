using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using STFCCommunityMod.Launcher.Core;

namespace STFCCommunityMod.Launcher;

public partial class App : Application
{
    private IAsyncDisposable? recoveryHandoffLease;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (!IsBoundSelfUpdateChild(e.Args))
        {
            var layout = PerUserInstallLayout.FromLocalApplicationData(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
            var lease = new LauncherOperationLock(layout.StateDirectory)
                .TryAcquireAsync()
                .AsTask()
                .GetAwaiter()
                .GetResult();
            if (lease is null)
            {
                Shutdown();
                return;
            }
            var handedOff = false;
            try
            {
                var recovery = LauncherUpdateRecovery.InspectBeforeStartup(
                    layout.StateDirectory,
                    layout.ProgramDirectory);
                if (recovery is not null)
                {
                    var startInfo = new ProcessStartInfo(recovery.RunnerUpdater.Path)
                    {
                        UseShellExecute = false,
                        WorkingDirectory = Path.GetDirectoryName(recovery.RunnerUpdater.Path),
                        CreateNoWindow = true,
                    };
                    startInfo.ArgumentList.Add("--recover-journal");
                    startInfo.ArgumentList.Add(recovery.JournalPath);
                    startInfo.ArgumentList.Add("--journal-sha256");
                    startInfo.ArgumentList.Add(recovery.JournalSha256);
                    startInfo.ArgumentList.Add("--parent-process-id");
                    startInfo.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
                    _ = LauncherVerifiedExecutable.Start(recovery.RunnerUpdater, startInfo);
                    recoveryHandoffLease = lease;
                    handedOff = true;
                    Shutdown();
                    return;
                }
            }
            finally
            {
                if (!handedOff)
                {
                    lease.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }
            }
        }
        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        recoveryHandoffLease?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        base.OnExit(e);
    }

    private static bool IsBoundSelfUpdateChild(string[] arguments)
    {
        if (arguments.Length != 3 || arguments[0] != "--self-update-child")
        {
            return false;
        }
        var transactionId = arguments[2];
        if (!Guid.TryParseExact(transactionId, "N", out _))
        {
            return false;
        }
        string acknowledgementPath;
        try
        {
            acknowledgementPath = Path.GetFullPath(arguments[1]);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or IOException)
        {
            return false;
        }
        var layout = PerUserInstallLayout.FromCurrentUser();
        var expectedPath = Path.Combine(
            layout.StateDirectory,
            "self-update",
            transactionId,
            "startup.ack");
        return string.Equals(
            acknowledgementPath,
            Path.GetFullPath(expectedPath),
            StringComparison.OrdinalIgnoreCase);
    }

}
