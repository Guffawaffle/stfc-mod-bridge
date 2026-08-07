using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace STFCCommunityMod.Launcher.Core;

internal sealed record LauncherUpdateRecoveryJournal(
    int SchemaVersion,
    string TransactionId,
    string StateRoot,
    string TargetDirectory,
    string BackupDirectory,
    string LauncherRelativePath,
    string ReleaseVerifierRelativePath,
    LauncherUpdateBoundFile RunnerUpdater,
    string LauncherSha256,
    string ReleaseVerifierSha256,
    IReadOnlyList<LauncherUpdateFile> PreviousFiles);

internal interface ILauncherUpdateRecoveryJournalProtector
{
    byte[] Protect(byte[] contents);

    byte[] Unprotect(byte[] protectedContents);
}

[SupportedOSPlatform("windows")]
internal sealed class WindowsDpapiLauncherUpdateRecoveryJournalProtector
    : ILauncherUpdateRecoveryJournalProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("STFC Mod Bridge self-update recovery v1");

    public byte[] Protect(byte[] contents)
    {
        ArgumentNullException.ThrowIfNull(contents);
        return ProtectedData.Protect(contents, Entropy, DataProtectionScope.CurrentUser);
    }

    public byte[] Unprotect(byte[] protectedContents)
    {
        ArgumentNullException.ThrowIfNull(protectedContents);
        return ProtectedData.Unprotect(protectedContents, Entropy, DataProtectionScope.CurrentUser);
    }
}

