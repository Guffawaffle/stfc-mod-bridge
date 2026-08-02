using System.Diagnostics;
using System.IO;

namespace STFCCommunityMod.Launcher.Services;

internal interface IDiagnosticFolderService
{
    bool TryOpen(string? directory, out string message);
}

internal sealed class WindowsDiagnosticFolderService : IDiagnosticFolderService
{
    public bool TryOpen(string? directory, out string message)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            message = "The requested folder is not available.";
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                ArgumentList = { Path.GetFullPath(directory) },
                UseShellExecute = false,
            });
            message = "Opened the requested folder.";
            return true;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            message = "Windows could not open the requested folder.";
            return false;
        }
    }
}
