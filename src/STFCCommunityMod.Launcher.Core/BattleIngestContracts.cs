using System.Collections.Frozen;

namespace STFCCommunityMod.Launcher.Core;

public static class BattleIngestProtocol
{
    public const string Version = "stfc.sidecar.ingest.v1";
    public const string Route = "/api/sidecar/ingest";
    public const string CompatibilityTokenHeader = "stfc-sync-token";
    public const string BattleEventsKind = "battle.events";
    public const string FleetRuntimeKind = "fleet.runtime";
    public const string TransportChunkKind = "transport.chunk";
    public const string SidecarEventsVersion = "stfc.sidecar.events.v0";
    public const string FleetRuntimeVersion = "stfc.fleet.runtime_snapshot.v1";
    public const string TransportChunkVersion = "stfc.sidecar.ingest.chunk.v1";
}

public sealed record BattleIngestCollectionDemand(
    bool BattleCollection,
    bool FleetCollection);

public sealed class BattleIngestActivation
{
    private readonly FrozenSet<string> acceptedKinds;

    private BattleIngestActivation(
        IEnumerable<string> acceptedKinds,
        bool reviewedFeatureComposition)
    {
        this.acceptedKinds = acceptedKinds.ToFrozenSet(StringComparer.Ordinal);
        IsReviewedFeatureComposition = reviewedFeatureComposition;
    }

    public bool ShouldListen => acceptedKinds.Count > 0;

    public IReadOnlySet<string> AcceptedKinds => acceptedKinds;

    public bool IsReviewedFeatureComposition { get; }

    public bool Accepts(string kind) => acceptedKinds.Contains(kind);

    public static BattleIngestActivation Resolve(
        LauncherRuntimeProfile runtime,
        BattleIngestCollectionDemand demand)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(demand);
        if (!runtime.HasCapability(LauncherCapabilityIds.SidecarIngestV1))
        {
            return new([], reviewedFeatureComposition: false);
        }

        var kinds = new List<string>(2);
        if (demand.BattleCollection
            && runtime.HasCapability(LauncherCapabilityIds.BattleCaptureV1))
        {
            kinds.Add(BattleIngestProtocol.BattleEventsKind);
        }
        if (demand.FleetCollection
            && runtime.HasCapability(LauncherCapabilityIds.FleetRuntimeSnapshotV1))
        {
            kinds.Add(BattleIngestProtocol.FleetRuntimeKind);
        }
        return new(kinds, reviewedFeatureComposition: false);
    }

    public static BattleIngestActivation Resolve(LauncherBattleFeatureSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var kinds = new List<string>(2);
        if (snapshot.BattleCollection.State == LauncherPlayerFeatureState.Enabled)
        {
            kinds.Add(BattleIngestProtocol.BattleEventsKind);
        }
        if (snapshot.FleetCollection.State == LauncherPlayerFeatureState.Enabled)
        {
            kinds.Add(BattleIngestProtocol.FleetRuntimeKind);
        }
        return new(kinds, reviewedFeatureComposition: true);
    }
}

