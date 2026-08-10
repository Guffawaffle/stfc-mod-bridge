using System.Security.Cryptography;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using STFCCommunityMod.Launcher.Core;

namespace STFCCommunityMod.Launcher.Core.Tests;

[TestClass]
public sealed class FleetRuntimeSnapshotSinkTests
{
    [TestMethod]
    public async Task CheckedInGoldenSnapshotProjectsWithoutRetainingRawBytes()
    {
        var fixtureText = await File.ReadAllTextAsync(
            Path.Combine(
                AppContext.BaseDirectory,
                "Fixtures",
                "BattleBridge",
                "fleet-runtime.golden.v1.json"));
        using var fixture = JsonDocument.Parse(fixtureText);
        var exactBytes = Encoding.UTF8.GetBytes(
            fixture.RootElement.GetProperty("envelope").GetRawText());
        var envelope = Parse(exactBytes);
        var expectedEvidence = Sha256(envelope.ExactEventBytes[0].Span);
        await using var sink = new FleetRuntimeSnapshotSink();

        var result = await sink.CommitAsync(envelope, CancellationToken.None);
        var status = sink.ReadStatus();
        var snapshot = status.Current!;

        Assert.AreEqual(1, result.AcceptedRecords);
        Assert.AreEqual(FleetRuntimeProjectionDisposition.Advanced, status.LastDisposition);
        Assert.AreEqual(1, status.AcceptedBatches);
        Assert.AreEqual(1, status.AdvancedSnapshots);
        Assert.AreEqual("stfc-community-mod", snapshot.Source);
        Assert.AreEqual("golden-session-1", snapshot.SessionId);
        Assert.AreEqual("golden-fleet-batch-17", snapshot.BatchId);
        Assert.AreEqual("fleet-slot-warping", snapshot.ObservationSource);
        Assert.AreEqual(1779105900000, snapshot.ObservedAtMs);
        Assert.IsTrue(snapshot.FleetBarTracked);
        Assert.AreEqual(0, snapshot.SelectedIndex);
        Assert.AreEqual(expectedEvidence, snapshot.EvidenceSha256);
        Assert.AreEqual(2, snapshot.Slots.Count);
        Assert.AreEqual("slot-0", snapshot.Slots[0].SlotKey);
        Assert.AreEqual("fleet-91d9c9b95bba5fe6", snapshot.Slots[0].FleetKey);
        Assert.AreEqual("91d9c9b95bba5fe669d6e7707a27ef8e", snapshot.Slots[0].ShipKeyHash);
        Assert.AreEqual("9007199254740993888", snapshot.Slots[0].ShipIdentityId);
        Assert.AreEqual("warping", snapshot.Slots[0].State);
        Assert.AreEqual("hull:Redacted Test Ship", snapshot.Slots[0].ShipType);
        Assert.AreEqual(1307832955, snapshot.Slots[0].HullSpecId);
        Assert.AreEqual(90500, snapshot.Slots[0].ActiveTimerRemainingMs);
        Assert.AreEqual("FleetPlayerData.Timer.RemainingTime", snapshot.Slots[0].ActiveTimerSource);
        Assert.AreEqual("empty", snapshot.Slots[1].State);
        Assert.AreEqual("slot", snapshot.Slots[1].AssignmentKind);

        exactBytes.AsSpan().Fill((byte)'x');
        Assert.AreEqual("warping", snapshot.Slots[0].State);
        Assert.AreEqual("hull:Redacted Test Ship", snapshot.Slots[0].ShipType);
        Assert.AreEqual(expectedEvidence, snapshot.EvidenceSha256);
    }

