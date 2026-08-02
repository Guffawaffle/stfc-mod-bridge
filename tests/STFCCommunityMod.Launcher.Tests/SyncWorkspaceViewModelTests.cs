using System.Text;
using STFCCommunityMod.Launcher.Core;
using STFCCommunityMod.Launcher.ViewModels;

namespace STFCCommunityMod.Launcher.Tests;

[TestClass]
public sealed class SyncWorkspaceViewModelTests
{
    [TestMethod]
    public void OpeningExistingTopologyDoesNotMutateSourceOrRevealSavedToken()
    {
        using var fixture = SyncFixture.Create(
            """
            # source must remain byte-identical
            [sync.targets.community]
            url = "https://community.example.invalid/sync"
            token = "vip-secret-value"
            """);
        var before = File.ReadAllBytes(fixture.Path);

        var viewModel = fixture.CreateViewModel();
        var target = viewModel.Targets.Single();

        CollectionAssert.AreEqual(before, File.ReadAllBytes(fixture.Path));
        Assert.AreEqual("community", target.Name);
        Assert.AreEqual("Saved token configured", target.TokenStatus);
        Assert.IsFalse(target.TokenStatus.Contains("vip-secret-value", StringComparison.Ordinal));
        Assert.IsFalse(target.ValidationSummary.Contains("vip-secret-value", StringComparison.Ordinal));
    }

    [TestMethod]
    public void AddSurfaceSupportsMixedTypedTargetsAndSidecarSingleton()
    {
        using var fixture = SyncFixture.Create("# empty\n");
        var viewModel = fixture.CreateViewModel();

        viewModel.AddSidecarCommand.Execute(null);
        viewModel.NewTargetName = "majel-vip";
        viewModel.AddMajelCommand.Execute(null);
        viewModel.AddSpocksClubCommand.Execute(null);

        Assert.AreEqual(3, viewModel.Targets.Count);
        Assert.IsTrue(viewModel.Targets.Any(target => target.Name == "local-sidecar"));
        Assert.IsTrue(viewModel.Targets.Any(target => target.Name == "majel-vip"));
        Assert.IsTrue(viewModel.Targets.Any(target => target.Name == "spocksclub"));
        Assert.IsFalse(viewModel.AddSidecarCommand.CanExecute(null));
        Assert.AreEqual("Local Sidecar", viewModel.Targets.Single(target => target.Name == "local-sidecar").KindLabel);
        Assert.AreEqual("Majel", viewModel.Targets.Single(target => target.Name == "majel-vip").KindLabel);
    }

    [TestMethod]
    public void FeedOverrideShowsEffectiveValueAndProvenance()
    {
        using var fixture = SyncFixture.Create(
            """
            [sync]
            jobs = false

            [sync.targets.community]
            url = "https://community.example.invalid/sync"
            token = "fixture-secret"
            """);
        var target = fixture.CreateViewModel().Targets.Single();
        var jobs = target.Feeds.Single(feed => feed.Label == "Jobs");

        Assert.AreEqual(SyncBooleanOverrideChoice.UseGlobal, jobs.Choice);
        Assert.IsFalse(jobs.EffectiveEnabled);
        StringAssert.Contains(jobs.EffectiveSummary, "inherited");

        jobs.Choice = SyncBooleanOverrideChoice.Enabled;

        Assert.IsTrue(jobs.EffectiveEnabled);
        StringAssert.Contains(jobs.EffectiveSummary, "target override");
    }

    [TestMethod]
    public void SecretReplacementRequiresExplicitActionAndDisplayRemainsOpaque()
    {
        using var fixture = SyncFixture.Create(
            """
            [sync.targets.community]
            url = "https://community.example.invalid/sync"
            token = "old-secret"
            """);
        var viewModel = fixture.CreateViewModel();
        var target = viewModel.Targets.Single();
        target.SetReplacementToken("new-secret");

        Assert.IsFalse(viewModel.HasPendingChanges);
        target.ReplaceTokenCommand.Execute(null);

        Assert.IsTrue(viewModel.HasPendingChanges);
        Assert.AreEqual("Saved token configured", viewModel.Targets.Single().TokenStatus);
        Assert.IsFalse(viewModel.Targets.Single().TokenStatus.Contains("new-secret", StringComparison.Ordinal));
        Assert.IsFalse(File.ReadAllText(fixture.Path).Contains("new-secret", StringComparison.Ordinal));
    }

