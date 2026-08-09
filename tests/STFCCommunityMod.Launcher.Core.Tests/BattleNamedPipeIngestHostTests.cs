using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace STFCCommunityMod.Launcher.Core.Tests;

[TestClass]
public sealed class BattleNamedPipeIngestHostTests
{
    [TestMethod]
    public async Task EligibleRuntimeAcceptsExactBytesFromTheAuthorizedProcess()
    {
        RequireWindows();
        var pipeName = PipeName();
        var credential = Credential();
        var sink = new RecordingSink();
        await using var host = Host(pipeName, credential, sink, Environment.ProcessId);

        Assert.AreEqual(BattleLocalIpcState.Listening, (await host.StartAsync()).State);
        var exact = Encoding.UTF8.GetBytes(BattleEnvelope("accepted"));
        var response = await SendAsync(pipeName, Header(credential), exact);

        Assert.AreEqual("accepted", response.RootElement.GetProperty("status").GetString());
        Assert.AreEqual(1, response.RootElement.GetProperty("acceptedRecords").GetInt32());
        CollectionAssert.AreEqual(exact, sink.Envelopes.Single().ExactEnvelopeBytes.ToArray());
        Assert.AreEqual(1, host.GetHealth().AcceptedRequests);
    }

    [TestMethod]
    public async Task WrongCredentialFailsBeforePayloadCommit()
    {
        RequireWindows();
        var pipeName = PipeName();
        var credential = Credential();
        var sink = new RecordingSink();
        await using var host = Host(pipeName, credential, sink, Environment.ProcessId);
        await host.StartAsync();

        var wrongCredential = await SendAsync(
            pipeName,
            Header(Credential()),
            Encoding.UTF8.GetBytes(BattleEnvelope("wrong-token")));
        Assert.AreEqual("unauthorized", wrongCredential.RootElement.GetProperty("failure").GetString());
        Assert.AreEqual(0, sink.Envelopes.Count);
        Assert.AreEqual(1, host.GetHealth().RejectedRequests);
    }

    [TestMethod]
    public async Task WrongProcessFailsBeforePayloadCommit()
    {
        RequireWindows();
        var pipeName = PipeName();
        var credential = Credential();
        var sink = new RecordingSink();
        await using var host = Host(pipeName, credential, sink, Environment.ProcessId + 1);
        await host.StartAsync();

        var response = await SendAsync(
            pipeName,
            Header(credential),
            Encoding.UTF8.GetBytes(BattleEnvelope("wrong-process")));

        Assert.AreEqual("unauthorized", response.RootElement.GetProperty("failure").GetString());
        Assert.AreEqual(0, sink.Envelopes.Count);
    }

    [TestMethod]
    public async Task ClosedHeaderRejectsUnknownRolePropertiesAndEscapedDuplicateKeys()
    {
        RequireWindows();
        var pipeName = PipeName();
        var credential = Credential();
        var sink = new RecordingSink();
        await using var host = Host(pipeName, credential, sink, Environment.ProcessId);
        await host.StartAsync();

        var wrongRole = Header(credential).Replace(
            BattleLocalIpcProtocol.RuntimeRole,
            "bridge-shell",
            StringComparison.Ordinal);
        var wrongProtocol = Header(credential).Replace(
            BattleLocalIpcProtocol.Version,
            "stfc.battle-bridge.local-ipc.v2",
            StringComparison.Ordinal);
        var unknown = Header(credential).TrimEnd('}') + ",\"endpoint\":\"local\"}";
        var duplicate = Header(credential).TrimEnd('}')
            + ",\"r\\u006fle\":\"stfc-mod-runtime\"}";

        using (var roleResponse = await SendAsync(
                   pipeName,
                   wrongRole,
                   Encoding.UTF8.GetBytes(BattleEnvelope("wrong-role"))))
        {
            Assert.AreEqual("unauthorized", roleResponse.RootElement.GetProperty("failure").GetString());
        }
        using (var protocolResponse = await SendAsync(
                   pipeName,
                   wrongProtocol,
                   Encoding.UTF8.GetBytes(BattleEnvelope("wrong-protocol"))))
        {
            Assert.AreEqual(
                "unsupported-protocol",
                protocolResponse.RootElement.GetProperty("failure").GetString());
        }
        foreach (var hostile in new[] { unknown, duplicate })
        {
            using var response = await SendAsync(
                pipeName,
                hostile,
                Encoding.UTF8.GetBytes(BattleEnvelope(Guid.NewGuid().ToString("N"))));
            Assert.AreEqual("invalid-request", response.RootElement.GetProperty("failure").GetString());
        }
        Assert.AreEqual(0, sink.Envelopes.Count);
    }

