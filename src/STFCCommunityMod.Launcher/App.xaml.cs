using System.IO;
using System.Windows;
using STFCCommunityMod.Launcher.Core;

namespace STFCCommunityMod.Launcher;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Contains("--uninstall", StringComparer.OrdinalIgnoreCase))
        {
            ShowUninstall();
            return;
        }

        ConfigureSelfUpdateAcknowledgement(e.Args);
        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }

    private void ShowUninstall()
    {
        try
        {
            var layout = PerUserInstallLayout.FromCurrentUser();
            var expectedPath = Path.GetFullPath(Path.Combine(layout.ProgramDirectory, ModBridgeProductIdentity.ExecutableName));
            var processPath = Path.GetFullPath(Environment.ProcessPath
                ?? throw new InvalidOperationException("Windows did not provide the application path."));
            if (!string.Equals(expectedPath, processPath, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Run uninstall from Windows Installed Apps or the installed application.");
            }

            var window = new ApplicationUninstallWindow(layout);
            MainWindow = window;
            window.Show();
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            MessageBox.Show(
                exception.Message,
                "STFC Mod Bridge uninstall",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private void ConfigureSelfUpdateAcknowledgement(string[] arguments)
    {
        var index = Array.IndexOf(arguments, "--self-update-ack");
        if (index < 0 || index + 2 >= arguments.Length)
        {
            return;
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
    }

}
