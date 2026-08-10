using System.Buffers.Binary;
using System.ComponentModel;
using System.Globalization;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace STFCCommunityMod.Launcher.Core;

public sealed class ReviewedModArtifactCandidateReceipt
{
    internal ReviewedModArtifactCandidateReceipt(
        string receiptId,
        ModReleaseArtifact artifact,
        string certificationFingerprint,
        ModArtifactIdentityReceipt dllIdentity,
        ModArtifactIdentityReceipt? runtimeManifestIdentity,
        ReviewedRuntimeActivation? runtimeActivation,
        ModInstallationAttribution installationAttribution)
    {
        ReceiptId = receiptId;
        Artifact = artifact;
        CertificationFingerprint = certificationFingerprint;
        DllIdentity = dllIdentity;
        RuntimeManifestIdentity = runtimeManifestIdentity;
        RuntimeActivation = runtimeActivation;
        InstallationAttribution = installationAttribution;
    }

    public string ReceiptId { get; }

    public ModReleaseArtifact Artifact { get; }

    public string CertificationFingerprint { get; }

    public ModArtifactIdentityReceipt DllIdentity { get; }

    public ModArtifactIdentityReceipt? RuntimeManifestIdentity { get; }

    public ReviewedRuntimeActivation? RuntimeActivation { get; }

    public ModInstallationAttribution InstallationAttribution { get; }
}

public sealed class ReviewedModArtifactCandidateLease : IAsyncDisposable
{
    private const int Available = 0;
    private const int Claimed = 1;
    private const int CleanupPending = 2;
    private const int Disposed = 3;
    private readonly string candidateDirectory;
    private readonly string dllPath;
    private readonly string? runtimeManifestPath;
    private readonly string ownershipPath;
    private readonly Func<SafeFileHandle, bool> markDeleteOnClose;
    private readonly Func<CancellationToken, ValueTask>? afterDeploymentClaimed;
    private readonly CandidateAcquisitionLifetime? candidateLifetime;
    private readonly Action<ReviewedModArtifactCandidateLease>? afterReleased;
    private FileStream? dllStream;
    private FileStream? runtimeManifestStream;
    private FileStream? ownershipStream;
    private readonly SemaphoreSlim operationGate = new(1, 1);
    private object? activeClaim;
    private int state;

    internal ReviewedModArtifactCandidateLease(
        string candidateDirectory,
        string dllPath,
        string? runtimeManifestPath,
        FileStream? dllStream,
        FileStream? runtimeManifestStream,
        FileStream? ownershipStream,
        ReviewedModArtifactCandidateReceipt receipt,
        Func<SafeFileHandle, bool> markDeleteOnClose,
        Func<CancellationToken, ValueTask>? afterDeploymentClaimed,
        CandidateAcquisitionLifetime? candidateLifetime,
        Action<ReviewedModArtifactCandidateLease>? afterReleased)
    {
        this.candidateDirectory = candidateDirectory;
        this.dllPath = dllPath;
        this.runtimeManifestPath = runtimeManifestPath;
        ownershipPath = Path.Combine(candidateDirectory, CandidateOwnershipStore.FileName);
        this.dllStream = dllStream;
        this.runtimeManifestStream = runtimeManifestStream;
        this.ownershipStream = ownershipStream;
        Receipt = receipt;
        this.markDeleteOnClose = markDeleteOnClose;
        this.afterDeploymentClaimed = afterDeploymentClaimed;
        this.candidateLifetime = candidateLifetime;
        this.afterReleased = afterReleased;
    }

    public ReviewedModArtifactCandidateReceipt Receipt { get; }

    internal string CandidateDirectory => candidateDirectory;

    internal bool TryClaim(out object? claim)
    {
        claim = new object();
        if (Interlocked.CompareExchange(ref state, Claimed, Available) != Available)
        {
            claim = null;
            return false;
        }
        Volatile.Write(ref activeClaim, claim);
        return true;
    }

    internal ValueTask AfterClaimedAsync(CancellationToken cancellationToken) =>
        afterDeploymentClaimed?.Invoke(cancellationToken) ?? ValueTask.CompletedTask;

