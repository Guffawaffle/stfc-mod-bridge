using System.Security.Cryptography;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;
using STFCCommunityMod.Launcher.Core;

namespace STFCCommunityMod.Launcher.Core.Tests;

[TestClass]
public sealed class BattleIngestCredentialStoreTests
{
    private static readonly DateTimeOffset Created =
        new(2026, 8, 9, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void ConstructorAndAbsentLoadArePassive()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var stateRoot = Path.Combine(temporaryDirectory.Path, "missing-state");
        var protector = new RecordingProtector();

        var store = new BattleIngestCredentialStore(stateRoot, protector);
        var result = store.Load();

        Assert.AreEqual(BattleCredentialLoadState.Absent, result.State);
        Assert.AreEqual("credential-absent", result.Code);
        Assert.IsNull(result.Lease);
        Assert.IsFalse(Directory.Exists(stateRoot));
        Assert.AreEqual(0, protector.ProtectCalls + protector.UnprotectCalls);
    }

    [TestMethod]
    public void CandidateIsCanonicalBoundedAndOwnsExactCredential()
    {
        var protector = new RecordingProtector();
        using var candidate = CreateCandidate(protector);

        Assert.AreEqual(1, protector.ProtectCalls);
        Assert.AreEqual(32, candidate.Lease.Credential.Length);
        Assert.AreEqual(43, ExtractString(candidate.ProtectedBytes.Span, "credential").Length);
        Assert.AreEqual(32, candidate.Lease.Metadata.CredentialId.Length);
        StringAssert.Matches(candidate.Lease.Metadata.CredentialId, new("^[0-9a-f]{32}$"));
        Assert.AreEqual(BattleLocalIpcProtocol.Version, candidate.Lease.Metadata.ProtocolVersion);
        Assert.AreEqual(candidate.ProtectedBytes.Length, candidate.Lease.Metadata.ProtectedByteCount);
        Assert.AreEqual(
            Convert.ToHexString(SHA256.HashData(candidate.ProtectedBytes.Span)).ToLowerInvariant(),
            candidate.Lease.Metadata.ProtectedSha256);
        Assert.IsTrue(protector.LastProtectInput!.All(value => value == 0));
    }

    [TestMethod]
    public void RotationAdvancesExactlyOneGeneration()
    {
        using var candidate = BattleIngestCredentialCodec.CreateCandidate(
            "stfc-mod-bridge.battle.v1",
            7,
            Created,
            Created.AddDays(1),
            BattleCredentialRotationReason.Manual,
            new RecordingProtector());

        Assert.AreEqual(8L, candidate.Lease.Metadata.Generation);
        Assert.AreEqual(BattleCredentialRotationReason.Manual, candidate.Lease.Metadata.RotationReason);
    }

    [TestMethod]
    public void CandidateAndReadableLeaseZeroOwnedSecretsOnDispose()
    {
        var protector = new RecordingProtector();
        var candidate = CreateCandidate(protector);
        var candidateLease = candidate.Lease;
        candidate.Dispose();
        Assert.IsTrue(candidateLease.IsZeroedForTest());
        Assert.ThrowsException<ObjectDisposedException>(() => _ = candidate.ProtectedBytes);

        using var second = CreateCandidate(protector);
        var protectedBytes = second.ProtectedBytes.ToArray();
        var lease = BattleIngestCredentialCodec.Decode(protectedBytes, protector);
        lease.Dispose();
        Assert.IsTrue(lease.IsZeroedForTest());
        Assert.ThrowsException<ObjectDisposedException>(() => _ = lease.Credential.Length);
    }

    [TestMethod]
    public void PassiveStoreReadsExactCanonicalRecord()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var protector = new RecordingProtector();
        using var candidate = CreateCandidate(protector);
        var store = CreateStoreWithBytes(temporaryDirectory.Path, protector, candidate.ProtectedBytes.Span);

        var result = store.Load();

