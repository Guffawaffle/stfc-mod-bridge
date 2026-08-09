using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Win32.SafeHandles;

namespace STFCCommunityMod.Launcher.Core.Tests;

[TestClass]
public sealed class ReviewedModArtifactCandidateRecoveryTests
{
    private static readonly byte[] DllBytes = Encoding.UTF8.GetBytes("reviewed candidate recovery DLL");
    private static readonly byte[] RuntimeBytes = Encoding.UTF8.GetBytes("reviewed runtime manifest");

    [TestMethod]
    public async Task ChangedCompletedMemberFailsClosedAndRetainsRecoveryAuthority()
    {
        RequireWindows();
        using var temporaryDirectory = new TemporaryDirectory();
        var stateDirectory = temporaryDirectory.CreateDirectory("state");
        var fixture = CreateResidue(stateDirectory, CrashStage.ConfirmationWait);
        var dllPath = Path.Combine(fixture.CandidateDirectory, "version.dll");
        var changed = Enumerable.Repeat((byte)'x', DllBytes.Length).ToArray();
        File.WriteAllBytes(dllPath, changed);

        var result = await fixture.Recovery.RecoverUnderLifetimeAsync(CancellationToken.None);

        Assert.AreEqual(ReviewedCandidateRecoveryState.Blocked, result.State);
        CollectionAssert.AreEqual(changed, File.ReadAllBytes(dllPath));
        Assert.IsTrue(File.Exists(Path.Combine(fixture.CandidateDirectory, CandidateOwnershipStore.FileName)));
        Assert.IsFalse(File.Exists(Path.Combine(fixture.CandidateDirectory, "ownership.dpapi.validation-only")));
    }

    [TestMethod]
    public async Task NonemptyPreparedMemberFailsClosedAndRetainsRecoveryAuthority()
    {
        RequireWindows();
        using var temporaryDirectory = new TemporaryDirectory();
        var stateDirectory = temporaryDirectory.CreateDirectory("state");
        var fixture = CreateResidue(stateDirectory, CrashStage.CreatedDllBeforeWritingReceipt);
        var dllPath = Path.Combine(fixture.CandidateDirectory, "version.dll");
        File.WriteAllText(dllPath, "not an empty pre-write stage");

        var result = await fixture.Recovery.RecoverUnderLifetimeAsync(CancellationToken.None);

        Assert.AreEqual(ReviewedCandidateRecoveryState.Blocked, result.State);
        Assert.AreEqual("not an empty pre-write stage", File.ReadAllText(dllPath));
        Assert.IsTrue(File.Exists(Path.Combine(fixture.CandidateDirectory, CandidateOwnershipStore.FileName)));
    }

