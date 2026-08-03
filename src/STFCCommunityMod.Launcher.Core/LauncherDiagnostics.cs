using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace STFCCommunityMod.Launcher.Core;

public enum LauncherDiagnosticLevel
{
    Healthy,
    Attention,
    Error,
    Unavailable,
    Informational,
}

public sealed record LauncherDiagnosticFact(
    string Name,
    LauncherDiagnosticLevel Level,
    string Summary,
    string NextAction,
    string Id = "",
    string EvidenceSource = "launcher-local",
    string? TechnicalDetail = null)
{
    public string StatusLabel => Level switch
    {
        LauncherDiagnosticLevel.Healthy => "Healthy",
        LauncherDiagnosticLevel.Attention => "Needs attention",
        LauncherDiagnosticLevel.Error => "Error",
        LauncherDiagnosticLevel.Informational => "Informational",
        _ => "Unknown",
    };

    public string AutomationName => $"{Name}, {StatusLabel}. {Summary}";
}

public sealed record LauncherDiagnosticDocument(
    int SchemaVersion,
    DateTimeOffset GeneratedAtUtc,
    string LauncherVersion,
    string OperatingSystem,
    IReadOnlyList<LauncherDiagnosticFact> Health,
    IReadOnlyList<string> RecentModLog);