        Assert.AreEqual(BattleCredentialLoadState.Readable, result.State, result.Code);
        using var lease = result.Lease!;
        CollectionAssert.AreEqual(candidate.Lease.Credential.ToArray(), lease.Credential.ToArray());
        Assert.AreEqual("stfc-mod-bridge.battle.v1", lease.Metadata.PipeName);
        Assert.AreEqual(1L, lease.Metadata.Generation);
        Assert.IsTrue(protector.LastUnprotectOutput!.All(value => value == 0));
    }

    [TestMethod]
    [SupportedOSPlatform("windows")]
    public void WindowsDpapiRoundTripsOnlyForCurrentUserAndExactEntropy()
    {
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("DPAPI is Windows-only.");
        using var temporaryDirectory = new TemporaryDirectory();
        var protector = new WindowsDpapiBattleCredentialProtector();
        using var candidate = CreateCandidate(protector);
        var store = CreateStoreWithBytes(temporaryDirectory.Path, protector, candidate.ProtectedBytes.Span);

        var result = store.Load();

        Assert.AreEqual(BattleCredentialLoadState.Readable, result.State, result.Code);
        using var lease = result.Lease!;
        CollectionAssert.AreEqual(candidate.Lease.Credential.ToArray(), lease.Credential.ToArray());
    }

    [TestMethod]
    [SupportedOSPlatform("windows")]
    public void WindowsDpapiRejectsDifferentEntropy()
    {
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("DPAPI is Windows-only.");
        using var temporaryDirectory = new TemporaryDirectory();
        var wrongEntropy = Encoding.UTF8.GetBytes("not the reviewed Battle credential entropy");
        var protectedBytes = ProtectedData.Protect(
            Encoding.UTF8.GetBytes("not a credential record"),
            wrongEntropy,
            DataProtectionScope.CurrentUser);
        var store = CreateStoreWithBytes(
            temporaryDirectory.Path,
            new WindowsDpapiBattleCredentialProtector(),
            protectedBytes);

        var result = store.Load();

        Assert.AreEqual(BattleCredentialLoadState.Invalid, result.State);
        Assert.AreEqual("credential-invalid", result.Code);
    }

    [TestMethod]
    [SupportedOSPlatform("windows")]
    public void ReaderRefusesFileReparsePointWithoutTouchingTarget()
    {
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("Reparse-point proof is Windows-only.");
        using var temporaryDirectory = new TemporaryDirectory();
        var protector = new RecordingProtector();
        using var candidate = CreateCandidate(protector);
        var store = new BattleIngestCredentialStore(Path.Combine(temporaryDirectory.Path, "state"), protector);
        Directory.CreateDirectory(Path.GetDirectoryName(store.Path)!);
        var foreign = Path.Combine(temporaryDirectory.Path, "foreign.dpapi");
        File.WriteAllBytes(foreign, candidate.ProtectedBytes.ToArray());
        try
        {
            File.CreateSymbolicLink(store.Path, foreign);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            Assert.Inconclusive("Creating a file symlink is unavailable on this Windows host.");
        }

        var result = store.Load();

        Assert.AreEqual(BattleCredentialLoadState.Invalid, result.State);
        CollectionAssert.AreEqual(candidate.ProtectedBytes.ToArray(), File.ReadAllBytes(foreign));
    }

    [TestMethod]
    public void OversizeProtectedRecordRejectsBeforeUnprotect()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var protector = new RecordingProtector();
        var bytes = new byte[BattleIngestCredentialCodec.MaximumProtectedBytes + 1];
        var store = CreateStoreWithBytes(temporaryDirectory.Path, protector, bytes);

        var result = store.Load();

        Assert.AreEqual(BattleCredentialLoadState.TooLarge, result.State);
        Assert.AreEqual(0, protector.UnprotectCalls);
    }

    [TestMethod]
    public void TamperOrUnprotectFailureIsTypedAndBounded()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var protector = new RecordingProtector { UnprotectFailure = new CryptographicException("private detail") };
        var store = CreateStoreWithBytes(temporaryDirectory.Path, protector, [1, 2, 3]);

        var result = store.Load();

        Assert.AreEqual(BattleCredentialLoadState.Invalid, result.State);
        Assert.AreEqual("credential-invalid", result.Code);
        Assert.IsFalse(result.Code.Contains("private", StringComparison.Ordinal));
    }

    [DataTestMethod]
    [DataRow("duplicate")]
    [DataRow("unknown")]
    [DataRow("missing")]
    [DataRow("wrong-type")]
    [DataRow("case-drift")]
    [DataRow("unsafe-pipe")]
    [DataRow("bad-protocol")]
    [DataRow("bad-id")]
    [DataRow("zero-generation")]
    [DataRow("bad-timestamp")]
    [DataRow("bad-reason")]
    [DataRow("noncanonical")]
    public void HostileClosedSchemaRecordsReject(string mutation)
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var protector = new RecordingProtector();
        using var candidate = CreateCandidate(protector);
        var json = Encoding.UTF8.GetString(candidate.ProtectedBytes.Span);
        json = mutation switch
        {
            "duplicate" => json.Replace(
                "\"schema\":", "\"schema\":\"stfc.battle-ingest-credential.v1\",\"\\u0073chema\":",
                StringComparison.Ordinal),
            "unknown" => json[..^1] + ",\"extra\":\"x\"}",
            "missing" => json.Replace("\"pipeName\":\"stfc-mod-bridge.battle.v1\",", "", StringComparison.Ordinal),
            "wrong-type" => json.Replace("\"generation\":1", "\"generation\":\"1\"", StringComparison.Ordinal),
            "case-drift" => json.Replace(BattleIngestCredentialCodec.Schema,
                "STFC.battle-ingest-credential.v1", StringComparison.Ordinal),
            "unsafe-pipe" => json.Replace("stfc-mod-bridge.battle.v1", "../battle", StringComparison.Ordinal),
            "bad-protocol" => json.Replace(BattleLocalIpcProtocol.Version, "stfc.battle-bridge.local-ipc.v2",
                StringComparison.Ordinal),
            "bad-id" => json.Replace(ExtractString(Encoding.UTF8.GetBytes(json), "credentialId"),
                "ABCDEF0123456789ABCDEF0123456789", StringComparison.Ordinal),
            "zero-generation" => json.Replace("\"generation\":1", "\"generation\":0", StringComparison.Ordinal),
            "bad-timestamp" => Regex.Replace(
                json,
                "\\\"createdAtUtc\\\":\\\"[^\\\"]+\\\"",
                "\"createdAtUtc\":\"2026-08-09T13:00:00.0000000+01:00\"",
                RegexOptions.CultureInvariant),
            "bad-reason" => json.Replace("\"rotationReason\":\"initial\"",
                "\"rotationReason\":\"automatic\"", StringComparison.Ordinal),
            "noncanonical" => " " + json,
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        };
        var store = CreateStoreWithBytes(temporaryDirectory.Path, protector, Encoding.UTF8.GetBytes(json));

        var result = store.Load();

        Assert.AreEqual(BattleCredentialLoadState.Invalid, result.State, mutation);
        Assert.IsNull(result.Lease);
    }

    [TestMethod]
    public void CandidateMetadataRulesAndPipeValidationFailClosed()
    {
        var protector = new RecordingProtector();
        Assert.ThrowsException<ArgumentException>(() => BattleIngestCredentialCodec.CreateCandidate(
            "bad/pipe", 1, Created, Created, BattleCredentialRotationReason.Initial, protector));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => BattleIngestCredentialCodec.CreateCandidate(
            "valid.pipe", -1, Created, Created, BattleCredentialRotationReason.Initial, protector));
        Assert.ThrowsException<ArgumentException>(() => BattleIngestCredentialCodec.CreateCandidate(
            "valid.pipe", 1, Created, Created, BattleCredentialRotationReason.Initial, protector));
        Assert.ThrowsException<ArgumentException>(() => BattleIngestCredentialCodec.CreateCandidate(
            "valid.pipe", 0, Created, Created, BattleCredentialRotationReason.Manual, protector));
        Assert.AreEqual(0, protector.ProtectCalls);
    }

    private static BattleCredentialCandidate CreateCandidate(IBattleCredentialProtector protector) =>
        BattleIngestCredentialCodec.CreateCandidate(
            "stfc-mod-bridge.battle.v1", 0, Created, Created,
            BattleCredentialRotationReason.Initial, protector);

    private static BattleIngestCredentialStore CreateStoreWithBytes(
        string root,
        IBattleCredentialProtector protector,
        ReadOnlySpan<byte> bytes)
    {
        var store = new BattleIngestCredentialStore(Path.Combine(root, "state"), protector);
        Directory.CreateDirectory(Path.GetDirectoryName(store.Path)!);
        File.WriteAllBytes(store.Path, bytes.ToArray());
        return store;
    }

    private static string ExtractString(ReadOnlySpan<byte> json, string property)
    {
        using var document = System.Text.Json.JsonDocument.Parse(json.ToArray());
        return document.RootElement.GetProperty(property).GetString()!;
    }

    private sealed class RecordingProtector : IBattleCredentialProtector
    {
        public int ProtectCalls { get; private set; }
        public int UnprotectCalls { get; private set; }
        public Exception? UnprotectFailure { get; init; }
        public byte[]? LastProtectInput { get; private set; }
        public byte[]? LastUnprotectOutput { get; private set; }

        public byte[] Protect(byte[] plaintext)
        {
            ProtectCalls++;
            LastProtectInput = plaintext;
            return plaintext.ToArray();
        }

        public byte[] Unprotect(byte[] protectedBytes)
        {
            UnprotectCalls++;
            if (UnprotectFailure is not null) throw UnprotectFailure;
            LastUnprotectOutput = protectedBytes.ToArray();
            return LastUnprotectOutput;
        }
    }
}