public sealed record BattleIngestLimits(
    int MaximumRequestBytes,
    int MaximumBatchEvents,
    int MaximumChunkCount,
    int MaximumReassembledBytes,
    int MaximumPendingChunkBytes,
    int MaximumPendingChunkGroups,
    int MaximumQueuedBytes,
    int MaximumQueuedBatches,
    int MaximumConcurrentRequests,
    int RequestsPerWindow,
    TimeSpan RateWindow,
    TimeSpan RequestTimeout,
    TimeSpan PendingChunkTimeout,
    TimeSpan ShutdownDrainTimeout)
{
    private static readonly TimeSpan MaximumTimerDuration =
        TimeSpan.FromMilliseconds(uint.MaxValue - 1d);

    public static BattleIngestLimits Default { get; } = new(
        MaximumRequestBytes: 512 * 1024,
        MaximumBatchEvents: 256,
        MaximumChunkCount: 512,
        MaximumReassembledBytes: 16 * 1024 * 1024,
        MaximumPendingChunkBytes: 32 * 1024 * 1024,
        MaximumPendingChunkGroups: 8,
        MaximumQueuedBytes: 24 * 1024 * 1024,
        MaximumQueuedBatches: 32,
        MaximumConcurrentRequests: 16,
        RequestsPerWindow: 240,
        RateWindow: TimeSpan.FromSeconds(1),
        RequestTimeout: TimeSpan.FromSeconds(15),
        PendingChunkTimeout: TimeSpan.FromMinutes(2),
        ShutdownDrainTimeout: TimeSpan.FromSeconds(5));

    internal void Validate()
    {
        if (MaximumRequestBytes is < 1024 or > 4 * 1024 * 1024
            || MaximumBatchEvents is < 1 or > 4096
            || MaximumChunkCount is < 1 or > 4096
            || MaximumReassembledBytes < MaximumRequestBytes
            || MaximumReassembledBytes > 16 * 1024 * 1024
            || MaximumPendingChunkBytes < 2L * MaximumReassembledBytes
            || MaximumPendingChunkBytes > 64 * 1024 * 1024
            || MaximumPendingChunkGroups is < 1 or > 64
            || MaximumQueuedBytes < MaximumRequestBytes
            || MaximumQueuedBytes < MaximumReassembledBytes
            || MaximumQueuedBytes > 64 * 1024 * 1024
            || MaximumQueuedBatches is < 1 or > 1024
            || MaximumConcurrentRequests is < 1 or > 256
            || RequestsPerWindow is < 1 or > 10000
            || !IsSafeTimerDuration(RateWindow)
            || !IsSafeTimerDuration(RequestTimeout)
            || !IsSafeTimerDuration(PendingChunkTimeout)
            || !IsSafeTimerDuration(ShutdownDrainTimeout))
        {
            throw new ArgumentOutOfRangeException(nameof(BattleIngestLimits));
        }
    }

    private static bool IsSafeTimerDuration(TimeSpan value) =>
        value > TimeSpan.Zero && value <= MaximumTimerDuration;
}

public sealed record BattleIngestEnvelope(
    string ProtocolVersion,
    string Kind,
    string BatchId,
    DateTimeOffset ProducedAt,
    string SessionId,
    string Source,
    string ModVersion,
    string PayloadProtocol,
    ReadOnlyMemory<byte> ExactEnvelopeBytes,
    IReadOnlyList<ReadOnlyMemory<byte>> ExactEventBytes);

public sealed record BattleIngestCommitResult(int AcceptedRecords);

public interface IBattleIngestSink
{
    /// <summary>
    /// Atomically commits or rejects one already transport-validated envelope.
    /// Implementations must honor cancellation, complete or roll back any
    /// transaction before returning, and must not retain the supplied byte views.
    /// The host joins this operation during shutdown and cannot safely detach an
    /// in-process storage mutation.
    /// </summary>
    ValueTask<BattleIngestCommitResult> CommitAsync(
        BattleIngestEnvelope envelope,
        CancellationToken cancellationToken);
}

public enum BattleIngestListenerState
{
    Inactive,
    Starting,
    Listening,
    Stopping,
    Stopped,
    PortUnavailable,
    Failed,
}

public enum BattleIngestFailureCode
{
    None,
    Unauthorized,
    InvalidRequest,
    UnsupportedProtocol,
    PayloadTooLarge,
    RateLimited,
    Busy,
    ChunkConflict,
    BatchConflict,
    TimedOut,
    StorageRejected,
    PortUnavailable,
    StartFailed,
    ListenerFailed,
    ShutdownTimedOut,
}

public sealed record BattleIngestHealthSnapshot(
    BattleIngestListenerState ListenerState,
    int BoundPort,
    long AcceptedBatches,
    long DuplicateBatches,
    long RejectedRequests,
    int PendingChunkGroups,
    long PendingChunkBytes,
    int PendingBatches,
    long PendingBatchBytes,
    BattleIngestFailureCode LastFailure,
    string LastTransition);

public enum BattleIngestStartStatus
{
    Started,
    Inactive,
    PortUnavailable,
    Failed,
}

public sealed record BattleIngestStartResult(
    BattleIngestStartStatus Status,
    int BoundPort,
    BattleIngestFailureCode FailureCode);