    [TestMethod]
    public async Task ScopedOrderingDistinguishesDuplicateStaleAndConflict()
    {
        await using var sink = new FleetRuntimeSnapshotSink();
        var first = Envelope("batch-1", "2026-08-10T12:00:01.000Z", "Docked");
        var older = Envelope("batch-old", "2026-08-10T12:00:00.000Z", "Warping");
        var sameState = Envelope("batch-same-state", "2026-08-10T12:00:01.000Z", "Docked");
        var ambiguous = Envelope("batch-ambiguous", "2026-08-10T12:00:01.000Z", "Mining");

        await sink.CommitAsync(first, CancellationToken.None);
        await sink.CommitAsync(first, CancellationToken.None);
        await sink.CommitAsync(older, CancellationToken.None);
        await sink.CommitAsync(sameState, CancellationToken.None);

        await Assert.ThrowsExceptionAsync<InvalidDataException>(async () =>
            await sink.CommitAsync(ambiguous, CancellationToken.None));
        await Assert.ThrowsExceptionAsync<InvalidDataException>(async () =>
            await sink.CommitAsync(
                Envelope("batch-1", "2026-08-10T12:00:02.000Z", "Mining"),
                CancellationToken.None));
        await Assert.ThrowsExceptionAsync<InvalidDataException>(async () =>
            await sink.CommitAsync(
                Envelope("batch-other-session", "2026-08-10T12:00:02.000Z", "Docked", sessionId: "session-2"),
                CancellationToken.None));
        await Assert.ThrowsExceptionAsync<InvalidDataException>(async () =>
            await sink.CommitAsync(
                Envelope("batch-other-source", "2026-08-10T12:00:02.000Z", "Docked", source: "other-mod"),
                CancellationToken.None));

        var status = sink.ReadStatus();
        Assert.AreEqual("batch-1", status.Current!.BatchId);
        Assert.AreEqual("docked", status.Current.Slots[0].State);
        Assert.AreEqual(4, status.AcceptedBatches);
        Assert.AreEqual(1, status.AdvancedSnapshots);
        Assert.AreEqual(2, status.DuplicateBatches);
        Assert.AreEqual(1, status.StaleBatches);
        Assert.AreEqual(4, status.ConflictingBatches);
    }

    [TestMethod]
    public async Task ProjectionNormalizesOnlyTheReviewedSlotFields()
    {
        var payload = new JsonObject
        {
            ["type"] = BattleIngestProtocol.FleetRuntimeKind,
            ["schemaVersion"] = BattleIngestProtocol.FleetRuntimeVersion,
            ["source"] = "  fleet   slot   observer  ",
            ["observedAtMs"] = 1234,
            ["fleetBarTracked"] = false,
            ["selectedIndex"] = 7,
            ["slots"] = new JsonArray
            {
                new JsonObject { ["slotIndex"] = -1, ["present"] = true },
                new JsonObject
                {
                    ["slotIndex"] = 1,
                    ["present"] = true,
                    ["fleetId"] = 11,
                    ["currentStateName"] = "Docked",
                    ["hullName"] = "First",
                },
                new JsonObject
                {
                    ["slotIndex"] = 1,
                    ["present"] = true,
                    ["fleetId"] = 12,
                    ["currentStateName"] = "Warping",
                    ["hullName"] = "  USS   Reliant  ",
                    ["hullSpecId"] = 99,
                    ["shipIdentityProbe"] = new JsonObject
                    {
                        ["shipId"] = 9007199254740993d,
                        ["source"] = "untrusted",
                    },
                    ["activeTimer"] = new JsonObject
                    {
                        ["remainingSeconds"] = 3.75,
                    },
                    ["credential"] = "must-not-project",
                },
                new JsonObject
                {
                    ["slotIndex"] = 2,
                    ["present"] = false,
                    ["hullName"] = "must-not-project",
                    ["activeTimer"] = new JsonObject { ["remainingMs"] = 50 },
                },
            },
        };
        await using var sink = new FleetRuntimeSnapshotSink();

        await sink.CommitAsync(
            Envelope("normalized", "2026-08-10T12:00:01.000Z", payload: payload),
            CancellationToken.None);

        var snapshot = sink.ReadStatus().Current!;
        Assert.AreEqual("fleet slot observer", snapshot.ObservationSource);
        Assert.AreEqual(2, snapshot.Slots.Count);
        Assert.AreEqual("slot-1", snapshot.Slots[0].SlotKey);
        Assert.AreEqual("unavailable", snapshot.Slots[0].State);
        Assert.AreEqual("hull:USS Reliant", snapshot.Slots[0].ShipType);
        Assert.IsNull(snapshot.Slots[0].ShipIdentityId);
        Assert.AreEqual(3750, snapshot.Slots[0].ActiveTimerRemainingMs);
        Assert.AreEqual("unknown", snapshot.Slots[0].ActiveTimerSource);
        Assert.AreEqual("slot-2", snapshot.Slots[1].SlotKey);
        Assert.AreEqual("unavailable", snapshot.Slots[1].State);
        Assert.IsNull(snapshot.Slots[1].ShipType);
        Assert.IsNull(snapshot.Slots[1].ActiveTimerRemainingMs);
    }

