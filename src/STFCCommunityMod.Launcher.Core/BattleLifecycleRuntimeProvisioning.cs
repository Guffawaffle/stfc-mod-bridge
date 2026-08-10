namespace STFCCommunityMod.Launcher.Core;

/// <summary>
/// Single-owner bridge from a completed marker-last lifecycle transaction to
/// the dormant runtime composition coordinator. It consumes only exact
/// resources already opened by their respective owners; it performs no path
/// discovery, creation, download, policy decision, or listener registration.
/// </summary>
internal sealed class BattleLifecycleRuntimeProvisioningFactory :
    IBattleRuntimeProvisioningFactory,
    IAsyncDisposable
{
    private const int Available = 0;
    private const int Claimed = 1;
    private const int CleanupPending = 2;
    private const int Disposed = 3;

    private readonly LauncherBattleFeatureSnapshot approvedFeatures;
    private readonly BattleRuntimeProvisioningOwnedLifetime ownedLifetime;
    private readonly BattleCredentialLease credentialLease;
    private readonly BattleRuntimeClientReceiptResult runtimeClient;
    private readonly IBattleIngestSink? battleSink;
    private readonly IBattleIngestSink? fleetSink;
    private readonly string pipeName;
    private readonly string runtimeEvidenceSha256;
    private readonly SemaphoreSlim lifecycleGate = new(1, 1);
    private int state;

    private BattleLifecycleRuntimeProvisioningFactory(
        LauncherBattleFeatureSnapshot approvedFeatures,
        BattleRuntimeProvisioningOwnedLifetime ownedLifetime,
        BattleCredentialLease credentialLease,
        BattleRuntimeClientReceiptResult runtimeClient,
        IBattleIngestSink? battleSink,
        IBattleIngestSink? fleetSink,
        string pipeName,
        string runtimeEvidenceSha256)
    {
        this.approvedFeatures = approvedFeatures;
        this.ownedLifetime = ownedLifetime;
        this.credentialLease = credentialLease;
        this.runtimeClient = runtimeClient;
        this.battleSink = battleSink;
        this.fleetSink = fleetSink;
        this.pipeName = pipeName;
        this.runtimeEvidenceSha256 = runtimeEvidenceSha256;
    }

    public static BattleLifecycleRuntimeProvisioningFactory Create(
        BattleLifecycleRuntimeHandoffReceipt cleanupReceipt,
        ReviewedRuntimeActivation runtimeActivation,
        LauncherBattleFeatureSnapshot committedFeatures,
        BattleIngestCredentialStore credentialStore,
        BattleRuntimeClientReceiptResult runtimeClient,
        IBattleIngestSink? battleSink,
        IBattleIngestSink? fleetSink,
        IAsyncDisposable sinkLifetime,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(cleanupReceipt);
        ArgumentNullException.ThrowIfNull(runtimeActivation);
        ArgumentNullException.ThrowIfNull(committedFeatures);
        ArgumentNullException.ThrowIfNull(credentialStore);
        ArgumentNullException.ThrowIfNull(runtimeClient);
        ArgumentNullException.ThrowIfNull(sinkLifetime);

        var marker = cleanupReceipt.Marker;
        if (marker.Stage != BattleLifecycleStage.CleanupPending
            || marker.OperationKind != BattleLifecycleOperationKind.FeatureActivation
            || marker.ImplementationVersion != BattleLifecycleActivationPreparer.ImplementationVersion
            || !marker.SharedTargetAfter
            || !ReferenceEquals(committedFeatures.ActivationPlan, runtimeActivation.ActivationPlan)
            || !ReferenceEquals(runtimeActivation.RuntimeProfile, runtimeActivation.ActivationPlan.Runtime))
        {
            throw Invalid();
        }

        ValidateCommittedFeatures(marker, committedFeatures);
        var activation = BattleIngestActivation.Resolve(committedFeatures);
        if (!activation.ShouldListen
            || activation.Accepts(BattleIngestProtocol.BattleEventsKind) != (battleSink is not null)
            || activation.Accepts(BattleIngestProtocol.FleetRuntimeKind) != (fleetSink is not null))
        {
            throw Invalid();
        }

        var credentialBinding = marker.Credential ?? throw Invalid();
        var credentialResource = marker.Resources.SingleOrDefault(resource => resource.Role == "ingest-credential")
            ?? throw Invalid();
        var expectedCredentialPath = Path.Combine(
            Path.GetDirectoryName(cleanupReceipt.RuntimePath) ?? throw Invalid(),
            BattleIngestCredentialCodec.FileName);
        if (!PathEquals(credentialStore.Path, expectedCredentialPath)
            || credentialResource.PrimaryRelativePath != $"battle/{BattleIngestCredentialCodec.FileName}"
            || credentialResource.After is not { } credentialIdentity
            || credentialIdentity.ByteCount != credentialBinding.ProtectedByteCount
            || credentialIdentity.Sha256 != credentialBinding.ProtectedSha256)
        {
            throw Invalid();
        }

        var runtimeReceipt = runtimeClient.Receipt;
        if (runtimeClient.State != BattleRuntimeClientReceiptState.Ready
            || runtimeReceipt is null
            || runtimeReceipt.RuntimeEvidenceSha256 != runtimeActivation.EvidenceSourceSha256)
        {
            throw Invalid();
        }

        var credentialLoad = credentialStore.Load();
        var credentialLease = credentialLoad.Lease;
        if (credentialLoad.State != BattleCredentialLoadState.Readable
            || credentialLease is null
            || credentialLease.Metadata.Generation != credentialBinding.Generation
            || credentialLease.Metadata.ProtectedByteCount != credentialBinding.ProtectedByteCount
            || credentialLease.Metadata.ProtectedSha256 != credentialBinding.ProtectedSha256)
        {
            credentialLease?.Dispose();
            throw Invalid();
        }
        try
        {
            var runtimeClaim = cleanupReceipt.RuntimeLease.ClaimForProvisioning(
                cleanupReceipt.RuntimePath,
                cleanupReceipt.RuntimeIdentity,
                marker.OwnerId);
            return new(
                committedFeatures,
                new(
                    runtimeClaim,
                    credentialLease,
                    sinkLifetime,
                    timeProvider ?? TimeProvider.System),
                credentialLease,
                runtimeClient,
                battleSink,
                fleetSink,
                credentialLease.Metadata.PipeName,
                runtimeActivation.EvidenceSourceSha256);
        }
        catch
        {
            credentialLease.Dispose();
            throw;
        }
    }

    public async ValueTask<BattleRuntimeProvisioningLease> OpenAsync(
        LauncherBattleFeatureSnapshot features,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(features);
        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(
                state == Disposed,
                this);
            if (state != Available)
            {
                throw new InvalidOperationException("The lifecycle provisioning handoff has already been claimed.");
            }
            if (!Equivalent(features, approvedFeatures))
            {
                throw new InvalidOperationException(
                    "The requested Battle feature snapshot does not match the committed lifecycle handoff.");
            }
            cancellationToken.ThrowIfCancellationRequested();
            var provisioning = new BattleRuntimeProvisioningLease(
                pipeName,
                credentialLease.Credential,
                runtimeEvidenceSha256,
                runtimeClient.CreateAuthorizer(),
                battleSink,
                fleetSink,
                ownedLifetime);
            state = Claimed;
            return provisioning;
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (state is Claimed or Disposed)
            {
                return;
            }
            state = CleanupPending;
            await ownedLifetime.DisposeAsync().ConfigureAwait(false);
            state = Disposed;
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    private static void ValidateCommittedFeatures(
        BattleLifecycleMarker marker,
        LauncherBattleFeatureSnapshot features)
    {
        var expectedAffected = marker.FeatureTransitions
            .Where(transition => transition.Before != transition.After)
            .Select(transition => transition.FeatureId)
            .ToArray();
        if (!marker.AffectedFeatureIds.SequenceEqual(expectedAffected, StringComparer.Ordinal))
        {
            throw Invalid();
        }
        foreach (var transition in marker.FeatureTransitions)
        {
            var feature = features.GetFeature(transition.FeatureId);
            if (feature.Preference != transition.After
                || feature.State == LauncherPlayerFeatureState.Enabled
                    != (transition.After == LauncherPlayerFeaturePreference.Enabled))
            {
                throw Invalid();
            }
        }
    }

    private static bool Equivalent(
        LauncherBattleFeatureSnapshot left,
        LauncherBattleFeatureSnapshot right) =>
        ReferenceEquals(left.ActivationPlan, right.ActivationPlan)
        && left.Features.Count == right.Features.Count
        && left.Features.All(item =>
            right.Features.TryGetValue(item.Key, out var value) && item.Value == value);

    private static bool PathEquals(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static InvalidDataException Invalid() =>
        new("The Battle lifecycle provisioning handoff is invalid.");
}

internal sealed class BattleRuntimeProvisioningOwnedLifetime : IAsyncDisposable
{
    private readonly SemaphoreSlim disposeGate = new(1, 1);
    private readonly TimeProvider timeProvider;
    private BattleRuntimeLockLease.BattleRuntimeLockProvisioningClaim? runtimeLease;
    private BattleCredentialLease? credentialLease;
    private IAsyncDisposable? sinkLifetime;

    internal BattleRuntimeProvisioningOwnedLifetime(
        BattleRuntimeLockLease.BattleRuntimeLockProvisioningClaim runtimeLease,
        BattleCredentialLease credentialLease,
        IAsyncDisposable sinkLifetime,
        TimeProvider timeProvider)
    {
        this.runtimeLease = runtimeLease ?? throw new ArgumentNullException(nameof(runtimeLease));
        this.credentialLease = credentialLease ?? throw new ArgumentNullException(nameof(credentialLease));
        this.sinkLifetime = sinkLifetime ?? throw new ArgumentNullException(nameof(sinkLifetime));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public async ValueTask DisposeAsync()
    {
        await disposeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (sinkLifetime is not null)
            {
                await sinkLifetime.DisposeAsync().ConfigureAwait(false);
                sinkLifetime = null;
            }
            if (runtimeLease is not null)
            {
                if (runtimeLease.Record.State == BattleRuntimeLockState.Running)
                {
                    var now = timeProvider.GetUtcNow();
                    if (now.Offset != TimeSpan.Zero)
                    {
                        now = now.ToUniversalTime();
                    }
                    await runtimeLease.MarkCleanAsync(now).ConfigureAwait(false);
                }
                await runtimeLease.DisposeAsync().ConfigureAwait(false);
                runtimeLease = null;
            }
            credentialLease?.Dispose();
            credentialLease = null;
        }
        finally
        {
            disposeGate.Release();
        }
    }
}
