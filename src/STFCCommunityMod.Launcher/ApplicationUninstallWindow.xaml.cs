using System.Diagnostics;
using System.IO;
using System.Windows;
using STFCCommunityMod.Launcher.Core;

namespace STFCCommunityMod.Launcher;

public partial class ApplicationUninstallWindow : Window
{
    private readonly PerUserInstallLayout layout;

    public ApplicationUninstallWindow(PerUserInstallLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        this.layout = layout;
        InitializeComponent();
        ProgramDirectoryText.Text = layout.ProgramDirectory;
        StateDirectoryText.Text = layout.StateDirectory;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => Close();

    private void UninstallButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ScheduleCleanup(RemoveStateCheckBox.IsChecked == true);
            Application.Current.Shutdown();
        }
        catch (Exception exception) when (
            exception is IOException
                or InvalidOperationException
                or UnauthorizedAccessException
                or System.ComponentModel.Win32Exception)
        {
            MessageBox.Show(
                this,
                $"Uninstall could not start: {exception.Message}",
                "STFC Mod Bridge uninstall",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void ScheduleCleanup(bool removeState)
    {
        EnsureNoOtherBridgeProcess();
        var scriptPath = Path.Combine(layout.ProgramDirectory, "Uninstall-Launcher.ps1");
        if (!File.Exists(scriptPath))
        {
            throw new InvalidOperationException("The installed uninstall helper is missing. Run Setup again to repair it.");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = Path.Combine(
                Environment.SystemDirectory,
                "WindowsPowerShell",
                "v1.0",
                "powershell.exe"),
            CreateNoWindow = true,
            UseShellExecute = false,
            WorkingDirectory = Path.GetTempPath(),
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-WindowStyle");
        startInfo.ArgumentList.Add("Hidden");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(scriptPath);
        startInfo.ArgumentList.Add("-WaitForProcessId");
        startInfo.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("-Quiet");
        if (removeState)
        {
            startInfo.ArgumentList.Add("-RemoveState");
        }

        _ = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Windows did not start the uninstall cleanup helper.");
    }

    private static void EnsureNoOtherBridgeProcess()
    {
        var processes = Process.GetProcessesByName(ModBridgeProductIdentity.ProcessName);
        try
        {
            if (processes.Any(process => process.Id != Environment.ProcessId))
            {
                throw new InvalidOperationException("Close every other STFC Mod Bridge window before uninstalling it.");
            }
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
