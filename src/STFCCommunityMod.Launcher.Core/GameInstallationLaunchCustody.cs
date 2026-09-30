using System.Collections.Concurrent;
using System.Diagnostics;

namespace STFCCommunityMod.Launcher.Core;

/// <summary>Holds native installation access until the exact Process.Start child exits.</summary>
internal static class GameInstallationLaunchCustody
{
    private static readonly ConcurrentDictionary<Guid, HeldChild> Children = new();

    public static void Retain(IGameExecutableProcess child, IDisposable lease)
    {
        var id = Guid.NewGuid();
        var held = new HeldChild(child, lease);
        Children[id] = held;
        _ = ReleaseAfterExitAsync(id, held);
    }

    private static async Task ReleaseAfterExitAsync(Guid id, HeldChild held)
    {
        try { await held.Child.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); }
        catch (Exception exception)
        {
            // Never infer process exit after a failed lifetime observation. Keeping this
            // custody entry alive blocks this installation until Bridge exits.
            Trace.TraceError($"Game process exit could not be observed; installation custody remains held until Bridge exits: {exception.Message}");
            return;
        }
        try { await held.Child.DisposeAsync().ConfigureAwait(false); }
        finally { held.Lease.Dispose(); Children.TryRemove(id, out _); }
    }
    private sealed record HeldChild(IGameExecutableProcess Child, IDisposable Lease);
}
