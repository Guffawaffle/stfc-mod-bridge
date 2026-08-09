using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using STFCCommunityMod.Launcher.Core;

namespace STFCCommunityMod.Launcher.Core.Tests;

[TestClass]
public sealed class BattleIngestHostTests
{
    [TestMethod]
    public async Task ValidCapabilityQualifiedBattleAndFleetRequestsCommitOffTheRequestPath()
    {
        var sink = new RecordingSink();
        await using var fixture = await HostFixture.StartAsync(
            EligibleActivation("guffawaffle.stfc-community-mod", battle: true, fleet: true),
            sink);

        var battle = await fixture.PostAsync(BattleEnvelope("battle-one"));
        var fleet = await fixture.PostAsync(FleetEnvelope("fleet-one"));

        Assert.AreEqual(HttpStatusCode.Accepted, battle.StatusCode);
        Assert.AreEqual(HttpStatusCode.Accepted, fleet.StatusCode);
        Assert.AreEqual(2, sink.Envelopes.Count);
        Assert.AreEqual(BattleIngestProtocol.BattleEventsKind, sink.Envelopes[0].Kind);
        Assert.AreEqual(BattleIngestProtocol.FleetRuntimeKind, sink.Envelopes[1].Kind);
        Assert.IsTrue(MemoryMarshal.TryGetArray(sink.Envelopes[0].ExactEnvelopeBytes, out var envelopeArray));
        Assert.IsTrue(MemoryMarshal.TryGetArray(sink.Envelopes[0].ExactEventBytes[0], out var eventArray));
        Assert.AreSame(envelopeArray.Array, eventArray.Array);
        Assert.AreEqual(2, fixture.Host.GetHealth().AcceptedBatches);
    }

    [TestMethod]
    public async Task CheckedInGoldenBattleAndFleetEnvelopesPreserveExactLexemeSlices()
    {
        var sink = new RecordingSink();
        await using var fixture = await HostFixture.StartAsync(
            EligibleActivation("future-compatible-runtime", battle: true, fleet: true),
            sink);
        var fixtureTexts = new List<string>();
        var exactInputs = new List<byte[]>();
        foreach (var name in new[] { "battle-capture.golden.v1.json", "fleet-runtime.golden.v1.json" })
        {
            var fixtureText = await File.ReadAllTextAsync(
                Path.Combine(AppContext.BaseDirectory, "Fixtures", "BattleBridge", name));
            fixtureTexts.Add(fixtureText);
            using var document = JsonDocument.Parse(fixtureText);
            var exactEnvelope = document.RootElement.GetProperty("envelope").GetRawText();
            exactInputs.Add(Encoding.UTF8.GetBytes(exactEnvelope));

            Assert.AreEqual(HttpStatusCode.Accepted, (await fixture.PostAsync(exactEnvelope)).StatusCode);
        }

        Assert.AreEqual(2, sink.Envelopes.Count);
        for (var index = 0; index < sink.Envelopes.Count; ++index)
        {
            var envelope = sink.Envelopes[index];
            Assert.IsTrue(exactInputs[index].AsSpan().SequenceEqual(envelope.ExactEnvelopeBytes.Span));
            using var fixtureDocument = JsonDocument.Parse(fixtureTexts[index]);
            var expected = fixtureDocument.RootElement.GetProperty("expectedParsedEnvelope");
            Assert.AreEqual(expected.GetProperty("protocolVersion").GetString(), envelope.ProtocolVersion);
            Assert.AreEqual(expected.GetProperty("kind").GetString(), envelope.Kind);
            Assert.AreEqual(expected.GetProperty("batchId").GetString(), envelope.BatchId);
            Assert.AreEqual(expected.GetProperty("producedAt").GetDateTimeOffset(), envelope.ProducedAt);
            Assert.AreEqual(expected.GetProperty("sessionId").GetString(), envelope.SessionId);
            Assert.AreEqual(expected.GetProperty("source").GetString(), envelope.Source);
            Assert.AreEqual(expected.GetProperty("modVersion").GetString(), envelope.ModVersion);
            Assert.AreEqual(expected.GetProperty("payloadProtocol").GetString(), envelope.PayloadProtocol);
            using var parsed = JsonDocument.Parse(envelope.ExactEnvelopeBytes);
            var payload = parsed.RootElement.GetProperty("payload");
            var expectedLexeme = payload.ValueKind == JsonValueKind.Array
                ? payload[0].GetRawText()
                : payload.GetRawText();
            Assert.AreEqual(expectedLexeme, Encoding.UTF8.GetString(envelope.ExactEventBytes[0].Span));
            var expectedEvent = expected.GetProperty("payload");
            expectedEvent = expectedEvent.ValueKind == JsonValueKind.Array ? expectedEvent[0] : expectedEvent;
            Assert.IsTrue(
                JsonNode.DeepEquals(
                    JsonNode.Parse(expectedEvent.GetRawText()),
                    JsonNode.Parse(envelope.ExactEventBytes[0].Span)));
            Assert.IsTrue(MemoryMarshal.TryGetArray(envelope.ExactEnvelopeBytes, out var outer));
            Assert.IsTrue(MemoryMarshal.TryGetArray(envelope.ExactEventBytes[0], out var inner));
            Assert.AreSame(outer.Array, inner.Array);
        }
    }

