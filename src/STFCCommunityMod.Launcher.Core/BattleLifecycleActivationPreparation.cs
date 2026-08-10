using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace STFCCommunityMod.Launcher.Core;

internal sealed record BattleLocalTargetChangeReviewReceipt
{
    private readonly string[] affectedFeatureIds;

    public BattleLocalTargetChangeReviewReceipt(
        string sourceSha256,
        IReadOnlyList<string> affectedFeatureIds,
        string pipeName)
    {
        ArgumentNullException.ThrowIfNull(affectedFeatureIds);
        if (sourceSha256.Length != 64
            || sourceSha256.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
        {
            throw new ArgumentException("The reviewed local-target source identity is invalid.", nameof(sourceSha256));
        }
        var features = affectedFeatureIds.ToArray();
        if (features.Length is <= 0 or > 2
            || !features.SequenceEqual(features.OrderBy(value => value, StringComparer.Ordinal))
            || features.Distinct(StringComparer.Ordinal).Count() != features.Length
            || features.Any(value => value is not (
                LauncherFeatureIds.BattleCollection or LauncherFeatureIds.FleetCollection)))
        {
            throw new ArgumentException("The reviewed local-target feature set is invalid.", nameof(affectedFeatureIds));
        }
        SourceSha256 = sourceSha256;
        this.affectedFeatureIds = features;
        PipeName = BattleLocalIpcProtocol.RequirePipeName(pipeName, nameof(pipeName));
    }

    public string SourceSha256 { get; }

    public IReadOnlyList<string> AffectedFeatureIds => affectedFeatureIds.ToArray();

    public string PipeName { get; }
}

internal sealed class BattleLifecycleActivationPreparation : IDisposable
{
    private byte[] configurationCandidate;
    private bool disposed;

    internal BattleLifecycleActivationPreparation(
        BattleLifecycleMarker marker,
        BattleRuntimeLockRecord runtimeRecord,
        BattleCredentialCandidate credentialCandidate,
        byte[] configurationCandidate)
    {
        Marker = marker;
        RuntimeRecord = runtimeRecord;
        CredentialCandidate = credentialCandidate;
        this.configurationCandidate = configurationCandidate;
    }

    public BattleLifecycleMarker Marker { get; }

    public BattleRuntimeLockRecord RuntimeRecord { get; }

    public BattleCredentialCandidate CredentialCandidate { get; }

    public ReadOnlyMemory<byte> ConfigurationCandidate
    {
        get
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return configurationCandidate;
        }
    }

    internal IReadOnlyDictionary<string, ReadOnlyMemory<byte>> CandidateBytes()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var credential = Marker.Resources.Single(resource => resource.Role == "ingest-credential");
        return new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal)
        {
            [credential.CandidateRelativePath!] = CredentialCandidate.ProtectedBytes,
            [Marker.Configuration!.CandidateRelativePath] = configurationCandidate,
        };
    }

    public void Dispose()
    {
        if (disposed) return;
        CredentialCandidate.Dispose();
        CryptographicOperations.ZeroMemory(configurationCandidate);
        configurationCandidate = [];
        disposed = true;
    }

    internal bool IsConfigurationZeroedForTest() =>
        configurationCandidate.Length == 0 || configurationCandidate.All(value => value == 0);
}

internal sealed class BattleLifecyclePreparedActivation : IAsyncDisposable
{
    private readonly BattleLifecycleActivationPreparation preparation;
    private readonly BattleRuntimeLockLease runtimeLease;
    private int disposed;

    internal BattleLifecyclePreparedActivation(
        BattleLifecycleActivationPreparation preparation,
        BattleRuntimeLockLease runtimeLease)
    {
        this.preparation = preparation;
        this.runtimeLease = runtimeLease;
    }

    public BattleLifecycleMarker Marker => preparation.Marker;

    public BattleRuntimeLockLease RuntimeLease => runtimeLease;

    public BattleCredentialLease CredentialLease => preparation.CredentialCandidate.Lease;

    internal ReadOnlyMemory<byte> ProtectedCredentialCandidate =>
        preparation.CredentialCandidate.ProtectedBytes;

    internal ReadOnlyMemory<byte> ConfigurationCandidate => preparation.ConfigurationCandidate;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        try
        {
            await runtimeLease.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            preparation.Dispose();
        }
    }
}