    [TestMethod]
    public void TargetOverridesRepresentInheritedExplicitFalseAndExplicitClear()
    {
        using var fixture = SyncFixture.Create(
            """
            [sync]
            proxy = "http://global-proxy.example.invalid:8080"
            verify_ssl = true

            [sync.targets.community]
            url = "https://community.example.invalid/sync"
            token = "fixture-secret"
            """);
        var target = fixture.CreateViewModel().Targets.Single();

        StringAssert.Contains(target.ProxySummary, "Inherited");
        Assert.AreEqual(SyncBooleanOverrideChoice.UseGlobal, target.VerifySslChoice);

        target.ProxyText = string.Empty;
        target.VerifySslChoice = SyncBooleanOverrideChoice.Disabled;
        target.UnsafeTlsChoice = SyncBooleanOverrideChoice.Enabled;

        Assert.AreEqual("Explicitly cleared", target.ProxySummary);
        Assert.AreEqual(SyncBooleanOverrideChoice.Disabled, target.VerifySslChoice);
        Assert.AreEqual(SyncBooleanOverrideChoice.Enabled, target.UnsafeTlsChoice);
    }

    [TestMethod]
    public void SidecarOnlyControlsAreExposedOnlyForSidecar()
    {
        using var fixture = SyncFixture.Create(
            """
            [sidecar.sync]
            enabled = true
            url = "http://127.0.0.1:43127/api/sidecar/ingest"
            token = "fixture-sidecar-secret"

            [sync.targets.community]
            url = "https://community.example.invalid/sync"
            token = "fixture-community-secret"
            """);
        var viewModel = fixture.CreateViewModel();
        var sidecar = viewModel.Targets.Single(target => target.Name == "local-sidecar");
        var community = viewModel.Targets.Single(target => target.Name == "community");

        Assert.IsTrue(sidecar.ShowSidecarControls);
        Assert.IsFalse(community.ShowSidecarControls);
        sidecar.BattlelogEnrichmentChoice = SyncBooleanOverrideChoice.Enabled;
        sidecar.FleetRuntimeModeChoice = "request_only";
        Assert.AreEqual(SyncBooleanOverrideChoice.Enabled, sidecar.BattlelogEnrichmentChoice);
        Assert.AreEqual("request_only", sidecar.FleetRuntimeModeChoice);
    }

    [TestMethod]
    public void SiblingDraftBlocksSyncSaveWithoutDiscardingEitherDraft()
    {
        using var fixture = SyncFixture.Create(
            """
            [sync.targets.community]
            url = "https://community.example.invalid/sync"
            token = "fixture-secret"
            """);
        var siblingPending = false;
        var viewModel = fixture.CreateViewModel(() => siblingPending);
        viewModel.GlobalVerifySsl = false;
        viewModel.GlobalUnsafeTls = true;
        Assert.IsTrue(viewModel.CanSave);

        siblingPending = true;

        Assert.IsFalse(viewModel.CanSave);
        Assert.IsTrue(viewModel.HasPendingChanges);
        StringAssert.Contains(viewModel.SaveAvailability, "non-sync settings");
    }

    [TestMethod]
    public void LegacyRootEditRequiresExplicitMigrationConfirmation()
    {
        using var fixture = SyncFixture.Create(
            """
            [sync]
            url = "https://community.example.invalid/sync"
            token = "fixture-legacy-secret"
            """);
        var viewModel = fixture.CreateViewModel();
        viewModel.Targets.Single().Url = "https://replacement.example.invalid/sync";

        Assert.IsTrue(viewModel.HasLegacyRootTarget);
        Assert.IsFalse(viewModel.CanSave);

        viewModel.MigrateLegacyRoot = true;

        Assert.IsTrue(viewModel.CanSave);
    }

    [TestMethod]
    public async Task SaveUsesAtomicWorkspaceAndReloadsCommittedTopology()
    {
        using var fixture = SyncFixture.Create(
            """
            [sync]
            jobs = true

            [sync.targets.community]
            url = "https://community.example.invalid/sync"
            token = "fixture-secret"
            """);
        var viewModel = fixture.CreateViewModel();
        viewModel.GlobalFeeds.Single(feed => feed.Label == "Jobs").IsEnabled = false;

        viewModel.SaveCommand.Execute(null);
        await WaitUntilAsync(() => !viewModel.HasPendingChanges);

        StringAssert.Contains(File.ReadAllText(fixture.Path), "jobs = false");
        Assert.IsTrue(File.Exists(fixture.Path + ".bak"));
        StringAssert.Contains(viewModel.OperationStatus, "Restart the game");
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 100 && !condition(); ++attempt)
        {
            await Task.Delay(10);
        }

        Assert.IsTrue(condition(), "The asynchronous view-model action did not finish.");
    }

    private sealed class SyncFixture : IDisposable
    {
        private SyncFixture(string path) => Path = path;
        public string Path { get; }

        public static SyncFixture Create(string contents)
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"stfc-launcher-sync-{Guid.NewGuid():N}.toml");
            File.WriteAllText(path, contents, new UTF8Encoding(false));
            return new(path);
        }

        public SyncWorkspaceViewModel CreateViewModel(Func<bool>? siblingPending = null) =>
            new(() => Path, new TomlConfigurationRepository(), siblingPending);

        public void Dispose()
        {
            foreach (var path in new[] { Path, Path + ".bak" })
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }
    }
}