    internal async Task<ReviewedModArtifactCandidateContents> ConsumeAsync(
        object claim,
        string expectedCertificationFingerprint,
        ModInstallationAttribution expectedInstallationAttribution,
        CancellationToken cancellationToken)
    {
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (state != Claimed || !ReferenceEquals(Volatile.Read(ref activeClaim), claim))
            {
                throw new InvalidOperationException(
                    "The reviewed artifact candidate is not owned by this deployment claim.");
            }
            if (!FixedTimeEquals(Receipt.CertificationFingerprint, expectedCertificationFingerprint)
                || Receipt.InstallationAttribution != expectedInstallationAttribution)
            {
                throw new InvalidDataException("The reviewed artifact candidate authority has changed.");
            }

            var dll = await ReadExactAsync(
                dllStream!,
                Receipt.DllIdentity,
                "DLL candidate",
                cancellationToken).ConfigureAwait(false);
            byte[]? runtimeManifest = null;
            if (Receipt.RuntimeManifestIdentity is not null)
            {
                runtimeManifest = await ReadExactAsync(
                    runtimeManifestStream!,
                    Receipt.RuntimeManifestIdentity,
                    "runtime-manifest candidate",
                    cancellationToken).ConfigureAwait(false);
            }
            return new(dll, runtimeManifest, Receipt.RuntimeActivation);
        }
        finally
        {
            operationGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await operationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (state == Disposed)
            {
                return;
            }
            if (state == Claimed)
            {
                throw new InvalidOperationException(
                    "The reviewed artifact candidate is owned by an active deployment.");
            }
            if (state == Available)
            {
                state = CleanupPending;
            }
            await CleanupUnderGateAsync().ConfigureAwait(false);
        }
        finally
        {
            operationGate.Release();
        }
    }

    internal async ValueTask CleanupClaimAsync(object claim)
    {
        await operationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (state == Disposed)
            {
                return;
            }
            if (state is not (Claimed or CleanupPending)
                || !ReferenceEquals(Volatile.Read(ref activeClaim), claim))
            {
                throw new InvalidOperationException(
                    "The reviewed artifact candidate cleanup claim is invalid.");
            }
            state = CleanupPending;
            await CleanupUnderGateAsync().ConfigureAwait(false);
        }
        finally
        {
            operationGate.Release();
        }
    }

    private async ValueTask CleanupUnderGateAsync()
    {
        Exception? cleanupFailure = null;
        try
        {
            await CloseExactStreamAsync(
                runtimeManifestStream,
                runtimeManifestPath,
                Receipt.RuntimeManifestIdentity).ConfigureAwait(false);
            runtimeManifestStream = null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            cleanupFailure = exception;
        }
        try
        {
            await CloseExactStreamAsync(dllStream, dllPath, Receipt.DllIdentity).ConfigureAwait(false);
            dllStream = null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            cleanupFailure = cleanupFailure is null
                ? exception
                : new AggregateException(cleanupFailure, exception);
        }
        if (cleanupFailure is not null)
        {
            state = CleanupPending;
            throw new IOException("Reviewed candidate cleanup could not delete every exact owned member.", cleanupFailure);
        }
        try
        {
            await CloseExactMetadataStreamAsync(ownershipStream).ConfigureAwait(false);
            ownershipStream = null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            state = CleanupPending;
            throw new IOException("Reviewed candidate cleanup could not delete its exact ownership receipt.", exception);
        }
        try
        {
            Directory.Delete(candidateDirectory, recursive: false);
        }
        catch (IOException)
        {
            // A changed or foreign file is not receipt-owned and must survive cleanup.
        }
        catch (UnauthorizedAccessException)
        {
            // Cleanup is best effort; never broaden ownership after access changed.
        }
        Volatile.Write(ref activeClaim, null);
        state = Disposed;
        if (candidateLifetime is not null)
        {
            await candidateLifetime.DisposeAsync().ConfigureAwait(false);
        }
        afterReleased?.Invoke(this);
    }

    private static async Task<byte[]> ReadExactAsync(
        FileStream stream,
        ModArtifactIdentityReceipt identity,
        string subject,
        CancellationToken cancellationToken)
    {
        if (stream.Length != identity.Size || identity.Size > int.MaxValue)
        {
            throw new InvalidDataException($"The locked {subject} no longer matches its receipt.");
        }
        stream.Position = 0;
        var bytes = new byte[checked((int)identity.Size)];
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        if (!FixedTimeEquals(Convert.ToHexString(SHA256.HashData(bytes)), identity.Sha256))
        {
            throw new InvalidDataException($"The locked {subject} no longer matches its receipt.");
        }
        return bytes;
    }

    private async ValueTask CloseExactStreamAsync(
        FileStream? stream,
        string? path,
        ModArtifactIdentityReceipt? identity)
    {
        if (stream is null)
        {
            return;
        }
        if (OperatingSystem.IsWindows() && !markDeleteOnClose(stream.SafeFileHandle))
        {
            throw new IOException("The exact reviewed candidate file could not be marked for deletion.");
        }
        await stream.DisposeAsync().ConfigureAwait(false);
        if (!OperatingSystem.IsWindows())
        {
            DeleteIfOwned(path, identity);
        }
    }

    private async ValueTask CloseExactMetadataStreamAsync(FileStream? stream)
    {
        if (stream is null)
        {
            return;
        }
        if (OperatingSystem.IsWindows() && !markDeleteOnClose(stream.SafeFileHandle))
        {
            throw new IOException("The exact reviewed candidate ownership receipt could not be marked for deletion.");
        }
        await stream.DisposeAsync().ConfigureAwait(false);
        if (!OperatingSystem.IsWindows())
        {
            File.Delete(ownershipPath);
        }
    }

    private static void DeleteIfOwned(string? path, ModArtifactIdentityReceipt? identity)
    {
        if (path is null || identity is null || !File.Exists(path))
        {
            return;
        }
        try
        {
            var info = new FileInfo(path);
            if (info.Length != identity.Size)
            {
                return;
            }
            string hash;
            using (var stream = File.OpenRead(path))
            {
                hash = Convert.ToHexString(SHA256.HashData(stream));
            }
            if (FixedTimeEquals(hash, identity.Sha256)
                && new FileInfo(path).Length == identity.Size)
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static bool FixedTimeEquals(string left, string right)
    {
        if (left.Length != 64 || right.Length != 64)
        {
            return false;
        }
        return CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(left),
            Convert.FromHexString(right));
    }
}

internal sealed record ReviewedModArtifactCandidateContents(
    byte[] Dll,
    byte[]? RuntimeManifest,
    ReviewedRuntimeActivation? RuntimeActivation);

