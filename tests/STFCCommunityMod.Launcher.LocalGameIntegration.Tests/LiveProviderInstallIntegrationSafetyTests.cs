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
        campaign.RecordFixtureOwnedGameFileRevision(
            "community_patch_settings.toml",
            original);

        campaign.RestoreConfigurationBaseline();

        CollectionAssert.AreEqual(original, File.ReadAllBytes(configurationPath));
        Assert.IsFalse(mutations.Contains(DirectGameMutationKind.WriteRestoreStage));
        Assert.IsFalse(mutations.Contains(DirectGameMutationKind.PromoteRestoreStage));
        CollectionAssert.Contains(mutations, DirectGameMutationKind.RestoreLastWriteTime);
        campaign.AssertBaseline("Metadata-only cleanup did not restore its protected baseline.");
    }

    [TestMethod]
    [TestCategory("Deterministic")]
    public void UnownedMetadataEditIsPreserved()
    {
        using var target = new TemporaryHarnessTarget();
        var configurationPath = Path.Combine(
            target.GameDirectory,
            "community_patch_settings.toml");
        File.WriteAllText(configurationPath, "[graphics]\nfree_resize = true\n");
        using var campaign = new RestorableGameInstallCampaign(
            target.GameDirectory,
            new MutableGameProcessInspector(target.GameDirectory));
        var externalTimestamp = File.GetLastWriteTimeUtc(configurationPath).AddMinutes(1);
        File.SetLastWriteTimeUtc(configurationPath, externalTimestamp);

        var failure = Assert.ThrowsException<InvalidOperationException>(
            campaign.RestoreConfigurationBaseline);

        StringAssert.Contains(failure.Message, "external protected-file revision");
        Assert.AreEqual(externalTimestamp, File.GetLastWriteTimeUtc(configurationPath));
    }

    [TestMethod]
    [TestCategory("Deterministic")]
    public void MetadataEditAfterOwnershipReceiptIsPreserved()
    {
        using var target = new TemporaryHarnessTarget();
        var configurationPath = Path.Combine(
            target.GameDirectory,
            "community_patch_settings.toml");
        File.WriteAllText(configurationPath, "baseline");
        using var campaign = new RestorableGameInstallCampaign(
            target.GameDirectory,
            new MutableGameProcessInspector(target.GameDirectory));
        campaign.WriteGameFileAtomically(
            "community_patch_settings.toml",
            "bridge"u8.ToArray());
        var externalTimestamp = File.GetLastWriteTimeUtc(configurationPath).AddMinutes(1);
        File.SetLastWriteTimeUtc(configurationPath, externalTimestamp);

        Assert.ThrowsException<InvalidOperationException>(campaign.RestoreConfigurationBaseline);

        Assert.AreEqual("bridge", File.ReadAllText(configurationPath));
        Assert.AreEqual(externalTimestamp, File.GetLastWriteTimeUtc(configurationPath));
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
        var failPromotion = false;
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
            campaign.WriteGameFileAtomically(
                "community_patch_settings.toml",
                changed);
            failPromotion = true;
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

        var failure = await CaptureFailureAsync(() =>
            RunConfigurationRestoreRecoveryAsync(
                target.GameDirectory,
                configurationPath,
                campaign.StateDirectory,
                original,
                campaign,
                () => admissions++));

        Assert.IsInstanceOfType<AssertFailedException>(failure, failure.ToString());
        Assert.AreEqual(0, admissions, "Unsafe preflight reached the direct-mutation boundary.");
        Assert.AreEqual(0, mutations, "Unsafe preflight performed a harness-owned game mutation.");
        campaign.AssertBaseline("Unsafe preflight changed the protected game target.");
    }

    [DataTestMethod]
    [TestCategory("Deterministic")]
    [DataRow(false)]
    [DataRow(true)]
    public async Task UnsafeCleanBaselinePreflightCreatesNoToml(bool catalogBlocked)
    {
        using var target = new TemporaryHarnessTarget();
        var configurationPath = Path.Combine(
            target.GameDirectory,
            "community_patch_settings.toml");
        var mutations = 0;
        using var campaign = new RestorableGameInstallCampaign(
            target.GameDirectory,
            new MutableGameProcessInspector(target.GameDirectory),
            (_, _) => mutations++);
        var source = catalogBlocked
            ? "[graphics]\nfree_resize = \"not-a-boolean\"\n"u8.ToArray()
            : "[graphics]\nfree_resize = true\nfree_resize = false\n"u8.ToArray();
        var evidence = catalogBlocked
            ? LauncherConfigurationDiagnosisEvidence.Supported(
                "guffawaffle",
                "stable",
                LauncherConfigurationSchemaLoader.LoadFile(Path.Combine(
                    RepositoryRoot(),
                    "docs",
                    "windows-launcher",
                    "config-schema.guffawaffle.v1.json")))
            : null;

        var failure = await CaptureFailureAsync(() =>
            RunConfigurationRestoreRecoveryAsync(
                target.GameDirectory,
                configurationPath,
                campaign.StateDirectory,
                baselineConfiguration: null,
                campaign: campaign,
                createdConfigurationContents: source,
                preflightEvidence: evidence));

        Assert.IsInstanceOfType<AssertFailedException>(failure, failure.ToString());
        Assert.IsFalse(File.Exists(configurationPath));
        Assert.AreEqual(0, mutations, "Blocked clean-target preflight reached a game mutation.");
        campaign.AssertBaseline("Blocked clean-target preflight changed the game target.");
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
        campaign.RecordFixtureOwnedGameFileRevision(
            "community_patch_settings.toml",
            "[graphics]\nfree_resize = true\n"u8.ToArray());
        Assert.ThrowsException<InvalidOperationException>(campaign.RestoreConfigurationBaseline);

        Assert.AreEqual(2, mutations, "Every blocked mutation must reach its final admission hook.");
        Assert.IsTrue(File.Exists(configurationPath));
        inspector.SelectedState = GameProcessInspectionState.NotRunning;
        campaign.RestoreConfigurationBaseline();
        campaign.AssertBaseline("Closed-target cleanup did not remove the harness-created TOML.");
    }

    [DataTestMethod]
    [TestCategory("Deterministic")]
    [DataRow(1)]
    [DataRow(2)]
    public void DirectStageWriteRechecksExactInstallAfterCreation(int blockedOccurrence)
    {
        using var target = new TemporaryHarnessTarget();
        var inspector = new MutableGameProcessInspector(target.GameDirectory);
        var occurrence = 0;
        using var campaign = new RestorableGameInstallCampaign(
            target.GameDirectory,
            inspector,
            (kind, _) =>
            {
                if (kind == DirectGameMutationKind.WriteRestoreStage
                    && ++occurrence == blockedOccurrence)
                {
                    inspector.SelectedState = GameProcessInspectionState.RunningTarget;
                }
            });

        Assert.ThrowsException<InvalidOperationException>(() =>
            campaign.WriteGameFileAtomically(
                "community_patch_settings.toml",
                "bridge"u8.ToArray()));

        Assert.AreEqual(
            blockedOccurrence == 1 ? 0 : 1,
            Directory.GetFiles(
                target.GameDirectory,
                ".stfc-bridge-integration-*.restore-stage").Length);
        inspector.SelectedState = GameProcessInspectionState.NotRunning;
        campaign.EmergencyRestore();
        Assert.AreEqual(0, Directory.GetFiles(
            target.GameDirectory,
            ".stfc-bridge-integration-*.restore-stage").Length);
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
    public void EmergencyRestorePreservesUnownedFilesAndPatternSentinels()
    {
        using var target = new TemporaryHarnessTarget();
        var sentinels = new[]
        {
            Path.Combine(target.GameDirectory, ".version.dll.external.stage"),
            Path.Combine(target.GameDirectory, ".version.dll.external.rollback"),
            Path.Combine(
                target.GameDirectory,
                ".stfc-bridge-integration-version.dll.external.restore-stage"),
        };
        foreach (var sentinel in sentinels)
        {
            File.WriteAllText(sentinel, "external");
        }
        using var campaign = new RestorableGameInstallCampaign(
            target.GameDirectory,
            new MutableGameProcessInspector(target.GameDirectory));
        var configurationPath = Path.Combine(
            target.GameDirectory,
            "community_patch_settings.toml");
        File.WriteAllText(configurationPath, "external");

        Assert.ThrowsException<InvalidOperationException>(campaign.EmergencyRestore);

        Assert.AreEqual("external", File.ReadAllText(configurationPath));
        foreach (var sentinel in sentinels)
        {
            Assert.AreEqual("external", File.ReadAllText(sentinel));
        }
    }

    [TestMethod]
    [TestCategory("Deterministic")]
    public void RestoreStageSquattingAndPromotionEditsArePreserved()
    {
        using var target = new TemporaryHarnessTarget();
        var configurationPath = Path.Combine(
            target.GameDirectory,
            "community_patch_settings.toml");
        File.WriteAllText(configurationPath, "baseline");
        string? squattedStage = null;
        using (var campaign = new RestorableGameInstallCampaign(
            target.GameDirectory,
            new MutableGameProcessInspector(target.GameDirectory),
            (kind, path) =>
            {
                if (kind == DirectGameMutationKind.WriteRestoreStage)
                {
                    squattedStage = path;
                    File.WriteAllText(path, "sentinel");
                }
            }))
        {
            Assert.ThrowsException<IOException>(() =>
                campaign.WriteGameFileAtomically(
                    "community_patch_settings.toml",
                    "bridge"u8.ToArray()));
            Assert.AreEqual("sentinel", File.ReadAllText(squattedStage!));
            Assert.AreEqual("baseline", File.ReadAllText(configurationPath));
        }

        File.Delete(squattedStage!);
        var changed = false;
        using var promotionCampaign = new RestorableGameInstallCampaign(
            target.GameDirectory,
            new MutableGameProcessInspector(target.GameDirectory),
            (kind, _) =>
            {
                if (kind == DirectGameMutationKind.PromoteRestoreDestination && !changed)
                {
                    changed = true;
                    File.WriteAllText(configurationPath, "external-edit");
                }
            });
        Assert.ThrowsException<InvalidOperationException>(() =>
            promotionCampaign.WriteGameFileAtomically(
                "community_patch_settings.toml",
                "bridge"u8.ToArray()));
        Assert.AreEqual("external-edit", File.ReadAllText(configurationPath));
    }

    [DataTestMethod]
    [TestCategory("Deterministic")]
    [DataRow(false)]
    [DataRow(true)]
    public void ModifiedRestoreStageIsPreserved(bool metadataOnly)
    {
        using var target = new TemporaryHarnessTarget();
        string? stagePath = null;
        using var campaign = new RestorableGameInstallCampaign(
            target.GameDirectory,
            new MutableGameProcessInspector(target.GameDirectory),
            (kind, path) =>
            {
                if (kind != DirectGameMutationKind.PromoteRestoreStage)
                {
                    return;
                }
                stagePath = path;
                if (metadataOnly)
                {
                    File.SetLastWriteTimeUtc(
                        path,
                        File.GetLastWriteTimeUtc(path).AddMinutes(1));
                }
                else
                {
                    File.WriteAllText(path, "external-stage");
                }
            });

        Assert.ThrowsException<InvalidOperationException>(() =>
            campaign.WriteGameFileAtomically(
                "community_patch_settings.toml",
                "bridge"u8.ToArray()));

        Assert.IsNotNull(stagePath);
        Assert.IsTrue(File.Exists(stagePath));
        if (!metadataOnly)
        {
            Assert.AreEqual("external-stage", File.ReadAllText(stagePath));
        }
    }

    [DataTestMethod]
    [TestCategory("Deterministic")]
    [DataRow(false)]
    [DataRow(true)]
    public void FinalDirectPromotionRecheckPreservesPathReplacement(bool replaceStage)
    {
        using var target = new TemporaryHarnessTarget();
        var configurationPath = Path.Combine(
            target.GameDirectory,
            "community_patch_settings.toml");
        var baseline = "baseline"u8.ToArray();
        File.WriteAllBytes(configurationPath, baseline);
        string? replacedStage = null;
        using var campaign = new RestorableGameInstallCampaign(
            target.GameDirectory,
            new MutableGameProcessInspector(target.GameDirectory),
            beforePromotionCommit: (stagePath, destinationPath) =>
            {
                var replacementPath = replaceStage ? stagePath : destinationPath;
                var replacement = replaceStage ? "external-stage"u8.ToArray() : baseline;
                File.Delete(replacementPath);
                File.WriteAllBytes(replacementPath, replacement);
                if (replaceStage)
                {
                    replacedStage = replacementPath;
                }
            });

        Assert.ThrowsException<InvalidOperationException>(() =>
            campaign.WriteGameFileAtomically(
                "community_patch_settings.toml",
                "bridge"u8.ToArray()));

        CollectionAssert.AreEqual(baseline, File.ReadAllBytes(configurationPath));
        if (replaceStage)
        {
            Assert.IsNotNull(replacedStage);
            Assert.AreEqual("external-stage", File.ReadAllText(replacedStage));
        }
    }

    [TestMethod]
    [TestCategory("Deterministic")]
    public void SameByteExternalReplacementDoesNotInheritCampaignOwnership()
    {
        using var target = new TemporaryHarnessTarget();
        var configurationPath = Path.Combine(
            target.GameDirectory,
            "community_patch_settings.toml");
        var contents = "bridge"u8.ToArray();
        using var campaign = new RestorableGameInstallCampaign(
            target.GameDirectory,
            new MutableGameProcessInspector(target.GameDirectory));
        campaign.WriteGameFileAtomically(
            "community_patch_settings.toml",
            contents);
        File.Delete(configurationPath);
        File.WriteAllBytes(configurationPath, contents);

        Assert.ThrowsException<InvalidOperationException>(() =>
            campaign.RecordOwnedGameFileRevision(
                "community_patch_settings.toml",
                contents));
        Assert.ThrowsException<InvalidOperationException>(campaign.RestoreConfigurationBaseline);
        CollectionAssert.AreEqual(contents, File.ReadAllBytes(configurationPath));
    }

    [DataTestMethod]
    [TestCategory("Deterministic")]
    [DataRow((int)DirectGameMutationKind.WriteTomlTemporary, 1)]
    [DataRow((int)DirectGameMutationKind.WriteTomlTemporary, 2)]
    [DataRow((int)DirectGameMutationKind.PromoteTomlTemporary, 1)]
    [DataRow((int)DirectGameMutationKind.DeleteTomlTemporary, 1)]
    public async Task AtomicTomlBoundariesRecheckTheExactInstall(
        int boundary,
        int blockedOccurrence)
    {
        var blockedBoundary = (DirectGameMutationKind)boundary;
        using var target = new TemporaryHarnessTarget();
        var configurationPath = Path.Combine(
            target.GameDirectory,
            "community_patch_settings.toml");
        var original = "[graphics]\nfree_resize = true\n"u8.ToArray();
        var changed = "[graphics]\nfree_resize = false\n"u8.ToArray();
        File.WriteAllBytes(configurationPath, original);
        var inspector = new MutableGameProcessInspector(target.GameDirectory);
        var occurrence = 0;
        using var campaign = new RestorableGameInstallCampaign(
            target.GameDirectory,
            inspector,
            (kind, _) =>
            {
                if (kind == blockedBoundary)
                {
                    occurrence++;
                    if (occurrence == blockedOccurrence)
                    {
                        inspector.SelectedState = GameProcessInspectionState.RunningTarget;
                    }
                }
            });
        var store = new AtomicTomlStore(
            blockedBoundary == DirectGameMutationKind.DeleteTomlTemporary
                ? (_, _, _) => ValueTask.FromException(
                    new IOException("Injected pre-promotion failure."))
                : null,
            retainAdjacentBackup: false,
            mutationAdmission: campaign.AtomicTomlMutationAdmission);

        try
        {
            await store.SaveDocumentAsync(configurationPath, original, changed);
        }
        catch (InvalidOperationException)
        {
            // The harness process admission is expected to reject the mutation.
        }

        CollectionAssert.AreEqual(original, File.ReadAllBytes(configurationPath));
        var stages = Directory.GetFiles(
            target.GameDirectory,
            ".community_patch_settings.toml.*.tmp");
        Assert.AreEqual(
            blockedBoundary == DirectGameMutationKind.WriteTomlTemporary
                && blockedOccurrence == 1
                    ? 0
                    : 1,
            stages.Length);
        inspector.SelectedState = GameProcessInspectionState.NotRunning;
        campaign.EmergencyRestore();
        Assert.AreEqual(0, Directory.GetFiles(
            target.GameDirectory,
            ".community_patch_settings.toml.*.tmp").Length);
        campaign.AssertBaseline("Atomic boundary recovery did not restore the exact baseline.");
    }

    [DataTestMethod]
    [TestCategory("Deterministic")]
    [DataRow(false)]
    [DataRow(true)]
    public async Task AtomicPromotionPreservesModifiedStage(bool metadataOnly)
    {
        using var target = new TemporaryHarnessTarget();
        var configurationPath = Path.Combine(
            target.GameDirectory,
            "community_patch_settings.toml");
        var original = "[graphics]\nfree_resize = true\n"u8.ToArray();
        var changed = "[graphics]\nfree_resize = false\n"u8.ToArray();
        File.WriteAllBytes(configurationPath, original);
        string? stagePath = null;
        using var campaign = new RestorableGameInstallCampaign(
            target.GameDirectory,
            new MutableGameProcessInspector(target.GameDirectory));
        var store = new AtomicTomlStore(
            (temporaryPath, _, _) =>
            {
                stagePath = temporaryPath;
                if (metadataOnly)
                {
                    File.SetLastWriteTimeUtc(
                        temporaryPath,
                        File.GetLastWriteTimeUtc(temporaryPath).AddMinutes(1));
                }
                else
                {
                    File.WriteAllText(temporaryPath, "external-stage");
                }
                return ValueTask.CompletedTask;
            },
            retainAdjacentBackup: false,
            mutationAdmission: campaign.AtomicTomlMutationAdmission);

        var result = await store.SaveDocumentAsync(configurationPath, original, changed);

        Assert.AreEqual(AtomicTomlWriteState.IoFailure, result.State, result.Error);
        CollectionAssert.AreEqual(original, File.ReadAllBytes(configurationPath));
        Assert.IsNotNull(stagePath);
        Assert.IsTrue(File.Exists(stagePath));
    }

    [TestMethod]
    [TestCategory("Deterministic")]
    public async Task AtomicPromotionPreservesSameByteDestinationReplacement()
    {
        using var target = new TemporaryHarnessTarget();
        var configurationPath = Path.Combine(
            target.GameDirectory,
            "community_patch_settings.toml");
        var original = "[graphics]\nfree_resize = true\n"u8.ToArray();
        var changed = "[graphics]\nfree_resize = false\n"u8.ToArray();
        File.WriteAllBytes(configurationPath, original);
        using var campaign = new RestorableGameInstallCampaign(
            target.GameDirectory,
            new MutableGameProcessInspector(target.GameDirectory));
        var store = new AtomicTomlStore(
            (_, destinationPath, _) =>
            {
                File.Delete(destinationPath);
                File.WriteAllBytes(destinationPath, original);
                return ValueTask.CompletedTask;
            },
            retainAdjacentBackup: false,
            mutationAdmission: campaign.AtomicTomlMutationAdmission);

        var result = await store.SaveDocumentAsync(configurationPath, original, changed);

        Assert.AreEqual(AtomicTomlWriteState.IoFailure, result.State, result.Error);
        CollectionAssert.AreEqual(original, File.ReadAllBytes(configurationPath));
    }

    [DataTestMethod]
    [TestCategory("Deterministic")]
    [DataRow(false)]
    [DataRow(true)]
    public async Task FinalAtomicPromotionRecheckPreservesPathReplacement(bool replaceStage)
    {
        using var target = new TemporaryHarnessTarget();
        var configurationPath = Path.Combine(
            target.GameDirectory,
            "community_patch_settings.toml");
        var original = "[graphics]\nfree_resize = true\n"u8.ToArray();
        var changed = "[graphics]\nfree_resize = false\n"u8.ToArray();
        File.WriteAllBytes(configurationPath, original);
        using var campaign = new RestorableGameInstallCampaign(
            target.GameDirectory,
            new MutableGameProcessInspector(target.GameDirectory));
        var admission = new FinalPromotionSwapAdmission(
            campaign.AtomicTomlMutationAdmission,
            replaceStage,
            original);
        var store = new AtomicTomlStore(
            beforeReplace: null,
            retainAdjacentBackup: false,
            mutationAdmission: admission);

        var result = await store.SaveDocumentAsync(configurationPath, original, changed);

        Assert.AreEqual(AtomicTomlWriteState.IoFailure, result.State, result.Error);
        CollectionAssert.AreEqual(original, File.ReadAllBytes(configurationPath));
        if (replaceStage)
        {
            Assert.IsNotNull(admission.ReplacedPath);
            Assert.AreEqual("external-stage", File.ReadAllText(admission.ReplacedPath));
        }
    }

    [TestMethod]
    [TestCategory("Deterministic")]
    public async Task StageRegistrationFailureDeletesThroughCreationHandle()
    {
        using var target = new TemporaryHarnessTarget();
        using (var campaign = new RestorableGameInstallCampaign(
            target.GameDirectory,
            new MutableGameProcessInspector(target.GameDirectory),
            beforeStageReceipt: _ => throw new IOException("Injected receipt failure.")))
        {
            Assert.ThrowsException<IOException>(() =>
                campaign.WriteGameFileAtomically(
                    "community_patch_settings.toml",
                    "bridge"u8.ToArray()));
            Assert.AreEqual(0, Directory.GetFiles(
                target.GameDirectory,
                ".stfc-bridge-integration-*.restore-stage").Length);
        }

        var configurationPath = Path.Combine(
            target.GameDirectory,
            "community_patch_settings.toml");
        var original = "[graphics]\nfree_resize = true\n"u8.ToArray();
        var changed = "[graphics]\nfree_resize = false\n"u8.ToArray();
        File.WriteAllBytes(configurationPath, original);
        using var atomicCampaign = new RestorableGameInstallCampaign(
            target.GameDirectory,
            new MutableGameProcessInspector(target.GameDirectory));
        var store = new AtomicTomlStore(
            beforeReplace: null,
            retainAdjacentBackup: false,
            mutationAdmission: new ThrowingTemporaryCreatedAdmission(
                atomicCampaign.AtomicTomlMutationAdmission));

        var result = await store.SaveDocumentAsync(configurationPath, original, changed);

        Assert.AreEqual(AtomicTomlWriteState.IoFailure, result.State, result.Error);
        Assert.AreEqual(0, Directory.GetFiles(
            target.GameDirectory,
            ".community_patch_settings.toml.*.tmp").Length);
        CollectionAssert.AreEqual(original, File.ReadAllBytes(configurationPath));
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

    [TestMethod]
    [TestCategory("Deterministic")]
    public void CampaignSetupAndDisposeFailuresCrossSanitizedBoundaries()
    {
        const string sentinelGame = @"E:\sentinel-player\STFC\default\game";
        var setup = Assert.ThrowsException<AssertFailedException>(() =>
            OpenCampaignThroughSanitizedBoundary(
                sentinelGame,
                _ => throw new IOException($"Could not inspect {sentinelGame}.")));
        Assert.IsNull(setup.InnerException);
        Assert.IsFalse(
            setup.ToString().Contains(sentinelGame, StringComparison.OrdinalIgnoreCase));

        using var target = new TemporaryHarnessTarget();
        var campaign = new RestorableGameInstallCampaign(
            target.GameDirectory,
            new MutableGameProcessInspector(target.GameDirectory),
            beforeStateDirectoryDelete: path =>
                throw new IOException($"Could not delete {path}."));
        var stateDirectory = campaign.StateDirectory;
        try
        {
            var disposal = Assert.ThrowsException<AssertFailedException>(campaign.Dispose);
            Assert.IsNull(disposal.InnerException);
            Assert.IsFalse(
                disposal.ToString().Contains(stateDirectory, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            if (Directory.Exists(stateDirectory))
            {
                Directory.Delete(stateDirectory, recursive: true);
            }
        }
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

    private sealed class ThrowingTemporaryCreatedAdmission(
        IAtomicTomlMutationAdmission inner) : IAtomicTomlMutationAdmission
    {
        public ValueTask AdmitAsync(
            AtomicTomlMutationBoundary boundary,
            string temporaryPath,
            string destinationPath,
            CancellationToken cancellationToken) =>
            inner.AdmitAsync(boundary, temporaryPath, destinationPath, cancellationToken);

        public void TemporaryCreated(string temporaryPath, ExactFileRevision revision) =>
            throw new IOException("Injected receipt failure.");

        public void TemporaryCompleted(string temporaryPath, ExactFileRevision revision) =>
            inner.TemporaryCompleted(temporaryPath, revision);

        public void TemporaryRemoved(string temporaryPath) =>
            inner.TemporaryRemoved(temporaryPath);

        public void DestinationCommitted(string destinationPath, ExactFileRevision revision) =>
            inner.DestinationCommitted(destinationPath, revision);

        public void VerifyCommitAllowed(
            AtomicTomlMutationBoundary boundary,
            string temporaryPath,
            string destinationPath) =>
            inner.VerifyCommitAllowed(boundary, temporaryPath, destinationPath);
    }

    private sealed class FinalPromotionSwapAdmission(
        IAtomicTomlMutationAdmission inner,
        bool replaceStage,
        byte[] destinationContents) : IAtomicTomlMutationAdmission
    {
        private bool replaced;

        public string? ReplacedPath { get; private set; }

        public ValueTask AdmitAsync(
            AtomicTomlMutationBoundary boundary,
            string temporaryPath,
            string destinationPath,
            CancellationToken cancellationToken) =>
            inner.AdmitAsync(boundary, temporaryPath, destinationPath, cancellationToken);

        public void TemporaryCreated(string temporaryPath, ExactFileRevision revision) =>
            inner.TemporaryCreated(temporaryPath, revision);

        public void TemporaryCompleted(string temporaryPath, ExactFileRevision revision) =>
            inner.TemporaryCompleted(temporaryPath, revision);

        public void TemporaryRemoved(string temporaryPath) =>
            inner.TemporaryRemoved(temporaryPath);

        public void DestinationCommitted(string destinationPath, ExactFileRevision revision) =>
            inner.DestinationCommitted(destinationPath, revision);

        public void VerifyCommitAllowed(
            AtomicTomlMutationBoundary boundary,
            string temporaryPath,
            string destinationPath)
        {
            inner.VerifyCommitAllowed(boundary, temporaryPath, destinationPath);
            if (replaced || boundary != AtomicTomlMutationBoundary.Promotion)
            {
                return;
            }
            replaced = true;
            ReplacedPath = replaceStage ? temporaryPath : destinationPath;
            File.Delete(ReplacedPath);
            File.WriteAllBytes(
                ReplacedPath,
                replaceStage ? "external-stage"u8.ToArray() : destinationContents);
        }
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