internal static class LauncherUpdateRecoveryJournalStore
{
    internal const string FileName = "recovery.journal.dpapi";
    private const int SchemaVersion = 1;
    private const int MaximumProtectedBytes = 256 * 1024;
    private const long MaximumRunnerBytes = 256L * 1024L * 1024L;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        MaxDepth = 16,
        PropertyNameCaseInsensitive = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static string Create(
        LauncherUpdatePlan plan,
        ILauncherUpdateRecoveryJournalProtector protector)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(protector);
        var launcher = RequirePreviousFile(plan, plan.LauncherRelativePath);
        var verifier = RequirePreviousFile(plan, plan.ReleaseVerifierRelativePath);
        if (!AuthenticatedReleaseManifestPolicy.FixedTimeDigestEquals(
                launcher.Sha256,
                plan.CurrentLauncher.Sha256)
            || !AuthenticatedReleaseManifestPolicy.FixedTimeDigestEquals(
                verifier.Sha256,
                plan.CurrentReleaseVerifier.Sha256))
        {
            throw new InvalidDataException("The recovery inventory does not match the verified installed authority.");
        }
        var journal = new LauncherUpdateRecoveryJournal(
            SchemaVersion,
            plan.TransactionId,
            Path.GetFullPath(plan.StateRoot),
            Path.GetFullPath(plan.TargetDirectory),
            Path.GetFullPath(plan.BackupDirectory),
            plan.LauncherRelativePath,
            plan.ReleaseVerifierRelativePath,
            plan.RunnerUpdater,
            launcher.Sha256.ToLowerInvariant(),
            verifier.Sha256.ToLowerInvariant(),
            plan.PreviousFiles);
        var protectedBytes = protector.Protect(JsonSerializer.SerializeToUtf8Bytes(journal, JsonOptions));
        if (protectedBytes.LongLength is <= 0 or > MaximumProtectedBytes)
        {
            throw new InvalidDataException("The protected recovery journal is outside its size bound.");
        }
        var path = Path.Combine(Path.GetDirectoryName(plan.RunnerUpdater.Path)!, FileName);
        WriteDurably(path, protectedBytes);
        _ = Load(path, protector);
        return path;
    }

    public static LauncherUpdateRecoveryJournal Load(
        string path,
        ILauncherUpdateRecoveryJournalProtector protector,
        string? expectedProtectedSha256 = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(protector);
        var fullPath = Path.GetFullPath(path);
        var info = new FileInfo(fullPath);
        if (!info.Exists
            || info.Length is <= 0 or > MaximumProtectedBytes
            || (info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("The protected recovery journal is missing or unsafe.");
        }
        var protectedBytes = File.ReadAllBytes(fullPath);
        if (protectedBytes.LongLength != info.Length)
        {
            throw new InvalidDataException("The protected recovery journal changed while it was read.");
        }
        var protectedSha256 = Convert.ToHexString(SHA256.HashData(protectedBytes)).ToLowerInvariant();
        if (expectedProtectedSha256 is not null
            && !AuthenticatedReleaseManifestPolicy.FixedTimeDigestEquals(
                protectedSha256,
                expectedProtectedSha256))
        {
            throw new InvalidDataException("The protected recovery journal changed after handoff.");
        }
        byte[] bytes;
        try
        {
            bytes = protector.Unprotect(protectedBytes);
        }
        catch (CryptographicException exception)
        {
            throw new InvalidDataException("The recovery journal failed current-user protection validation.", exception);
        }
        RejectDuplicateProperties(bytes);
        try
        {
            var journal = JsonSerializer.Deserialize<LauncherUpdateRecoveryJournal>(bytes, JsonOptions)
                ?? throw new InvalidDataException("The recovery journal is empty.");
            Validate(journal, fullPath);
            return journal;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The recovery journal is outside its closed schema.", exception);
        }
    }

    public static string HashProtected(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.SequentialScan);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static LauncherUpdateFile RequirePreviousFile(LauncherUpdatePlan plan, string relativePath)
    {
        var matches = plan.PreviousFiles
            .Where(file => string.Equals(file.RelativePath, relativePath, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return matches.Length == 1
            ? matches[0]
            : throw new InvalidDataException("The recovery inventory does not contain each installed authority exactly once.");
    }

    private static void Validate(LauncherUpdateRecoveryJournal journal, string journalPath)
    {
        var transactionRoot = Path.GetDirectoryName(journalPath)!;
        if (journal.RunnerUpdater is null
            || journal.PreviousFiles is null
            || string.IsNullOrWhiteSpace(journal.StateRoot)
            || string.IsNullOrWhiteSpace(journal.TargetDirectory)
            || string.IsNullOrWhiteSpace(journal.BackupDirectory)
            || journal.SchemaVersion != SchemaVersion
            || !Guid.TryParseExact(journal.TransactionId, "N", out _)
            || !PathEquals(transactionRoot, Path.Combine(journal.StateRoot, "self-update", journal.TransactionId))
            || !PathEquals(journal.BackupDirectory, Path.Combine(transactionRoot, "backup"))
            || journal.LauncherRelativePath != ModBridgeProductIdentity.ExecutableName
            || journal.ReleaseVerifierRelativePath != ModBridgeProductIdentity.ReleaseVerifierExecutableName
            || !PathEquals(
                journal.RunnerUpdater.Path,
                Path.Combine(transactionRoot, ModBridgeProductIdentity.UpdaterExecutableName))
            || !ReleaseSelectionAttestationPolicy.Sha256Pattern().IsMatch(journal.RunnerUpdater.Sha256)
            || journal.RunnerUpdater.Size is <= 0 or > MaximumRunnerBytes
            || !ReleaseSelectionAttestationPolicy.Sha256Pattern().IsMatch(journal.LauncherSha256)
            || !ReleaseSelectionAttestationPolicy.Sha256Pattern().IsMatch(journal.ReleaseVerifierSha256))
        {
            throw new InvalidDataException("The recovery journal identity or fixed paths are invalid.");
        }
        LauncherUpdateTransactionSecurity.ValidatePayloadRecords(journal.PreviousFiles, requireFiles: true);
        var launcher = journal.PreviousFiles.SingleOrDefault(file =>
            string.Equals(file.RelativePath, journal.LauncherRelativePath, StringComparison.OrdinalIgnoreCase));
        var verifier = journal.PreviousFiles.SingleOrDefault(file =>
            string.Equals(file.RelativePath, journal.ReleaseVerifierRelativePath, StringComparison.OrdinalIgnoreCase));
        if (launcher is null
            || verifier is null
            || !AuthenticatedReleaseManifestPolicy.FixedTimeDigestEquals(launcher.Sha256, journal.LauncherSha256)
            || !AuthenticatedReleaseManifestPolicy.FixedTimeDigestEquals(
                verifier.Sha256,
                journal.ReleaseVerifierSha256))
        {
            throw new InvalidDataException("The recovery journal does not bind its installed launcher/verifier authority.");
        }
    }

    private static void WriteDurably(string path, byte[] contents)
    {
        using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            64 * 1024,
            FileOptions.WriteThrough);
        stream.Write(contents);
        stream.Flush(flushToDisk: true);
    }

    private static void RejectDuplicateProperties(byte[] bytes)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 16,
            });
            RejectDuplicateProperties(document.RootElement);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The recovery journal is not bounded canonical JSON.", exception);
        }
    }

    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new InvalidDataException(
                        $"The recovery journal contains duplicate property '{property.Name}'.");
                }
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                RejectDuplicateProperties(item);
            }
        }
    }

    private static bool PathEquals(string left, string right) =>
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
}

