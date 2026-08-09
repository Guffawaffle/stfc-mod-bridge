using System.Buffers.Binary;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using STFCCommunityMod.Launcher.Core;

namespace STFCCommunityMod.Launcher.Core.Tests;

[TestClass]
public sealed class BattleRuntimeCompositionTests
{
    [TestMethod]
    public async Task InactiveCompositionNeverRequestsProvisionedResources()
    {
        var factory = new RecordingProvisioningFactory();
        await using var coordinator = new BattleRuntimeCompositionCoordinator(factory, SmallLimits());

        await coordinator.RecomposeAsync(Snapshot(
            LauncherPlayerFeaturePreference.Unset,
            LauncherPlayerFeaturePreference.Disabled));

        Assert.AreEqual(0, factory.OpenCount);
        Assert.AreEqual(BattleRuntimeCompositionState.Dormant, coordinator.GetHealth().State);
        Assert.AreEqual(0, coordinator.GetHealth().AcceptedKinds.Count);
    }

    [TestMethod]
    public async Task EnabledBattleFamilyStartsOnceAndHandsExactBytesToItsSink()
    {
        RequireWindows();
        var factory = new RecordingProvisioningFactory();
        await using var coordinator = new BattleRuntimeCompositionCoordinator(factory, SmallLimits());
        var snapshot = Snapshot(
            LauncherPlayerFeaturePreference.Enabled,
            LauncherPlayerFeaturePreference.Disabled);

        await coordinator.RecomposeAsync(snapshot);
        await coordinator.RecomposeAsync(snapshot);

        var opened = factory.Opened.Single();
        var exact = Encoding.UTF8.GetBytes(BattleEnvelope("runtime-battle"));
        using var response = await SendAsync(opened.PipeName, opened.Credential, exact);
        Assert.AreEqual("accepted", response.RootElement.GetProperty("status").GetString());
        CollectionAssert.AreEqual(exact, opened.BattleSink.Envelopes.Single().ExactEnvelopeBytes.ToArray());
        Assert.AreEqual(0, opened.FleetSink.Envelopes.Count);
        Assert.AreEqual(1, factory.OpenCount);
        CollectionAssert.AreEquivalent(
            new[] { BattleIngestProtocol.BattleEventsKind },
            coordinator.GetHealth().AcceptedKinds.ToArray());
    }

    [TestMethod]
    public async Task PerFeatureChangeDrainsOldHostAndReopensOnlyTheRequestedFamily()
    {
        RequireWindows();
        var factory = new RecordingProvisioningFactory();
        await using var coordinator = new BattleRuntimeCompositionCoordinator(factory, SmallLimits());
        await coordinator.RecomposeAsync(Snapshot(
            LauncherPlayerFeaturePreference.Enabled,
            LauncherPlayerFeaturePreference.Disabled));
        var first = factory.Opened.Single();

        await coordinator.RecomposeAsync(Snapshot(
            LauncherPlayerFeaturePreference.Disabled,
            LauncherPlayerFeaturePreference.Enabled));

        var second = factory.Opened.Last();
        Assert.AreEqual(2, factory.OpenCount);
        Assert.AreEqual(1, first.Lifetime.DisposeCount);
        await AssertCannotConnectAsync(first.PipeName);
        using var response = await SendAsync(
            second.PipeName,
            second.Credential,
            Encoding.UTF8.GetBytes(FleetEnvelope("runtime-fleet")));
        Assert.AreEqual("accepted", response.RootElement.GetProperty("status").GetString());
        Assert.AreEqual(0, second.BattleSink.Envelopes.Count);
        Assert.AreEqual(1, second.FleetSink.Envelopes.Count);
        CollectionAssert.AreEquivalent(
            new[] { BattleIngestProtocol.FleetRuntimeKind },
            coordinator.GetHealth().AcceptedKinds.ToArray());
    }

