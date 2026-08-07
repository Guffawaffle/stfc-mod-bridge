using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace STFCCommunityMod.Launcher.Core;

internal sealed record LauncherUpdateCompletionJournal(
    int SchemaVersion,
    string TransactionId,
    string StateRoot,
    string TargetDirectory,
    string LauncherRelativePath,
    string ReleaseVerifierRelativePath,
    string RecoveryJournalSha256,
    string LauncherSha256,
    string ReleaseVerifierSha256,
    IReadOnlyList<LauncherUpdateFile> Files);

internal static class LauncherUpdateCompletionJournalStore
{
    internal const string FileName = "completion.journal.dpapi";
    private const int SchemaVersion = 1;
    private const int MaximumProtectedBytes = 256 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        MaxDepth = 16,
        PropertyNameCaseInsensitive = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static string Create(
        LauncherUpdatePlan plan,
        string recoveryJournalSha256,
        ILauncherUpdateRecoveryJournalProtector protector)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(protector);
        var launcher = RequireFile(plan.Files, plan.LauncherRelativePath);
        var verifier = RequireFile(plan.Files, plan.ReleaseVerifierRelativePath);
        if (!AuthenticatedReleaseManifestPolicy.FixedTimeDigestEquals(
                launcher.Sha256,
                plan.CandidateLauncher.Sha256)
            || !AuthenticatedReleaseManifestPolicy.FixedTimeDigestEquals(
                verifier.Sha256,
                plan.CandidateReleaseVerifier.Sha256)
            || launcher.Size != plan.CandidateLauncher.Size
            || verifier.Size != plan.CandidateReleaseVerifier.Size)
        {
            throw new InvalidDataException("The completion inventory does not match the verified candidate authority.");
        }
        var journal = new LauncherUpdateCompletionJournal(
            SchemaVersion,
            plan.TransactionId,
            Path.GetFullPath(plan.StateRoot),
            Path.GetFullPath(plan.TargetDirectory),
            plan.LauncherRelativePath,
            plan.ReleaseVerifierRelativePath,
            recoveryJournalSha256.ToLowerInvariant(),
            launcher.Sha256.ToLowerInvariant(),
            verifier.Sha256.ToLowerInvariant(),
            plan.Files);
        var protectedBytes = protector.Protect(JsonSerializer.SerializeToUtf8Bytes(journal, JsonOptions));
        if (protectedBytes.LongLength is <= 0 or > MaximumProtectedBytes)
        {
            throw new InvalidDataException("The protected completion journal is outside its size bound.");
        }
        var path = Path.Combine(Path.GetDirectoryName(plan.RunnerUpdater.Path)!, FileName);
        try
        {
            WriteDurably(path, protectedBytes);
            _ = Load(path, protector);
            return path;
        }
        catch
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
            throw;
        }
    }

    public static LauncherUpdateCompletionJournal Load(
        string path,
        ILauncherUpdateRecoveryJournalProtector protector)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(protector);
        var fullPath = Path.GetFullPath(path);
        var info = new FileInfo(fullPath);
        if (!info.Exists
            || info.Length is <= 0 or > MaximumProtectedBytes
            || (info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("The protected completion journal is missing or unsafe.");
        }
        var protectedBytes = File.ReadAllBytes(fullPath);
        if (protectedBytes.LongLength != info.Length)
        {
            throw new InvalidDataException("The protected completion journal changed while it was read.");
        }
        byte[] bytes;
        try
        {
            bytes = protector.Unprotect(protectedBytes);
        }
        catch (CryptographicException exception)
        {
            throw new InvalidDataException("The completion journal failed current-user protection validation.", exception);
        }
        RejectDuplicateProperties(bytes);
        try
        {
            var journal = JsonSerializer.Deserialize<LauncherUpdateCompletionJournal>(bytes, JsonOptions)
                ?? throw new InvalidDataException("The completion journal is empty.");
            Validate(journal, fullPath);
            return journal;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The completion journal is outside its closed schema.", exception);
        }
    }

    private static LauncherUpdateFile RequireFile(
        IReadOnlyList<LauncherUpdateFile> files,
        string relativePath)
    {
        var matches = files
            .Where(file => string.Equals(file.RelativePath, relativePath, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return matches.Length == 1
            ? matches[0]
            : throw new InvalidDataException("The completion inventory does not contain each installed authority exactly once.");
    }

    private static void Validate(LauncherUpdateCompletionJournal journal, string journalPath)
    {
        var transactionRoot = Path.GetDirectoryName(journalPath)!;
        if (journal.Files is null
            || string.IsNullOrWhiteSpace(journal.StateRoot)
            || string.IsNullOrWhiteSpace(journal.TargetDirectory)
            || journal.SchemaVersion != SchemaVersion
            || !Guid.TryParseExact(journal.TransactionId, "N", out _)
            || !PathEquals(transactionRoot, Path.Combine(journal.StateRoot, "self-update", journal.TransactionId))
            || journal.LauncherRelativePath != ModBridgeProductIdentity.ExecutableName
            || journal.ReleaseVerifierRelativePath != ModBridgeProductIdentity.ReleaseVerifierExecutableName
            || !ReleaseSelectionAttestationPolicy.Sha256Pattern().IsMatch(journal.RecoveryJournalSha256)
            || !ReleaseSelectionAttestationPolicy.Sha256Pattern().IsMatch(journal.LauncherSha256)
            || !ReleaseSelectionAttestationPolicy.Sha256Pattern().IsMatch(journal.ReleaseVerifierSha256))
        {
            throw new InvalidDataException("The completion journal identity or fixed paths are invalid.");
        }
        LauncherUpdateTransactionSecurity.ValidatePayloadRecords(journal.Files, requireFiles: true);
        var launcher = RequireFile(journal.Files, journal.LauncherRelativePath);
        var verifier = RequireFile(journal.Files, journal.ReleaseVerifierRelativePath);
        if (!AuthenticatedReleaseManifestPolicy.FixedTimeDigestEquals(launcher.Sha256, journal.LauncherSha256)
            || !AuthenticatedReleaseManifestPolicy.FixedTimeDigestEquals(
                verifier.Sha256,
                journal.ReleaseVerifierSha256))
        {
            throw new InvalidDataException("The completion journal does not bind its installed launcher/verifier authority.");
        }
    }

    private static void WriteDurably(string path, byte[] contents)
    {
        var temporaryPath = path + ".tmp";
        try
        {
            using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.WriteThrough))
            {
                stream.Write(contents);
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
            throw new InvalidDataException("The completion journal is not bounded canonical JSON.", exception);
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
                        $"The completion journal contains duplicate property '{property.Name}'.");
                }
                RejectDuplicateProperties(property.Value);
            }
            return;
        }
        if (element.ValueKind == JsonValueKind.Array)
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