    [TestMethod]
    public async Task ShareDeniedMemberRetainsMetadataUntilExactRetrySucceeds()
    {
        RequireWindows();
        using var temporaryDirectory = new TemporaryDirectory();
        var stateDirectory = temporaryDirectory.CreateDirectory("state");
        var fixture = CreateResidue(stateDirectory, CrashStage.VerifiedDll);
        var dllPath = Path.Combine(fixture.CandidateDirectory, "version.dll");
        using (var blocker = new FileStream(dllPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var blocked = await fixture.Recovery.RecoverUnderLifetimeAsync(CancellationToken.None);
            Assert.AreEqual(ReviewedCandidateRecoveryState.Blocked, blocked.State);
            Assert.IsTrue(File.Exists(dllPath));
            Assert.IsTrue(File.Exists(Path.Combine(fixture.CandidateDirectory, CandidateOwnershipStore.FileName)));
        }

        var recovered = await fixture.Recovery.RecoverUnderLifetimeAsync(CancellationToken.None);
        Assert.AreEqual(ReviewedCandidateRecoveryState.Recovered, recovered.State, recovered.Message);
        Assert.IsFalse(Directory.Exists(fixture.CandidateDirectory));
    }

    [TestMethod]
    public async Task TamperedMetadataUnknownDirectoryAndForeignSiblingRemainUntouched()
    {
        RequireWindows();
        using var temporaryDirectory = new TemporaryDirectory();
        var stateDirectory = temporaryDirectory.CreateDirectory("state");
        var fixture = CreateResidue(stateDirectory, CrashStage.VerifiedPair);
        var metadataPath = Path.Combine(fixture.CandidateDirectory, CandidateOwnershipStore.FileName);
        var metadata = File.ReadAllBytes(metadataPath);
        metadata[metadata.Length / 2] ^= 0x5a;
        File.WriteAllBytes(metadataPath, metadata);
        var foreignPath = Path.Combine(fixture.CandidateDirectory, "notes.txt");
        File.WriteAllText(foreignPath, "foreign");

        var result = await fixture.Recovery.RecoverUnderLifetimeAsync(CancellationToken.None);

        Assert.AreEqual(ReviewedCandidateRecoveryState.Blocked, result.State);
        CollectionAssert.AreEqual(metadata, File.ReadAllBytes(metadataPath));
        Assert.AreEqual("foreign", File.ReadAllText(foreignPath));
        Assert.IsTrue(File.Exists(Path.Combine(fixture.CandidateDirectory, "version.dll")));

        var unknown = Directory.CreateDirectory(Path.Combine(
            Path.Combine(stateDirectory, "artifact-candidates"),
            Guid.NewGuid().ToString("N"))).FullName;
        var second = await fixture.Recovery.RecoverUnderLifetimeAsync(CancellationToken.None);
        Assert.AreEqual(ReviewedCandidateRecoveryState.Blocked, second.State);
        Assert.IsTrue(Directory.Exists(unknown));
    }

    [DataTestMethod]
    [DataRow("null-dll")]
    [DataRow("missing-digest")]
    [DataRow("null-digest")]
    [DataRow("null-file-identity-string")]
    public async Task AuthenticatedMalformedOwnershipSchemaReturnsBlockedWithoutMutation(string mutation)
    {
        RequireWindows();
        using var temporaryDirectory = new TemporaryDirectory();
        var stateDirectory = temporaryDirectory.CreateDirectory("state");
        var fixture = CreateResidue(stateDirectory, CrashStage.ConfirmationWait);
        var metadataPath = Path.Combine(fixture.CandidateDirectory, CandidateOwnershipStore.FileName);
        var protector = new IntegrityTestProtector();
        var node = JsonNode.Parse(protector.Unprotect(File.ReadAllBytes(metadataPath)))!.AsObject();
        var dll = node["dll"]!.AsObject();
        switch (mutation)
        {
            case "null-dll":
                node["dll"] = null;
                break;
            case "missing-digest":
                dll.Remove("expectedSha256");
                break;
            case "null-digest":
                dll["expectedSha256"] = null;
                break;
            case "null-file-identity-string":
                dll["fileIdentity"]!.AsObject()["volumeSerialNumber"] = null;
                break;
            default:
                Assert.Fail($"Unknown metadata mutation: {mutation}");
                break;
        }
        var authenticatedMalformed = protector.Protect(Encoding.UTF8.GetBytes(node.ToJsonString()));
        File.WriteAllBytes(metadataPath, authenticatedMalformed);
        var dllPath = Path.Combine(fixture.CandidateDirectory, "version.dll");
        var beforeDll = File.ReadAllBytes(dllPath);

        var result = await fixture.Recovery.RecoverUnderLifetimeAsync(CancellationToken.None);

        Assert.AreEqual(ReviewedCandidateRecoveryState.Blocked, result.State);
        CollectionAssert.AreEqual(authenticatedMalformed, File.ReadAllBytes(metadataPath));
        CollectionAssert.AreEqual(beforeDll, File.ReadAllBytes(dllPath));
    }

    [TestMethod]
    public async Task ForeignSiblingSurvivesWhileExactOwnedMembersAreRemoved()
    {
        RequireWindows();
        using var temporaryDirectory = new TemporaryDirectory();
        var stateDirectory = temporaryDirectory.CreateDirectory("state");
        var fixture = CreateResidue(stateDirectory, CrashStage.ConfirmationWait);
        var foreignPath = Path.Combine(fixture.CandidateDirectory, "notes.txt");
        File.WriteAllText(foreignPath, "foreign");

        var result = await fixture.Recovery.RecoverUnderLifetimeAsync(CancellationToken.None);

        Assert.AreEqual(ReviewedCandidateRecoveryState.Blocked, result.State);
        Assert.IsTrue(File.Exists(foreignPath));
        Assert.IsFalse(File.Exists(Path.Combine(fixture.CandidateDirectory, "version.dll")));
        Assert.IsFalse(File.Exists(Path.Combine(fixture.CandidateDirectory, CandidateOwnershipStore.FileName)));
    }

    [TestMethod]
    public async Task ValidAtomicNextMetadataIsValidatedWithoutCreatingRecoveryFiles()
    {
        RequireWindows();
        using var temporaryDirectory = new TemporaryDirectory();
        var stateDirectory = temporaryDirectory.CreateDirectory("state");
        var fixture = CreateResidue(stateDirectory, CrashStage.VerifiedDll);
        File.Copy(
            Path.Combine(fixture.CandidateDirectory, CandidateOwnershipStore.FileName),
            Path.Combine(fixture.CandidateDirectory, CandidateOwnershipStore.NextFileName));

        var result = await fixture.Recovery.RecoverUnderLifetimeAsync(CancellationToken.None);

        Assert.AreEqual(ReviewedCandidateRecoveryState.Recovered, result.State, result.Message);
        Assert.IsFalse(File.Exists(Path.Combine(fixture.CandidateDirectory, CandidateOwnershipStore.FileName)));
        Assert.IsFalse(File.Exists(Path.Combine(fixture.CandidateDirectory, CandidateOwnershipStore.NextFileName)));
        Assert.IsFalse(Directory.Exists(fixture.CandidateDirectory));
        Assert.IsFalse(Directory.EnumerateFileSystemEntries(
            Path.Combine(stateDirectory, "artifact-candidates"),
            "*.validation-only",
            SearchOption.AllDirectories).Any());
    }

    [TestMethod]
    public async Task RepeatedCrashRecoveryCyclesStayWithinCheckedInCountAndByteBounds()
    {
        RequireWindows();
        using var temporaryDirectory = new TemporaryDirectory();
        var stateDirectory = temporaryDirectory.CreateDirectory("state");
        for (var cycle = 0; cycle < 8; cycle++)
        {
            var fixture = CreateResidue(stateDirectory, CrashStage.PartialDll);
            Assert.IsTrue(DirectorySize(Path.Combine(stateDirectory, "artifact-candidates"))
                <= CandidateRecoveryService.MaximumAggregateBytes);
            var result = await fixture.Recovery.RecoverUnderLifetimeAsync(CancellationToken.None);
            Assert.AreEqual(ReviewedCandidateRecoveryState.Recovered, result.State, result.Message);
            Assert.AreEqual(0, Directory.GetDirectories(Path.Combine(stateDirectory, "artifact-candidates")).Length);
        }

        var root = Path.Combine(stateDirectory, "artifact-candidates");
        for (var index = 0; index <= CandidateRecoveryService.MaximumCandidateDirectories; index++)
        {
            Directory.CreateDirectory(Path.Combine(root, Guid.NewGuid().ToString("N")));
        }
        var bounded = new CandidateRecoveryService(
            root,
            new CandidateOwnershipStore(new IntegrityTestProtector()),
            CandidateFileNative.TryMarkDeleteOnClose);
        var blocked = await bounded.RecoverUnderLifetimeAsync(CancellationToken.None);
        Assert.AreEqual(ReviewedCandidateRecoveryState.Blocked, blocked.State);
        Assert.AreEqual(
            CandidateRecoveryService.MaximumCandidateDirectories + 1,
            Directory.GetDirectories(root).Length);
    }

    [TestMethod]
    public async Task AggregateLogicalByteBoundBlocksBeforeAnyOwnedDeletion()
    {
        RequireWindows();
        using var temporaryDirectory = new TemporaryDirectory();
        var stateDirectory = temporaryDirectory.CreateDirectory("state");
        var fixture = CreateResidue(stateDirectory, CrashStage.CreatedDllBeforeWritingReceipt);
        var foreignPath = Path.Combine(fixture.CandidateDirectory, "large-foreign.bin");
        using (var foreign = new FileStream(foreignPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            foreign.SetLength(CandidateRecoveryService.MaximumAggregateBytes + 1);
        }

        var result = await fixture.Recovery.RecoverUnderLifetimeAsync(CancellationToken.None);

        Assert.AreEqual(ReviewedCandidateRecoveryState.Blocked, result.State);
        Assert.IsTrue(File.Exists(Path.Combine(fixture.CandidateDirectory, "version.dll")));
        Assert.IsTrue(File.Exists(Path.Combine(fixture.CandidateDirectory, CandidateOwnershipStore.FileName)));
        Assert.AreEqual(CandidateRecoveryService.MaximumAggregateBytes + 1, new FileInfo(foreignPath).Length);
    }

    [TestMethod]
    public async Task ReparseCandidateIsPreservedAndBlocksRecovery()
    {
        RequireWindows();
        using var temporaryDirectory = new TemporaryDirectory();
        var stateDirectory = temporaryDirectory.CreateDirectory("state");
        var root = Directory.CreateDirectory(Path.Combine(stateDirectory, "artifact-candidates")).FullName;
        var outside = temporaryDirectory.CreateDirectory("outside");
        var link = Path.Combine(root, Guid.NewGuid().ToString("N"));
        await CreateJunctionAsync(link, outside);

        var recovery = new CandidateRecoveryService(
            root,
            new CandidateOwnershipStore(new IntegrityTestProtector()),
            CandidateFileNative.TryMarkDeleteOnClose);
        var result = await recovery.RecoverUnderLifetimeAsync(CancellationToken.None);

        Assert.AreEqual(ReviewedCandidateRecoveryState.Blocked, result.State);
        Assert.IsTrue(Directory.Exists(link));
        Assert.IsTrue(Directory.Exists(outside));
        Directory.Delete(link, recursive: false);
    }

    [TestMethod]
    public async Task MemberReplacementWithJunctionCannotFollowOrDeleteForeignTarget()
    {
        RequireWindows();
        using var temporaryDirectory = new TemporaryDirectory();
        var stateDirectory = temporaryDirectory.CreateDirectory("state");
        var fixture = CreateResidue(stateDirectory, CrashStage.CreatedDllBeforeWritingReceipt);
        var outside = temporaryDirectory.CreateDirectory("foreign-target");
        var foreignPath = Path.Combine(outside, "keep.txt");
        File.WriteAllText(foreignPath, "foreign target");
        var replaced = false;
        var recovery = new CandidateRecoveryService(
            fixture.CandidateRoot,
            fixture.OwnershipStore,
            CandidateFileNative.TryMarkDeleteOnClose,
            async path =>
            {
                if (!replaced && Path.GetFileName(path) == "version.dll")
                {
                    File.Delete(path);
                    await CreateJunctionAsync(path, outside);
                    replaced = true;
                }
            });

        var result = await recovery.RecoverUnderLifetimeAsync(CancellationToken.None);

        var junctionPath = Path.Combine(fixture.CandidateDirectory, "version.dll");
        Assert.AreEqual(ReviewedCandidateRecoveryState.Blocked, result.State);
        Assert.IsTrue(replaced);
        Assert.AreEqual("foreign target", File.ReadAllText(foreignPath));
        Assert.IsTrue(Directory.Exists(junctionPath));
        Assert.IsTrue(File.Exists(Path.Combine(fixture.CandidateDirectory, CandidateOwnershipStore.FileName)));
        Directory.Delete(junctionPath, recursive: false);
    }

    private static RecoveryFixture CreateResidue(string stateDirectory, CrashStage stage)
    {
        var protector = new IntegrityTestProtector();
        var store = new CandidateOwnershipStore(protector);
        var root = Directory.CreateDirectory(Path.Combine(stateDirectory, "artifact-candidates")).FullName;
        var receiptId = Guid.NewGuid().ToString("N");
        var directory = Directory.CreateDirectory(Path.Combine(root, receiptId)).FullName;
        var includeRuntime = stage is CrashStage.VerifiedDll
            or CrashStage.VerifiedPair
            or CrashStage.ConfirmationWait;
        var artifact = new ModReleaseArtifact(
            new Uri("https://example.invalid/version.dll"),
            "version.dll",
            DllBytes.Length,
            Convert.ToHexString(SHA256.HashData(DllBytes)),
            "1.0.0.0",
            includeRuntime
                ? new(
                    new Uri("https://example.invalid/stfc-runtime-manifest.json"),
                    ArtifactBoundRuntimeManifestParser.ManagedFileName,
                    RuntimeBytes.Length,
                    Convert.ToHexString(SHA256.HashData(RuntimeBytes)),
                    new string('a', 40),
                    "Guffawaffle/stfc-mod",
                    "v1")
                : null);
        var ownership = CandidateOwnershipStore.Create(
            receiptId,
            new string('B', 64),
            new("guffawaffle", "stable", "guffawaffle.stfc-community-mod"),
            artifact);
        store.Save(directory, ownership);

        if (stage == CrashStage.CreatedDllBeforeWritingReceipt)
        {
            using var empty = new FileStream(
                Path.Combine(directory, artifact.FileName),
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None);
            empty.Flush(flushToDisk: true);
        }
        else
        {
            var dllContents = stage == CrashStage.PartialDll ? DllBytes[..8] : DllBytes;
            var dllIdentity = WriteCrashMember(Path.Combine(directory, artifact.FileName), dllContents);
            ownership = CandidateOwnershipStore.UpdateDll(
                ownership,
                stage == CrashStage.PartialDll ? CandidateMemberStage.Writing : CandidateMemberStage.Complete,
                dllIdentity);
            store.Save(directory, ownership);
        }

        if (stage is CrashStage.VerifiedPair or CrashStage.ConfirmationWait)
        {
            var runtimeIdentity = WriteCrashMember(
                Path.Combine(directory, artifact.RuntimeManifest!.FileName),
                RuntimeBytes);
            ownership = CandidateOwnershipStore.UpdateRuntimeManifest(
                ownership,
                CandidateMemberStage.Complete,
                runtimeIdentity);
            store.Save(directory, ownership);
        }
        return new(
            directory,
            root,
            store,
            new CandidateRecoveryService(root, store, CandidateFileNative.TryMarkDeleteOnClose));
    }

    private static CandidateFileIdentity WriteCrashMember(string path, byte[] contents)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        var identity = CandidateFileNative.ReadIdentity(stream.SafeFileHandle);
        stream.Write(contents);
        stream.Flush(flushToDisk: true);
        return identity;
    }

    private static long DirectorySize(string root) => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
        .Sum(path => new FileInfo(path).Length);

    private static async Task CreateJunctionAsync(string link, string target)
    {
        var start = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("/d");
        start.ArgumentList.Add("/c");
        start.ArgumentList.Add("mklink");
        start.ArgumentList.Add("/J");
        start.ArgumentList.Add(link);
        start.ArgumentList.Add(target);
        using var junction = Process.Start(start)
            ?? throw new InvalidOperationException("Could not start the reparse-point fixture process.");
        await junction.WaitForExitAsync();
        Assert.AreEqual(0, junction.ExitCode, await junction.StandardError.ReadToEndAsync());
    }

    private static void RequireWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Reviewed candidate crash recovery is Windows-specific.");
        }
    }

    public enum CrashStage
    {
        PartialDll,
        CreatedDllBeforeWritingReceipt,
        VerifiedDll,
        VerifiedPair,
        ConfirmationWait,
    }

    private sealed record RecoveryFixture(
        string CandidateDirectory,
        string CandidateRoot,
        CandidateOwnershipStore OwnershipStore,
        CandidateRecoveryService Recovery);

    private sealed class IntegrityTestProtector : ICandidateOwnershipProtector
    {
        private static readonly byte[] Key = SHA256.HashData(Encoding.UTF8.GetBytes("candidate recovery tests"));

        public byte[] Protect(byte[] contents)
        {
            var result = new byte[contents.Length + 32];
            contents.CopyTo(result, 0);
            HMACSHA256.HashData(Key, contents).CopyTo(result, contents.Length);
            return result;
        }

        public byte[] Unprotect(byte[] protectedContents)
        {
            if (protectedContents.Length < 33)
            {
                throw new CryptographicException("Test ownership metadata is truncated.");
            }
            var contents = protectedContents.AsSpan(0, protectedContents.Length - 32).ToArray();
            var expected = HMACSHA256.HashData(Key, contents);
            if (!CryptographicOperations.FixedTimeEquals(
                expected,
                protectedContents.AsSpan(protectedContents.Length - 32, 32)))
            {
                throw new CryptographicException("Test ownership metadata was changed.");
            }
            return contents;
        }
    }
}