    [TestMethod]
    public async Task CapabilityLossStopsCollectionAndReleasesProvisionedLifetime()
    {
        RequireWindows();
        var factory = new RecordingProvisioningFactory();
        await using var coordinator = new BattleRuntimeCompositionCoordinator(factory, SmallLimits());
        await coordinator.RecomposeAsync(Snapshot(
            LauncherPlayerFeaturePreference.Enabled,
            LauncherPlayerFeaturePreference.Disabled));
        var opened = factory.Opened.Single();

        await coordinator.RecomposeAsync(Snapshot(
            LauncherPlayerFeaturePreference.Enabled,
            LauncherPlayerFeaturePreference.Disabled,
            includeBattleCapability: false));

        await AssertCannotConnectAsync(opened.PipeName);
        Assert.AreEqual(1, opened.Lifetime.DisposeCount);
        Assert.AreEqual(BattleRuntimeCompositionState.Dormant, coordinator.GetHealth().State);
        Assert.AreEqual(0, coordinator.GetHealth().AcceptedKinds.Count);
    }

    [TestMethod]
    public async Task MissingFamilySinkFailsClosedAndReleasesProvisioning()
    {
        RequireWindows();
        var factory = new RecordingProvisioningFactory(includeBattleSink: false);
        await using var coordinator = new BattleRuntimeCompositionCoordinator(factory, SmallLimits());

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => coordinator.RecomposeAsync(Snapshot(
            LauncherPlayerFeaturePreference.Enabled,
            LauncherPlayerFeaturePreference.Disabled)));