    [TestMethod]
    public async Task ConcurrentOutOfOrderCommitsConvergeOnNewestObservation()
    {
        await using var sink = new FleetRuntimeSnapshotSink();
        var started = DateTimeOffset.Parse(
            "2026-08-10T12:00:00.000Z",
            CultureInfo.InvariantCulture);
        var envelopes = Enumerable.Range(0, 64)
            .Select(index => Envelope(
                $"batch-{index}",
                started.AddMilliseconds(index).ToString("O", CultureInfo.InvariantCulture),
                $"state-{index}"))
            .OrderByDescending(envelope => envelope.BatchId, StringComparer.Ordinal)
            .ToArray();

        await Task.WhenAll(envelopes.Select(async envelope =>
            await sink.CommitAsync(envelope, CancellationToken.None)));

        var status = sink.ReadStatus();
        Assert.AreEqual(64, status.AcceptedBatches);
        Assert.AreEqual("batch-63", status.Current!.BatchId);
        Assert.AreEqual("state-63", status.Current.Slots[0].State);
        Assert.AreEqual(0, status.ConflictingBatches);
    }

    [TestMethod]
    public async Task BatchReceiptMemoryIsBoundedAndPinsTheCurrentIdentity()
    {
        await using var sink = new FleetRuntimeSnapshotSink();
        var started = DateTimeOffset.Parse(
            "2026-08-10T12:00:00.000Z",
            CultureInfo.InvariantCulture);
        await sink.CommitAsync(
            Envelope("current", started.AddSeconds(10).ToString("O", CultureInfo.InvariantCulture), "Docked"),
            CancellationToken.None);
        for (var index = 0; index < 2050; index++)
        {
            await sink.CommitAsync(
                Envelope(
                    $"batch-{index}",
                    started.AddMilliseconds(index).ToString("O", CultureInfo.InvariantCulture),
                    $"state-{index}"),
                CancellationToken.None);
        }

        var status = sink.ReadStatus();
        Assert.AreEqual(2048, status.RetainedBatchReceipts);
        Assert.AreEqual(2051, status.AcceptedBatches);
        Assert.AreEqual("current", status.Current!.BatchId);
        Assert.AreEqual("docked", status.Current.Slots[0].State);

        await Assert.ThrowsExceptionAsync<InvalidDataException>(async () =>
            await sink.CommitAsync(
                Envelope("current", started.AddSeconds(11).ToString("O", CultureInfo.InvariantCulture), "Mining"),
                CancellationToken.None));
    }

    [TestMethod]
    public async Task EmptySnapshotIsIdempotentNoEvidenceAndStillBindsProducerScope()
    {
        await using var sink = new FleetRuntimeSnapshotSink();
        var empty = Payload("Docked");
        empty["slots"] = new JsonArray();
        var envelope = Envelope("empty", "2026-08-10T12:00:01.000Z", empty);

        Assert.AreEqual(
            0,
            (await sink.CommitAsync(envelope, CancellationToken.None)).AcceptedRecords);
        Assert.AreEqual(
            0,
            (await sink.CommitAsync(envelope, CancellationToken.None)).AcceptedRecords);

        var status = sink.ReadStatus();
        Assert.IsNull(status.Current);
        Assert.AreEqual(1, status.NoEvidenceBatches);
        Assert.AreEqual(1, status.DuplicateBatches);
        await Assert.ThrowsExceptionAsync<InvalidDataException>(async () =>
            await sink.CommitAsync(
                Envelope("other", "2026-08-10T12:00:02.000Z", "Docked", sessionId: "session-2"),
                CancellationToken.None));
    }

    [TestMethod]
    public async Task CancellationAndDisposalAreTerminalWithoutSideEffects()
    {
        var sink = new FleetRuntimeSnapshotSink();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsExceptionAsync<OperationCanceledException>(async () =>
            await sink.CommitAsync(
                Envelope("canceled", "2026-08-10T12:00:01.000Z", "Docked"),
                cancellation.Token));
        Assert.IsNull(sink.ReadStatus().Current);

        await sink.DisposeAsync();
        await sink.DisposeAsync();
        Assert.ThrowsException<ObjectDisposedException>(() => sink.ReadStatus());
        await Assert.ThrowsExceptionAsync<ObjectDisposedException>(async () =>
            await sink.CommitAsync(
                Envelope("after-dispose", "2026-08-10T12:00:02.000Z", "Docked"),
                CancellationToken.None));
    }