internal static class BattleLifecycleActivationPreparer
{
    internal const string ImplementationVersion = "battle-activation-prepare-v1";
    private const int MaximumConfigurationBytes = 8 * 1024 * 1024;
    private static readonly string[] FeatureIds =
    [
        LauncherFeatureIds.BattleCollection,
        LauncherFeatureIds.FleetCollection,
    ];
    private static readonly HashSet<string> AllowedMutationPaths = new(StringComparer.Ordinal)
    {
        "sidecar.sync.enabled",
        "sidecar.sync.transport",
        "sidecar.sync.pipe_name",
        "sidecar.sync.url",
        "sidecar.sync.token",
        "sidecar.sync.battlelogs_realtime",
        "sidecar.sync.fleet_runtime",
    };

    public static BattleLifecycleActivationPreparation Create(
        LauncherBattleFeatureSnapshot snapshot,
        IReadOnlyList<string> requestedFeatureIds,
        ConfigurationDocumentSnapshot configuration,
        string pipeName,
        BattleLocalTargetChangeReviewReceipt? existingLocalTargetReview,
        IBattleCredentialProtector credentialProtector,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(requestedFeatureIds);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(credentialProtector);
        var requested = requestedFeatureIds.ToArray();
        if (requested.Length is <= 0 or > 2
            || !requested.SequenceEqual(requested.OrderBy(value => value, StringComparer.Ordinal))
            || requested.Distinct(StringComparer.Ordinal).Count() != requested.Length
            || requested.Any(value => !FeatureIds.Contains(value, StringComparer.Ordinal)))
        {
            throw new ArgumentException("Requested Battle features must be a closed ordered set.", nameof(requestedFeatureIds));
        }
        foreach (var featureId in requested)
        {
            var feature = snapshot.GetFeature(featureId);
            if (!feature.Decision.IsActive || feature.Preference == LauncherPlayerFeaturePreference.Enabled)
            {
                throw new InvalidOperationException("Only an eligible, not-yet-enabled Battle feature can be prepared.");
            }
        }
        if (FeatureIds.Except(requested, StringComparer.Ordinal).Any(featureId =>
                snapshot.GetFeature(featureId).Preference == LauncherPlayerFeaturePreference.Enabled))
        {
            throw new InvalidOperationException(
                "Adding a Battle category to an active shared target requires its existing credential receipt.");
        }

        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        if (now.Offset != TimeSpan.Zero)
        {
            now = now.ToUniversalTime();
        }
        var operationId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        var ownerId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        var runtimeRecord = new BattleRuntimeLockRecord(
            ownerId,
            BattleRuntimeLockState.Running,
            Environment.ProcessId,
            Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture),
            now,
            null);
        var normalizedPipeName = BattleLocalIpcProtocol.RequirePipeName(pipeName, nameof(pipeName));
        var credentialCandidate = BattleIngestCredentialCodec.CreateCandidate(
            normalizedPipeName,
            previousGeneration: 0,
            createdAtUtc: now,
            rotatedAtUtc: now,
            BattleCredentialRotationReason.Initial,
            credentialProtector);
        try
        {
            var transitions = FeatureIds.Select(featureId =>
            {
                var before = snapshot.GetFeature(featureId).Preference;
                var after = requested.Contains(featureId, StringComparer.Ordinal)
                    ? LauncherPlayerFeaturePreference.Enabled
                    : before;
                return new BattleLifecycleFeatureTransition(featureId, before, after);
            }).ToArray();
            var configurationCandidate = BuildConfigurationCandidate(
                configuration,
                transitions,
                normalizedPipeName,
                credentialCandidate.Lease.EncodeForTomlProjection(),
                existingLocalTargetReview,
                requested,
                out var mutationReceiptSha256);
            try
            {
                var runtimeBytes = BattleRuntimeLockCodec.Encode(runtimeRecord);
                try
                {
                    var credentialMetadata = credentialCandidate.Lease.Metadata;
                    var configurationSource = configuration.Contents;
                    try
                    {
                        var configurationSourceSha = Hash(configurationSource);
                        var credentialCandidatePath =
                            $"battle/recovery/{operationId}/candidate/{BattleIngestCredentialCodec.FileName}.next";
                        var configurationCandidatePath =
                            $"battle/recovery/{operationId}/candidate/community_patch_settings.toml.next";
                        var marker = new BattleLifecycleMarker(
                            operationId,
                            BattleLifecycleOperationKind.FeatureActivation,
                            ownerId,
                            BattleLifecycleStage.Prepared,
                            requested,
                            [
                                new(
                                    "ingest-credential",
                                    $"battle/{BattleIngestCredentialCodec.FileName}",
                                    null,
                                    credentialCandidatePath,
                                    new(
                                        credentialMetadata.ProtectedByteCount,
                                        credentialMetadata.ProtectedSha256)),
                                new(
                                    "runtime-lock",
                                    $"battle/{BattleRuntimeLockCodec.FileName}",
                                    null,
                                    null,
                                    Identity(runtimeBytes)),
                            ],
                            new(
                                credentialMetadata.Generation,
                                credentialMetadata.ProtectedByteCount,
                                credentialMetadata.ProtectedSha256),
                            new(
                                configurationSourceSha,
                                BattleLifecycleJournalStore.PathIdentity(configuration.Path),
                                configurationSource.LongLength,
                                configurationSourceSha,
                                configurationCandidatePath,
                                configurationCandidate.LongLength,
                                Hash(configurationCandidate),
                                mutationReceiptSha256,
                                null,
                                null),
                            transitions,
                            transitions.Any(item => item.Before == LauncherPlayerFeaturePreference.Enabled),
                            transitions.Any(item => item.After == LauncherPlayerFeaturePreference.Enabled),
                            now,
                            now,
                            ImplementationVersion,
                            MutationBlocked: true,
                            SafeReadsAllowed: true);
                        var validationBytes = BattleLifecycleMarkerCodec.Protect(
                            marker,
                            new ValidationOnlyMarkerProtector());
                        CryptographicOperations.ZeroMemory(validationBytes);
                        return new(marker, runtimeRecord, credentialCandidate, configurationCandidate);
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(configurationSource);
                    }
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(runtimeBytes);
                }
            }
            catch
            {
                CryptographicOperations.ZeroMemory(configurationCandidate);
                throw;
            }
        }
        catch
        {
            credentialCandidate.Dispose();
            throw;
        }
    }

    public static async Task<BattleLifecyclePreparedActivation> PersistAsync(
        LauncherOperationLease operationLease,
        BattleLifecycleJournalStore journal,
        BattleRuntimeLockStore runtimeStore,
        BattleLifecycleActivationPreparation preparation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operationLease);
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(runtimeStore);
        ArgumentNullException.ThrowIfNull(preparation);
        await journal.CreatePreparedAsync(operationLease, preparation.Marker, cancellationToken)
            .ConfigureAwait(false);
        var runtimeLease = await runtimeStore.CreateBoundRunningAsync(
            operationLease,
            journal,
            preparation.RuntimeRecord,
            cancellationToken).ConfigureAwait(false);
        try
        {
            await journal.WritePreparedCandidatesAsync(
                operationLease,
                preparation.CandidateBytes(),
                cancellationToken).ConfigureAwait(false);
            return new(preparation, runtimeLease);
        }
        catch
        {
            await runtimeLease.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static byte[] BuildConfigurationCandidate(
        ConfigurationDocumentSnapshot configuration,
        IReadOnlyList<BattleLifecycleFeatureTransition> transitions,
        string pipeName,
        string credential,
        BattleLocalTargetChangeReviewReceipt? existingLocalTargetReview,
        IReadOnlyList<string> requestedFeatureIds,
        out string mutationReceiptSha256)
    {
        var source = configuration.Contents;
        try
        {
            var load = SyncTopologyTomlAdapter.Load(source);
            if (!load.IsValid || load.Topology is null)
            {
                throw new InvalidDataException("The Battle activation configuration source is invalid.");
            }
            var desired = load.Topology;
            if (!desired.Targets.TryGetValue(SyncDesiredTopology.LocalSidecarIdentity, out var target))
            {
                var addition = desired.AddTarget(SyncDesiredTopology.LocalSidecarIdentity, SyncTargetKind.LocalSidecar);
                if (!addition.Succeeded)
                {
                    throw new InvalidDataException("The local Battle target could not be added.");
                }
                desired = addition.Topology;
                target = desired.Targets[SyncDesiredTopology.LocalSidecarIdentity];
            }
            else if (existingLocalTargetReview is null
                || !string.Equals(
                    existingLocalTargetReview.SourceSha256,
                    Hash(source),
                    StringComparison.Ordinal)
                || !existingLocalTargetReview.AffectedFeatureIds.SequenceEqual(
                    requestedFeatureIds,
                    StringComparer.Ordinal)
                || existingLocalTargetReview.PipeName != pipeName)
            {
                throw new InvalidOperationException("Changing an existing local target requires explicit review.");
            }

            var battleEnabled = transitions.Single(item =>
                item.FeatureId == LauncherFeatureIds.BattleCollection).After == LauncherPlayerFeaturePreference.Enabled;
            var fleetEnabled = transitions.Single(item =>
                item.FeatureId == LauncherFeatureIds.FleetCollection).After == LauncherPlayerFeaturePreference.Enabled;
            var updatedTarget = target
                .WithEnabled(battleEnabled || fleetEnabled)
                .WithConnection(target.Url, SyncSecret.FromPlainText(credential))
                .WithLocalTransport(
                    SyncOverride.Explicit(SyncLocalTransport.NamedPipe),
                    SyncOverride.Explicit(pipeName))
                .WithDataOverride(SyncDataKind.BattlelogsRealtime, SyncOverride.Explicit(battleEnabled))
                .WithDataOverride(SyncDataKind.FleetRuntime, SyncOverride.Explicit(fleetEnabled));
            var transition = desired.UpdateTarget(
                SyncDesiredTopology.LocalSidecarIdentity,
                _ => updatedTarget);
            if (!transition.Succeeded)
            {
                throw new InvalidDataException("The local Battle target transition is invalid.");
            }
            desired = transition.Topology;
            var plan = SyncTopologyPersistencePlanner.Build(load, desired);
            if (!plan.IsValid
                || plan.Mutations.Count == 0
                || plan.Mutations.Any(mutation =>
                    mutation.Kind is SyncTomlMutationKind.RenameTable or SyncTomlMutationKind.RemoveTable
                    || !AllowedMutationPaths.Contains(mutation.Path)))
            {
                throw new InvalidDataException("The Battle activation TOML plan exceeds its exact ownership boundary.");
            }
            var edit = plan.Apply(source);
            if (!edit.IsValid || !edit.Changed || edit.Contents is null
                || edit.Contents.LongLength > MaximumConfigurationBytes)
            {
                throw new InvalidDataException("The Battle activation TOML candidate is invalid.");
            }
            var verification = SyncTopologyTomlAdapter.Load(edit.Contents);
            if (!verification.IsValid
                || verification.Topology is null
                || !verification.Topology.Resolve().Targets.Any(resolved =>
                    resolved.Name == SyncDesiredTopology.LocalSidecarIdentity
                    && resolved.Enabled == (battleEnabled || fleetEnabled)
                    && resolved.CredentialsConfigured
                    && resolved.LocalTransport?.Value == SyncLocalTransport.NamedPipe
                    && resolved.LocalPipeName?.Value == pipeName
                    && resolved.DataKinds[SyncDataKind.BattlelogsRealtime].Value == battleEnabled
                    && resolved.DataKinds[SyncDataKind.FleetRuntime].Value == fleetEnabled))
            {
                CryptographicOperations.ZeroMemory(edit.Contents);
                throw new InvalidDataException("The Battle activation TOML candidate did not verify.");
            }
            mutationReceiptSha256 = MutationReceipt(plan);
            return edit.Contents;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(source);
        }
    }

    private static string MutationReceipt(SyncTopologyPersistencePlan plan)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("schema", "stfc.battle-config-mutation-receipt.v1");
            writer.WriteStartArray("mutations");
            foreach (var mutation in plan.Mutations)
            {
                writer.WriteStartObject();
                writer.WriteString("kind", mutation.Kind.ToString());
                writer.WriteString("path", mutation.Path);
                if (mutation.DestinationPath is not null)
                {
                    writer.WriteString("destinationPath", mutation.DestinationPath);
                }
                writer.WriteBoolean("containsSecret", mutation.ContainsSecret);
                if (mutation.RenderedValue is not null)
                {
                    writer.WriteString("valueSha256", HashText(mutation.RenderedValue));
                }
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return Hash(stream.ToArray());
    }

    private static BattleLifecycleFileIdentity Identity(ReadOnlySpan<byte> bytes) =>
        new(bytes.Length, Hash(bytes));

    private static string Hash(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string HashText(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        try
        {
            return Hash(bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private sealed class ValidationOnlyMarkerProtector : IBattleLifecycleMarkerProtector
    {
        public byte[] Protect(byte[] plaintext) => plaintext.ToArray();

        public byte[] Unprotect(byte[] protectedBytes) => protectedBytes.ToArray();
    }
}