internal sealed class LauncherUpdatePayloadLease(IReadOnlyList<FileStream> streams) : IDisposable
{
    public void Dispose()
    {
        for (var index = streams.Count - 1; index >= 0; index--)
        {
            streams[index].Dispose();
        }
    }
}

internal static class LauncherUpdatePayloadTransaction
{
    public static void CreateBackup(string sourceRoot, string backupRoot, IReadOnlyList<LauncherUpdateFile> expected)
    {
        LauncherFilesystemSafety.RejectReparsePoints(sourceRoot, "Mod Bridge recovery backup source");
        if (Directory.Exists(backupRoot))
        {
            throw new InvalidDataException("The Mod Bridge recovery backup already exists.");
        }
        Directory.CreateDirectory(backupRoot);
        try
        {
            CopyExpectedFiles(sourceRoot, backupRoot, expected, preserveLauncherUntilLast: false);
            VerifyPayload(backupRoot, expected, "recovery backup");
        }
        catch
        {
            Directory.Delete(backupRoot, recursive: true);
            throw;
        }
    }

    public static void InstallPreservingLauncher(
        string stageRoot,
        string targetRoot,
        IReadOnlyList<LauncherUpdateFile> expected,
        string launcherRelativePath) =>
        ReplacePayload(stageRoot, targetRoot, expected, launcherRelativePath, afterReplace: null);

    internal static void InstallPreservingLauncher(
        string stageRoot,
        string targetRoot,
        IReadOnlyList<LauncherUpdateFile> expected,
        string launcherRelativePath,
        Action<string> afterReplace) =>
        ReplacePayload(stageRoot, targetRoot, expected, launcherRelativePath, afterReplace);

    public static void RestorePreservingLauncher(
        string backupRoot,
        string targetRoot,
        IReadOnlyList<LauncherUpdateFile> expected,
        string launcherRelativePath) =>
        ReplacePayload(backupRoot, targetRoot, expected, launcherRelativePath, afterReplace: null);

    public static void VerifyPayload(
        string root,
        IReadOnlyList<LauncherUpdateFile> expected,
        string context)
    {
        using var payloadLease = RetainVerifiedPayload(root, expected, context);
    }

