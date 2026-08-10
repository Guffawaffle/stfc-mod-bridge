using System.ComponentModel;
using System.Diagnostics;

namespace STFCCommunityMod.Launcher.Core;

internal enum BattleRuntimeClientReceiptState
{
    Absent,
    Ready,
    Ambiguous,
    Unavailable,
}

internal sealed record BattleRuntimeClientReceiptResult(
    BattleRuntimeClientReceiptState State,
    BattleNamedPipeAuthorizedProcess? Receipt,
    string Code)
{
    public ExactProcessBattleNamedPipeClientAuthorizer CreateAuthorizer()
    {
        if (State != BattleRuntimeClientReceiptState.Ready || Receipt is null)
        {
            throw new InvalidOperationException(
                "A ready Battle runtime process receipt is required for authorization.");
        }
        return new([Receipt]);
    }
}

internal sealed record BattleRuntimeClientProcessObservation(
    int ProcessId,
    DateTimeOffset ProcessStartUtc,
    string? ExecutablePath,
    bool IsInspectable = true);

/// <summary>
/// Captures one exact currently-running STFC process receipt for the selected
/// game installation. Discovery does not start a process, listener, monitor, or
/// background task and does not infer runtime evidence.
/// </summary>
internal sealed class SystemBattleRuntimeClientReceiptProvider
{
    private const string PrimeProcessName = "prime";
    private const int MaximumObservedProcesses = 64;
    private readonly Func<IReadOnlyList<BattleRuntimeClientProcessObservation>> captureProcesses;

    public SystemBattleRuntimeClientReceiptProvider()
        : this(CaptureProcesses)
    {
    }

    internal SystemBattleRuntimeClientReceiptProvider(
        Func<IReadOnlyList<BattleRuntimeClientProcessObservation>> captureProcesses)
    {
        this.captureProcesses = captureProcesses
            ?? throw new ArgumentNullException(nameof(captureProcesses));
    }

    public BattleRuntimeClientReceiptResult Discover(
        string gameDirectory,
        string runtimeEvidenceSha256)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameDirectory);
        var targetExecutable = Path.GetFullPath(Path.Combine(gameDirectory, "prime.exe"));
        var normalizedRuntimeEvidence = RequireSha256(runtimeEvidenceSha256);
        IReadOnlyList<BattleRuntimeClientProcessObservation> observations;
        try
        {
            observations = captureProcesses()
                ?? throw new InvalidOperationException("Battle runtime process capture returned no result.");
        }
        catch (Exception exception) when (
            exception is Win32Exception
                or InvalidOperationException
                or NotSupportedException
                or IOException
                or UnauthorizedAccessException)
        {
            return Unavailable();
        }

        if (observations.Count > MaximumObservedProcesses)
        {
            return Unavailable();
        }

        var processIds = new HashSet<int>();
        var matches = new List<BattleNamedPipeAuthorizedProcess>(2);
        foreach (var observation in observations)
        {
            if (observation is null
                || !observation.IsInspectable
                || observation.ProcessId <= 0
                || observation.ProcessStartUtc.Offset != TimeSpan.Zero
                || string.IsNullOrWhiteSpace(observation.ExecutablePath)
                || !Path.IsPathFullyQualified(observation.ExecutablePath)
                || !processIds.Add(observation.ProcessId))
            {
                return Unavailable();
            }

            string observedExecutable;
            try
            {
                observedExecutable = Path.GetFullPath(observation.ExecutablePath);
            }
            catch (Exception exception) when (
                exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return Unavailable();
            }
            if (!SystemGameProcessInspector.PathEquals(targetExecutable, observedExecutable))
            {
                continue;
            }

            try
            {
                matches.Add(new(
                    unchecked((uint)observation.ProcessId),
                    observation.ProcessStartUtc,
                    observedExecutable,
                    normalizedRuntimeEvidence));
            }
            catch (ArgumentException)
            {
                return Unavailable();
            }
        }

        return matches.Count switch
        {
            0 => new(BattleRuntimeClientReceiptState.Absent, null, "battle-runtime-client-absent"),
            1 => new(BattleRuntimeClientReceiptState.Ready, matches[0], "battle-runtime-client-ready"),
            _ => new(BattleRuntimeClientReceiptState.Ambiguous, null, "battle-runtime-client-ambiguous"),
        };
    }

    private static IReadOnlyList<BattleRuntimeClientProcessObservation> CaptureProcesses()
    {
        var processes = Process.GetProcessesByName(PrimeProcessName);
        try
        {
            var observations = new List<BattleRuntimeClientProcessObservation>(processes.Length);
            foreach (var process in processes)
            {
                try
                {
                    var executablePath = process.MainModule?.FileName;
                    observations.Add(new(
                        process.Id,
                        new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero),
                        executablePath,
                        !string.IsNullOrWhiteSpace(executablePath)));
                }
                catch (Exception exception) when (
                    exception is Win32Exception
                        or InvalidOperationException
                        or NotSupportedException
                        or IOException
                        or UnauthorizedAccessException)
                {
                    observations.Add(new(
                        TryGetProcessId(process),
                        DateTimeOffset.UnixEpoch,
                        null,
                        IsInspectable: false));
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

    private static int TryGetProcessId(Process process)
    {
        try
        {
            return process.Id;
        }
        catch (InvalidOperationException)
        {
            return 0;
        }
    }

    private static BattleRuntimeClientReceiptResult Unavailable() =>
        new(BattleRuntimeClientReceiptState.Unavailable, null, "battle-runtime-client-unavailable");

    private static string RequireSha256(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length != 64 || value.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException("Runtime evidence SHA-256 is invalid.", nameof(value));
        }
        return value.ToLowerInvariant();
    }
}
