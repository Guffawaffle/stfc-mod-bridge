using System.Diagnostics;
using System.ComponentModel;

namespace STFCCommunityMod.Launcher.Core;

public sealed class SystemGameProcessInspector : IGameProcessIdentityInspector
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

    public GameProcessInspectionState Inspect(string gameDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameDirectory);
        var targetExecutable = Path.GetFullPath(Path.Combine(gameDirectory, "prime.exe"));
        var targetIsRunning = false;
        foreach (var process in captureProcesses())
        {
            if (!process.IsInspectable)
            {
                // A prime.exe process that cannot be attributed safely blocks mutation.
                return GameProcessInspectionState.Unattributable;
            }
            bool sameInstall;
            try
            {
                sameInstall = !string.IsNullOrWhiteSpace(process.ExecutablePath)
                    && PathEquals(targetExecutable, process.ExecutablePath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                or ArgumentException or NotSupportedException)
            {
                return GameProcessInspectionState.Unattributable;
            }
            if (sameInstall)
            {
                targetIsRunning = true;
            }
        }

        return targetIsRunning
            ? GameProcessInspectionState.RunningTarget
            : GameProcessInspectionState.NotRunning;
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
                    observations.Add(new(executablePath, !string.IsNullOrWhiteSpace(executablePath), process.Id));
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

    public IReadOnlyList<int>? CaptureTargetProcessIds(string gameDirectory)
    {
        var target = Path.GetFullPath(Path.Combine(gameDirectory, "prime.exe"));
        var ids = new List<int>();
        foreach (var process in captureProcesses())
        {
            if (!process.IsInspectable || process.ProcessId <= 0 || string.IsNullOrWhiteSpace(process.ExecutablePath)) return null;
            try { if (PathEquals(target, process.ExecutablePath)) ids.Add(process.ProcessId); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                or ArgumentException or NotSupportedException) { return null; }
        }
        return ids;
    }

    internal static bool PathEquals(string left, string right)
    {
        var first = Path.GetFullPath(left);
        var second = Path.GetFullPath(right);
        if (!string.Equals(Path.GetFileName(first), Path.GetFileName(second),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            return false;
        }
        return GameDirectoryIdentity.SameLocation(Path.GetDirectoryName(first)!, Path.GetDirectoryName(second)!);
    }

    internal sealed record GameProcessObservation(
        string? ExecutablePath,
        bool IsInspectable = true,
        int ProcessId = 0);
}
