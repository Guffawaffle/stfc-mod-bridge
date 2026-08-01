using System.Collections.Concurrent;

namespace STFCCommunityMod.Launcher.Core;

/// <summary>
/// Serializes launcher-owned operations within one process and across launcher
/// processes. A launch handoff can hold the same lease used by deployment so a
/// mod mutation cannot start between accepting Launch and prime.exe starting.
/// </summary>
public sealed class LauncherOperationLock
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> ProcessGates = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    private readonly string stateDirectory;

    public LauncherOperationLock(string stateDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDirectory);
        this.stateDirectory = Path.GetFullPath(stateDirectory);
    }

    public async ValueTask<IAsyncDisposable?> TryAcquireAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(stateDirectory);
        var gate = ProcessGates.GetOrAdd(stateDirectory, static _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        try
        {
            var stream = new FileStream(
                Path.Combine(stateDirectory, "operation.lock"),
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                1,
                FileOptions.Asynchronous);
            return new Lease(gate, stream);
        }
        catch (IOException)
        {
            gate.Release();
            return null;
        }
        catch
        {
            gate.Release();
            throw;
        }
    }

    private sealed class Lease(SemaphoreSlim gate, FileStream stream) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            stream.Dispose();
            gate.Release();
            return ValueTask.CompletedTask;
        }
    }
}