    [TestMethod]
    public async Task EveryAcceptedRequestRequiresTheDedicatedCredential()
    {
        var sink = new RecordingSink();
        await using var fixture = await HostFixture.StartAsync(
            EligibleActivation("guffawaffle.stfc-community-mod", battle: true),
            sink);
        using var noToken = new StringContent(BattleEnvelope("no-token"), Encoding.UTF8, "application/json");
        var missing = await fixture.Client.PostAsync(BattleIngestProtocol.Route, noToken);
        using var wrongRequest = new HttpRequestMessage(HttpMethod.Post, BattleIngestProtocol.Route)
        {
            Content = new StringContent(BattleEnvelope("wrong-token"), Encoding.UTF8, "application/json"),
        };
        wrongRequest.Headers.TryAddWithoutValidation(
            BattleIngestProtocol.CompatibilityTokenHeader,
            CreateToken());
        var wrong = await fixture.Client.SendAsync(wrongRequest);

        Assert.AreEqual(HttpStatusCode.Unauthorized, missing.StatusCode);
        Assert.AreEqual(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.AreEqual(0, sink.Envelopes.Count);
        Assert.IsFalse(
            JsonSerializer.Serialize(fixture.Host.GetHealth()).Contains(
                fixture.Token,
                StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task BearerCredentialWorksButAmbiguousOrRepeatedCredentialsFailClosed()
    {
        var sink = new RecordingSink();
        await using var fixture = await HostFixture.StartAsync(
            EligibleActivation("future-compatible-runtime", battle: true),
            sink);
        using var bearer = new HttpRequestMessage(HttpMethod.Post, BattleIngestProtocol.Route)
        {
            Content = new StringContent(BattleEnvelope("bearer"), Encoding.UTF8, "application/json"),
        };
        bearer.Headers.Authorization = new AuthenticationHeaderValue("Bearer", fixture.Token);
        var accepted = await fixture.Client.SendAsync(bearer);
        using var ambiguous = fixture.CreateRequest(BattleEnvelope("ambiguous"));
        ambiguous.Headers.Authorization = new AuthenticationHeaderValue("Bearer", fixture.Token);
        var ambiguousResult = await fixture.Client.SendAsync(ambiguous);
        using var repeated = fixture.CreateRequest(BattleEnvelope("repeated"));
        repeated.Headers.Remove(BattleIngestProtocol.CompatibilityTokenHeader);
        repeated.Headers.TryAddWithoutValidation(
            BattleIngestProtocol.CompatibilityTokenHeader,
            new[] { fixture.Token, fixture.Token });
        var repeatedResult = await fixture.Client.SendAsync(repeated);

        Assert.AreEqual(HttpStatusCode.Accepted, accepted.StatusCode);
        Assert.AreEqual(HttpStatusCode.Unauthorized, ambiguousResult.StatusCode);
        Assert.AreEqual(HttpStatusCode.Unauthorized, repeatedResult.StatusCode);
        Assert.AreEqual(1, sink.Envelopes.Count);
    }

    [TestMethod]
    public async Task OnlyTheExactProducerMethodPathAndQuerylessRouteAreAccepted()
    {
        var sink = new RecordingSink();
        await using var fixture = await HostFixture.StartAsync(
            EligibleActivation("future-compatible-runtime", battle: true),
            sink);

        Assert.AreEqual(
            HttpStatusCode.Accepted,
            (await fixture.PostAsync(BattleEnvelope("exact-route"))).StatusCode);
        Assert.AreEqual(
            HttpStatusCode.NotFound,
            (await fixture.SendAsync(HttpMethod.Post, BattleIngestProtocol.Route + "/", BattleEnvelope("slash")))
                .StatusCode);
        Assert.AreEqual(
            HttpStatusCode.NotFound,
            (await fixture.SendAsync(HttpMethod.Post, BattleIngestProtocol.Route + "?x=1", BattleEnvelope("query")))
                .StatusCode);
        Assert.AreEqual(
            HttpStatusCode.NotFound,
            (await fixture.SendAsync(HttpMethod.Post, "/api/sidecar/other", BattleEnvelope("other"))).StatusCode);
        Assert.AreEqual(
            HttpStatusCode.MethodNotAllowed,
            (await fixture.SendAsync(HttpMethod.Get, BattleIngestProtocol.Route)).StatusCode);
        Assert.AreEqual(1, sink.Envelopes.Count);
    }

    [TestMethod]
    public async Task MalformedUnsupportedAndOversizedRequestsFailBeforeStorage()
    {
        var limits = SmallLimits() with { MaximumRequestBytes = 1024 };
        var sink = new RecordingSink();
        await using var fixture = await HostFixture.StartAsync(
            EligibleActivation("guffawaffle.stfc-community-mod", battle: true),
            sink,
            limits: limits);

        var malformed = await fixture.PostAsync("{not-json");
        var unsupported = await fixture.PostAsync(
            BattleEnvelope("old-version").Replace(
                BattleIngestProtocol.Version,
                "stfc.sidecar.ingest.v0",
                StringComparison.Ordinal));
        var oversized = await fixture.PostAsync(new string('x', 1025));

        Assert.AreEqual(HttpStatusCode.BadRequest, malformed.StatusCode);
        Assert.AreEqual((HttpStatusCode)422, unsupported.StatusCode);
        Assert.AreEqual(HttpStatusCode.RequestEntityTooLarge, oversized.StatusCode);
        Assert.AreEqual(0, sink.Envelopes.Count);
    }

    [TestMethod]
    public async Task DuplicateJsonKeysAndMalformedFamilyShapesFailBeforeStorage()
    {
        var sink = new RecordingSink();
        await using var fixture = await HostFixture.StartAsync(
            EligibleActivation("future-compatible-runtime", battle: true, fleet: true),
            sink);
        var duplicateKey = BattleEnvelope("duplicate-key").Replace(
            "\"kind\":\"battle.events\"",
            "\"kind\":\"battle.events\",\"kind\":\"battle.events\"",
            StringComparison.Ordinal);
        var malformedCapture = BattleEnvelope("bad-shape").Replace(
            "\"capture\":{\"sourceKind\":\"fixture\"}",
            "\"capture\":[]",
            StringComparison.Ordinal);
        var malformedFleet = FleetEnvelope("bad-fleet").Replace(
            "\"slots\":[{\"slotIndex\":0,\"present\":false}]",
            "\"slots\":{}",
            StringComparison.Ordinal);

        Assert.AreEqual(HttpStatusCode.BadRequest, (await fixture.PostAsync(duplicateKey)).StatusCode);
        Assert.AreEqual(HttpStatusCode.BadRequest, (await fixture.PostAsync(malformedCapture)).StatusCode);
        Assert.AreEqual(HttpStatusCode.BadRequest, (await fixture.PostAsync(malformedFleet)).StatusCode);
        Assert.AreEqual(0, sink.Envelopes.Count);
    }

    [TestMethod]
    public async Task OverlongCredentialAndInvalidMediaTypeRejectWithoutThrowing()
    {
        var sink = new RecordingSink();
        await using var fixture = await HostFixture.StartAsync(
            EligibleActivation("future-compatible-runtime", battle: true),
            sink);
        using var longHeader = new HttpRequestMessage(HttpMethod.Post, BattleIngestProtocol.Route)
        {
            Content = new StringContent(BattleEnvelope("long-auth"), Encoding.UTF8, "application/json"),
        };
        longHeader.Headers.TryAddWithoutValidation(
            BattleIngestProtocol.CompatibilityTokenHeader,
            new string('a', 4096));
        var unauthorized = await fixture.Client.SendAsync(longHeader);
        using var badMedia = new HttpRequestMessage(HttpMethod.Post, BattleIngestProtocol.Route)
        {
            Content = new StringContent(BattleEnvelope("bad-media"), Encoding.UTF8),
        };
        badMedia.Content.Headers.ContentType = new("application/jsonjunk");
        badMedia.Headers.TryAddWithoutValidation(
            BattleIngestProtocol.CompatibilityTokenHeader,
            fixture.Token);
        var invalidMedia = await fixture.Client.SendAsync(badMedia);

        Assert.AreEqual(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        Assert.AreEqual(HttpStatusCode.BadRequest, invalidMedia.StatusCode);
        Assert.AreEqual(0, sink.Envelopes.Count);
    }

    [TestMethod]
    public async Task DuplicateBatchIsIdempotentAndConflictingReplayIsRejected()
    {
        var sink = new RecordingSink();
        await using var fixture = await HostFixture.StartAsync(
            EligibleActivation("guffawaffle.stfc-community-mod", battle: true),
            sink);
        var envelope = BattleEnvelope("same-batch");

        var first = await fixture.PostAsync(envelope);
        var duplicate = await fixture.PostAsync(envelope);
        var conflict = await fixture.PostAsync(
            envelope.Replace("journal-1", "journal-2", StringComparison.Ordinal));

        Assert.AreEqual(HttpStatusCode.Accepted, first.StatusCode);
        Assert.AreEqual(HttpStatusCode.Accepted, duplicate.StatusCode);
        Assert.AreEqual(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.AreEqual(1, sink.Envelopes.Count);
        Assert.AreEqual(1, fixture.Host.GetHealth().DuplicateBatches);
    }

    [TestMethod]
    public async Task MixedBattleBatchWithUnadvertisedSupplementalFamilyRejectsAtomically()
    {
        var sink = new RecordingSink();
        await using var fixture = await HostFixture.StartAsync(
            EligibleActivation("guffawaffle.stfc-community-mod", battle: true),
            sink);
        using var document = JsonDocument.Parse(BattleEnvelope("mixed-batch"));
        var root = document.RootElement;
        var capture = root.GetProperty("payload")[0];
        var report = JsonSerializer.SerializeToElement(new
        {
            protocolVersion = BattleIngestProtocol.SidecarEventsVersion,
            type = "battle.report",
            schemaVersion = "stfc.sidecar.battle-report.v0",
            timestamp = "2026-08-09T12:00:00.000Z",
            journalId = "journal-1",
        });
        var mixed = JsonSerializer.Serialize(new
        {
            protocolVersion = BattleIngestProtocol.Version,
            kind = BattleIngestProtocol.BattleEventsKind,
            batchId = "mixed-batch",
            producedAt = "2026-08-09T12:00:00.000Z",
            sessionId = "test-session",
            source = "stfc-community-mod",
            modVersion = "2.1.0",
            payloadProtocol = BattleIngestProtocol.SidecarEventsVersion,
            payload = new[] { capture, report },
        });

        var response = await fixture.PostAsync(mixed);

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.AreEqual(0, sink.Envelopes.Count);
    }

    [TestMethod]
    public async Task ChunkReplayIsIdempotentAndConflictingChunkDropsTheGroup()
    {
        var sink = new RecordingSink();
        await using var fixture = await HostFixture.StartAsync(
            EligibleActivation("guffawaffle.stfc-community-mod", battle: true),
            sink);
        var chunks = Chunk(BattleEnvelope("chunked-batch"), 80).ToArray();
        Assert.IsTrue(chunks.Length > 1);

        foreach (var chunk in chunks)
        {
            Assert.AreEqual(HttpStatusCode.Accepted, (await fixture.PostAsync(chunk)).StatusCode);
        }
        foreach (var chunk in chunks)
        {
            Assert.AreEqual(HttpStatusCode.Accepted, (await fixture.PostAsync(chunk)).StatusCode);
        }
        Assert.AreEqual(1, sink.Envelopes.Count);

        var conflictGroup = Chunk(BattleEnvelope("conflict-batch"), 80).ToArray();
        Assert.AreEqual(HttpStatusCode.Accepted, (await fixture.PostAsync(conflictGroup[0])).StatusCode);
        var changed = conflictGroup[0].Replace("YQ==", "Yg==", StringComparison.Ordinal);
        if (changed == conflictGroup[0])
        {
            using var document = JsonDocument.Parse(conflictGroup[0]);
            var root = document.RootElement;
            changed = ChunkEnvelope(
                root.GetProperty("payload").GetProperty("originalBatchId").GetString()!,
                root.GetProperty("payload").GetProperty("originalKind").GetString()!,
                0,
                root.GetProperty("payload").GetProperty("chunkCount").GetInt32(),
                root.GetProperty("payload").GetProperty("totalBytes").GetInt32(),
                Encoding.UTF8.GetBytes("conflicting-bytes"));
        }
        Assert.AreEqual(HttpStatusCode.Conflict, (await fixture.PostAsync(changed)).StatusCode);
        Assert.AreEqual(0, fixture.Host.GetHealth().PendingChunkGroups);
    }

    [TestMethod]
    public void ChunkGroupsAreScopedToSourceAndSessionAndRetainMemoryThroughProcessing()
    {
        var limits = SmallLimits();
        var processor = new BattleIngestEnvelopeProcessor(
            EligibleActivation("future-compatible-runtime", battle: true),
            limits);
        var firstEnvelope = BattleEnvelope("shared-group");
        var secondEnvelope = firstEnvelope
            .Replace("test-session", "second-session", StringComparison.Ordinal)
            .Replace("journal-1", "journal-2", StringComparison.Ordinal);
        var firstChunks = Chunk(firstEnvelope, 80).ToArray();
        var secondChunks = Chunk(secondEnvelope, 80).ToArray();

        for (var index = 0; index < firstChunks.Length - 1; ++index)
        {
            Assert.AreEqual(
                BattleIngestParseStatus.ChunkPending,
                processor.Parse(Encoding.UTF8.GetBytes(firstChunks[index])).Status);
        }
        for (var index = 0; index < secondChunks.Length - 1; ++index)
        {
            Assert.AreEqual(
                BattleIngestParseStatus.ChunkPending,
                processor.Parse(Encoding.UTF8.GetBytes(secondChunks[index])).Status);
        }
        Assert.AreEqual(2, processor.PendingChunks.Groups);

        var firstComplete = processor.Parse(Encoding.UTF8.GetBytes(firstChunks[^1]));
        var secondComplete = processor.Parse(Encoding.UTF8.GetBytes(secondChunks[^1]));

        Assert.AreEqual(BattleIngestParseStatus.Complete, firstComplete.Status);
        Assert.AreEqual(BattleIngestParseStatus.Complete, secondComplete.Status);
        Assert.IsTrue(
            Encoding.UTF8.GetBytes(firstEnvelope).AsSpan()
                .SequenceEqual(firstComplete.Envelope!.ExactEnvelopeBytes.Span));
        Assert.IsTrue(
            Encoding.UTF8.GetBytes(secondEnvelope).AsSpan()
                .SequenceEqual(secondComplete.Envelope!.ExactEnvelopeBytes.Span));
        Assert.AreEqual(0, processor.PendingChunks.Groups);
        Assert.AreEqual(
            firstComplete.Envelope!.ExactEnvelopeBytes.Length + secondComplete.Envelope!.ExactEnvelopeBytes.Length,
            processor.PendingChunks.Bytes);
        firstComplete.ProcessingLease!.Dispose();
        secondComplete.ProcessingLease!.Dispose();
        Assert.AreEqual(0, processor.PendingChunks.Bytes);
    }

    [TestMethod]
    public async Task InFlightChunkDuplicateRemainsChargedUntilSharedCommitCompletes()
    {
        var sink = new HeldSink();
        await using var fixture = await HostFixture.StartAsync(
            EligibleActivation("future-compatible-runtime", battle: true),
            sink);
        var envelope = BattleEnvelope("held-chunk-duplicate");
        var envelopeBytes = Encoding.UTF8.GetByteCount(envelope);
        var chunks = Chunk(envelope, 80).ToArray();
        foreach (var chunk in chunks[..^1])
        {
            Assert.AreEqual(HttpStatusCode.Accepted, (await fixture.PostAsync(chunk)).StatusCode);
        }
        var original = fixture.PostAsync(chunks[^1]);
        await sink.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() => fixture.Host.GetHealth().PendingChunkBytes == envelopeBytes);

        foreach (var chunk in chunks[..^1])
        {
            Assert.AreEqual(HttpStatusCode.Accepted, (await fixture.PostAsync(chunk)).StatusCode);
        }
        var duplicate = fixture.PostAsync(chunks[^1]);

        await WaitUntilAsync(() => fixture.Host.GetHealth().PendingChunkBytes == 2L * envelopeBytes);
        Assert.IsFalse(duplicate.IsCompleted);
        sink.Release.TrySetResult();
        Assert.AreEqual(HttpStatusCode.Accepted, (await original).StatusCode);
        Assert.AreEqual(HttpStatusCode.Accepted, (await duplicate).StatusCode);
        await WaitUntilAsync(() => fixture.Host.GetHealth().PendingChunkBytes == 0);
    }

    [TestMethod]
    public async Task BatchDedupeIsScopedToSourceAndSession()
    {
        var sink = new RecordingSink();
        await using var fixture = await HostFixture.StartAsync(
            EligibleActivation("future-compatible-runtime", battle: true),
            sink);
        var first = BattleEnvelope("shared-batch");
        var otherSession = first.Replace("test-session", "another-session", StringComparison.Ordinal);
        var otherSource = first.Replace("stfc-community-mod", "future-mod", StringComparison.Ordinal);

        Assert.AreEqual(HttpStatusCode.Accepted, (await fixture.PostAsync(first)).StatusCode);
        Assert.AreEqual(HttpStatusCode.Accepted, (await fixture.PostAsync(otherSession)).StatusCode);
        Assert.AreEqual(HttpStatusCode.Accepted, (await fixture.PostAsync(otherSource)).StatusCode);
        Assert.AreEqual(3, sink.Envelopes.Count);
    }

    [TestMethod]
    public async Task RequestTimeoutCancelsUncommittedStorageAndAllowsRetry()
    {
        var sink = new RecordingSink(TimeSpan.FromSeconds(5));
        var limits = SmallLimits() with { RequestTimeout = TimeSpan.FromMilliseconds(150) };
        await using var fixture = await HostFixture.StartAsync(
            EligibleActivation("guffawaffle.stfc-community-mod", battle: true),
            sink,
            limits: limits);

        var response = await fixture.PostAsync(BattleEnvelope("timeout-batch"));
        var body = await response.Content.ReadAsStringAsync();

        Assert.AreEqual(HttpStatusCode.RequestTimeout, response.StatusCode);
        StringAssert.Contains(body, "timed-out");
        await WaitUntilAsync(() => sink.Cancellations > 0);
        Assert.AreEqual(0, fixture.Host.GetHealth().AcceptedBatches);
    }

    [TestMethod]
    public async Task StorageFailureHasADistinctTruthfulResponse()
    {
        await using var fixture = await HostFixture.StartAsync(
            EligibleActivation("future-compatible-runtime", battle: true),
            new ThrowingSink());

        var response = await fixture.PostAsync(BattleEnvelope("storage-failure"));
        var body = await response.Content.ReadAsStringAsync();

        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        StringAssert.Contains(body, "storage-rejected");
        Assert.AreEqual(BattleIngestFailureCode.StorageRejected, fixture.Host.GetHealth().LastFailure);
    }

    [TestMethod]
    public async Task RateLimitIsDeterministicAndDoesNotReachStorage()
    {
        var limits = SmallLimits() with { RequestsPerWindow = 1, RateWindow = TimeSpan.FromMinutes(1) };
        var sink = new RecordingSink();
        await using var fixture = await HostFixture.StartAsync(
            EligibleActivation("guffawaffle.stfc-community-mod", battle: true),
            sink,
            limits: limits);

        var first = await fixture.PostAsync(BattleEnvelope("rate-one"));
        var second = await fixture.PostAsync(BattleEnvelope("rate-two"));

        Assert.AreEqual(HttpStatusCode.Accepted, first.StatusCode);
        Assert.AreEqual(HttpStatusCode.TooManyRequests, second.StatusCode);
        Assert.AreEqual(1, sink.Envelopes.Count);
    }

    [TestMethod]
    public async Task UnauthorizedPressureCannotBypassGlobalRateOrConcurrencyAdmission()
    {
        var held = new HeldSink();
        var concurrencyLimits = SmallLimits() with { MaximumConcurrentRequests = 1 };
        await using (var fixture = await HostFixture.StartAsync(
            EligibleActivation("future-compatible-runtime", battle: true),
            held,
            concurrencyLimits))
        {
            var occupying = fixture.PostAsync(BattleEnvelope("admission-occupying"));
            await held.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            using var unauthorizedBody = new StringContent(
                BattleEnvelope("unauthorized-pressure"),
                Encoding.UTF8,
                "application/json");

            var pressure = await fixture.Client.PostAsync(BattleIngestProtocol.Route, unauthorizedBody);

            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, pressure.StatusCode);
            held.Release.TrySetResult();
            Assert.AreEqual(HttpStatusCode.Accepted, (await occupying).StatusCode);
        }

        var rateLimits = SmallLimits() with
        {
            RequestsPerWindow = 1,
            RateWindow = TimeSpan.FromMinutes(1),
        };
        await using var rateFixture = await HostFixture.StartAsync(
            EligibleActivation("future-compatible-runtime", battle: true),
            new RecordingSink(),
            rateLimits);
        using var missingTokenBody = new StringContent(
            BattleEnvelope("rate-unauthorized"),
            Encoding.UTF8,
            "application/json");
        var unauthorized = await rateFixture.Client.PostAsync(BattleIngestProtocol.Route, missingTokenBody);
        var rateLimited = await rateFixture.PostAsync(BattleEnvelope("rate-after-unauthorized"));

        Assert.AreEqual(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        Assert.AreEqual(HttpStatusCode.TooManyRequests, rateLimited.StatusCode);
    }

    [TestMethod]
    public async Task EarlyRejectedRequestsReleaseTheirGlobalAdmissionPermit()
    {
        var limits = SmallLimits() with { MaximumConcurrentRequests = 1 };
        var sink = new RecordingSink();
        await using var fixture = await HostFixture.StartAsync(
            EligibleActivation("future-compatible-runtime", battle: true),
            sink,
            limits);
        for (var index = 0; index < 10; ++index)
        {
            using var missingToken = new StringContent(
                BattleEnvelope($"missing-{index}"),
                Encoding.UTF8,
                "application/json");
            Assert.AreEqual(
                HttpStatusCode.Unauthorized,
                (await fixture.Client.PostAsync(BattleIngestProtocol.Route, missingToken)).StatusCode);
            Assert.AreEqual(
                HttpStatusCode.NotFound,
                (await fixture.SendAsync(HttpMethod.Post, $"/wrong-{index}", BattleEnvelope($"wrong-{index}")))
                    .StatusCode);
        }

        Assert.AreEqual(
            HttpStatusCode.Accepted,
            (await fixture.PostAsync(BattleEnvelope("after-early-rejections"))).StatusCode);
        Assert.AreEqual(1, sink.Envelopes.Count);
    }

    [TestMethod]
    public async Task ConcurrentDuplicateWaitersReturnDeterministicallyWhenQueueIsFull()
    {
        var sink = new HeldSink();
        var limits = SmallLimits() with
        {
            MaximumQueuedBatches = 1,
            RequestTimeout = TimeSpan.FromSeconds(5),
        };
        await using var fixture = await HostFixture.StartAsync(
            EligibleActivation("future-compatible-runtime", battle: true),
            sink,
            limits);
        var occupying = fixture.PostAsync(BattleEnvelope("occupying"));
        await sink.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var duplicates = Enumerable.Range(0, 16)
            .Select(_ => fixture.PostAsync(BattleEnvelope("queue-full-duplicate")))
            .ToArray();
        var results = await Task.WhenAll(duplicates).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.IsTrue(results.All(response => response.StatusCode == HttpStatusCode.ServiceUnavailable));
        sink.Release.TrySetResult();
        Assert.AreEqual(HttpStatusCode.Accepted, (await occupying).StatusCode);
    }

    [TestMethod]
    public async Task PortCollisionFailsClosedWithoutStartingAWorker()
    {
        var token = CreateToken();
        var port = ReservedUnusedPort();
        await using var first = new BattleIngestHost(
            EligibleActivation("future-compatible-runtime", battle: true),
            token,
            new RecordingSink(),
            port,
            SmallLimits());
        var firstResult = await first.StartAsync();
        Assert.AreEqual(BattleIngestStartStatus.Started, firstResult.Status);
        await using var second = new BattleIngestHost(
            EligibleActivation("future-compatible-runtime", battle: true),
            CreateToken(),
            new RecordingSink(),
            port,
            SmallLimits());

        var collision = await second.StartAsync();

        Assert.AreEqual(BattleIngestStartStatus.PortUnavailable, collision.Status);
        Assert.AreEqual(BattleIngestListenerState.PortUnavailable, second.GetHealth().ListenerState);
        Assert.AreEqual(0, second.GetHealth().PendingBatches);
    }

    [TestMethod]
    public async Task CleanShutdownStopsAcceptsAndDrainsCommittedWork()
    {
        var sink = new RecordingSink();
        var fixture = await HostFixture.StartAsync(
            EligibleActivation("future-compatible-runtime", battle: true),
            sink);
        Assert.AreEqual(
            HttpStatusCode.Accepted,
            (await fixture.PostAsync(BattleEnvelope("before-stop"))).StatusCode);

        await fixture.Host.StopAsync();

        Assert.AreEqual(BattleIngestListenerState.Stopped, fixture.Host.GetHealth().ListenerState);
        await Assert.ThrowsExceptionAsync<HttpRequestException>(
            () => fixture.PostAsync(BattleEnvelope("after-stop")));
        await fixture.DisposeAsync();
    }

    [TestMethod]
    public async Task ShutdownClearsIncompleteChunkGroups()
    {
        var fixture = await HostFixture.StartAsync(
            EligibleActivation("future-compatible-runtime", battle: true),
            new RecordingSink());
        var firstChunk = Chunk(BattleEnvelope("stop-chunks"), 80).First();
        Assert.AreEqual(HttpStatusCode.Accepted, (await fixture.PostAsync(firstChunk)).StatusCode);
        Assert.AreEqual(1, fixture.Host.GetHealth().PendingChunkGroups);

        await fixture.Host.StopAsync();

        Assert.AreEqual(0, fixture.Host.GetHealth().PendingChunkGroups);
        Assert.AreEqual(0, fixture.Host.GetHealth().PendingChunkBytes);
        await fixture.DisposeAsync();
    }

    [TestMethod]
    public async Task RacingChunkDeliveryAndShutdownAlwaysLeavesZeroPendingMemory()
    {
        for (var iteration = 0; iteration < 2; ++iteration)
        {
            var fixture = await HostFixture.StartAsync(
                EligibleActivation("future-compatible-runtime", battle: true),
                new RecordingSink());
            var delivery = fixture.PostAsync(Chunk(BattleEnvelope($"racing-chunk-{iteration}"), 80).First());
            var stopping = fixture.Host.StopAsync();
            try
            {
                _ = await delivery;
            }
            catch (HttpRequestException)
            {
            }
            await stopping;

            Assert.AreEqual(0, fixture.Host.GetHealth().PendingChunkGroups);
            Assert.AreEqual(0, fixture.Host.GetHealth().PendingChunkBytes);
            await fixture.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task StopAndDisposeAreOneShotAndCloseThePort()
    {
        var port = ReservedUnusedPort();
        var host = new BattleIngestHost(
            EligibleActivation("future-compatible-runtime", battle: true),
            CreateToken(),
            new RecordingSink(),
            port,
            SmallLimits());
        Assert.AreEqual(BattleIngestStartStatus.Started, (await host.StartAsync()).Status);

        await host.StopAsync();

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => host.StartAsync());
        using (var probe = new TcpListener(IPAddress.Loopback, port))
        {
            probe.Start();
        }
        await host.DisposeAsync();
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => host.StartAsync());
    }

    [TestMethod]
    public async Task RepeatedStartStopDisposeDoesNotLeakAcceptLoopAbortRaces()
    {
        for (var iteration = 0; iteration < 40; ++iteration)
        {
            var port = ReservedUnusedPort();
            var host = new BattleIngestHost(
                EligibleActivation("future-compatible-runtime", battle: true),
                CreateToken(),
                new RecordingSink(),
                port,
                SmallLimits());
            Assert.AreEqual(BattleIngestStartStatus.Started, (await host.StartAsync()).Status);
            await host.StopAsync();
            await host.DisposeAsync();
            using var probe = new TcpListener(IPAddress.Loopback, port);
            probe.Start();
        }
    }

    [TestMethod]
    public async Task ConcurrentStartAndStopSerializeWithoutNullOwnerOrOpenPort()
    {
        for (var iteration = 0; iteration < 40; ++iteration)
        {
            var port = ReservedUnusedPort();
            var host = new BattleIngestHost(
                EligibleActivation("future-compatible-runtime", battle: true),
                CreateToken(),
                new RecordingSink(),
                port,
                SmallLimits());
            var gate = new Barrier(2);
            var start = Task.Run(async () =>
            {
                gate.SignalAndWait();
                try
                {
                    _ = await host.StartAsync();
                }
                catch (InvalidOperationException)
                {
                }
            });
            var stop = Task.Run(async () =>
            {
                gate.SignalAndWait();
                await host.StopAsync();
            });

            await Task.WhenAll(start, stop).WaitAsync(TimeSpan.FromSeconds(3));
            await host.DisposeAsync();
            using var probe = new TcpListener(IPAddress.Loopback, port);
            probe.Start();
        }
    }

    [TestMethod]
    public async Task UnexpectedAcceptFailureIsTypedAndStopStillCleansUp()
    {
        var port = ReservedUnusedPort();
        var host = new BattleIngestHost(
            EligibleActivation("future-compatible-runtime", battle: true),
            CreateToken(),
            new RecordingSink(),
            port,
            SmallLimits())
        {
            AcceptContextAsync = _ => Task.FromException<HttpListenerContext>(
                new IOException("injected accept failure")),
        };

        Assert.AreEqual(BattleIngestStartStatus.Started, (await host.StartAsync()).Status);
        await WaitUntilAsync(() => host.GetHealth().ListenerState == BattleIngestListenerState.Failed);
        Assert.AreEqual(BattleIngestFailureCode.ListenerFailed, host.GetHealth().LastFailure);
        Assert.AreEqual("listener-failed", host.GetHealth().LastTransition);

        await host.StopAsync();
        await host.DisposeAsync();
        using var probe = new TcpListener(IPAddress.Loopback, port);
        probe.Start();
    }

    [TestMethod]
    public void UnsafeTimerAndReassemblyOverlapLimitsAreRejected()
    {
        var baseLimits = SmallLimits();

        Assert.ThrowsException<ArgumentOutOfRangeException>(
            () => (baseLimits with { RequestTimeout = TimeSpan.FromDays(60) }).Validate());
        Assert.ThrowsException<ArgumentOutOfRangeException>(
            () => (baseLimits with { RateWindow = TimeSpan.MaxValue }).Validate());
        Assert.ThrowsException<ArgumentOutOfRangeException>(
            () => (baseLimits with
            {
                MaximumPendingChunkBytes = 2 * baseLimits.MaximumReassembledBytes - 1,
            }).Validate());
    }

    [TestMethod]
    public async Task CallerCancelledStopStillCompletesCleanup()
    {
        var sink = new RecordingSink(TimeSpan.FromSeconds(5));
        var limits = SmallLimits() with
        {
            RequestTimeout = TimeSpan.FromSeconds(10),
            ShutdownDrainTimeout = TimeSpan.FromSeconds(5),
        };
        var fixture = await HostFixture.StartAsync(
            EligibleActivation("future-compatible-runtime", battle: true),
            sink,
            limits);
        var request = fixture.PostAsync(BattleEnvelope("caller-cancel"));
        await WaitUntilAsync(() => fixture.Host.GetHealth().PendingBatches == 1);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsExceptionAsync<OperationCanceledException>(
            () => fixture.Host.StopAsync(cancelled.Token));

        Assert.AreEqual(1, sink.Cancellations);
        Assert.AreEqual(0, fixture.Host.GetHealth().PendingBatches);
        Assert.AreEqual(0, fixture.Host.GetHealth().PendingChunkGroups);
        try
        {
            _ = await request;
        }
        catch (HttpRequestException)
        {
        }
        await fixture.DisposeAsync();
    }

    [TestMethod]
    public async Task CancellationIgnoringSinkKeepsShutdownJoinedUntilItTerminates()
    {
        var sink = new HeldSink();
        var limits = SmallLimits() with
        {
            RequestTimeout = TimeSpan.FromSeconds(10),
            ShutdownDrainTimeout = TimeSpan.FromMilliseconds(100),
        };
        var fixture = await HostFixture.StartAsync(
            EligibleActivation("future-compatible-runtime", battle: true),
            sink,
            limits);
        var request = fixture.PostAsync(BattleEnvelope("held-shutdown"));
        await sink.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var stopping = fixture.Host.StopAsync();
        await Task.Delay(250);

        Assert.IsFalse(stopping.IsCompleted);
        Assert.AreEqual("shutdown-cancellation-requested", fixture.Host.GetHealth().LastTransition);
        sink.Release.TrySetResult();
        await stopping;
        Assert.AreEqual(BattleIngestListenerState.Failed, fixture.Host.GetHealth().ListenerState);
        Assert.AreEqual(0, fixture.Host.GetHealth().PendingBatches);
        try
        {
            _ = await request;
        }
        catch (HttpRequestException)
        {
        }
        await fixture.DisposeAsync();
    }

    [TestMethod]
    public async Task ShutdownDeadlineCancelsStorageAndLeavesNoDetachedWorker()
    {
        var sink = new RecordingSink(TimeSpan.FromSeconds(5));
        var limits = SmallLimits() with
        {
            RequestTimeout = TimeSpan.FromSeconds(10),
            ShutdownDrainTimeout = TimeSpan.FromMilliseconds(100),
        };
        var fixture = await HostFixture.StartAsync(
            EligibleActivation("future-compatible-runtime", battle: true),
            sink,
            limits);
        var request = fixture.PostAsync(BattleEnvelope("shutdown-timeout"));
        await WaitUntilAsync(() => fixture.Host.GetHealth().PendingBatches == 1);

        await fixture.Host.StopAsync();

        await WaitUntilAsync(() => sink.Cancellations == 1);
        Assert.AreEqual(BattleIngestListenerState.Failed, fixture.Host.GetHealth().ListenerState);
        Assert.AreEqual(BattleIngestFailureCode.ShutdownTimedOut, fixture.Host.GetHealth().LastFailure);
        Assert.AreEqual(0, fixture.Host.GetHealth().PendingBatches);
        try
        {
            _ = await request;
        }
        catch (HttpRequestException)
        {
        }
        await fixture.DisposeAsync();
    }

    [TestMethod]
    public void PendingChunkMemoryExpiresWithoutATimerOrWorker()
    {
        var clock = new TestTimeProvider(new DateTimeOffset(2026, 8, 9, 12, 0, 0, TimeSpan.Zero));
        var limits = SmallLimits() with { PendingChunkTimeout = TimeSpan.FromSeconds(5) };
        var processor = new BattleIngestEnvelopeProcessor(
            EligibleActivation("future-compatible-runtime", battle: true),
            limits,
            clock);
        var first = Chunk(BattleEnvelope("expiring-chunks"), 80).First();

        Assert.AreEqual(BattleIngestParseStatus.ChunkPending, processor.Parse(Encoding.UTF8.GetBytes(first)).Status);
        Assert.AreEqual(1, processor.PendingChunks.Groups);
        clock.Advance(TimeSpan.FromSeconds(6));

        Assert.AreEqual(0, processor.PendingChunks.Groups);
        Assert.AreEqual(0, processor.PendingChunks.Bytes);
    }

    [TestMethod]
    public async Task CurrentUnknownNetnivAndInactiveDemandCreateNoListener()
    {
        var unknownNetniv = new LauncherRuntimeProfile(
            LauncherRuntimeManifestDetector.NetnivDistributionId,
            new Version(1, 1, 4),
            null,
            null,
            [],
            []);
        var activation = BattleIngestActivation.Resolve(
            unknownNetniv,
            new(true, true));
        await using var host = new BattleIngestHost(
            activation,
            CreateToken(),
            new RecordingSink(),
            ReservedUnusedPort(),
            SmallLimits());

        var result = await host.StartAsync();

        Assert.AreEqual(BattleIngestStartStatus.Inactive, result.Status);
        Assert.AreEqual(0, result.BoundPort);
        Assert.AreEqual(0, host.GetHealth().PendingBatches);
    }

    [TestMethod]
    public async Task FutureCompatibleNetnivUsesCapabilitiesWithoutProviderBranch()
    {
        var sink = new RecordingSink();
        await using var fixture = await HostFixture.StartAsync(
            EligibleActivation(LauncherRuntimeManifestDetector.NetnivDistributionId, battle: true),
            sink);

        var response = await fixture.PostAsync(BattleEnvelope("future-netniv"));

        Assert.AreEqual(HttpStatusCode.Accepted, response.StatusCode);
        Assert.AreEqual(1, sink.Envelopes.Count);
    }

    private static BattleIngestActivation EligibleActivation(
        string distribution,
        bool battle = false,
        bool fleet = false)
    {
        var capabilities = new List<string> { LauncherCapabilityIds.SidecarIngestV1 };
        if (battle)
        {
            capabilities.Add(LauncherCapabilityIds.BattleCaptureV1);
        }
        if (fleet)
        {
            capabilities.Add(LauncherCapabilityIds.FleetRuntimeSnapshotV1);
        }
        var profile = new LauncherRuntimeProfile(
            distribution,
            new Version(2, 1),
            "fixture",
            null,
            capabilities,
            []);
        return BattleIngestActivation.Resolve(profile, new(battle, fleet));
    }

    private static BattleIngestLimits SmallLimits() =>
        BattleIngestLimits.Default with
        {
            MaximumReassembledBytes = 1024 * 1024,
            MaximumPendingChunkBytes = 2 * 1024 * 1024,
            MaximumQueuedBytes = 2 * 1024 * 1024,
            PendingChunkTimeout = TimeSpan.FromSeconds(1),
            ShutdownDrainTimeout = TimeSpan.FromSeconds(1),
        };

    private static string BattleEnvelope(string batchId) =>
        JsonSerializer.Serialize(new
        {
            protocolVersion = BattleIngestProtocol.Version,
            kind = BattleIngestProtocol.BattleEventsKind,
            batchId,
            producedAt = "2026-08-09T12:00:00.000Z",
            sessionId = "test-session",
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
                    journalId = "journal-1",
                    capture = new { sourceKind = "fixture" },
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
            sessionId = "test-session",
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

    private static IEnumerable<string> Chunk(string envelope, int chunkBytes)
    {
        var bytes = Encoding.UTF8.GetBytes(envelope);
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        var batchId = root.GetProperty("batchId").GetString()!;
        var kind = root.GetProperty("kind").GetString()!;
        var sessionId = root.GetProperty("sessionId").GetString()!;
        var source = root.GetProperty("source").GetString()!;
        var count = (bytes.Length + chunkBytes - 1) / chunkBytes;
        for (var index = 0; index < count; ++index)
        {
            var length = Math.Min(chunkBytes, bytes.Length - index * chunkBytes);
            yield return ChunkEnvelope(
                batchId,
                kind,
                index,
                count,
                bytes.Length,
                bytes.AsSpan(index * chunkBytes, length).ToArray(),
                sessionId,
                source);
        }
    }

    private static string ChunkEnvelope(
        string batchId,
        string kind,
        int index,
        int count,
        int totalBytes,
        byte[] bytes,
        string sessionId = "test-session",
        string source = "stfc-community-mod") =>
        JsonSerializer.Serialize(new
        {
            protocolVersion = BattleIngestProtocol.Version,
            kind = BattleIngestProtocol.TransportChunkKind,
            batchId = $"{batchId}:chunk:{index + 1}",
            producedAt = "2026-08-09T12:00:00.000Z",
            sessionId,
            source,
            modVersion = "2.1.0",
            payloadProtocol = BattleIngestProtocol.TransportChunkVersion,
            payload = new
            {
                schemaVersion = BattleIngestProtocol.TransportChunkVersion,
                chunkGroupId = batchId,
                chunkIndex = index,
                chunkCount = count,
                totalBytes,
                originalKind = kind,
                originalBatchId = batchId,
                chunkEncoding = "base64",
                chunkBase64 = Convert.ToBase64String(bytes),
            },
        });

    private static string CreateToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static int ReservedUnusedPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (!predicate() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }
        Assert.IsTrue(predicate());
    }

    private sealed class RecordingSink(TimeSpan? delay = null) : IBattleIngestSink
    {
        private readonly object gate = new();
        private int cancellations;

        public List<BattleIngestEnvelope> Envelopes { get; } = [];

        public List<int> CommitThreadIds { get; } = [];

        public int Cancellations => Volatile.Read(ref cancellations);

        public async ValueTask<BattleIngestCommitResult> CommitAsync(
            BattleIngestEnvelope envelope,
            CancellationToken cancellationToken)
        {
            try
            {
                if (delay is not null)
                {
                    await Task.Delay(delay.Value, cancellationToken);
                }
                lock (gate)
                {
                    Envelopes.Add(envelope);
                    CommitThreadIds.Add(Environment.CurrentManagedThreadId);
                }
                return new(envelope.ExactEventBytes.Count);
            }
            catch (OperationCanceledException)
            {
                Interlocked.Increment(ref cancellations);
                throw;
            }
        }
    }

    private sealed class HeldSink : IBattleIngestSink
    {
        public TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<BattleIngestCommitResult> CommitAsync(
            BattleIngestEnvelope envelope,
            CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            await Release.Task.ConfigureAwait(false);
            return new(envelope.ExactEventBytes.Count);
        }
    }

    private sealed class ThrowingSink : IBattleIngestSink
    {
        public ValueTask<BattleIngestCommitResult> CommitAsync(
            BattleIngestEnvelope envelope,
            CancellationToken cancellationToken) =>
            ValueTask.FromException<BattleIngestCommitResult>(new IOException("fixture storage failure"));
    }

    private sealed class HostFixture : IAsyncDisposable
    {
        private HostFixture(BattleIngestHost host, HttpClient client, string token)
        {
            Host = host;
            Client = client;
            Token = token;
        }

        public BattleIngestHost Host { get; }
        public HttpClient Client { get; }
        public string Token { get; }

        public static async Task<HostFixture> StartAsync(
            BattleIngestActivation activation,
            IBattleIngestSink sink,
            BattleIngestLimits? limits = null)
        {
            var token = CreateToken();
            var host = new BattleIngestHost(
                activation,
                token,
                sink,
                ReservedUnusedPort(),
                limits ?? SmallLimits());
            var result = await host.StartAsync();
            Assert.AreEqual(BattleIngestStartStatus.Started, result.Status);
            var client = new HttpClient
            {
                BaseAddress = new Uri($"http://127.0.0.1:{result.BoundPort}"),
                Timeout = TimeSpan.FromSeconds(3),
            };
            return new(host, client, token);
        }

        public Task<HttpResponseMessage> PostAsync(string json)
        {
            return Client.SendAsync(CreateRequest(json));
        }

        public HttpRequestMessage CreateRequest(string json) =>
            CreateRequest(HttpMethod.Post, BattleIngestProtocol.Route, json);

        public Task<HttpResponseMessage> SendAsync(
            HttpMethod method,
            string route,
            string? json = null) =>
            Client.SendAsync(CreateRequest(method, route, json));

        private HttpRequestMessage CreateRequest(HttpMethod method, string route, string? json)
        {
            var request = new HttpRequestMessage(method, route);
            if (json is not null)
            {
                request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            }
            request.Headers.TryAddWithoutValidation(
                BattleIngestProtocol.CompatibilityTokenHeader,
                Token);
            return request;
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Host.DisposeAsync();
        }
    }

    private sealed class TestTimeProvider(DateTimeOffset current) : TimeProvider
    {
        private DateTimeOffset current = current;

        public override DateTimeOffset GetUtcNow() => current;

        public void Advance(TimeSpan duration) => current += duration;
    }
}
