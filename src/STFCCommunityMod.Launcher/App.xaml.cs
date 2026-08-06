using System.IO;
using System.Windows;

namespace STFCCommunityMod.Launcher;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ConfigureSelfUpdateAcknowledgement(e.Args);
        var window = new MainWindow();
        MainWindow = window;
        window.Show();
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
