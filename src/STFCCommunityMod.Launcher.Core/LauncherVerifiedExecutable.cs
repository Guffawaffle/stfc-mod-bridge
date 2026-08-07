using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;

namespace STFCCommunityMod.Launcher.Core;

public static class LauncherVerifiedExecutable
{
    private const long MaximumExecutableBytes = 256L * 1024L * 1024L;

    [SupportedOSPlatform("windows")]
    public static Process Start(LauncherUpdateBoundFile executable, ProcessStartInfo startInfo)
    {
        var verifier = new WindowsAuthenticodeVerifier(
            LauncherSelfUpdateAuthority.WindowsArtifactPublisher,
            LauncherSelfUpdateAuthority.WindowsArtifactSigningIdentityEku);
        return Start(executable, startInfo, verifier, Process.Start);
    }

    internal static Process Start(
        LauncherUpdateBoundFile executable,
        ProcessStartInfo startInfo,
        IModArtifactAuthenticityVerifier authenticityVerifier,
        Func<ProcessStartInfo, Process?> processStarter)
    {
        ArgumentNullException.ThrowIfNull(executable);
        ArgumentNullException.ThrowIfNull(startInfo);
        ArgumentNullException.ThrowIfNull(authenticityVerifier);
        ArgumentNullException.ThrowIfNull(processStarter);
        var executablePath = Path.GetFullPath(executable.Path);
        if (startInfo.UseShellExecute
            || !PathEquals(startInfo.FileName, executablePath)
            || executable.Size is <= 0 or > MaximumExecutableBytes
            || !ReleaseSelectionAttestationPolicy.Sha256Pattern().IsMatch(executable.Sha256))
        {
            throw new InvalidDataException("The verified executable launch identity is invalid.");
        }

        using var executableLock = new FileStream(
            executablePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.SequentialScan);
        if (executableLock.Length != executable.Size
            || (File.GetAttributes(executablePath) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("The verified executable changed before launch.");
        }
        var digest = Convert.ToHexString(SHA256.HashData(executableLock)).ToLowerInvariant();
        if (!AuthenticatedReleaseManifestPolicy.FixedTimeDigestEquals(digest, executable.Sha256))
        {
            throw new InvalidDataException("The verified executable digest changed before launch.");
        }
        var authenticity = authenticityVerifier.Verify(executablePath);
        if (!authenticity.IsTrusted)
        {
            throw new InvalidDataException(
                $"The verified executable Authenticode policy failed: {authenticity.Message}");
        }

        return processStarter(startInfo)
            ?? throw new InvalidOperationException("Windows did not start the verified executable.");
    }

    private static bool PathEquals(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(left))
        {
            return false;
        }
        return string.Equals(Path.GetFullPath(left), right, StringComparison.OrdinalIgnoreCase);
    }
}