    [TestMethod]
    public async Task InactiveConstructionAndStartCreateNoPipe()
    {
        RequireWindows();
        var pipeName = PipeName();
        await using var host = new BattleNamedPipeIngestHost(
            InactiveActivation(),
            pipeName,
            Credential(),
            new RecordingSink(),
            new ExactProcessBattleNamedPipeClientAuthorizer([CurrentProcessReceipt()]),
            EvidenceSha256);

        await AssertCannotConnectAsync(pipeName);
        Assert.AreEqual(BattleLocalIpcState.Inactive, (await host.StartAsync()).State);
        await AssertCannotConnectAsync(pipeName);
        Assert.AreEqual(BattleLocalIpcState.Inactive, host.GetHealth().State);
    }

    [TestMethod]
    public async Task FirstInstanceCollisionFailsClosedAndStopReleasesThePipe()
    {
        RequireWindows();
        var pipeName = PipeName();
        var credential = Credential();
        await using var first = Host(pipeName, credential, new RecordingSink(), Environment.ProcessId);
        await using var second = Host(pipeName, credential, new RecordingSink(), Environment.ProcessId);

        Assert.AreEqual(BattleLocalIpcState.Listening, (await first.StartAsync()).State);
        Assert.AreEqual(BattleLocalIpcState.Failed, (await second.StartAsync()).State);
        await first.StopAsync();
        await AssertCannotConnectAsync(pipeName);
        Assert.AreEqual(BattleLocalIpcState.Stopped, first.GetHealth().State);
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => first.StartAsync());
    }

    [TestMethod]
    public async Task OversizedHeaderAndPayloadAreRejectedBeforeTheSink()
    {
        RequireWindows();
        var pipeName = PipeName();
        var credential = Credential();
        var sink = new RecordingSink();
        await using var host = Host(pipeName, credential, sink, Environment.ProcessId);
        await host.StartAsync();

        using var headerResponse = await SendAnnouncedHeaderLengthAsync(
            pipeName,
            BattleLocalIpcProtocol.MaximumHeaderBytes + 1);
        using var payloadResponse = await SendAnnouncedPayloadLengthAsync(
            pipeName,
            Header(credential),
            BattleIngestLimits.Default.MaximumRequestBytes + 1);

        Assert.AreEqual("payload-too-large", headerResponse.RootElement.GetProperty("failure").GetString());
        Assert.AreEqual("payload-too-large", payloadResponse.RootElement.GetProperty("failure").GetString());
        Assert.AreEqual(0, sink.Envelopes.Count);
    }

    [TestMethod]
    public async Task SinkFailureHasAClosedResponseAndDoesNotCountAsAccepted()
    {
        RequireWindows();
        var pipeName = PipeName();
        var credential = Credential();
        await using var host = Host(pipeName, credential, new ThrowingSink(), Environment.ProcessId);
        await host.StartAsync();

        using var response = await SendAsync(
            pipeName,
            Header(credential),
            Encoding.UTF8.GetBytes(BattleEnvelope("sink-failure")));

        Assert.AreEqual("storage-rejected", response.RootElement.GetProperty("failure").GetString());
        Assert.AreEqual(0, host.GetHealth().AcceptedRequests);
    }

    [TestMethod]
    public async Task FixedWindowRateLimitRejectsBeforePayloadRead()
    {
        RequireWindows();
        var pipeName = PipeName();
        var credential = Credential();
        var sink = new RecordingSink();
        await using var host = new BattleNamedPipeIngestHost(
            EligibleActivation(),
            pipeName,
            credential,
            sink,
            new ExactProcessBattleNamedPipeClientAuthorizer([CurrentProcessReceipt()]),
            EvidenceSha256,
            BattleIngestLimits.Default with
            {
                RequestsPerWindow = 1,
                RequestTimeout = TimeSpan.FromSeconds(2),
                ShutdownDrainTimeout = TimeSpan.FromSeconds(1),
            },
            new FixedTimeProvider());
        await host.StartAsync();

        using var accepted = await SendAsync(
            pipeName,
            Header(credential),
            Encoding.UTF8.GetBytes(BattleEnvelope("rate-first")));
        using var limited = await SendAsync(
            pipeName,
            Header(credential),
            Encoding.UTF8.GetBytes(BattleEnvelope("rate-second")));

        Assert.AreEqual("accepted", accepted.RootElement.GetProperty("status").GetString());
        Assert.AreEqual("rate-limited", limited.RootElement.GetProperty("failure").GetString());
        Assert.AreEqual(1, sink.Envelopes.Count);
    }

    [TestMethod]
    public async Task StopCancelsAnIncompleteHandshakeAndReleasesThePipe()
    {
        RequireWindows();
        var pipeName = PipeName();
        var credential = Credential();
        await using var host = new BattleNamedPipeIngestHost(
            EligibleActivation(),
            pipeName,
            credential,
            new RecordingSink(),
            new ExactProcessBattleNamedPipeClientAuthorizer([CurrentProcessReceipt()]),
            EvidenceSha256,
            BattleIngestLimits.Default with
            {
                RequestTimeout = TimeSpan.FromSeconds(2),
                ShutdownDrainTimeout = TimeSpan.FromMilliseconds(50),
            });
        await host.StartAsync();
        using var client = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await client.ConnectAsync(2000);

        await host.StopAsync();

        Assert.AreEqual(BattleLocalIpcState.Stopped, host.GetHealth().State);
        Assert.AreEqual(0, host.GetHealth().ActiveRequests);
        await AssertCannotConnectAsync(pipeName);
    }

    [TestMethod]
    public void ProcessAuthorizerDoesNotGrantAnotherRoleOrOperation()
    {
        var processId = unchecked((uint)Environment.ProcessId);
        var receipt = CurrentProcessReceipt();
        var authorizer = new ExactProcessBattleNamedPipeClientAuthorizer([receipt]);

        Assert.IsTrue(authorizer.IsAuthorized(new(
            processId,
            BattleLocalIpcProtocol.RuntimeRole,
            BattleLocalIpcProtocol.IngestOperation), EvidenceSha256));
        Assert.IsFalse(authorizer.IsAuthorized(new(processId, "bridge-shell", "ingest"), EvidenceSha256));
        Assert.IsFalse(authorizer.IsAuthorized(
            new(processId, BattleLocalIpcProtocol.RuntimeRole, "manage"),
            EvidenceSha256));
        Assert.IsFalse(authorizer.IsAuthorized(
            new(processId + 1, BattleLocalIpcProtocol.RuntimeRole, "ingest"),
            EvidenceSha256));
        Assert.IsFalse(authorizer.IsAuthorized(
            new(processId, BattleLocalIpcProtocol.RuntimeRole, "ingest"),
            new string('b', 64)));
        Assert.IsFalse(new ExactProcessBattleNamedPipeClientAuthorizer(
            [new(
                processId,
                receipt.ProcessStartUtc.AddTicks(1),
                receipt.ExecutablePath,
                receipt.RuntimeEvidenceSha256)])
            .IsAuthorized(
                new(processId, BattleLocalIpcProtocol.RuntimeRole, "ingest"),
                EvidenceSha256));
    }

    [TestMethod]
    public void LegacyCapabilityOnlyActivationCannotConstructThePipeHost()
    {
        var legacy = BattleIngestActivation.Resolve(
            new(
                "compatible.runtime",
                new Version(1, 0),
                "test",
                null,
                [LauncherCapabilityIds.SidecarIngestV1, LauncherCapabilityIds.BattleCaptureV1],
                []),
            new(BattleCollection: true, FleetCollection: false));

        Assert.IsTrue(legacy.ShouldListen);
        Assert.IsFalse(legacy.IsReviewedFeatureComposition);
        Assert.ThrowsException<ArgumentException>(() =>
            new BattleNamedPipeIngestHost(
                legacy,
                PipeName(),
                Credential(),
                new RecordingSink(),
                new ExactProcessBattleNamedPipeClientAuthorizer([CurrentProcessReceipt()]),
                EvidenceSha256));
    }

    private static BattleNamedPipeIngestHost Host(
        string pipeName,
        string credential,
        IBattleIngestSink sink,
        int processId) =>
        new(
            EligibleActivation(),
            pipeName,
            credential,
            sink,
            new ExactProcessBattleNamedPipeClientAuthorizer(
                processId == Environment.ProcessId
                    ? [CurrentProcessReceipt()]
                    : [new(
                        unchecked((uint)processId),
                        CurrentProcessReceipt().ProcessStartUtc,
                        CurrentProcessReceipt().ExecutablePath,
                        EvidenceSha256)]),
            EvidenceSha256,
            BattleIngestLimits.Default with
            {
                RequestTimeout = TimeSpan.FromSeconds(2),
                ShutdownDrainTimeout = TimeSpan.FromSeconds(1),
            });

    private static BattleIngestActivation EligibleActivation() =>
        BattleIngestActivation.Resolve(
            LauncherBattleFeatureComposer.Compose(
                LauncherFeatureResolver.Resolve(
                    new(
                        "compatible.runtime",
                        new Version(1, 0),
                        "test",
                        null,
                        [LauncherCapabilityIds.SidecarIngestV1, LauncherCapabilityIds.BattleCaptureV1],
                        []),
                    LauncherFeatureCatalog.All),
                new(
                    LauncherPlayerFeaturePreference.Enabled,
                    LauncherPlayerFeaturePreference.Unset)));

    private static BattleIngestActivation InactiveActivation() =>
        BattleIngestActivation.Resolve(
            LauncherBattleFeatureComposer.Compose(
                LauncherFeatureResolver.Resolve(
                    LauncherRuntimeProfile.Unknown("test", "no capabilities"),
                    LauncherFeatureCatalog.All),
                new(
                    LauncherPlayerFeaturePreference.Enabled,
                    LauncherPlayerFeaturePreference.Enabled)));

    private static string Header(string credential) =>
        JsonSerializer.Serialize(new
        {
            protocolVersion = BattleLocalIpcProtocol.Version,
            role = BattleLocalIpcProtocol.RuntimeRole,
            operation = BattleLocalIpcProtocol.IngestOperation,
            credential,
        });

    private static string BattleEnvelope(string batchId) =>
        JsonSerializer.Serialize(new
        {
            protocolVersion = BattleIngestProtocol.Version,
            kind = BattleIngestProtocol.BattleEventsKind,
            batchId,
            producedAt = "2026-08-09T12:00:00.000Z",
            sessionId = "pipe-test-session",
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
                    capture = new { sourceKind = "pipe-fixture" },
                },
            },
        });

    private static async Task<JsonDocument> SendAsync(
        string pipeName,
        string header,
        byte[] payload)
    {
        using var client = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await client.ConnectAsync(2000);
        await WriteFrameAsync(client, Encoding.UTF8.GetBytes(header));
        await client.FlushAsync();
        var handshake = JsonDocument.Parse(await ReadFrameAsync(client));
        if (handshake.RootElement.GetProperty("status").GetString() != "ready")
        {
            return handshake;
        }
        handshake.Dispose();
        await WriteFrameAsync(client, payload);
        await client.FlushAsync();
        return JsonDocument.Parse(await ReadFrameAsync(client));
    }

    private static async Task<JsonDocument> SendAnnouncedPayloadLengthAsync(
        string pipeName,
        string header,
        int payloadLength)
    {
        using var client = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await client.ConnectAsync(2000);
        await WriteFrameAsync(client, Encoding.UTF8.GetBytes(header));
        await client.FlushAsync();
        using var handshake = JsonDocument.Parse(await ReadFrameAsync(client));
        Assert.AreEqual("ready", handshake.RootElement.GetProperty("status").GetString());
        var length = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(length, payloadLength);
        await client.WriteAsync(length);
        await client.FlushAsync();
        return JsonDocument.Parse(await ReadFrameAsync(client));
    }

    private static async Task<JsonDocument> SendAnnouncedHeaderLengthAsync(
        string pipeName,
        int headerLength)
    {
        using var client = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await client.ConnectAsync(2000);
        var length = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(length, headerLength);
        await client.WriteAsync(length);
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
        var lengthBytes = new byte[sizeof(int)];
        await stream.ReadExactlyAsync(lengthBytes);
        var length = BinaryPrimitives.ReadInt32LittleEndian(lengthBytes);
        var bytes = new byte[length];
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

    private static string Credential()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static string PipeName() => $"stfc-battle-test-{Guid.NewGuid():N}";

    private static BattleNamedPipeAuthorizedProcess CurrentProcessReceipt()
    {
        using var process = Process.GetCurrentProcess();
        return new(
            unchecked((uint)process.Id),
            new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero),
            process.MainModule!.FileName,
            EvidenceSha256);
    }

    private const string EvidenceSha256 =
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private static void RequireWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("The named-pipe identity proof is Windows-only.");
        }
    }

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

    private sealed class ThrowingSink : IBattleIngestSink
    {
        public ValueTask<BattleIngestCommitResult> CommitAsync(
            BattleIngestEnvelope envelope,
            CancellationToken cancellationToken) =>
            ValueTask.FromException<BattleIngestCommitResult>(new IOException("fixture storage failure"));
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch;
    }
}
