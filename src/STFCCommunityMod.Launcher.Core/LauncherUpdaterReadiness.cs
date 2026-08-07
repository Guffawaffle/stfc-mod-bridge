using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace STFCCommunityMod.Launcher.Core;

internal static class LauncherUpdaterReadiness
{
    internal const string FileName = "updater.ready";
    private const int MaximumReadyBytes = 128;

    public static void Publish(string path, string planSha256)
    {
        Validate(path, planSha256);
        var bytes = Encoding.ASCII.GetBytes(planSha256);
        var temporaryPath = path + ".tmp";
        try
        {
            using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, path, overwrite: false);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public static void WaitForReady(
        Process updater,
        string path,
        string planSha256,
        TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(updater);
        Validate(path, planSha256);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            if (File.Exists(path))
            {
                var info = new FileInfo(path);
                if (info.Length is <= 0 or > MaximumReadyBytes
                    || (info.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException("The updater readiness acknowledgement is invalid.");
                }
                var bytes = File.ReadAllBytes(path);
                if (bytes.LongLength != info.Length
                    || !CryptographicOperations.FixedTimeEquals(bytes, Encoding.ASCII.GetBytes(planSha256)))
                {
                    throw new InvalidDataException("The updater readiness acknowledgement does not match this plan.");
                }
                updater.Refresh();
                if (updater.HasExited)
                {
                    throw new InvalidOperationException(
                        "The update helper exited after acknowledging its retained plan.");
                }
                return;
            }
            updater.Refresh();
            if (updater.HasExited)
            {
                throw new InvalidOperationException("The update helper exited before retaining its authenticated plan.");
            }
            Thread.Sleep(25);
        }
        throw new TimeoutException("The update helper did not retain its authenticated plan before the readiness timeout.");
    }

    private static void Validate(string path, string planSha256)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(planSha256);
        if (Path.GetFileName(path) != FileName
            || !ReleaseSelectionAttestationPolicy.Sha256Pattern().IsMatch(planSha256))
        {
            throw new InvalidDataException("The updater readiness identity is invalid.");
        }
    }
}