public sealed class ReviewedModArtifactCandidateAcquirer : IAsyncDisposable
{
    private const long MaximumArtifactBytes = 128L * 1024L * 1024L;
    private readonly string candidateRoot;
    private readonly string stateDirectory;
    private readonly IModArtifactDownloader downloader;
    private readonly IModArtifactVersionReader versionReader;
    private readonly IModArtifactAuthenticityVerifier authenticityVerifier;
    private readonly ModInstallationAttribution installationAttribution;
    private readonly ReviewedReleaseCertification certification;
    private readonly string certificationFingerprint;
    private readonly Func<string, CancellationToken, ValueTask>? afterCandidateFileOpened;
    private readonly Func<string, CancellationToken, ValueTask>? afterCandidateFileCreatedBeforeOwnershipPersisted;
    private readonly Func<string, CancellationToken, ValueTask>? afterCandidatePartialWrite;
    private readonly Func<string, CancellationToken, ValueTask>? afterCandidateMemberVerified;
    private readonly Func<SafeFileHandle, bool> markCandidateDelete;
    private readonly Func<CancellationToken, ValueTask>? afterDeploymentClaimed;
    private readonly CandidateOwnershipStore ownershipStore;
    private readonly CandidateRecoveryService recoveryService;
    private readonly SemaphoreSlim acquisitionGate = new(1, 1);
    private ReviewedModArtifactCandidateLease? pendingCleanup;
    private ReviewedModArtifactCandidateLease? activeLease;
    private bool disposed;

    public ReviewedModArtifactCandidateAcquirer(
        string stateDirectory,
        IModArtifactDownloader downloader,
        IModArtifactVersionReader versionReader,
        IModArtifactAuthenticityVerifier authenticityVerifier,
        ModInstallationAttribution installationAttribution,
        ReviewedReleaseCertification certification)
        : this(
            stateDirectory,
            downloader,
            versionReader,
            authenticityVerifier,
            installationAttribution,
            certification,
            afterCandidateFileOpened: null,
            markCandidateDelete: null,
            afterDeploymentClaimed: null,
            ownershipProtector: null,
            afterCandidateFileCreatedBeforeOwnershipPersisted: null,
            afterCandidatePartialWrite: null,
            afterCandidateMemberVerified: null)
    {
    }