    [TestMethod]
    public async Task DirectSinkBoundaryRejectsMalformedDuplicateAndOversizedPayloads()
    {
        await using var sink = new FleetRuntimeSnapshotSink();
        var duplicate = Encoding.UTF8.GetBytes(
            "{\"type\":\"fleet.runtime\",\"schemaVersion\":\"stfc.fleet.runtime_snapshot.v1\","
            + "\"slots\":[],\"slots\":[]}");
        var exactEnvelope = Encoding.UTF8.GetBytes("{}");
        var direct = new BattleIngestEnvelope(
            BattleIngestProtocol.Version,
            BattleIngestProtocol.FleetRuntimeKind,
            "duplicate",
            DateTimeOffset.Parse("2026-08-10T12:00:01.000Z", CultureInfo.InvariantCulture),
            "session-1",
            "stfc-community-mod",
            "test",
            BattleIngestProtocol.FleetRuntimeVersion,
            exactEnvelope,
            [duplicate]);

        await Assert.ThrowsExceptionAsync<InvalidDataException>(async () =>
            await sink.CommitAsync(direct, CancellationToken.None));

        var oversized = new byte[512 * 1024 + 1];
        var oversizedEnvelope = direct with
        {
            BatchId = "oversized",
            ExactEnvelopeBytes = oversized,
            ExactEventBytes = [oversized],
        };
        await Assert.ThrowsExceptionAsync<InvalidDataException>(async () =>
            await sink.CommitAsync(oversizedEnvelope, CancellationToken.None));
        Assert.IsNull(sink.ReadStatus().Current);
    }

    private static BattleIngestEnvelope Envelope(
        string batchId,
        string producedAt,
        string state,
        string sessionId = "session-1",
        string source = "stfc-community-mod") =>
        Envelope(batchId, producedAt, Payload(state), sessionId, source);

    private static BattleIngestEnvelope Envelope(
        string batchId,
        string producedAt,
        JsonObject payload,
        string sessionId = "session-1",
        string source = "stfc-community-mod")
    {
        var root = new JsonObject
        {
            ["protocolVersion"] = BattleIngestProtocol.Version,
            ["kind"] = BattleIngestProtocol.FleetRuntimeKind,
            ["batchId"] = batchId,
            ["producedAt"] = producedAt,
            ["sessionId"] = sessionId,
            ["source"] = source,
            ["modVersion"] = "test",
            ["payloadProtocol"] = BattleIngestProtocol.FleetRuntimeVersion,
            ["payload"] = payload,
        };
        return Parse(Encoding.UTF8.GetBytes(root.ToJsonString()));
    }

    private static JsonObject Payload(string state) =>
        new()
        {
            ["type"] = BattleIngestProtocol.FleetRuntimeKind,
            ["schemaVersion"] = BattleIngestProtocol.FleetRuntimeVersion,
            ["source"] = "test-observer",
            ["observedAtMs"] = 1,
            ["fleetBarTracked"] = true,
            ["selectedIndex"] = 0,
            ["slots"] = new JsonArray
            {
                new JsonObject
                {
                    ["slotIndex"] = 0,
                    ["present"] = true,
                    ["fleetId"] = 1,
                    ["currentStateName"] = state,
                },
            },
        };

    private static BattleIngestEnvelope Parse(byte[] bytes)
    {
        var runtime = new LauncherRuntimeProfile(
            "future-compatible-runtime",
            new Version(1, 0),
            "test",
            null,
            [
                LauncherCapabilityIds.SidecarIngestV1,
                LauncherCapabilityIds.FleetRuntimeSnapshotV1,
            ],
            [new("test", "Fleet runtime sink fixture")]);
        var processor = new BattleIngestEnvelopeProcessor(
            BattleIngestActivation.Resolve(runtime, new(false, true)),
            BattleIngestLimits.Default);
        var result = processor.Parse(bytes);
        Assert.AreEqual(BattleIngestParseStatus.Complete, result.Status);
        return result.Envelope!;
    }

    private static string Sha256(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