    public static LauncherUpdatePayloadLease RetainVerifiedPayload(
        string root,
        IReadOnlyList<LauncherUpdateFile> expected,
        string context)
    {
        ArgumentNullException.ThrowIfNull(expected);
        LauncherUpdateTransactionSecurity.ValidatePayloadRecords(expected, requireFiles: true);
        LauncherFilesystemSafety.RejectReparsePoints(root, $"Mod Bridge {context}");
        var expectedPaths = expected
            .Select(file => NormalizeRelativePath(file.RelativePath))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var actualPaths = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(path => NormalizeRelativePath(Path.GetRelativePath(root, path)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!actualPaths.SetEquals(expectedPaths))
        {
            throw new InvalidDataException($"The Mod Bridge {context} file identity changed.");
        }
        var streams = new List<FileStream>(expected.Count);
        try
        {
            foreach (var expectedFile in expected.OrderBy(file => file.RelativePath, StringComparer.Ordinal))
            {
                var path = ResolveContainedPath(root, expectedFile.RelativePath);
                var stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    64 * 1024,
                    FileOptions.SequentialScan);
                streams.Add(stream);
                if (stream.Length != expectedFile.Size
                    || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException($"The Mod Bridge {context} file identity changed.");
                }
                var digest = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
                if (!AuthenticatedReleaseManifestPolicy.FixedTimeDigestEquals(digest, expectedFile.Sha256))
                {
                    throw new InvalidDataException($"The Mod Bridge {context} failed verification.");
                }
            }
            actualPaths = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Select(path => NormalizeRelativePath(Path.GetRelativePath(root, path)))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!actualPaths.SetEquals(expectedPaths))
            {
                throw new InvalidDataException($"The Mod Bridge {context} file identity changed.");
            }
            return new LauncherUpdatePayloadLease(streams);
        }
        catch
        {
            for (var index = streams.Count - 1; index >= 0; index--)
            {
                streams[index].Dispose();
            }
            throw;
        }
    }

    private static void ReplacePayload(
        string sourceRoot,
        string targetRoot,
        IReadOnlyList<LauncherUpdateFile> expected,
        string launcherRelativePath,
        Action<string>? afterReplace)
    {
        LauncherFilesystemSafety.RejectReparsePoints(sourceRoot, "Mod Bridge update source");
        if (Directory.Exists(targetRoot))
        {
            LauncherFilesystemSafety.RejectReparsePoints(targetRoot, "Mod Bridge update target");
        }
        else
        {
            Directory.CreateDirectory(targetRoot);
        }
        CopyExpectedFiles(
            sourceRoot,
            targetRoot,
            expected,
            preserveLauncherUntilLast: true,
            launcherRelativePath,
            afterReplace);
        var expectedPaths = expected
            .Select(file => NormalizeRelativePath(file.RelativePath))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFiles(targetRoot, "*", SearchOption.AllDirectories))
        {
            var relative = NormalizeRelativePath(Path.GetRelativePath(targetRoot, path));
            if (!expectedPaths.Contains(relative))
            {
                File.Delete(path);
            }
        }
        VerifyPayload(targetRoot, expected, "installed payload");
    }

    private static void CopyExpectedFiles(
        string sourceRoot,
        string destinationRoot,
        IReadOnlyList<LauncherUpdateFile> expected,
        bool preserveLauncherUntilLast,
        string? launcherRelativePath = null,
        Action<string>? afterReplace = null)
    {
        IEnumerable<LauncherUpdateFile> ordered = preserveLauncherUntilLast
            ? expected.OrderBy(file =>
                string.Equals(file.RelativePath, launcherRelativePath, StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            : expected;
        foreach (var file in ordered)
        {
            var source = ResolveContainedPath(sourceRoot, file.RelativePath);
            var destination = ResolveContainedPath(destinationRoot, file.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            var temporary = destination + $".{Guid.NewGuid():N}.stfc-update";
            try
            {
                using (var input = new FileStream(
                    source,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    64 * 1024,
                    FileOptions.SequentialScan))
                using (var output = new FileStream(
                    temporary,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    64 * 1024,
                    FileOptions.WriteThrough))
                {
                    input.CopyTo(output);
                    output.Flush(flushToDisk: true);
                }
                VerifyFile(temporary, file);
                File.Move(temporary, destination, overwrite: true);
                afterReplace?.Invoke(file.RelativePath);
            }
            finally
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
        }
    }

    private static string ResolveContainedPath(string root, string relativePath)
    {
        var fullRoot = Path.GetFullPath(root);
        var path = Path.GetFullPath(Path.Combine(fullRoot, relativePath));
        if (!path.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The update payload path escaped its reviewed root.");
        }
        return path;
    }

    private static string NormalizeRelativePath(string path) => path.Replace('\\', '/');

    private static void VerifyFile(string path, LauncherUpdateFile expected)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length != expected.Size || (info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("A durable update temporary changed size or identity.");
        }
        var digest = HashFile(path);
        if (!AuthenticatedReleaseManifestPolicy.FixedTimeDigestEquals(digest, expected.Sha256))
        {
            throw new InvalidDataException("A durable update temporary failed its bound hash check.");
        }
    }

    private static string HashFile(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.SequentialScan);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
