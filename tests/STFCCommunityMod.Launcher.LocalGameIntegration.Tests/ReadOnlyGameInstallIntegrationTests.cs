using System.Security.Cryptography;
using System.Text;
using STFCCommunityMod.Launcher.Core;

namespace STFCCommunityMod.Launcher.LocalGameIntegration.Tests;

[TestClass]
[DoNotParallelize]
[TestCategory("LocalGameIntegration")]
public sealed class ReadOnlyGameInstallIntegrationTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void OptedInDirectoryPassesProductionValidationWithoutMutation()
    {
        var gameDirectory = LocalGameIntegrationTarget.RequireOptedInDirectory();
        var before = ReadOnlyDirectoryFingerprint.Capture(gameDirectory);

        var validation = GameInstallValidator.Validate(gameDirectory);

        Assert.IsTrue(validation.IsValid);
        Assert.AreEqual(
            Path.GetFullPath(gameDirectory),
            validation.GameDirectory,
            StringComparer.OrdinalIgnoreCase);
        Assert.IsNotNull(validation.PrimeExecutablePath);
        Assert.IsTrue(File.Exists(validation.PrimeExecutablePath));
        Assert.AreEqual(before, ReadOnlyDirectoryFingerprint.Capture(gameDirectory));
        TestContext.WriteLine("Game-root validation: valid");
    }

    [TestMethod]
    public void ProductionEnvironmentOverrideDiscoversTheExactOptedInTarget()
    {
        var gameDirectory = LocalGameIntegrationTarget.RequireOptedInDirectory();
        var before = ReadOnlyDirectoryFingerprint.Capture(gameDirectory);
        var originalOverride = Environment.GetEnvironmentVariable("STFC_GAME_DIRECTORY");
        try
        {
            Environment.SetEnvironmentVariable("STFC_GAME_DIRECTORY", gameDirectory);
            var discovery = new GameInstallDiscovery(
                new MissingSelectionStore(),
                [BoundedGameInstallCandidateProvider.FromCurrentMachine()]);

            var matches = discovery.Discover().Candidates
                .Where(candidate => candidate.Evidence.Any(
                    evidence => evidence.Source == GameInstallCandidateSource.EnvironmentOverride))
                .ToArray();

            Assert.AreEqual(1, matches.Length);
            Assert.IsTrue(matches[0].Validation.IsValid);
            Assert.AreEqual(
                Path.GetFullPath(gameDirectory),
                matches[0].GameDirectory,
                StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable("STFC_GAME_DIRECTORY", originalOverride);
        }

        Assert.AreEqual(before, ReadOnlyDirectoryFingerprint.Capture(gameDirectory));
        TestContext.WriteLine("Bounded environment discovery: exact target found");
    }

    [TestMethod]
    public void InstallationInspectionReportsTheObservedStateWithoutMutation()
    {
        var gameDirectory = LocalGameIntegrationTarget.RequireOptedInDirectory();
        var before = ReadOnlyDirectoryFingerprint.Capture(gameDirectory);
        var inspector = new ModInstallationInspector(
            new EmptyDeploymentStateReader(),
            new SystemModInstallationFileSystem());

        var evidence = inspector.Capture(gameDirectory, isGameRunning: false);
        var health = LauncherHealthResolver.Resolve(
            evidence,
            new(
                "local-integration",
                "stable",
                "local-integration",
                CanMutate: true,
                UnavailableReason: string.Empty));

        Assert.IsTrue(
            evidence.State is ModInstallationEvidenceState.NotInstalled
                or ModInstallationEvidenceState.ManualInstallation,
            $"Unexpected read-only installation state: {evidence.State}.");
        if (evidence.State == ModInstallationEvidenceState.ManualInstallation)
        {
            Assert.IsFalse(string.IsNullOrWhiteSpace(evidence.InstalledSha256));
            Assert.IsNotNull(evidence.BinaryProvenance);
        }
        else
        {
            Assert.AreEqual(ModManagementActionKind.Install, health.ModManagement.ActionKind);
            Assert.IsTrue(health.ModManagement.CanExecute);
        }
        Assert.AreEqual(before, ReadOnlyDirectoryFingerprint.Capture(gameDirectory));
        TestContext.WriteLine($"Community-mod state: {evidence.State}");
        TestContext.WriteLine($"Bridge action: {health.ModManagement.ActionKind}");
        TestContext.WriteLine(
            $"Binary provenance: {evidence.BinaryProvenance?.State.ToString() ?? "not applicable"}");
    }

    [TestMethod]
    public void ConfigurationReadReportsPresenceOrAbsenceWithoutMutation()
    {
        var gameDirectory = LocalGameIntegrationTarget.RequireOptedInDirectory();
        var before = ReadOnlyDirectoryFingerprint.Capture(gameDirectory);
        var configurationPath = Path.Combine(gameDirectory, "community_patch_settings.toml");

        var result = new TomlConfigurationRepository().Read(configurationPath);

        if (File.Exists(configurationPath))
        {
            Assert.IsTrue(
                result.State is ConfigurationRepositoryReadState.Succeeded
                    or ConfigurationRepositoryReadState.Invalid,
                $"Unexpected configuration read state: {result.State}.");
        }
        else
        {
            Assert.AreEqual(ConfigurationRepositoryReadState.NoConfigurationSelected, result.State);
        }
        Assert.AreNotEqual(ConfigurationRepositoryReadState.IoFailure, result.State);
        Assert.AreEqual(before, ReadOnlyDirectoryFingerprint.Capture(gameDirectory));
        TestContext.WriteLine($"Configuration state: {result.State}");
        if (result.ValidationError is not null)
        {
            TestContext.WriteLine($"Configuration diagnostic: {result.ValidationError.Code}");
        }
    }

    [TestMethod]
    public void ProcessInspectionIsScopedToTheExactOptedInInstallation()
    {
        var gameDirectory = LocalGameIntegrationTarget.RequireOptedInDirectory();
        var before = ReadOnlyDirectoryFingerprint.Capture(gameDirectory);

        var processState = new SystemGameProcessInspector().Inspect(gameDirectory);

        Assert.AreEqual(
            GameProcessInspectionState.NotRunning,
            processState,
            "The opted-in integration installation is running or a prime.exe process could not be attributed safely.");
        Assert.AreEqual(before, ReadOnlyDirectoryFingerprint.Capture(gameDirectory));
        TestContext.WriteLine("Install-scoped process inspection: target is stopped");
    }

    private sealed class MissingSelectionStore : IGameInstallSelectionStore
    {
        public GameInstallSelectionLoadResult Load() => GameInstallSelectionLoadResult.Missing();

        public void Save(string gameDirectory) =>
            throw new InvalidOperationException("Read-only discovery must not persist a selection.");
    }

    private sealed class EmptyDeploymentStateReader : IModDeploymentStateReader
    {
        public ModDeploymentJournal? ReadJournal() => null;

        public ModInstalledArtifactState? ReadInstalledState(string gameDirectory)
        {
            _ = gameDirectory;
            return null;
        }
    }

    private static class ReadOnlyDirectoryFingerprint
    {
        private static readonly string[] ContentHashedFiles =
            ["prime.exe", "version.dll", "community_patch_settings.toml"];

        public static string Capture(string gameDirectory)
        {
            var builder = new StringBuilder();
            foreach (var entryPath in Directory.EnumerateFileSystemEntries(
                         gameDirectory,
                         "*",
                         SearchOption.TopDirectoryOnly)
                     .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
            {
                var attributes = File.GetAttributes(entryPath);
                var isDirectory = attributes.HasFlag(FileAttributes.Directory);
                builder.Append(Path.GetFileName(entryPath))
                    .Append('|')
                    .Append((int)attributes)
                    .Append('|')
                    .Append(isDirectory ? -1 : new FileInfo(entryPath).Length)
                    .Append('|')
                    .Append(File.GetLastWriteTimeUtc(entryPath).Ticks)
                    .AppendLine();
            }

            foreach (var fileName in ContentHashedFiles)
            {
                var path = Path.Combine(gameDirectory, fileName);
                builder.Append(fileName).Append('|');
                if (File.Exists(path))
                {
                    using var stream = File.OpenRead(path);
                    builder.Append(Convert.ToHexString(SHA256.HashData(stream)));
                }
                builder.AppendLine();
            }

            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
        }
    }
}
