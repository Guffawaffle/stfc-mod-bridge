using STFCCommunityMod.Launcher.Core;

namespace STFCCommunityMod.Launcher.LocalGameIntegration.Tests;

public sealed partial class LiveProviderInstallIntegrationTests
{
    [TestMethod]
    [TestCategory("Deterministic")]
    public void ExactBaselineCleanupPerformsNoGameMutation()
    {
        using var target = new TemporaryHarnessTarget();
        var configurationPath = Path.Combine(
            target.GameDirectory,
            "community_patch_settings.toml");
        File.WriteAllText(configurationPath, "[graphics]\nfree_resize = true\n");
        var originalTimestamp = new DateTime(2026, 8, 16, 12, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(configurationPath, originalTimestamp);
        var mutations = new List<DirectGameMutationKind>();

        using var campaign = new RestorableGameInstallCampaign(
            target.GameDirectory,
            new MutableGameProcessInspector(target.GameDirectory),
            (kind, _) => mutations.Add(kind));

        campaign.RestoreConfigurationBaseline();

        Assert.AreEqual(0, mutations.Count, "Exact cleanup must not rewrite bytes or metadata.");
        Assert.AreEqual(originalTimestamp, File.GetLastWriteTimeUtc(configurationPath));
        campaign.AssertBaseline("Exact cleanup changed its protected baseline.");
    }

    [TestMethod]
    [TestCategory("Deterministic")]
    public void MetadataOnlyCleanupDoesNotRewriteBytes()
    {
        using var target = new TemporaryHarnessTarget();
        var configurationPath = Path.Combine(
            target.GameDirectory,
            "community_patch_settings.toml");
        var original = "[graphics]\nfree_resize = true\n"u8.ToArray();
        File.WriteAllBytes(configurationPath, original);
        var mutations = new List<DirectGameMutationKind>();

        using var campaign = new RestorableGameInstallCampaign(
            target.GameDirectory,
            new MutableGameProcessInspector(target.GameDirectory),
            (kind, _) => mutations.Add(kind));
        File.SetLastWriteTimeUtc(
            configurationPath,
            File.GetLastWriteTimeUtc(configurationPath).AddMinutes(1));

        campaign.RestoreConfigurationBaseline();

        CollectionAssert.AreEqual(original, File.ReadAllBytes(configurationPath));
        Assert.IsFalse(mutations.Contains(DirectGameMutationKind.WriteRestoreStage));
        Assert.IsFalse(mutations.Contains(DirectGameMutationKind.PromoteRestoreStage));
        CollectionAssert.Contains(mutations, DirectGameMutationKind.RestoreLastWriteTime);
        campaign.AssertBaseline("Metadata-only cleanup did not restore its protected baseline.");
    }

    [TestMethod]
    [TestCategory("Deterministic")]
    public void InterruptedAtomicRestoreCanRecoverExactlyAndRetainState()
    {
        using var target = new TemporaryHarnessTarget();
        var configurationPath = Path.Combine(
            target.GameDirectory,
            "community_patch_settings.toml");
        var original = "[graphics]\nfree_resize = true\n"u8.ToArray();
        var changed = "[graphics]\nfree_resize = false\n"u8.ToArray();
        File.WriteAllBytes(configurationPath, original);
        var failPromotion = true;
        string? retainedState = null;

        try
        {
            using var campaign = new RestorableGameInstallCampaign(
                target.GameDirectory,
                new MutableGameProcessInspector(target.GameDirectory),
                (kind, _) =>
                {
                    if (kind == DirectGameMutationKind.PromoteRestoreStage && failPromotion)
                    {
                        failPromotion = false;
                        throw new IOException("Injected atomic-promotion interruption.");
                    }
                });
            File.WriteAllBytes(configurationPath, changed);
            campaign.PreserveStateForRecovery();
            retainedState = campaign.StateDirectory;

            Assert.ThrowsException<IOException>(campaign.RestoreConfigurationBaseline);
            CollectionAssert.AreEqual(changed, File.ReadAllBytes(configurationPath));

            campaign.EmergencyRestore();
            campaign.AssertBaseline("A retry did not recover the exact protected baseline.");
        }
        finally
        {
            if (retainedState is not null)
            {
                var wasRetained = Directory.Exists(retainedState);
                if (wasRetained)
                {
                    Directory.Delete(retainedState, recursive: true);
                }
                Assert.IsTrue(
                    wasRetained,
                    "Recovery state must remain available after an emergency path.");
            }
        }
    }

    [DataTestMethod]
    [TestCategory("Deterministic")]
    [DataRow("[graphics]\nfree_resize = true\nfree_resize = false\n")]
    [DataRow("[graphics]\nfree_resize = \"not-a-boolean\"\n")]
    public async Task UnsafePreflightNeverReachesDirectGameMutation(string configuration)
    {
        using var target = new TemporaryHarnessTarget();
        var configurationPath = Path.Combine(
            target.GameDirectory,
            "community_patch_settings.toml");
        var original = System.Text.Encoding.UTF8.GetBytes(configuration);
        File.WriteAllBytes(configurationPath, original);
        var admissions = 0;
        var mutations = 0;

        using var campaign = new RestorableGameInstallCampaign(
            target.GameDirectory,
            new MutableGameProcessInspector(target.GameDirectory),
            (_, _) => mutations++);

        await Assert.ThrowsExceptionAsync<AssertFailedException>(() =>
            RunConfigurationRestoreRecoveryAsync(
                target.GameDirectory,
                configurationPath,
                campaign.StateDirectory,
                original,
                campaign,
                () => admissions++));

        Assert.AreEqual(0, admissions, "Unsafe preflight reached the direct-mutation boundary.");
        Assert.AreEqual(0, mutations, "Unsafe preflight performed a harness-owned game mutation.");
        campaign.AssertBaseline("Unsafe preflight changed the protected game target.");
    }

    [TestMethod]
    [TestCategory("Deterministic")]
    public async Task ExactInstallStartingAfterPreflightBlocksDirectMutation()
    {
        using var target = new TemporaryHarnessTarget();
        var configurationPath = Path.Combine(
            target.GameDirectory,
            "community_patch_settings.toml");
        var original = "[graphics]\nfree_resize = true\n"u8.ToArray();
        File.WriteAllBytes(configurationPath, original);
        var inspector = new MutableGameProcessInspector(target.GameDirectory);
        var mutations = 0;

        using var campaign = new RestorableGameInstallCampaign(
            target.GameDirectory,
            inspector,
            (_, _) => mutations++);

        var failure = await CaptureFailureAsync(() => RunConfigurationRestoreRecoveryAsync(
                target.GameDirectory,
                configurationPath,
                campaign.StateDirectory,
                original,
                campaign,
                () => inspector.SelectedState = GameProcessInspectionState.RunningTarget));

        Assert.IsInstanceOfType<InvalidOperationException>(failure, failure.ToString());

        Assert.AreEqual(0, mutations, "The running target reached a direct game-file mutation.");
        campaign.AssertBaseline("The target changed after its game process started.");
    }

    [TestMethod]
    [TestCategory("Deterministic")]
    public void EveryHarnessWriteAndDeleteRechecksExactInstallProcessState()
    {
        using var target = new TemporaryHarnessTarget();
        var configurationPath = Path.Combine(
            target.GameDirectory,
            "community_patch_settings.toml");
        var inspector = new MutableGameProcessInspector(target.GameDirectory);
        var mutations = 0;

        using var campaign = new RestorableGameInstallCampaign(
            target.GameDirectory,
            inspector,
            (_, _) => mutations++);
        inspector.SelectedState = GameProcessInspectionState.RunningTarget;

        Assert.ThrowsException<InvalidOperationException>(() =>
            campaign.WriteGameFileAtomically(
                "community_patch_settings.toml",
                "[graphics]\nfree_resize = true\n"u8.ToArray()));
        Assert.IsFalse(File.Exists(configurationPath));

        File.WriteAllText(configurationPath, "[graphics]\nfree_resize = true\n");
        Assert.ThrowsException<InvalidOperationException>(campaign.RestoreConfigurationBaseline);

        Assert.AreEqual(0, mutations, "A running target reached the delete operation.");
        Assert.IsTrue(File.Exists(configurationPath));
        inspector.SelectedState = GameProcessInspectionState.NotRunning;
        campaign.RestoreConfigurationBaseline();
        campaign.AssertBaseline("Closed-target cleanup did not remove the harness-created TOML.");
    }

    [TestMethod]
    [TestCategory("Deterministic")]
    public void RunningDifferentInstallDoesNotBlockSelectedInstallMutation()
    {
        using var target = new TemporaryHarnessTarget();
        using var other = new TemporaryHarnessTarget();
        var inspector = new MutableGameProcessInspector(target.GameDirectory);
        inspector.SetState(other.GameDirectory, GameProcessInspectionState.RunningTarget);

        using var campaign = new RestorableGameInstallCampaign(target.GameDirectory, inspector);
        campaign.WriteGameFileAtomically(
            "community_patch_settings.toml",
            "[graphics]\nfree_resize = true\n"u8.ToArray());

        Assert.IsTrue(File.Exists(Path.Combine(
            target.GameDirectory,
            "community_patch_settings.toml")));
        Assert.IsTrue(inspector.InspectedDirectories.All(path =>
            string.Equals(
                Path.GetFullPath(path),
                Path.GetFullPath(target.GameDirectory),
                StringComparison.OrdinalIgnoreCase)));
        campaign.RestoreConfigurationBaseline();
        campaign.AssertBaseline("The selected installation did not return to baseline.");
    }

    [TestMethod]
    [TestCategory("Deterministic")]
    public void FailureRenderingContainsNoRawGameOrStatePath()
    {
        const string gameDirectory = @"E:\sentinel-player\STFC\default\game";
        const string stateDirectory = @"C:\Users\sentinel-player\AppData\Local\STFC Mod Bridge";
        var failure = new AggregateException(
            new IOException($"Could not replace {gameDirectory}\\community_patch_settings.toml."),
            new InvalidDataException($"Receipt under {stateDirectory} was invalid."));

        var sanitized = SanitizedFailure(
            "The recovery lab failed.",
            failure,
            gameDirectory,
            stateDirectory);
        var rendered = sanitized.ToString();

        Assert.IsNull(sanitized.InnerException);
        StringAssert.Contains(rendered, "%GAME_DIR%");
        StringAssert.Contains(rendered, "%STATE_DIR%");
        Assert.IsFalse(rendered.Contains(gameDirectory, StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(rendered.Contains(stateDirectory, StringComparison.OrdinalIgnoreCase));
    }

    private sealed class MutableGameProcessInspector : IGameProcessInspector
    {
        private readonly Dictionary<string, GameProcessInspectionState> states =
            new(StringComparer.OrdinalIgnoreCase);

        public MutableGameProcessInspector(string selectedGameDirectory)
        {
            SelectedGameDirectory = Path.GetFullPath(selectedGameDirectory);
            states[SelectedGameDirectory] = GameProcessInspectionState.NotRunning;
        }

        public string SelectedGameDirectory { get; }

        public GameProcessInspectionState SelectedState
        {
            get => states[SelectedGameDirectory];
            set => states[SelectedGameDirectory] = value;
        }

        public List<string> InspectedDirectories { get; } = [];

        public GameProcessInspectionState Inspect(string gameDirectory)
        {
            var canonical = Path.GetFullPath(gameDirectory);
            InspectedDirectories.Add(canonical);
            return states.GetValueOrDefault(
                canonical,
                GameProcessInspectionState.Unattributable);
        }

        public void SetState(string gameDirectory, GameProcessInspectionState state) =>
            states[Path.GetFullPath(gameDirectory)] = state;
    }

    private static async Task<Exception> CaptureFailureAsync(Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            return exception;
        }
        return new AssertFailedException("The expected failure did not occur.");
    }

    private sealed class TemporaryHarnessTarget : IDisposable
    {
        public TemporaryHarnessTarget()
        {
            RootDirectory = Path.Combine(
                Path.GetTempPath(),
                "stfc-bridge-harness-safety",
                Guid.NewGuid().ToString("N"));
            GameDirectory = Path.Combine(RootDirectory, "game");
            Directory.CreateDirectory(GameDirectory);
            File.WriteAllBytes(Path.Combine(GameDirectory, "prime.exe"), "test-prime"u8.ToArray());
        }

        public string RootDirectory { get; }

        public string GameDirectory { get; }

        public void Dispose()
        {
            if (Directory.Exists(RootDirectory))
            {
                Directory.Delete(RootDirectory, recursive: true);
            }
        }
    }
}
