using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace STFCCommunityMod.Launcher.Core;

public enum LauncherDiagnosticLevel
{
    Healthy,
    Attention,
    Unavailable,
}

public sealed record LauncherDiagnosticFact(
    string Name,
    LauncherDiagnosticLevel Level,
    string Summary,
    string NextAction);

public sealed record LauncherDiagnosticDocument(
    int SchemaVersion,
    DateTimeOffset GeneratedAtUtc,
    string LauncherVersion,
    string OperatingSystem,
    IReadOnlyList<LauncherDiagnosticFact> Health,
    IReadOnlyList<string> RecentModLog);

public sealed record LauncherDiagnosticPreview(
    LauncherDiagnosticDocument Document,
    string RedactedJson);

public sealed partial class LauncherDiagnosticRedactor
{
    private readonly IReadOnlyList<(string Value, string Replacement)> pathReplacements;

    public LauncherDiagnosticRedactor(string? userProfilePath, string? gameDirectory)
    {
        var replacements = new List<(string Value, string Replacement)>();
        AddPath(replacements, gameDirectory, "%GAME_DIR%");
        AddPath(replacements, userProfilePath, "%USERPROFILE%");
        pathReplacements = replacements;
    }

    public string Redact(string input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var redacted = input;
        foreach (var (value, replacement) in pathReplacements)
        {
            redacted = redacted.Replace(value, replacement, StringComparison.OrdinalIgnoreCase);
            redacted = redacted.Replace(
                value.Replace('\\', '/'),
                replacement,
                StringComparison.OrdinalIgnoreCase);
        }

        redacted = UserProfilePattern().Replace(redacted, "%USERPROFILE%");
        redacted = AuthorizationBearerPattern().Replace(redacted, "Authorization: Bearer <redacted>");
        redacted = SensitiveAssignmentPattern().Replace(redacted, match => $"{match.Groups[1].Value}=<redacted>");
        redacted = EndpointPattern().Replace(redacted, "<redacted-endpoint>");
        return redacted;
    }

