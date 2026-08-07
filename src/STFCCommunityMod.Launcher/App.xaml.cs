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
        if (!ConfigureSelfUpdateAcknowledgement(e.Args))
        {
            var layout = PerUserInstallLayout.FromLocalApplicationData(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
            var lease = new LauncherOperationLock(layout.StateDirectory)
                .TryAcquireAsync()
                .AsTask()
                .GetAwaiter()
                .GetResult();
            if (lease is not null)
            {
                var handedOff = false;
                try
                {
                    var recovery = LauncherUpdateRecovery.InspectBeforeStartup(
                        layout.StateDirectory,
                        layout.ProgramDirectory);
                    if (recovery is not null)
                    {
                        var startInfo = new ProcessStartInfo(recovery.RunnerPath)
                        {
                            UseShellExecute = false,
                            WorkingDirectory = Path.GetDirectoryName(recovery.RunnerPath),
                            CreateNoWindow = true,
                        };
                        startInfo.ArgumentList.Add("--recover-journal");
                        startInfo.ArgumentList.Add(recovery.JournalPath);
                        startInfo.ArgumentList.Add("--journal-sha256");
                        startInfo.ArgumentList.Add(recovery.JournalSha256);
                        startInfo.ArgumentList.Add("--parent-process-id");
                        startInfo.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
                        _ = Process.Start(startInfo)
                            ?? throw new InvalidOperationException("Windows did not start Mod Bridge recovery.");
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

    private bool ConfigureSelfUpdateAcknowledgement(string[] arguments)
    {
        var index = Array.IndexOf(arguments, "--self-update-ack");
        if (index < 0 || index + 2 >= arguments.Length)
        {
            return false;
        }
        var acknowledgementPath = arguments[index + 1];
        var transactionId = arguments[index + 2];
        EventHandler? activated = null;
        activated = (_, _) =>
        {
            Activated -= activated;
            try
            {
                var parent = Path.GetDirectoryName(Path.GetFullPath(acknowledgementPath));
                if (parent is not null && Directory.Exists(parent) && Guid.TryParseExact(transactionId, "N", out _))
                {
                    File.WriteAllText(acknowledgementPath, transactionId);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // The helper treats a missing acknowledgement as failed startup and rolls back.
            }
        };
        Activated += activated;
        return true;
    }

}