        var opened = factory.Opened.Single();
        Assert.AreEqual(1, opened.Lifetime.DisposeCount);
        Assert.AreEqual(BattleRuntimeCompositionState.Failed, coordinator.GetHealth().State);
        Assert.AreEqual(BattleIngestFailureCode.StartFailed, coordinator.GetHealth().LastFailure);
        await AssertCannotConnectAsync(opened.PipeName);
    }

    [TestMethod]
    public async Task DisposalStopsTheHostAndPermanentlyRejectsRecomposition()
    {
        RequireWindows();
        var factory = new RecordingProvisioningFactory();
        var coordinator = new BattleRuntimeCompositionCoordinator(factory, SmallLimits());
        var snapshot = Snapshot(
            LauncherPlayerFeaturePreference.Enabled,
            LauncherPlayerFeaturePreference.Disabled);
        await coordinator.RecomposeAsync(snapshot);
        var opened = factory.Opened.Single();

        await coordinator.DisposeAsync();
        await coordinator.DisposeAsync();

        await AssertCannotConnectAsync(opened.PipeName);
        Assert.AreEqual(1, opened.Lifetime.DisposeCount);
        Assert.AreEqual(BattleRuntimeCompositionState.Disposed, coordinator.GetHealth().State);
        await Assert.ThrowsExceptionAsync<ObjectDisposedException>(() => coordinator.RecomposeAsync(snapshot));
    }

    [TestMethod]
    public async Task FailedProvisioningCleanupRetainsOwnershipForExplicitRetry()
    {
        RequireWindows();
        var factory = new RecordingProvisioningFactory(lifetimeFailures: 1);
        await using var coordinator = new BattleRuntimeCompositionCoordinator(factory, SmallLimits());
        await coordinator.RecomposeAsync(Snapshot(
            LauncherPlayerFeaturePreference.Enabled,
            LauncherPlayerFeaturePreference.Disabled));
        var opened = factory.Opened.Single();

        await Assert.ThrowsExceptionAsync<IOException>(() => coordinator.RecomposeAsync(Snapshot(
            LauncherPlayerFeaturePreference.Disabled,
            LauncherPlayerFeaturePreference.Disabled)));

        Assert.AreEqual(BattleRuntimeCompositionState.Failed, coordinator.GetHealth().State);
        Assert.AreEqual(BattleIngestFailureCode.ShutdownTimedOut, coordinator.GetHealth().LastFailure);
        Assert.AreEqual(1, opened.Lifetime.DisposeCount);
        await AssertCannotConnectAsync(opened.PipeName);

        await coordinator.RecomposeAsync(Snapshot(
            LauncherPlayerFeaturePreference.Disabled,
            LauncherPlayerFeaturePreference.Disabled));

        Assert.AreEqual(2, opened.Lifetime.DisposeCount);
        Assert.AreEqual(BattleRuntimeCompositionState.Dormant, coordinator.GetHealth().State);
        Assert.AreEqual(1, factory.OpenCount);
    }

    [TestMethod]
    public async Task FailedStartCleanupRetainsProvisioningUntilASecondTransitionReleasesIt()
    {
        RequireWindows();
        var factory = new RecordingProvisioningFactory(
            includeBattleSink: false,
            lifetimeFailures: 1);
        await using var coordinator = new BattleRuntimeCompositionCoordinator(factory, SmallLimits());

        _ = await Assert.ThrowsExceptionAsync<AggregateException>(() => coordinator.RecomposeAsync(Snapshot(
            LauncherPlayerFeaturePreference.Enabled,
            LauncherPlayerFeaturePreference.Disabled)));

        var opened = factory.Opened.Single();
        Assert.AreEqual(1, opened.Lifetime.DisposeCount);
        Assert.AreEqual(BattleRuntimeCompositionState.Failed, coordinator.GetHealth().State);
        Assert.AreEqual(BattleIngestFailureCode.ShutdownTimedOut, coordinator.GetHealth().LastFailure);

        await coordinator.RecomposeAsync(Snapshot(
            LauncherPlayerFeaturePreference.Disabled,
            LauncherPlayerFeaturePreference.Disabled));

        Assert.AreEqual(2, opened.Lifetime.DisposeCount);
        Assert.AreEqual(BattleRuntimeCompositionState.Dormant, coordinator.GetHealth().State);
        Assert.AreEqual(1, factory.OpenCount);
    }

    private static LauncherBattleFeatureSnapshot Snapshot(
        LauncherPlayerFeaturePreference battle,
        LauncherPlayerFeaturePreference fleet,
        bool includeBattleCapability = true)
    {
        var capabilities = new List<string>
        {
            LauncherCapabilityIds.SidecarIngestV1,
            LauncherCapabilityIds.FleetRuntimeSnapshotV1,
        };
        if (includeBattleCapability)
        {
            capabilities.Add(LauncherCapabilityIds.BattleCaptureV1);
        }
        var plan = LauncherFeatureResolver.Resolve(
            new(
                "test.runtime",
                new Version(1, 0),
                "test-runtime",
                null,
                capabilities,
                [new("test", "Battle runtime composition fixture")]),
            LauncherFeatureCatalog.All);
        return LauncherBattleFeatureComposer.Compose(plan, new(battle, fleet));
    }

    private static BattleIngestLimits SmallLimits() =>
        BattleIngestLimits.Default with
        {
            RequestTimeout = TimeSpan.FromSeconds(2),
            ShutdownDrainTimeout = TimeSpan.FromSeconds(1),
        };

    private static async Task<JsonDocument> SendAsync(
        string pipeName,
        byte[] credential,
        byte[] payload)
    {
        using var client = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await client.ConnectAsync(2000);
        await WriteFrameAsync(client, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            protocolVersion = BattleLocalIpcProtocol.Version,
            role = BattleLocalIpcProtocol.RuntimeRole,
            operation = BattleLocalIpcProtocol.IngestOperation,
            credential = Convert.ToBase64String(credential)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_'),
        })));
        await client.FlushAsync();
        using var ready = JsonDocument.Parse(await ReadFrameAsync(client));
        Assert.AreEqual("ready", ready.RootElement.GetProperty("status").GetString());
        await WriteFrameAsync(client, payload);
        await client.FlushAsync();
        return JsonDocument.Parse(await ReadFrameAsync(client));
    }

    private static async Task WriteFrameAsync(Stream stream, byte[] bytes)
    {
        var length = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
        await stream.WriteAsync(length);
        await stream.WriteAsync(bytes);
    }

    private static async Task<byte[]> ReadFrameAsync(Stream stream)
    {
        var length = new byte[sizeof(int)];
        await stream.ReadExactlyAsync(length);
        var bytes = new byte[BinaryPrimitives.ReadInt32LittleEndian(length)];
        await stream.ReadExactlyAsync(bytes);
        return bytes;
    }

    private static async Task AssertCannotConnectAsync(string pipeName)
    {
        using var client = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await Assert.ThrowsExceptionAsync<TimeoutException>(() => client.ConnectAsync(75));
    }

    private static string BattleEnvelope(string batchId) =>
        JsonSerializer.Serialize(new
        {
            protocolVersion = BattleIngestProtocol.Version,
            kind = BattleIngestProtocol.BattleEventsKind,
            batchId,
            producedAt = "2026-08-09T12:00:00.000Z",
            sessionId = "runtime-composition",
            source = "stfc-community-mod",
            modVersion = "2.1.0",
            payloadProtocol = BattleIngestProtocol.SidecarEventsVersion,
            payload = new[]
            {
                new
                {
                    protocolVersion = BattleIngestProtocol.SidecarEventsVersion,
                    type = "battle.capture",
                    schemaVersion = "stfc.battle.capture.v1",
                    timestamp = "2026-08-09T12:00:00.000Z",
                    journalId = "runtime-journal",
                    capture = new { sourceKind = "runtime-composition" },
                },
            },
        });

    private static string FleetEnvelope(string batchId) =>
        JsonSerializer.Serialize(new
        {
            protocolVersion = BattleIngestProtocol.Version,
            kind = BattleIngestProtocol.FleetRuntimeKind,
            batchId,
            producedAt = "2026-08-09T12:00:00.000Z",
            sessionId = "runtime-composition",
            source = "stfc-community-mod",
            modVersion = "2.1.0",
            payloadProtocol = BattleIngestProtocol.FleetRuntimeVersion,
            payload = new
            {
                type = BattleIngestProtocol.FleetRuntimeKind,
                schemaVersion = BattleIngestProtocol.FleetRuntimeVersion,
                slots = new[] { new { slotIndex = 0, present = false } },
            },
        });

    private static void RequireWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("The named-pipe runtime composition proof is Windows-only.");
        }
    }

    private sealed class RecordingProvisioningFactory(
        bool includeBattleSink = true,
        int lifetimeFailures = 0) :
        IBattleRuntimeProvisioningFactory
    {
        public List<OpenedProvisioning> Opened { get; } = [];

        public int OpenCount => Opened.Count;

        public ValueTask<BattleRuntimeProvisioningLease> OpenAsync(
            LauncherBattleFeatureSnapshot features,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var opened = new OpenedProvisioning(
                $"stfc-battle-runtime-{Guid.NewGuid():N}",
                RandomNumberGenerator.GetBytes(32),
                new RecordingSink(),
                new RecordingSink(),
                new RecordingLifetime(lifetimeFailures));
            Opened.Add(opened);
            return ValueTask.FromResult(new BattleRuntimeProvisioningLease(
                opened.PipeName,
                opened.Credential,
                EvidenceSha256,
                new AllowCurrentProcessAuthorizer(),
                includeBattleSink ? opened.BattleSink : null,
                opened.FleetSink,
                opened.Lifetime));
        }
    }

    private sealed record OpenedProvisioning(
        string PipeName,
        byte[] Credential,
        RecordingSink BattleSink,
        RecordingSink FleetSink,
        RecordingLifetime Lifetime);

    private sealed class RecordingSink : IBattleIngestSink
    {
        public List<BattleIngestEnvelope> Envelopes { get; } = [];

        public ValueTask<BattleIngestCommitResult> CommitAsync(
            BattleIngestEnvelope envelope,
            CancellationToken cancellationToken)
        {
            Envelopes.Add(envelope);
            return ValueTask.FromResult(new BattleIngestCommitResult(envelope.ExactEventBytes.Count));
        }
    }

    private sealed class RecordingLifetime(int failuresRemaining) : IAsyncDisposable
    {
        public int DisposeCount { get; private set; }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            if (failuresRemaining-- > 0)
            {
                return ValueTask.FromException(new IOException("fixture cleanup failure"));
            }
            return ValueTask.CompletedTask;
        }
    }

    private sealed class AllowCurrentProcessAuthorizer : IBattleNamedPipeClientAuthorizer
    {
        public bool IsAuthorized(BattleNamedPipeClientIdentity identity, string runtimeEvidenceSha256) =>
            identity.ProcessId == Environment.ProcessId
            && identity.Role == BattleLocalIpcProtocol.RuntimeRole
            && identity.Operation == BattleLocalIpcProtocol.IngestOperation
            && runtimeEvidenceSha256 == EvidenceSha256;
    }

    private const string EvidenceSha256 =
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
}