    private static void AddPath(List<(string Value, string Replacement)> replacements, string? path, string replacement)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }
        try
        {
            replacements.Add((Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)), replacement));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            // Invalid paths are never added to the diagnostic output.
        }
    }

    [GeneratedRegex(@"(?i)\bC:\\Users\\[^\\\s\""']+")]
    private static partial Regex UserProfilePattern();

    [GeneratedRegex(
        @"(?i)\b(token|access[_-]?token|api[_-]?key|password|secret|cookie)\b\s*[:=]\s*(?:\""[^\""\r\n]*\""|'[^'\r\n]*'|[^\s,;]+)")]
    private static partial Regex SensitiveAssignmentPattern();

    [GeneratedRegex(@"(?i)\bauthorization\s*:\s*bearer\s+[^\s,;]+")]
    private static partial Regex AuthorizationBearerPattern();

    [GeneratedRegex(@"(?i)https?://[^\s\""'<>]+")]
    private static partial Regex EndpointPattern();
}

public sealed class LauncherDiagnosticService(
    ModDeploymentService deploymentService,
    IOfficialLauncherService officialLauncherService,
    IGameProcessInspector gameProcessInspector,
    string launcherVersion,
    TimeProvider? timeProvider = null)
{
    private const int MaximumConfigBytes = 4 * 1024 * 1024;
    private const int MaximumLogTailBytes = 64 * 1024;
    private const long MaximumModArtifactBytes = 128L * 1024L * 1024L;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };
    private readonly TimeProvider timeProvider = timeProvider ?? TimeProvider.System;

    public LauncherDiagnosticPreview BuildPreview(string? gameDirectory)
    {
        var health = new List<LauncherDiagnosticFact>();
        string? validGameDirectory = null;
        if (string.IsNullOrWhiteSpace(gameDirectory))
        {
            health.Add(Attention("Game folder", "No game folder is selected.", "Select the folder containing prime.exe."));
        }
        else
        {
            var validation = GameInstallValidator.Validate(gameDirectory);
            if (validation.IsValid)
            {
                validGameDirectory = validation.GameDirectory;
                health.Add(Healthy(
                    "Game folder",
                    "The selected folder contains prime.exe. Transactional write access is checked again before mutation."));
            }
            else
            {
                health.Add(Attention("Game folder", validation.Message, "Select the folder containing prime.exe."));
            }
        }

        health.Add(gameProcessInspector.IsGameRunning()
            ? Attention("Game process", "Star Trek Fleet Command is running.", "Close the game before repair or removal.")
            : Healthy("Game process", "Star Trek Fleet Command is not running."));
        health.Add(officialLauncherService.IsAvailable
            ? Healthy("Scopely launcher", "The supported per-user Scopely launcher is available.")
            : Attention(
                "Scopely launcher",
                "The supported per-user Scopely launcher is unavailable.",
                "Install or repair the Scopely launcher."));

        AddDeploymentFacts(health, validGameDirectory);
        AddConfigurationFact(health, validGameDirectory);
        var recentLog = ReadRecentModLog(health, validGameDirectory);

        var document = new LauncherDiagnosticDocument(
            1,
            timeProvider.GetUtcNow(),
            launcherVersion,
            Environment.OSVersion.VersionString,
            health,
            recentLog);
        var rawJson = JsonSerializer.Serialize(document, JsonOptions);
        var redactor = new LauncherDiagnosticRedactor(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            validGameDirectory);
        return new(document, redactor.Redact(rawJson));
    }

    public static async Task ExportAsync(
        LauncherDiagnosticPreview preview,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preview);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        var fullPath = Path.GetFullPath(outputPath);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException("The diagnostic export path has no parent directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(temporaryPath, preview.RedactedJson, new UTF8Encoding(false), cancellationToken);
            File.Move(temporaryPath, fullPath, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private void AddDeploymentFacts(List<LauncherDiagnosticFact> health, string? gameDirectory)
    {
        try
        {
            var journal = deploymentService.ReadJournal();
            if (journal is not null
                && journal.Phase is not (ModDeploymentPhase.Committed
                    or ModDeploymentPhase.RolledBack
                    or ModDeploymentPhase.Failed))
            {
                health.Add(Attention(
                    "Deployment transaction",
                    $"An incomplete {journal.Operation} transaction is in {journal.Phase}.",
                    "Use Recover before another mod operation."));
            }
            else
            {
                health.Add(Healthy(
                    "Deployment transaction",
                    journal is null ? "No deployment transaction is recorded." : $"The last transaction is {journal.Phase}."));
            }

            var state = deploymentService.ReadInstalledState();
            if (state is null)
            {
                health.Add(Attention("Community mod", "No Mod Control-managed mod is installed.", "Install or adopt the mod."));
                return;
            }
            if (gameDirectory is null || !PathEquals(state.GameDirectory, gameDirectory))
            {
                health.Add(Attention(
                    "Community mod",
                    "Mod Control-managed state belongs to a different or unavailable game folder.",
                    "Select the managed game folder or review removal with support."));
                return;
            }

            var targetPath = Path.Combine(gameDirectory, "version.dll");
            if (!File.Exists(targetPath))
            {
                health.Add(Attention("Community mod", "The managed version.dll is missing.", "Use Repair."));
                return;
            }
            if (new FileInfo(targetPath).Length > MaximumModArtifactBytes)
            {
                health.Add(Attention(
                    "Community mod",
                    "version.dll exceeds the Mod Control verification limit.",
                    "Use Repair; Mod Control will not load the oversized file into diagnostics."));
                return;
            }
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(targetPath)));
            health.Add(string.Equals(hash, state.Sha256, StringComparison.OrdinalIgnoreCase)
                ? Healthy("Community mod", $"Mod Control-managed version {state.Version} matches its SHA-256 identity.")
                : Attention(
                    "Community mod",
                    "version.dll differs from Mod Control-managed state.",
                    "Use Repair; Mod Control will not delete an unknown artifact."));
        }
        catch (Exception exception) when (
            exception is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            health.Add(Unavailable("Deployment state", exception.Message, "Preview diagnostics and request support."));
        }
    }

    private static void AddConfigurationFact(List<LauncherDiagnosticFact> health, string? gameDirectory)
    {
        if (gameDirectory is null)
        {
            health.Add(Unavailable("Configuration", "Configuration cannot be checked without a valid game folder.", "Select a game folder."));
            return;
        }
        var path = Path.Combine(gameDirectory, "community_patch_settings.toml");
        if (!File.Exists(path))
        {
            health.Add(Healthy("Configuration", "No override TOML exists; runtime defaults apply."));
            return;
        }
        try
        {
            var info = new FileInfo(path);
            if (info.Length > MaximumConfigBytes)
            {
                health.Add(Attention("Configuration", "The TOML exceeds the diagnostic parser limit.", "Open Settings and review the file."));
                return;
            }
            var load = SparseTomlDocument.Load(File.ReadAllBytes(path), out var document);
            var validation = load.Error ?? document?.ValidateForMutation().Error;
            health.Add(validation is null
                ? Healthy("Configuration", "The override TOML is valid UTF-8 and structurally editable.")
                : Attention("Configuration", validation.Message, "Open Settings and correct the reported TOML structure."));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            health.Add(Unavailable("Configuration", exception.Message, "Check file access and retry."));
        }
    }

    private static string[] ReadRecentModLog(
        List<LauncherDiagnosticFact> health,
        string? gameDirectory)
    {
        if (gameDirectory is null)
        {
            health.Add(Unavailable("Recent mod log", "A log location cannot be resolved without a game folder.", "Select a game folder."));
            return [];
        }
        var path = Path.Combine(gameDirectory, "community_patch.log");
        if (!File.Exists(path))
        {
            health.Add(Attention("Recent mod log", "community_patch.log is absent.", "Launch the game once, then refresh diagnostics."));
            return [];
        }
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            stream.Seek(-Math.Min(stream.Length, MaximumLogTailBytes), SeekOrigin.End);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var text = reader.ReadToEnd();
            var redactor = new LauncherDiagnosticRedactor(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                gameDirectory);
            var lines = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .TakeLast(200)
                .Select(redactor.Redact)
                .ToArray();
            health.Add(Healthy("Recent mod log", $"Included {lines.Length} redacted recent log lines."));
            return lines;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            health.Add(Unavailable("Recent mod log", exception.Message, "Close other tools holding the log and retry."));
            return [];
        }
    }

    private static LauncherDiagnosticFact Healthy(string name, string summary) =>
        new(name, LauncherDiagnosticLevel.Healthy, summary, "No action needed.");

    private static LauncherDiagnosticFact Attention(string name, string summary, string nextAction) =>
        new(name, LauncherDiagnosticLevel.Attention, summary, nextAction);

    private static LauncherDiagnosticFact Unavailable(string name, string summary, string nextAction) =>
        new(name, LauncherDiagnosticLevel.Unavailable, summary, nextAction);

    private static bool PathEquals(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
