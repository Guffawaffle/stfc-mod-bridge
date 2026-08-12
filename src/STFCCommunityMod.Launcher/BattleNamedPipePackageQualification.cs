using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using STFCCommunityMod.Launcher.Core;

namespace STFCCommunityMod.Launcher;

internal static class BattleNamedPipePackageQualification
{
    internal const string Argument = "--battle-ipc-package-qualification";
    internal const string StandaloneMode = "standalone";
    internal const string MsixMode = "msix";
    internal const string PackageIdentityName = "Guffawaffle.STFCModBridge";
    internal const string StateEvidenceSchema = "stfc.mod-bridge.package-state-qualification.v1";
    // SHA-256("stfc-mod-bridge:battle-ipc-package-qualification:v1").
    private const string EvidenceSha256 =
        "8d1acdade8d042812a6a2c6fe46480230a4e376d98392232e861b386870c71ba";

    public static bool TryRun(string[] arguments, out int exitCode)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Length == 0 || arguments[0] != Argument)
        {
            exitCode = 0;
            return false;
        }

        var expectPackaged = false;
        string? stateEvidenceNonce = null;
        try
        {
            if (arguments.Length < 2
                || arguments[1] is not (StandaloneMode or MsixMode)
                || (arguments[1] == StandaloneMode && arguments.Length != 2)
                || (arguments[1] == MsixMode
                    && (arguments.Length != 3 || !Guid.TryParseExact(arguments[2], "N", out _))))
            {
                throw new ArgumentException("The Battle IPC package qualification arguments are invalid.");
            }
            expectPackaged = arguments[1] == MsixMode;
            stateEvidenceNonce = arguments.Length == 3 ? arguments[2] : null;
            RunAsync(expectPackaged)
                .GetAwaiter()
                .GetResult();
            if (expectPackaged)
            {
                try
                {
                    WriteExternalStateEvidence(stateEvidenceNonce, "passed", null);
                }
                catch (Exception exception)
                {
                    throw new BattlePackageQualificationException("external-state", exception);
                }
            }
            exitCode = 0;
        }
        catch (BattlePackageQualificationException exception)
        {
            if (expectPackaged)
            {
                TryWriteFailureEvidence(stateEvidenceNonce, exception.Stage);
            }
            Console.Error.WriteLine($"Battle IPC package qualification failed at {exception.Stage}.");
            exitCode = 1;
        }
        catch (ArgumentException)
        {
            Console.Error.WriteLine("Battle IPC package qualification failed at arguments.");
            exitCode = 1;
        }
        return true;
    }

    internal static async Task RunAsync(bool expectPackaged)
    {
        var stage = "platform";
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                throw new PlatformNotSupportedException("Battle IPC package qualification requires Windows.");
            }
            stage = "package-identity";
            ValidatePackageIdentity(expectPackaged);

            stage = "setup";
            var pipeName = $"stfc-mod-bridge-package-proof-{Guid.NewGuid():N}";
            var credential = Credential();
            var wrongCredential = DifferentCredential(credential);
            var exactEnvelope = Encoding.UTF8.GetBytes(BattleEnvelope());
            var sink = new ExactQualificationSink(exactEnvelope);
            using var process = Process.GetCurrentProcess();
            var processReceipt = new BattleNamedPipeAuthorizedProcess(
                unchecked((uint)process.Id),
                new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero),
                process.MainModule?.FileName
                    ?? throw new InvalidOperationException("The qualification executable path is unavailable."),
                EvidenceSha256);
            await using var host = new BattleNamedPipeIngestHost(
                EligibleActivation(),
                pipeName,
                credential,
                sink,
                new ExactProcessBattleNamedPipeClientAuthorizer([processReceipt]),
                EvidenceSha256,
                BattleIngestLimits.Default with
                {
                    RequestTimeout = TimeSpan.FromSeconds(5),
                    ShutdownDrainTimeout = TimeSpan.FromSeconds(2),
                });
            await using var collision = new BattleNamedPipeIngestHost(
                EligibleActivation(),
                pipeName,
                credential,
                sink,
                new ExactProcessBattleNamedPipeClientAuthorizer([processReceipt]),
                EvidenceSha256,
                BattleIngestLimits.Default with
                {
                    RequestTimeout = TimeSpan.FromSeconds(5),
                    ShutdownDrainTimeout = TimeSpan.FromSeconds(2),
                });

            stage = "host-start";
            var started = await host.StartAsync().ConfigureAwait(false);
            if (started.State != BattleLocalIpcState.Listening)
            {
                throw new InvalidOperationException("The signed package could not bind its Battle named pipe.");
            }
            stage = "collision";
            var collisionResult = await collision.StartAsync().ConfigureAwait(false);
            if (collisionResult.State != BattleLocalIpcState.Failed)
            {
                throw new InvalidOperationException("The signed package did not reject a Battle pipe collision.");
            }

            stage = "unauthorized-request";
            using (var rejected = await SendAsync(pipeName, wrongCredential, exactEnvelope).ConfigureAwait(false))
            {
                if (rejected.RootElement.GetProperty("failure").GetString() != "unauthorized")
                {
                    throw new InvalidOperationException("The signed package did not reject an invalid IPC credential.");
                }
            }
            if (sink.AcceptedRecords != 0)
            {
                throw new InvalidOperationException("The signed package delivered an unauthorized IPC payload.");
            }

            stage = "authorized-request";
            using (var accepted = await SendAsync(pipeName, credential, exactEnvelope).ConfigureAwait(false))
            {
                if (accepted.RootElement.GetProperty("status").GetString() != "accepted"
                    || accepted.RootElement.GetProperty("acceptedRecords").GetInt32() != 1)
                {
                    throw new InvalidOperationException("The signed package rejected its authorized IPC payload.");
                }
            }
            if (sink.AcceptedRecords != 1 || !sink.ExactBytesMatched)
            {
                throw new InvalidOperationException("The signed package changed the exact accepted IPC payload.");
            }

            stage = "shutdown";
            await host.StopAsync().ConfigureAwait(false);
            if (host.GetHealth() is not
                {
                    State: BattleLocalIpcState.Stopped,
                    AcceptedRequests: 1,
                    RejectedRequests: 1,
                    ActiveRequests: 0,
                })
            {
                throw new InvalidOperationException("The signed package did not drain the Battle IPC host cleanly.");
            }
            stage = "post-stop";
            await AssertCannotConnectAsync(pipeName).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not BattlePackageQualificationException)
        {
            throw new BattlePackageQualificationException(stage, exception);
        }
    }

    private static void ValidatePackageIdentity(bool expectPackaged)
    {
        var packageFullName = WindowsPackageIdentity.CurrentPackageFullName;
        if (!expectPackaged)
        {
            if (packageFullName is not null)
            {
                throw new InvalidOperationException("The standalone qualification unexpectedly has package identity.");
            }
            return;
        }

        if (packageFullName is null
            || !packageFullName.StartsWith(PackageIdentityName + "_", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The MSIX qualification lacks the reviewed package identity.");
        }
    }

    private static void TryWriteFailureEvidence(string? nonce, string stage)
    {
        try
        {
            WriteExternalStateEvidence(nonce, "failed", stage);
        }
        catch
        {
            // The unpackaged qualification host treats missing or malformed evidence as failure.
        }
    }

    private static void WriteExternalStateEvidence(string? nonce, string status, string? stage)
    {
        if (nonce is null || !Guid.TryParseExact(nonce, "N", out _))
        {
            throw new InvalidOperationException("The packaged qualification state nonce is invalid.");
        }
        var layout = PerUserInstallLayout.FromLocalApplicationData(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        Directory.CreateDirectory(layout.StateDirectory);
        var evidencePath = Path.Combine(
            layout.StateDirectory,
            $"package-qualification-{nonce}.json");
        using var evidence = new FileStream(
            evidencePath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None);
        JsonSerializer.Serialize(evidence, new
        {
            schema = StateEvidenceSchema,
            nonce,
            status,
            stage,
        });
    }

    private static BattleIngestActivation EligibleActivation() =>
        BattleIngestActivation.Resolve(
            LauncherBattleFeatureComposer.Compose(
                LauncherFeatureResolver.Resolve(
                    new LauncherRuntimeProfile(
                        "package-qualified.runtime",
                        new Version(1, 0),
                        "package-qualification",
                        null,
                        [LauncherCapabilityIds.SidecarIngestV1, LauncherCapabilityIds.BattleCaptureV1],
                        []),
                    LauncherFeatureCatalog.All),
                new LauncherBattlePreferences(
                    LauncherPlayerFeaturePreference.Enabled,
                    LauncherPlayerFeaturePreference.Unset)));

    private static async Task<JsonDocument> SendAsync(
        string pipeName,
        string credential,
        byte[] envelope)
    {
        using var client = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await client.ConnectAsync(5000).ConfigureAwait(false);
        var header = JsonSerializer.SerializeToUtf8Bytes(new
        {
            protocolVersion = BattleLocalIpcProtocol.Version,
            role = BattleLocalIpcProtocol.RuntimeRole,
            operation = BattleLocalIpcProtocol.IngestOperation,
            credential,
        });
        await WriteFrameAsync(client, header).ConfigureAwait(false);
        await client.FlushAsync().ConfigureAwait(false);
        var handshake = JsonDocument.Parse(await ReadFrameAsync(client).ConfigureAwait(false));
        if (handshake.RootElement.GetProperty("status").GetString() != "ready")
        {
            return handshake;
        }
        handshake.Dispose();
        await WriteFrameAsync(client, envelope).ConfigureAwait(false);
        await client.FlushAsync().ConfigureAwait(false);
        return JsonDocument.Parse(await ReadFrameAsync(client).ConfigureAwait(false));
    }

    private static async Task WriteFrameAsync(Stream stream, byte[] bytes)
    {
        var length = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
        await stream.WriteAsync(length).ConfigureAwait(false);
        await stream.WriteAsync(bytes).ConfigureAwait(false);
    }

    private static async Task<byte[]> ReadFrameAsync(Stream stream)
    {
        var lengthBytes = new byte[sizeof(int)];
        await stream.ReadExactlyAsync(lengthBytes).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32LittleEndian(lengthBytes);
        if (length is <= 0 or > BattleLocalIpcProtocol.MaximumHeaderBytes)
        {
            throw new InvalidDataException("The Battle IPC qualification response length is invalid.");
        }
        var bytes = new byte[length];
        await stream.ReadExactlyAsync(bytes).ConfigureAwait(false);
        return bytes;
    }

    private static async Task AssertCannotConnectAsync(string pipeName)
    {
        using var client = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await client.ConnectAsync(100).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return;
        }
        catch (IOException)
        {
            return;
        }
        throw new InvalidOperationException("The Battle IPC qualification pipe remained reachable after shutdown.");
    }

    private static string Credential()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static string DifferentCredential(string credential) =>
        $"{(credential[0] == 'A' ? 'B' : 'A')}{credential[1..]}";

    private static string BattleEnvelope() =>
        JsonSerializer.Serialize(new
        {
            protocolVersion = BattleIngestProtocol.Version,
            kind = BattleIngestProtocol.BattleEventsKind,
            batchId = $"package-proof-{Guid.NewGuid():N}",
            producedAt = "2026-08-10T00:00:00.000Z",
            sessionId = "signed-package-qualification",
            source = "stfc-mod-bridge-package-proof",
            modVersion = "1.0.0",
            payloadProtocol = BattleIngestProtocol.SidecarEventsVersion,
            payload = new[]
            {
                new
                {
                    protocolVersion = BattleIngestProtocol.SidecarEventsVersion,
                    type = "battle.capture",
                    schemaVersion = "stfc.battle.capture.v1",
                    timestamp = "2026-08-10T00:00:00.000Z",
                    journalId = "package-proof-journal",
                    capture = new { sourceKind = "signed-package-proof" },
                },
            },
        });

    private sealed class ExactQualificationSink(byte[] expected) : IBattleIngestSink
    {
        private readonly byte[] expectedSha256 = SHA256.HashData(expected);

        public int AcceptedRecords { get; private set; }

        public bool ExactBytesMatched { get; private set; }

        public ValueTask<BattleIngestCommitResult> CommitAsync(
            BattleIngestEnvelope envelope,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AcceptedRecords += envelope.ExactEventBytes.Count;
            ExactBytesMatched = CryptographicOperations.FixedTimeEquals(
                SHA256.HashData(envelope.ExactEnvelopeBytes.Span),
                expectedSha256);
            return ValueTask.FromResult(new BattleIngestCommitResult(envelope.ExactEventBytes.Count));
        }
    }

    private sealed class BattlePackageQualificationException(string stage, Exception innerException)
        : Exception("Battle IPC package qualification failed.", innerException)
    {
        public string Stage { get; } = stage;
    }
}