    internal ReviewedModArtifactCandidateAcquirer(
        string stateDirectory,
        IModArtifactDownloader downloader,
        IModArtifactVersionReader versionReader,
        IModArtifactAuthenticityVerifier authenticityVerifier,
        ModInstallationAttribution installationAttribution,
        ReviewedReleaseCertification certification,
        Func<string, CancellationToken, ValueTask>? afterCandidateFileOpened,
        Func<SafeFileHandle, bool>? markCandidateDelete = null,
        Func<CancellationToken, ValueTask>? afterDeploymentClaimed = null,
        ICandidateOwnershipProtector? ownershipProtector = null,
        Func<string, CancellationToken, ValueTask>? afterCandidateFileCreatedBeforeOwnershipPersisted = null,
        Func<string, CancellationToken, ValueTask>? afterCandidatePartialWrite = null,
        Func<string, CancellationToken, ValueTask>? afterCandidateMemberVerified = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDirectory);
        this.stateDirectory = Path.GetFullPath(stateDirectory);
        candidateRoot = Path.Combine(this.stateDirectory, "artifact-candidates");
        this.downloader = downloader ?? throw new ArgumentNullException(nameof(downloader));
        this.versionReader = versionReader ?? throw new ArgumentNullException(nameof(versionReader));
        this.authenticityVerifier = authenticityVerifier ?? throw new ArgumentNullException(nameof(authenticityVerifier));
        this.installationAttribution = installationAttribution
            ?? throw new ArgumentNullException(nameof(installationAttribution));
        this.certification = certification ?? throw new ArgumentNullException(nameof(certification));
        this.afterCandidateFileOpened = afterCandidateFileOpened;
        this.afterCandidateFileCreatedBeforeOwnershipPersisted =
            afterCandidateFileCreatedBeforeOwnershipPersisted;
        this.afterCandidatePartialWrite = afterCandidatePartialWrite;
        this.afterCandidateMemberVerified = afterCandidateMemberVerified;
        this.markCandidateDelete = markCandidateDelete ?? CandidateFileNative.TryMarkDeleteOnClose;
        this.afterDeploymentClaimed = afterDeploymentClaimed;
        ownershipStore = new(ownershipProtector ?? new WindowsDpapiCandidateOwnershipProtector());
        recoveryService = new(candidateRoot, ownershipStore, this.markCandidateDelete);
        certificationFingerprint = CertificationFingerprint(certification);
    }

    public bool HasPendingCleanup => Volatile.Read(ref pendingCleanup) is not null;

    public async ValueTask DisposeAsync()
    {
        await acquisitionGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (disposed)
            {
                return;
            }
            if (activeLease is not null)
            {
                await activeLease.DisposeAsync().ConfigureAwait(false);
                activeLease = null;
            }
            await RetryPendingCleanupUnderGateAsync().ConfigureAwait(false);
            disposed = true;
        }
        finally
        {
            acquisitionGate.Release();
        }
    }

    public async ValueTask RetryPendingCleanupAsync(CancellationToken cancellationToken = default)
    {
        await acquisitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            await RetryPendingCleanupUnderGateAsync().ConfigureAwait(false);
        }
        finally
        {
            acquisitionGate.Release();
        }
    }

    public async Task<ReviewedModArtifactCandidateLease> AcquireAsync(
        ModReleaseArtifact artifact,
        CancellationToken cancellationToken = default)
    {
        await acquisitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            await RetryPendingCleanupUnderGateAsync().ConfigureAwait(false);
            if (activeLease is not null)
            {
                throw new InvalidOperationException(
                    "Finish or cancel the current reviewed mod download before starting another one.");
            }
            var lifetime = await CandidateAcquisitionLifetime.TryAcquireAsync(
                stateDirectory,
                cancellationToken).ConfigureAwait(false);
            if (lifetime is null)
            {
                throw new InvalidOperationException(
                    "Another Mod Bridge window is preparing or confirming a mod download. Finish or cancel it, then try again.");
            }
            var transferred = false;
            try
            {
                var recovery = await recoveryService.RecoverUnderLifetimeAsync(cancellationToken).ConfigureAwait(false);
                if (!recovery.CanAcquire)
                {
                    throw new InvalidDataException(recovery.Message);
                }
                var lease = await AcquireCoreAsync(artifact, lifetime, cancellationToken).ConfigureAwait(false);
                activeLease = lease;
                transferred = true;
                return lease;
            }
            finally
            {
                if (!transferred && pendingCleanup is null)
                {
                    await lifetime.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
        finally
        {
            acquisitionGate.Release();
        }
    }

    public async Task<ReviewedCandidateRecoveryResult> RecoverAbandonedCandidatesAsync(
        CancellationToken cancellationToken = default)
    {
        await acquisitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            try
            {
                await RetryPendingCleanupUnderGateAsync().ConfigureAwait(false);
            }
            catch (IOException)
            {
                return new(
                    ReviewedCandidateRecoveryState.Blocked,
                    0,
                    0,
                    1,
                    "Mod Bridge still owns an exact cleanup retry that Windows could not finish. Close other Bridge windows, then try recovery again; no new download or game change was started.");
            }
            if (activeLease is not null)
            {
                return new(
                    ReviewedCandidateRecoveryState.Busy,
                    0,
                    0,
                    0,
                    "A reviewed mod download is waiting for confirmation. Finish or cancel it before retrying recovery.");
            }
            await using var lifetime = await CandidateAcquisitionLifetime.TryAcquireAsync(
                stateDirectory,
                cancellationToken).ConfigureAwait(false);
            if (lifetime is null)
            {
                return new(
                    ReviewedCandidateRecoveryState.Busy,
                    0,
                    0,
                    0,
                    "Another Mod Bridge window is preparing or confirming a mod download. Close it or finish that action, then retry recovery.");
            }
            return await recoveryService.RecoverUnderLifetimeAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            acquisitionGate.Release();
        }
    }

    private async ValueTask RetryPendingCleanupUnderGateAsync()
    {
        var cleanup = pendingCleanup;
        if (cleanup is null)
        {
            return;
        }
        await cleanup.DisposeAsync().ConfigureAwait(false);
        pendingCleanup = null;
    }

    private async Task<ReviewedModArtifactCandidateLease> AcquireCoreAsync(
        ModReleaseArtifact artifact,
        CandidateAcquisitionLifetime lifetime,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        ValidateReviewedCoordinates(artifact);

        var receiptId = Guid.NewGuid().ToString("N");
        var directory = Path.Combine(candidateRoot, receiptId);
        var dllPath = Path.Combine(directory, artifact.FileName);
        var runtimePath = artifact.RuntimeManifest is null
            ? null
            : Path.Combine(directory, artifact.RuntimeManifest.FileName);
        FileStream? dllLock = null;
        FileStream? runtimeLock = null;
        FileStream? dllWrite = null;
        FileStream? runtimeWrite = null;
        FileStream? ownershipLock = null;
        var dllOwned = false;
        var runtimeOwned = false;
        var dllWritten = false;
        var runtimeWritten = false;
        try
        {
            Directory.CreateDirectory(candidateRoot);
            if ((File.GetAttributes(candidateRoot) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException("Reviewed candidate storage refuses filesystem links or reparse points.");
            }
            Directory.CreateDirectory(directory);
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException("Reviewed candidate storage refuses filesystem links or reparse points.");
            }
            var ownership = CandidateOwnershipStore.Create(
                receiptId,
                certificationFingerprint,
                installationAttribution,
                artifact);
            ownershipStore.Save(directory, ownership);
            var dll = await downloader.DownloadAsync(artifact.DownloadUri, cancellationToken).ConfigureAwait(false);
            VerifyDownload(dll, artifact.Size, artifact.Sha256, "DLL");
            dllWrite = CreateOwnedWrite(dllPath);
            if (afterCandidateFileCreatedBeforeOwnershipPersisted is not null)
            {
                await afterCandidateFileCreatedBeforeOwnershipPersisted(
                    dllPath,
                    cancellationToken).ConfigureAwait(false);
            }
            var dllFileIdentity = CandidateFileNative.ReadIdentity(dllWrite.SafeFileHandle);
            ownership = CandidateOwnershipStore.UpdateDll(
                ownership,
                CandidateMemberStage.Writing,
                dllFileIdentity);
            ownershipStore.Save(directory, ownership);
            await WriteOwnedAsync(dllWrite, dllPath, dll.Contents, cancellationToken).ConfigureAwait(false);
            await dllWrite.DisposeAsync().ConfigureAwait(false);
            dllWrite = null;
            dllWritten = true;
            await using (var verificationLock = new FileStream(
                dllPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                81920,
                FileOptions.Asynchronous))
            {
                VerifyLockedStream(verificationLock, artifact.Size, artifact.Sha256, "DLL");
                VerifyVersionAndAuthenticity(dllPath, artifact.ExpectedVersion);
            }
            dllLock = OpenLockedRead(dllPath);
            VerifyLockedStream(dllLock, artifact.Size, artifact.Sha256, "DLL");
            dllOwned = true;
            ownership = CandidateOwnershipStore.UpdateDll(
                ownership,
                CandidateMemberStage.Complete,
                dllFileIdentity);
            ownershipStore.Save(directory, ownership);
            if (afterCandidateMemberVerified is not null)
            {
                await afterCandidateMemberVerified(dllPath, cancellationToken).ConfigureAwait(false);
            }

            ModArtifactDownload? runtime = null;
            ParsedRuntimeManifest? parsed = null;
            ReviewedRuntimeActivation? activation = null;
            if (artifact.RuntimeManifest is not null)
            {
                runtime = await downloader.DownloadAsync(
                    artifact.RuntimeManifest.DownloadUri,
                    cancellationToken).ConfigureAwait(false);
                VerifyDownload(
                    runtime,
                    artifact.RuntimeManifest.Size,
                    artifact.RuntimeManifest.Sha256,
                    "runtime manifest");
                parsed = ArtifactBoundRuntimeManifestParser.Parse(
                    runtime.Contents,
                    artifact,
                    artifact.RuntimeManifest,
                    installationAttribution.RuntimeDistributionId);
                activation = ArtifactBoundRuntimeManifestParser.AuthorizeActivation(
                    parsed,
                    artifact,
                    artifact.RuntimeManifest,
                    certification) ?? throw new InvalidDataException(
                        "The runtime manifest pair is not authorized by the launcher-bundled reviewed certification.");
                runtimeWrite = CreateOwnedWrite(runtimePath!);
                if (afterCandidateFileCreatedBeforeOwnershipPersisted is not null)
                {
                    await afterCandidateFileCreatedBeforeOwnershipPersisted(
                        runtimePath!,
                        cancellationToken).ConfigureAwait(false);
                }
                var runtimeFileIdentity = CandidateFileNative.ReadIdentity(runtimeWrite.SafeFileHandle);
                ownership = CandidateOwnershipStore.UpdateRuntimeManifest(
                    ownership,
                    CandidateMemberStage.Writing,
                    runtimeFileIdentity);
                ownershipStore.Save(directory, ownership);
                await WriteOwnedAsync(
                    runtimeWrite,
                    runtimePath!,
                    runtime.Contents,
                    cancellationToken).ConfigureAwait(false);
                await runtimeWrite.DisposeAsync().ConfigureAwait(false);
                runtimeWrite = null;
                runtimeWritten = true;
                runtimeLock = OpenLockedRead(runtimePath!);
                VerifyLockedStream(
                    runtimeLock,
                    artifact.RuntimeManifest.Size,
                    artifact.RuntimeManifest.Sha256,
                    "runtime manifest");
                runtimeOwned = true;
                ownership = CandidateOwnershipStore.UpdateRuntimeManifest(
                    ownership,
                    CandidateMemberStage.Complete,
                    runtimeFileIdentity);
                ownershipStore.Save(directory, ownership);
                if (afterCandidateMemberVerified is not null)
                {
                    await afterCandidateMemberVerified(runtimePath!, cancellationToken).ConfigureAwait(false);
                }
            }

            ownershipLock = OpenValidatedOwnershipLock(directory);

            var dllIdentity = new ModArtifactIdentityReceipt(artifact.Size, artifact.Sha256.ToUpperInvariant());
            var runtimeIdentity = artifact.RuntimeManifest is null
                ? null
                : new ModArtifactIdentityReceipt(
                    artifact.RuntimeManifest.Size,
                    artifact.RuntimeManifest.Sha256.ToUpperInvariant());
            var receipt = new ReviewedModArtifactCandidateReceipt(
                receiptId,
                artifact,
                certificationFingerprint,
                dllIdentity,
                runtimeIdentity,
                activation,
                installationAttribution);
            var lease = new ReviewedModArtifactCandidateLease(
                directory,
                dllPath,
                runtimePath,
                dllLock,
                runtimeLock,
                ownershipLock,
                receipt,
                markCandidateDelete,
                afterDeploymentClaimed,
                lifetime,
                OnLeaseReleased);
            dllLock = null;
            runtimeLock = null;
            ownershipLock = null;
            return lease;
        }
        catch (Exception acquisitionFailure)
        {
            var runtimeCleanup = await CleanupAcquisitionMemberAsync(
                runtimeLock ?? runtimeWrite,
                runtimeOwned || runtimeWrite is not null,
                runtimeWritten,
                runtimePath,
                artifact.RuntimeManifest?.Size,
                artifact.RuntimeManifest?.Sha256).ConfigureAwait(false);
            var dllCleanup = await CleanupAcquisitionMemberAsync(
                dllLock ?? dllWrite,
                dllOwned || dllWrite is not null,
                dllWritten,
                dllPath,
                artifact.Size,
                artifact.Sha256).ConfigureAwait(false);
            Exception? ownershipCleanup = null;
            if (runtimeCleanup.Pending is null && dllCleanup.Pending is null)
            {
                ownershipCleanup = await CleanupOwnershipMetadataAsync(directory, ownershipLock).ConfigureAwait(false);
                ownershipLock = null;
            }
            DeleteCandidateDirectory(directory);
            var cleanupFailure = runtimeCleanup.Failure is null
                ? dllCleanup.Failure
                : dllCleanup.Failure is null
                    ? runtimeCleanup.Failure
                    : new AggregateException(runtimeCleanup.Failure, dllCleanup.Failure);
            if (ownershipCleanup is not null)
            {
                cleanupFailure = cleanupFailure is null
                    ? ownershipCleanup
                    : new AggregateException(cleanupFailure, ownershipCleanup);
            }
            if (cleanupFailure is null)
            {
                throw;
            }
            if (runtimeCleanup.Pending is not null || dllCleanup.Pending is not null)
            {
                var cleanupReceipt = new ReviewedModArtifactCandidateReceipt(
                    receiptId,
                    artifact,
                    certificationFingerprint,
                    new(artifact.Size, artifact.Sha256.ToUpperInvariant()),
                    artifact.RuntimeManifest is null
                        ? null
                        : new(
                            artifact.RuntimeManifest.Size,
                            artifact.RuntimeManifest.Sha256.ToUpperInvariant()),
                    runtimeActivation: null,
                    installationAttribution);
                var pendingOwnership = ownershipLock ?? OpenOwnershipLockIfValid(directory);
                ownershipLock = null;
                pendingCleanup = new(
                    directory,
                    dllPath,
                    runtimePath,
                    dllCleanup.Pending,
                    runtimeCleanup.Pending,
                    pendingOwnership,
                    cleanupReceipt,
                    markCandidateDelete,
                    afterDeploymentClaimed: null,
                    lifetime,
                    afterReleased: null);
            }
            throw new AggregateException(
                "Reviewed candidate acquisition failed and exact cleanup was incomplete.",
                acquisitionFailure,
                cleanupFailure);
        }
    }

    private async ValueTask<Exception?> CleanupOwnershipMetadataAsync(
        string directory,
        FileStream? stream)
    {
        var path = Path.Combine(directory, CandidateOwnershipStore.FileName);
        if (stream is null && File.Exists(path))
        {
            try
            {
                stream = OpenValidatedOwnershipLock(directory);
            }
            catch (Exception exception) when (exception is IOException
                or UnauthorizedAccessException
                or InvalidDataException)
            {
                return exception;
            }
        }
        if (stream is null)
        {
            return null;
        }
        try
        {
            await DeleteLockedStreamAsync(stream, path).ConfigureAwait(false);
            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            return exception;
        }
    }

    private FileStream? OpenOwnershipLockIfValid(string directory)
    {
        var path = Path.Combine(directory, CandidateOwnershipStore.FileName);
        if (!File.Exists(path))
        {
            return null;
        }
        try
        {
            return OpenValidatedOwnershipLock(directory);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException)
        {
            return null;
        }
    }

    private FileStream OpenValidatedOwnershipLock(string directory)
    {
        var path = Path.Combine(directory, CandidateOwnershipStore.FileName);
        var stream = OpenLockedRead(path);
        try
        {
            if (stream.Length is <= 0 or > CandidateOwnershipStore.MaximumProtectedBytes)
            {
                throw new InvalidDataException("Candidate ownership metadata is outside its size bound.");
            }
            var bytes = new byte[checked((int)stream.Length)];
            stream.ReadExactly(bytes);
            stream.Position = 0;
            _ = ownershipStore.LoadProtectedBytes(bytes, Path.GetFileName(directory));
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private void OnLeaseReleased(ReviewedModArtifactCandidateLease lease)
    {
        Interlocked.CompareExchange(ref activeLease, null, lease);
    }

    private async ValueTask<AcquisitionCleanupAttempt> CleanupAcquisitionMemberAsync(
        FileStream? stream,
        bool exactLocked,
        bool written,
        string? path,
        long? size,
        string? sha256)
    {
        if (path is null || size is null || sha256 is null)
        {
            return new(null, null);
        }
        try
        {
            if (stream is null && written)
            {
                stream = OpenLockedRead(path);
                VerifyLockedStream(stream, size.Value, sha256, "candidate cleanup member");
                exactLocked = true;
            }
            if (stream is null)
            {
                return new(null, null);
            }
            if (!exactLocked)
            {
                await stream.DisposeAsync().ConfigureAwait(false);
                return new(null, null);
            }
            await DeleteLockedStreamAsync(stream, path).ConfigureAwait(false);
            return new(null, null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            if (!exactLocked && stream is not null)
            {
                await stream.DisposeAsync().ConfigureAwait(false);
                stream = null;
            }
            return exactLocked
                ? new(stream, exception)
                : new(null, exception);
        }
    }

    private sealed record AcquisitionCleanupAttempt(FileStream? Pending, Exception? Failure);

    internal static string CertificationFingerprint(ReviewedReleaseCertification value)
    {
        var fields = new[]
        {
            "stfc-mod-bridge.reviewed-candidate-certification.v1",
            value.ProviderId, value.ChannelId, value.RuntimeDistributionId, value.Repository, value.Tag,
            value.ReleaseVersion, value.SourceCommit, value.AssetName,
            value.AssetSize.ToString(CultureInfo.InvariantCulture), value.AssetSha256,
            value.PayloadFileName, value.PayloadSize.ToString(CultureInfo.InvariantCulture), value.PayloadSha256,
            value.PayloadVersion,
            value.ObservedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            value.RuntimeManifest?.FileName ?? string.Empty,
            value.RuntimeManifest?.Size.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            value.RuntimeManifest?.Sha256 ?? string.Empty,
        };
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> length = stackalloc byte[sizeof(int)];
        foreach (var field in fields)
        {
            var bytes = Encoding.UTF8.GetBytes(field);
            BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
            hash.AppendData(length);
            hash.AppendData(bytes);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private void ValidateReviewedCoordinates(ModReleaseArtifact artifact)
    {
        var runtime = certification.RuntimeManifest;
        if (artifact.FileName != "version.dll"
            || artifact.Size is <= 0 or > MaximumArtifactBytes
            || artifact.Sha256.Length != 64
            || !artifact.Sha256.All(Uri.IsHexDigit)
            || string.IsNullOrWhiteSpace(artifact.ExpectedVersion)
            || !artifact.DownloadUri.IsAbsoluteUri
            || artifact.DownloadUri.Scheme != Uri.UriSchemeHttps
            || installationAttribution.ProviderId != certification.ProviderId
            || installationAttribution.ReleaseChannelId != certification.ChannelId
            || installationAttribution.RuntimeDistributionId != certification.RuntimeDistributionId
            || artifact.DownloadUri != certification.DownloadUri
            || artifact.FileName != certification.PayloadFileName
            || artifact.Size != certification.PayloadSize
            || !FixedTimeEquals(artifact.Sha256, certification.PayloadSha256)
            || artifact.ExpectedVersion != certification.PayloadVersion
            || (runtime is null) != (artifact.RuntimeManifest is null))
        {
            throw new InvalidDataException("The candidate does not match the selected reviewed release certification.");
        }
        if (runtime is not null
            && (artifact.RuntimeManifest!.DownloadUri
                    != ReviewedGitHubReleaseAssetClient.RuntimeManifestUri(certification)
                || artifact.RuntimeManifest.FileName != runtime.FileName
                || artifact.RuntimeManifest.Size != runtime.Size
                || !FixedTimeEquals(artifact.RuntimeManifest.Sha256, runtime.Sha256)
                || artifact.RuntimeManifest.ExpectedSourceRevision != certification.SourceCommit
                || artifact.RuntimeManifest.ExpectedRepository != certification.Repository
                || artifact.RuntimeManifest.ExpectedTag != certification.Tag))
        {
            throw new InvalidDataException("The candidate runtime manifest does not match the reviewed exact-pair certification.");
        }
    }

    private void VerifyVersionAndAuthenticity(string path, string expectedVersion)
    {
        if (versionReader.ReadVersion(path) != expectedVersion)
        {
            throw new InvalidDataException("The candidate DLL embedded version does not match the reviewed release.");
        }
        var result = authenticityVerifier.Verify(path);
        if (!result.IsTrusted)
        {
            throw new InvalidDataException($"The candidate DLL authenticity check failed: {result.Message}");
        }
    }

    private static void VerifyDownload(
        ModArtifactDownload download,
        long expectedSize,
        string expectedSha256,
        string subject)
    {
        if (download.StatusCode != HttpStatusCode.OK
            || download.DeclaredContentLength is not null && download.DeclaredContentLength != expectedSize
            || download.Contents.LongLength != expectedSize
            || !FixedTimeEquals(Convert.ToHexString(SHA256.HashData(download.Contents)), expectedSha256))
        {
            throw new InvalidDataException($"The downloaded {subject} does not match reviewed release metadata.");
        }
    }

    private static FileStream CreateOwnedWrite(string path)
    {
        return OperatingSystem.IsWindows()
            ? new FileStream(CandidateFileNative.CreateWriteDelete(path), FileAccess.Write, 81920, isAsync: true)
            : new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                81920,
                FileOptions.Asynchronous | FileOptions.WriteThrough);
    }

    private async Task WriteOwnedAsync(
        FileStream stream,
        string path,
        byte[] contents,
        CancellationToken cancellationToken)
    {
        if (afterCandidateFileOpened is not null)
        {
            await afterCandidateFileOpened(path, cancellationToken).ConfigureAwait(false);
        }
        if (afterCandidatePartialWrite is not null && contents.Length > 1)
        {
            var firstLength = contents.Length / 2;
            await stream.WriteAsync(contents.AsMemory(0, firstLength), cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
            await afterCandidatePartialWrite(path, cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync(contents.AsMemory(firstLength), cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await stream.WriteAsync(contents, cancellationToken).ConfigureAwait(false);
        }
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }

    private static FileStream OpenLockedRead(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);
        }
        var handle = CandidateFileNative.OpenReadDelete(path);
        return new(handle, FileAccess.Read, 81920, isAsync: false);
    }

    internal static FileStream OpenLockedReadForRecovery(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return OpenLockedRead(path);
        }
        return new(CandidateFileNative.OpenRecoveryReadDeleteNoFollow(path), FileAccess.Read, 81920, isAsync: false);
    }

    private static void VerifyLockedStream(
        FileStream stream,
        long expectedSize,
        string expectedSha256,
        string subject)
    {
        if (stream.Length != expectedSize)
        {
            throw new InvalidDataException($"The locked {subject} does not match reviewed release metadata.");
        }
        stream.Position = 0;
        var hash = Convert.ToHexString(SHA256.HashData(stream));
        stream.Position = 0;
        if (!FixedTimeEquals(hash, expectedSha256))
        {
            throw new InvalidDataException($"The locked {subject} does not match reviewed release metadata.");
        }
    }

    private async ValueTask DeleteLockedStreamAsync(FileStream stream, string path)
    {
        if (OperatingSystem.IsWindows() && !markCandidateDelete(stream.SafeFileHandle))
        {
            throw new IOException("The exact reviewed candidate file could not be marked for deletion.");
        }
        await stream.DisposeAsync().ConfigureAwait(false);
        if (!OperatingSystem.IsWindows())
        {
            File.Delete(path);
        }
    }

    private static void DeleteCandidateDirectory(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }
        try
        {
            Directory.Delete(directory, recursive: false);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static bool FixedTimeEquals(string left, string right)
    {
        if (left.Length != 64 || right.Length != 64 || !left.All(Uri.IsHexDigit) || !right.All(Uri.IsHexDigit))
        {
            return false;
        }
        return CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(left),
            Convert.FromHexString(right));
    }
}

internal static class CandidateFileNative
{
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint Delete = 0x00010000;
    private const uint WriteDac = 0x00040000;
    private const uint WriteOwner = 0x00080000;
    private const uint FileFlagOverlapped = 0x40000000;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileAttributeReparsePoint = 0x00000400;

    public static SafeFileHandle OpenReadDelete(string path)
    {
        var handle = CreateFile(
            path,
            GenericRead | Delete,
            FileShare.Read,
            IntPtr.Zero,
            FileMode.Open,
            0,
            IntPtr.Zero);
        return handle.IsInvalid
            ? throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not lock the reviewed candidate file.")
            : handle;
    }

    public static SafeFileHandle CreateWriteDelete(string path)
    {
        var handle = CreateFile(
            path,
            GenericWrite | Delete,
            FileShare.None,
            IntPtr.Zero,
            FileMode.CreateNew,
            FileFlagOverlapped,
            IntPtr.Zero);
        return handle.IsInvalid
            ? throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create the reviewed candidate file.")
            : handle;
    }

    public static SafeFileHandle CreateReadWriteDelete(string path)
    {
        var handle = CreateFile(
            path,
            GenericRead | GenericWrite | Delete | WriteDac | WriteOwner,
            FileShare.Read,
            IntPtr.Zero,
            FileMode.CreateNew,
            FileFlagOverlapped,
            IntPtr.Zero);
        return handle.IsInvalid
            ? throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create the Battle credential file.")
            : handle;
    }

    public static SafeFileHandle OpenRecoveryReadNoFollow(string path) => OpenNoFollow(
        path,
        GenericRead,
        FileShare.Read,
        flags: FileFlagOpenReparsePoint,
        "Could not lock the reviewed candidate recovery member.");

    public static SafeFileHandle OpenRecoveryReadDeleteNoFollow(string path) => OpenNoFollow(
        path,
        GenericRead | Delete,
        FileShare.Read,
        flags: FileFlagOpenReparsePoint,
        "Could not lock the reviewed candidate recovery member for deletion.");

    public static SafeFileHandle OpenRecoveryDirectoryReadNoFollow(string path) => OpenNoFollow(
        path,
        GenericRead,
        FileShare.Read,
        flags: FileFlagBackupSemantics | FileFlagOpenReparsePoint,
        "Could not lock the reviewed candidate recovery root.");

    public static SafeFileHandle OpenRecoveryDirectoryReadDeleteNoFollow(string path) => OpenNoFollow(
        path,
        GenericRead | Delete,
        FileShare.Read,
        flags: FileFlagBackupSemantics | FileFlagOpenReparsePoint,
        "Could not lock the reviewed candidate recovery directory.");

    private static SafeFileHandle OpenNoFollow(
        string path,
        uint desiredAccess,
        FileShare shareMode,
        uint flags,
        string errorMessage)
    {
        var handle = CreateFile(
            path,
            desiredAccess,
            shareMode,
            IntPtr.Zero,
            FileMode.Open,
            flags,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), errorMessage);
        }
        if (!GetFileInformationByHandle(handle, out var information))
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error, "Could not validate the reviewed candidate recovery handle.");
        }
        if ((information.FileAttributes & FileAttributeReparsePoint) != 0)
        {
            handle.Dispose();
            throw new InvalidDataException("Reviewed candidate recovery refuses filesystem links or reparse points.");
        }
        return handle;
    }

    public static bool TryMarkDeleteOnClose(SafeFileHandle handle)
    {
        var disposition = new FileDispositionInfo { DeleteFile = 1 };
        return SetFileInformationByHandle(
            handle,
            FileInfoByHandleClass.FileDispositionInfo,
            ref disposition,
            Marshal.SizeOf<FileDispositionInfo>());
    }

    public static CandidateFileIdentity ReadIdentity(SafeFileHandle handle)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Reviewed candidate file identity requires Windows.");
        }
        if (!GetFileInformationByHandle(handle, out var information))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not read the reviewed candidate file identity.");
        }
        var fileIndex = ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow;
        return new(
            information.VolumeSerialNumber.ToString("X8", CultureInfo.InvariantCulture),
            fileIndex.ToString("X16", CultureInfo.InvariantCulture));
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        FileShare shareMode,
        IntPtr securityAttributes,
        FileMode creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(
        SafeFileHandle file,
        FileInfoByHandleClass fileInformationClass,
        ref FileDispositionInfo fileInformation,
        int bufferSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle file,
        out ByHandleFileInformation fileInformation);

    private enum FileInfoByHandleClass
    {
        FileDispositionInfo = 4,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileDispositionInfo
    {
        public byte DeleteFile;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

}