public sealed record LauncherDiagnosticPreview(
    LauncherDiagnosticDocument Document,
    string RedactedJson,
    string RedactedSummary = "");

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
        redacted = SensitiveAssignmentPattern().Replace(redacted, RedactSensitiveAssignment);
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

    private static string RedactSensitiveAssignment(Match match)
    {
        var value = match.Groups["value"].Value;
        var replacement = value.Length >= 2
            && ((value[0] == '"' && value[^1] == '"')
                || (value[0] == '\'' && value[^1] == '\''))
                ? $"{value[0]}<redacted>{value[^1]}"
                : "<redacted>";
        return match.Groups["prefix"].Value + replacement;
    }

    [GeneratedRegex(@"(?i)\bC:\\Users\\[^\\\s\""']+")]
    private static partial Regex UserProfilePattern();

    [GeneratedRegex(
        @"(?ix)
        (?<prefix>
            [\""']?
            (?:token|access[_-]?token|refresh[_-]?token|api[_-]?key|password|passwd|secret|client[_-]?secret|cookie|set[_-]?cookie|authorization|session[_-]?(?:id|token|cookie))
            [\""']?
            \s*[:=]\s*
        )
        (?<value>
            \""(?:\\.|[^\""\\\r\n])*\""
            |
            '(?:\\.|[^'\\\r\n])*'
            |
            [^\s,;}\]]+
        )")]
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
    TimeProvider? timeProvider = null,
    LauncherConfigurationDiagnosisEvidence? configurationEvidence = null,
    string? runtimeDistributionId = null)
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

    public LauncherDiagnosticPreview BuildPreview(
        string? gameDirectory,
        LauncherHealthSnapshot? localHealth = null)
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
        health.Add(Unavailable(
            "Mod Control version and update",
            $"Installed Mod Control version {launcherVersion}. Update availability has not been checked in this local preview.",
            "Choose Check Mod Control update to perform a network-backed check.",
            "mod-control-update"));

        AddDeploymentFacts(health, validGameDirectory, localHealth?.Installation);
        AddLocalHealthFacts(health, localHealth);
        AddConfigurationFact(
            health,
            validGameDirectory,
            configurationEvidence,
            runtimeDistributionId);
        var recentLog = ReadRecentModLog(health, validGameDirectory);
        for (var index = 0; index < health.Count; index++)
        {
            if (string.IsNullOrWhiteSpace(health[index].Id))
            {
                health[index] = health[index] with { Id = StableFactId(health[index].Name) };
            }
        }

        var redactor = new LauncherDiagnosticRedactor(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            validGameDirectory);
        health = health.Select(fact => fact with
        {
            Name = redactor.Redact(fact.Name),
            Summary = redactor.Redact(fact.Summary),
            NextAction = redactor.Redact(fact.NextAction),
            EvidenceSource = redactor.Redact(fact.EvidenceSource),
            TechnicalDetail = fact.TechnicalDetail is null
                    ? null
                    : redactor.Redact(fact.TechnicalDetail),
        })
            .ToList();

        var document = new LauncherDiagnosticDocument(
            2,
            timeProvider.GetUtcNow(),
            launcherVersion,
            Environment.OSVersion.VersionString,
            health,
            recentLog);
        var rawJson = JsonSerializer.Serialize(document, JsonOptions);
        var summary = string.Join(
            Environment.NewLine,
            health.Select(fact =>
                $"{fact.Name}: {fact.StatusLabel} — {fact.Summary} Next: {fact.NextAction}"));
        return new(document, redactor.Redact(rawJson), redactor.Redact(summary));
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

    private void AddDeploymentFacts(
        List<LauncherDiagnosticFact> health,
        string? gameDirectory,
        ModInstallationEvidence? installation)
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
                health.Add(installation?.State switch
                {
                    ModInstallationEvidenceState.ManualInstallation => Informational(
                        "Managed artifact verification",
                        "A manual version.dll is present, but no Mod Control-managed SHA-256 identity is recorded.",
                        "Choose Check for updates when you want to compare or replace this installation.",
                        "managed-artifact-verification"),
                    ModInstallationEvidenceState.NotInstalled => Informational(
                        "Managed artifact verification",
                        "No managed SHA-256 identity is recorded because no community mod is installed.",
                        "No action needed.",
                        "managed-artifact-verification"),
                    _ => Unavailable(
                        "Managed artifact verification",
                        "No Mod Control-managed artifact identity is available.",
                        "Review the community mod installation check.",
                        "managed-artifact-verification"),
                });
                return;
            }
            if (gameDirectory is null || !PathEquals(state.GameDirectory, gameDirectory))
            {
                health.Add(Attention(
                    "Managed artifact verification",
                    "Mod Control-managed state belongs to a different or unavailable game folder.",
                    "Select the managed game folder or review removal with support.",
                    "managed-artifact-verification"));
                return;
            }

            var targetPath = Path.Combine(gameDirectory, "version.dll");
            if (!File.Exists(targetPath))
            {
                health.Add(Attention(
                    "Managed artifact verification",
                    "The managed version.dll is missing.",
                    "Use Repair.",
                    "managed-artifact-verification"));
                return;
            }
            if (new FileInfo(targetPath).Length > MaximumModArtifactBytes)
            {
                health.Add(Attention(
                    "Managed artifact verification",
                    "version.dll exceeds the Mod Control verification limit.",
                    "Use Repair; Mod Control will not load the oversized file into diagnostics.",
                    "managed-artifact-verification"));
                return;
            }
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(targetPath)));
            health.Add(string.Equals(hash, state.Sha256, StringComparison.OrdinalIgnoreCase)
                ? Healthy(
                    "Managed artifact verification",
                    $"Mod Control-managed version {state.Version} matches its SHA-256 identity.",
                    "managed-artifact-verification")
                : Attention(
                    "Managed artifact verification",
                    "version.dll differs from Mod Control-managed state.",
                    "Use Repair; Mod Control will not delete an unknown artifact.",
                    "managed-artifact-verification"));
        }
        catch (Exception exception) when (
            exception is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            health.Add(Unavailable("Deployment state", exception.Message, "Preview diagnostics and request support."));
        }
    }

    private static void AddLocalHealthFacts(
        List<LauncherDiagnosticFact> health,
        LauncherHealthSnapshot? localHealth)
    {
        if (localHealth is null)
        {
            health.Add(Unavailable(
                "Provider and runtime health",
                "Provider-aware local health evidence was not supplied.",
                "Refresh Diagnostics.",
                "local-health"));
            return;
        }

        foreach (var dimension in localHealth.Dimensions.Where(
                     item => item.Category is LauncherHealthDimensionCategory.ModInstallation
                         or LauncherHealthDimensionCategory.BinaryProvenance
                         or LauncherHealthDimensionCategory.ProviderCompatibility
                         or LauncherHealthDimensionCategory.UpdateAvailability
                         or LauncherHealthDimensionCategory.GameCompatibility
                         or LauncherHealthDimensionCategory.RuntimeActivation
                         or LauncherHealthDimensionCategory.NativeSupport
                         or LauncherHealthDimensionCategory.ProviderAvailability))
        {
            var id = $"local-health.{dimension.Category.ToString().ToLowerInvariant()}";
            var isInstallation = dimension.Category == LauncherHealthDimensionCategory.ModInstallation;
            health.Add(new(
                isInstallation ? "Community mod installation" : dimension.Title,
                dimension.Severity switch
                {
                    LauncherHealthSeverity.Healthy => LauncherDiagnosticLevel.Healthy,
                    LauncherHealthSeverity.ActionRequired => LauncherDiagnosticLevel.Error,
                    LauncherHealthSeverity.Informational => LauncherDiagnosticLevel.Informational,
                    _ => LauncherDiagnosticLevel.Unavailable,
                },
                isInstallation ? $"{dimension.Title}. {dimension.Detail}" : dimension.Detail,
                dimension.Severity == LauncherHealthSeverity.ActionRequired
                    ? "Review the selected provider and community mod state."
                    : localHealth.Installation.State switch
                    {
                        ModInstallationEvidenceState.ManualInstallation when isInstallation =>
                            "Choose Check for updates when you want to compare or replace this installation.",
                        ModInstallationEvidenceState.NotInstalled when isInstallation =>
                            "Choose Install to add the community mod.",
                        _ => "No action needed.",
                    },
                id,
                "launcher-local-health",
                dimension.TechnicalDetail));
        }
    }

    private static void AddConfigurationFact(
        List<LauncherDiagnosticFact> health,
        string? gameDirectory,
        LauncherConfigurationDiagnosisEvidence? evidence,
        string? runtimeDistributionId)
    {
        if (gameDirectory is null)
        {
            AddConfigurationUnavailable(
                health,
                "Configuration cannot be checked without a valid game folder.",
                "Select a game folder.");
            return;
        }
        var path = Path.Combine(gameDirectory, "community_patch_settings.toml");
        try
        {
            var exists = File.Exists(path);
            if (exists && new FileInfo(path).Length > MaximumConfigBytes)
            {
                health.Add(Attention(
                    "Active provider and configuration",
                    "The TOML exceeds the diagnostic parser limit.",
                    "Open Settings and review the file.",
                    "configuration"));
                health.Add(Unavailable(
                    "Data Sync configuration health",
                    "Data Sync health is unknown because the TOML exceeds the diagnostic parser limit.",
                    "Open Data Sync and review the source document.",
                    "data-sync-configuration"));
                return;
            }
            var contents = exists ? File.ReadAllBytes(path) : [];

            if (evidence is null)
            {
                AddConfigurationUnavailable(
                    health,
                    "Provider-owned configuration metadata is unavailable.",
                    "Review the release source or retry Diagnostics.");
                return;
            }

            var report = new ConfigurationHealthAnalyzer().Analyze(
                new ConfigurationDocumentSnapshot(path, contents),
                evidence);
            var errorCount = report.Findings.Count(item => item.Severity == ConfigurationDiagnosisSeverity.Error);
            var attentionCount = report.Findings.Count(item => item.Severity == ConfigurationDiagnosisSeverity.Attention);
            var unknownCount = report.Findings.Count(item => item.Severity == ConfigurationDiagnosisSeverity.Unknown);
            var publicFindings = report.Findings
                .Where(item => item.Sensitivity == LauncherConfigurationSensitivity.Public)
                .Select(item => $"{item.Code}: {item.Summary}")
                .Take(20)
                .ToArray();
            var detail = string.Join(Environment.NewLine, publicFindings);
            var diagnosisSummary = errorCount > 0
                ? $"Configuration diagnosis found {errorCount} error(s) and {attentionCount} item(s) needing attention."
                : attentionCount > 0
                    ? $"Configuration diagnosis found {attentionCount} item(s) needing attention."
                    : unknownCount > 0
                        ? "Configuration health is unknown for the selected provider or document syntax."
                        : exists
                            ? "The active override TOML is healthy for the selected provider catalog."
                            : "No override TOML exists; provider runtime defaults apply.";
            var runtimeLabel = string.IsNullOrWhiteSpace(runtimeDistributionId)
                ? "runtime identity unknown"
                : $"runtime {runtimeDistributionId}";
            var summary = $"Provider {report.Binding.ProviderId} · {report.Binding.ChannelId} · {runtimeLabel}. {diagnosisSummary}";
            var level = errorCount > 0
                ? LauncherDiagnosticLevel.Error
                : attentionCount > 0
                    ? LauncherDiagnosticLevel.Attention
                    : unknownCount > 0
                        ? LauncherDiagnosticLevel.Unavailable
                        : LauncherDiagnosticLevel.Healthy;
            health.Add(new(
                "Active provider and configuration",
                level,
                summary,
                level is LauncherDiagnosticLevel.Error or LauncherDiagnosticLevel.Attention
                    ? "Open Settings and review the reported configuration."
                    : "No action needed.",
                "configuration",
                report.Binding.EvidenceSource,
                string.IsNullOrWhiteSpace(detail) ? null : detail));

            var syncFindings = report.Findings
                .Where(item => item.Code.StartsWith("SYNC_", StringComparison.Ordinal))
                .ToArray();
            var syncErrors = syncFindings.Count(item => item.Severity == ConfigurationDiagnosisSeverity.Error);
            var syncAttention = syncFindings.Count(item => item.Severity == ConfigurationDiagnosisSeverity.Attention);
            var syncUnknown = syncFindings.Count(item => item.Severity == ConfigurationDiagnosisSeverity.Unknown);
            var providerCatalogUnknown = report.Binding.CatalogId is null;
            var syncLevel = providerCatalogUnknown
                ? LauncherDiagnosticLevel.Unavailable
                : syncErrors > 0
                ? LauncherDiagnosticLevel.Error
                : syncAttention > 0
                    ? LauncherDiagnosticLevel.Attention
                    : syncUnknown > 0
                        ? LauncherDiagnosticLevel.Unavailable
                        : LauncherDiagnosticLevel.Healthy;
            health.Add(new(
                "Data Sync configuration health",
                syncLevel,
                providerCatalogUnknown
                    ? "Data Sync health is unknown because the selected provider has no verified configuration catalog."
                    : syncFindings.Length == 0
                    ? "No Data Sync topology issues were established by the provider-owned catalog."
                    : $"Data Sync diagnosis found {syncErrors} error(s), {syncAttention} warning(s), and {syncUnknown} unknown result(s).",
                syncLevel is LauncherDiagnosticLevel.Error or LauncherDiagnosticLevel.Attention
                    ? "Open Data Sync and review the staged topology without saving unintended changes."
                    : "No action needed.",
                "data-sync-configuration",
                report.Binding.EvidenceSource));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AddConfigurationUnavailable(
                health,
                "Configuration evidence could not be read safely.",
                "Check file access and retry.",
                exception.GetType().Name);
        }
    }

    private static void AddConfigurationUnavailable(
        List<LauncherDiagnosticFact> health,
        string summary,
        string nextAction,
        string? technicalDetail = null)
    {
        health.Add(Unavailable(
            "Active provider and configuration",
            summary,
            nextAction,
            "configuration",
            technicalDetail: technicalDetail));
        health.Add(Unavailable(
            "Data Sync configuration health",
            summary,
            nextAction,
            "data-sync-configuration",
            technicalDetail: technicalDetail));
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

    private static LauncherDiagnosticFact Healthy(string name, string summary, string id = "") =>
        new(name, LauncherDiagnosticLevel.Healthy, summary, "No action needed.", id);

    private static LauncherDiagnosticFact Attention(
        string name,
        string summary,
        string nextAction,
        string id = "") =>
        new(name, LauncherDiagnosticLevel.Attention, summary, nextAction, id);

    private static LauncherDiagnosticFact Informational(
        string name,
        string summary,
        string nextAction,
        string id = "") =>
        new(name, LauncherDiagnosticLevel.Informational, summary, nextAction, id);

    private static LauncherDiagnosticFact Unavailable(
        string name,
        string summary,
        string nextAction,
        string id = "",
        string evidenceSource = "launcher-local",
        string? technicalDetail = null) =>
        new(
            name,
            LauncherDiagnosticLevel.Unavailable,
            summary,
            nextAction,
            id,
            evidenceSource,
            technicalDetail);

    private static bool PathEquals(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static string StableFactId(string name) => name switch
    {
        "Game folder" => "game-folder",
        "Game process" => "game-process",
        "Scopely launcher" => "scopely-launcher",
        "Deployment transaction" => "deployment-transaction",
        "Managed artifact verification" => "managed-artifact-verification",
        "Deployment state" => "deployment-state",
        "Recent mod log" => "mod-log",
        _ => throw new InvalidDataException(
            $"Diagnostic fact '{name}' requires an explicit stable identifier."),
    };
}
