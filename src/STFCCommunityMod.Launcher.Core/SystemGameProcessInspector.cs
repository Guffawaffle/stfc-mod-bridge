using System.Diagnostics;
using System.ComponentModel;

namespace STFCCommunityMod.Launcher.Core;

public sealed class SystemGameProcessInspector : IGameProcessInspector
{
    private const string PrimeProcessName = "prime";
    private readonly Func<IReadOnlyList<GameProcessObservation>> captureProcesses;

    public SystemGameProcessInspector()
        : this(CaptureProcesses)
    {
    }

    internal SystemGameProcessInspector(
        Func<IReadOnlyList<GameProcessObservation>> captureProcesses)
    {
        this.captureProcesses = captureProcesses
            ?? throw new ArgumentNullException(nameof(captureProcesses));
    }

    public bool IsGameRunning(string gameDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameDirectory);
        var targetExecutable = Path.GetFullPath(Path.Combine(gameDirectory, "prime.exe"));
        foreach (var process in captureProcesses())
        {
            if (!process.IsInspectable)
            {
                // A prime.exe process that cannot be attributed safely blocks mutation.
                return true;
            }
            if (!string.IsNullOrWhiteSpace(process.ExecutablePath)
                && PathEquals(targetExecutable, process.ExecutablePath))
            {
                return true;
            }
        }

        return false;
    }

    private static IReadOnlyList<GameProcessObservation> CaptureProcesses()
    {
        var processes = Process.GetProcessesByName(PrimeProcessName);
        try
        {
            var observations = new List<GameProcessObservation>(processes.Length);
            foreach (var process in processes)
            {
                try
                {
                    var executablePath = process.MainModule?.FileName;
                    observations.Add(new(executablePath, !string.IsNullOrWhiteSpace(executablePath)));
                }
                catch (Exception exception) when (
                    exception is Win32Exception
                        or InvalidOperationException
                        or NotSupportedException)
                {
                    observations.Add(new(null, IsInspectable: false));
                }
            }

            return observations;
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }

    internal static bool PathEquals(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);

    internal sealed record GameProcessObservation(
        string? ExecutablePath,
        bool IsInspectable = true);
}
