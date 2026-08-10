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

    public async ValueTask<LauncherOperationLease?> TryAcquireAsync(CancellationToken cancellationToken = default)
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
            return new LauncherOperationLease(stateDirectory, gate, stream);
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

}

public sealed class LauncherOperationLease : IAsyncDisposable
{
    private readonly object sync = new();
    private readonly string stateDirectory;
    private readonly SemaphoreSlim gate;
    private readonly FileStream stream;
    private TaskCompletionSource? disposalCompletion;
    private int activeOperations;
    private bool disposalRequested;
    private bool released;

    internal LauncherOperationLease(
        string stateDirectory,
        SemaphoreSlim gate,
        FileStream stream)
    {
        this.stateDirectory = stateDirectory;
        this.gate = gate;
        this.stream = stream;
    }

    internal IDisposable RetainFor(string expectedStateDirectory)
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposalRequested, this);
            var comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            if (!string.Equals(
                    stateDirectory,
                    Path.GetFullPath(expectedStateDirectory),
                    comparison))
            {
                throw new InvalidOperationException(
                    "The launcher operation lease belongs to a different state root.");
            }
            activeOperations++;
            return new OperationScope(this);
        }
    }

    public ValueTask DisposeAsync()
    {
        Task? pending = null;
        var release = false;
        lock (sync)
        {
            if (released)
            {
                return ValueTask.CompletedTask;
            }
            disposalRequested = true;
            if (activeOperations == 0)
            {
                released = true;
                release = true;
            }
            else
            {
                disposalCompletion ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
                pending = disposalCompletion.Task;
            }
        }
        if (release)
        {
            Release();
        }
        return pending is null ? ValueTask.CompletedTask : new(pending);
    }

    private void ReleaseOperation()
    {
        TaskCompletionSource? completion = null;
        var release = false;
        lock (sync)
        {
            activeOperations--;
            if (activeOperations < 0)
            {
                throw new InvalidOperationException("The launcher operation lease scope was released twice.");
            }
            if (activeOperations == 0 && disposalRequested && !released)
            {
                released = true;
                release = true;
                completion = disposalCompletion;
            }
        }
        if (release)
        {
            try
            {
                Release();
                completion?.TrySetResult();
            }
            catch (Exception exception)
            {
                completion?.TrySetException(exception);
                throw;
            }
        }
    }

    private void Release()
    {
        try
        {
            stream.Dispose();
        }
        finally
        {
            gate.Release();
        }
    }

    private sealed class OperationScope(LauncherOperationLease owner) : IDisposable
    {
        private LauncherOperationLease? owner = owner;

        public void Dispose() => Interlocked.Exchange(ref owner, null)?.ReleaseOperation();
    }
}
