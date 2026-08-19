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
    public void RuntimeManifestFixtureRestoresItsExactCampaignBaseline()
    {
        using var target = new TemporaryHarnessTarget();
        var manifestPath = Path.Combine(target.GameDirectory, RuntimeManifestFileName);
        var baseline = "{\"source\":\"human\"}\r\n"u8.ToArray();
        File.WriteAllBytes(manifestPath, baseline);
        var baselineTimestamp = new DateTime(2026, 8, 18, 12, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(manifestPath, baselineTimestamp);
        using var campaign = new RestorableGameInstallCampaign(
            target.GameDirectory,
            new MutableGameProcessInspector(target.GameDirectory));

        campaign.WriteGameFileAtomically(
            RuntimeManifestFileName,
            "{\"source\":\"test-fixture\"}\n"u8.ToArray());
        campaign.RestoreProtectedBaseline();

        CollectionAssert.AreEqual(baseline, File.ReadAllBytes(manifestPath));
        Assert.AreEqual(baselineTimestamp, File.GetLastWriteTimeUtc(manifestPath));
        campaign.AssertBaseline("Runtime-manifest cleanup did not restore its exact baseline.");
    }

    [DataTestMethod]
    [TestCategory("Deterministic")]
    [DataRow("version.dll")]
    [DataRow(RuntimeManifestFileName)]
    public void AdoptedHumanRestorationCannotInheritCampaignCleanupOwnership(string fileName)
    {
        using var target = new TemporaryHarnessTarget();
        var protectedPath = Path.Combine(target.GameDirectory, fileName);
        var baseline = "human-baseline"u8.ToArray();
        var external = "human-replacement"u8.ToArray();
        File.WriteAllBytes(protectedPath, baseline);
        using var campaign = new RestorableGameInstallCampaign(
            target.GameDirectory,
            new MutableGameProcessInspector(target.GameDirectory));
        var stagePath = Path.Combine(target.GameDirectory, $".{fileName}.adopted-restore-stage");
        File.WriteAllBytes(stagePath, external);

        campaign.CaptureDeploymentPromotion(stagePath, fileName);
        File.Delete(protectedPath);
        File.Move(stagePath, protectedPath);

        var ownershipFailure = Assert.ThrowsException<InvalidOperationException>(() =>
            campaign.CommitAdoptedRestoration(fileName));

        StringAssert.Contains(ownershipFailure.Message, "preserved without granting cleanup ownership");
        Assert.ThrowsException<InvalidOperationException>(campaign.EmergencyRestore);
        CollectionAssert.AreEqual(external, File.ReadAllBytes(protectedPath));
    }

    [TestMethod]
    [TestCategory("Deterministic")]
    public void CleanProviderJourneyRejectsAnOrphanRuntimeManifest()
    {
        using var target = new TemporaryHarnessTarget();
        Assert.IsTrue(IsCleanProviderJourneyTarget(target.GameDirectory));

        File.WriteAllText(
            Path.Combine(target.GameDirectory, RuntimeManifestFileName),
            "{\"managedBy\":\"external\"}\r\n");

        Assert.IsFalse(
            IsCleanProviderJourneyTarget(target.GameDirectory),
            "An orphan runtime manifest must route away from the clean install/switch journey.");
    }

    [TestMethod]
    [TestCategory("Deterministic")]
    public void FinalResidueAuditRejectsIsolatedRollbackBytes()
    {
        using var target = new TemporaryHarnessTarget();
        using var campaign = new RestorableGameInstallCampaign(
            target.GameDirectory,
            new MutableGameProcessInspector(target.GameDirectory));
        var rollbackDirectory = Path.Combine(campaign.StateDirectory, "rollback", "transaction");
        Directory.CreateDirectory(rollbackDirectory);
        File.WriteAllBytes(Path.Combine(rollbackDirectory, "version.dll"), [1, 2, 3]);

        var failure = Assert.ThrowsException<AssertFailedException>(() =>
            campaign.AssertNoFinalResidue([]));

        StringAssert.Contains(failure.Message, "transaction staging or rollback bytes");
        campaign.AssertBaseline("Residue detection changed the game target.");
    }

    [TestMethod]
    [TestCategory("Deterministic")]
    public void FinalResidueAuditRejectsAnOrphanCopyStageOwnershipReceipt()
    {
        using var target = new TemporaryHarnessTarget();
        using var campaign = new RestorableGameInstallCampaign(
            target.GameDirectory,
            new MutableGameProcessInspector(target.GameDirectory));
        var ownershipDirectory = Path.Combine(campaign.StateDirectory, "copy-stage-ownership");
        Directory.CreateDirectory(ownershipDirectory);
        File.WriteAllText(Path.Combine(ownershipDirectory, "orphan.json"), "{}");

        var failure = Assert.ThrowsException<AssertFailedException>(() =>
            campaign.AssertNoFinalResidue([]));

        StringAssert.Contains(failure.Message, "transaction staging or rollback bytes");
        campaign.AssertBaseline("Residue detection changed the game target.");
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
    public void ExactPromotionLockPreservesALateSameByteDestinationReplacement()
    {
        using var target = new TemporaryHarnessTarget();
        var configurationPath = Path.Combine(
            target.GameDirectory,
            "community_patch_settings.toml");
        var baseline = "baseline"u8.ToArray();
        File.WriteAllBytes(configurationPath, baseline);
        CandidateFileIdentity? replacementIdentity = null;
        using var campaign = new RestorableGameInstallCampaign(
            target.GameDirectory,
            new MutableGameProcessInspector(target.GameDirectory),
            beforeExactPromotionLock: (_, destinationPath) =>
            {
                File.Delete(destinationPath);
                File.WriteAllBytes(destinationPath, baseline);
                using var replacement = ExactFileMutation.Open(destinationPath);
                replacementIdentity = replacement.Identity;
            });

        Assert.ThrowsException<InvalidOperationException>(() =>
            campaign.WriteGameFileAtomically(
                "community_patch_settings.toml",
                "bridge"u8.ToArray()));

        CollectionAssert.AreEqual(baseline, File.ReadAllBytes(configurationPath));
        using var preserved = ExactFileMutation.Open(configurationPath);
        Assert.AreEqual(replacementIdentity, preserved.Identity);
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
    public async Task PartialStageWritesRemainExactlyOwnedForCleanup()
    {
        using var target = new TemporaryHarnessTarget();
        var configurationPath = Path.Combine(
            target.GameDirectory,
            "community_patch_settings.toml");
        var original = "[graphics]\nfree_resize = true\n"u8.ToArray();
        var changed = "[graphics]\nfree_resize = false\n"u8.ToArray();
        File.WriteAllBytes(configurationPath, original);

        using (var campaign = new RestorableGameInstallCampaign(
            target.GameDirectory,
            new MutableGameProcessInspector(target.GameDirectory),
            beforeStageFlush: _ => throw new IOException("Injected direct flush failure.")))
        {
            Assert.ThrowsException<IOException>(() =>
                campaign.WriteGameFileAtomically(
                    "community_patch_settings.toml",
                    changed));
            Assert.AreEqual(0, Directory.GetFiles(
                target.GameDirectory,
                ".stfc-bridge-integration-*.restore-stage").Length);
            CollectionAssert.AreEqual(original, File.ReadAllBytes(configurationPath));
        }

        using var atomicCampaign = new RestorableGameInstallCampaign(
            target.GameDirectory,
            new MutableGameProcessInspector(target.GameDirectory));
        var store = new AtomicTomlStore(
            beforeReplace: null,
            retainAdjacentBackup: false,
            mutationAdmission: new HookedMutationAdmission(
                atomicCampaign.AtomicTomlMutationAdmission,
                beforeTemporaryFlush: _ => throw new IOException("Injected atomic flush failure.")));

        var result = await store.SaveDocumentAsync(configurationPath, original, changed);

        Assert.AreEqual(AtomicTomlWriteState.IoFailure, result.State, result.Error);
        Assert.AreEqual(0, Directory.GetFiles(
            target.GameDirectory,
            ".community_patch_settings.toml.*.tmp").Length);
        CollectionAssert.AreEqual(original, File.ReadAllBytes(configurationPath));
    }

    [TestMethod]
    [TestCategory("Deterministic")]
    public async Task ProcessStartAtFinalCommitSeamBlocksDirectAndAtomicPromotion()
    {
        using var target = new TemporaryHarnessTarget();
        var configurationPath = Path.Combine(
            target.GameDirectory,
            "community_patch_settings.toml");
        var original = "[graphics]\nfree_resize = true\n"u8.ToArray();
        var changed = "[graphics]\nfree_resize = false\n"u8.ToArray();
        File.WriteAllBytes(configurationPath, original);

        var directInspector = new MutableGameProcessInspector(target.GameDirectory);
        using (var campaign = new RestorableGameInstallCampaign(
            target.GameDirectory,
            directInspector,
            beforePromotionCommit: (_, _) => directInspector.SetState(
                target.GameDirectory,
                GameProcessInspectionState.RunningTarget)))
        {
            Assert.ThrowsException<InvalidOperationException>(() =>
                campaign.WriteGameFileAtomically(
                    "community_patch_settings.toml",
                    changed));
            CollectionAssert.AreEqual(original, File.ReadAllBytes(configurationPath));
            directInspector.SetState(target.GameDirectory, GameProcessInspectionState.NotRunning);
            campaign.EmergencyRestore();
        }

        var atomicInspector = new MutableGameProcessInspector(target.GameDirectory);
        using var atomicCampaign = new RestorableGameInstallCampaign(
            target.GameDirectory,
            atomicInspector);
        var store = new AtomicTomlStore(
            beforeReplace: null,
            retainAdjacentBackup: false,
            mutationAdmission: new HookedMutationAdmission(
                atomicCampaign.AtomicTomlMutationAdmission,
                beforeCommitValidation: (_, _) => atomicInspector.SetState(
                    target.GameDirectory,
                    GameProcessInspectionState.RunningTarget)));

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            store.SaveDocumentAsync(configurationPath, original, changed));
        CollectionAssert.AreEqual(original, File.ReadAllBytes(configurationPath));
        atomicInspector.SetState(target.GameDirectory, GameProcessInspectionState.NotRunning);
        atomicCampaign.EmergencyRestore();
    }

    [DataTestMethod]
    [TestCategory("Deterministic")]
    [DataRow("save")]
    [DataRow("create")]
    [DataRow("transform")]
    public async Task PostCommitOwnershipFailureReturnsWarningAndRetainsExactRecovery(string operation)
    {
        using var target = new TemporaryHarnessTarget();
        var configurationPath = Path.Combine(
            target.GameDirectory,
            "community_patch_settings.toml");
        var original = "[graphics]\nfree_resize = true\n"u8.ToArray();
        var changed = "[graphics]\nfree_resize = false\n"u8.ToArray();
        if (operation != "create")
        {
            File.WriteAllBytes(configurationPath, original);
        }
        using var campaign = new RestorableGameInstallCampaign(
            target.GameDirectory,
            new MutableGameProcessInspector(target.GameDirectory));
        var store = new AtomicTomlStore(
            beforeReplace: null,
            retainAdjacentBackup: false,
            mutationAdmission: new HookedMutationAdmission(
                campaign.AtomicTomlMutationAdmission,
                afterPromotionBeforeOwnership: _ =>
                    throw new InvalidDataException("Injected ownership confirmation failure.")));

        var result = operation switch
        {
            "save" => await store.SaveDocumentAsync(configurationPath, original, changed),
            "create" => await store.CreateDocumentAsync(configurationPath, changed),
            "transform" => await store.SetOverrideAsync(
                configurationPath,
                "graphics.free_resize",
                "false"),
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };

        Assert.AreEqual(AtomicTomlWriteState.Succeeded, result.State, result.Error);
        Assert.IsFalse(string.IsNullOrWhiteSpace(result.Warning));
        campaign.EmergencyRestore();
        if (operation == "create")
        {
            Assert.IsFalse(File.Exists(configurationPath));
        }
        else
        {
            CollectionAssert.AreEqual(original, File.ReadAllBytes(configurationPath));
        }
    }

    [TestMethod]
    [TestCategory("Deterministic")]
    public async Task SameBytePostCommitReplacementDoesNotInheritAtomicOwnership()
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
            beforeReplace: null,
            retainAdjacentBackup: false,
            mutationAdmission: new HookedMutationAdmission(
                campaign.AtomicTomlMutationAdmission,
                afterPromotionBeforeOwnership: path =>
                {
                    File.Delete(path);
                    File.WriteAllBytes(path, changed);
                }));

        var result = await store.SaveDocumentAsync(configurationPath, original, changed);

        Assert.AreEqual(AtomicTomlWriteState.Succeeded, result.State, result.Error);
        Assert.IsFalse(string.IsNullOrWhiteSpace(result.Warning));
        Assert.ThrowsException<InvalidOperationException>(() => campaign.EmergencyRestore());
        CollectionAssert.AreEqual(changed, File.ReadAllBytes(configurationPath));
    }

    [TestMethod]
    [TestCategory("Deterministic")]
    public async Task SameByteDestinationReplacementBeforeAtomicSnapshotIsNotAdopted()
    {
        using var target = new TemporaryHarnessTarget();
        var configurationPath = Path.Combine(
            target.GameDirectory,
            "community_patch_settings.toml");
        var original = "[graphics]\nfree_resize = true\n"u8.ToArray();
        var changed = "[graphics]\nfree_resize = false\n"u8.ToArray();
        File.WriteAllBytes(configurationPath, original);
        ExactFileRevision baselineRevision;
        using (var exact = ExactFileMutation.Open(configurationPath))
        {
            baselineRevision = exact.CaptureRevision();
        }
        using var campaign = new RestorableGameInstallCampaign(
            target.GameDirectory,
            new MutableGameProcessInspector(target.GameDirectory));
        var replacementPath = Path.Combine(target.GameDirectory, "external-replacement.toml");
        File.WriteAllBytes(replacementPath, original);
        File.SetAttributes(replacementPath, baselineRevision.Attributes);
        File.SetLastWriteTimeUtc(
            replacementPath,
            new DateTime(baselineRevision.LastWriteTimeUtcTicks, DateTimeKind.Utc));
        File.Delete(configurationPath);
        File.Move(replacementPath, configurationPath);
        ExactFileRevision replacementRevision;
        using (var exact = ExactFileMutation.Open(configurationPath))
        {
            replacementRevision = exact.CaptureRevision();
        }
        Assert.AreNotEqual(baselineRevision.Identity, replacementRevision.Identity);
        Assert.AreEqual(baselineRevision.Length, replacementRevision.Length);
        Assert.AreEqual(baselineRevision.Sha256, replacementRevision.Sha256);
        Assert.AreEqual(baselineRevision.Attributes, replacementRevision.Attributes);
        Assert.AreEqual(
            baselineRevision.LastWriteTimeUtcTicks,
            replacementRevision.LastWriteTimeUtcTicks);
        var store = new AtomicTomlStore(
            beforeReplace: null,
            retainAdjacentBackup: false,
            mutationAdmission: campaign.AtomicTomlMutationAdmission);

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            store.SaveDocumentAsync(configurationPath, original, changed));

        CollectionAssert.AreEqual(original, File.ReadAllBytes(configurationPath));
        campaign.EmergencyRestore();
        CollectionAssert.AreEqual(original, File.ReadAllBytes(configurationPath));
        using (var exact = ExactFileMutation.Open(configurationPath))
        {
            Assert.AreEqual(replacementRevision.Identity, exact.Identity);
        }
    }

    [DataTestMethod]
    [TestCategory("Deterministic")]
    [DataRow("process-start")]
    [DataRow("same-byte-replacement")]
    public async Task FailedAbsentSourceSwitchPreservesUnadmittedRollbackTarget(string fault)
    {
        using var target = new TemporaryHarnessTarget();
        var configurationPath = Path.Combine(
            target.GameDirectory,
            "community_patch_settings.toml");
        var targetConfiguration = "[graphics]\nfree_resize = false\n"u8.ToArray();
        var inspector = new MutableGameProcessInspector(target.GameDirectory);
        using var campaign = new RestorableGameInstallCampaign(
            target.GameDirectory,
            inspector);
        var selectionStore = new FailingSelectionStore();
        selectionStore.Save(new("guffawaffle", "stable"));
        var backupStore = new ProviderScopedConfigurationBackupStore(campaign.StateDirectory);
        await backupStore.CreateAsync(new(
            target.GameDirectory,
            "netniv",
            configurationPath,
            targetConfiguration,
            "rollback-safety-seed"));
        var service = new LauncherProviderSourceSwitchService(
            LoadProviderCatalog(),
            selectionStore,
            backupStore,
            backupCompleted: null,
            configurationEvidenceResolver: null,
            atomicTomlMutationAdmission: campaign.AtomicTomlMutationAdmission);
        var preview = service.Preview("netniv", "stable", configurationPath);
        selectionStore.BeforeFailure = () =>
        {
            if (fault == "process-start")
            {
                inspector.SetState(
                    target.GameDirectory,
                    GameProcessInspectionState.RunningTarget);
            }
            else
            {
                File.Delete(configurationPath);
                File.WriteAllBytes(configurationPath, targetConfiguration);
            }
        };
        selectionStore.FailNextSave = true;

        var exception = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            service.ExecuteAsync(preview, preview.ConfirmationText));

        StringAssert.Contains(exception.Message, "rollback also failed");
        CollectionAssert.AreEqual(targetConfiguration, File.ReadAllBytes(configurationPath));
        if (fault == "process-start")
        {
            inspector.SetState(target.GameDirectory, GameProcessInspectionState.NotRunning);
            campaign.EmergencyRestore();
            Assert.IsFalse(File.Exists(configurationPath));
        }
        else
        {
            Assert.ThrowsException<InvalidOperationException>(() => campaign.EmergencyRestore());
            CollectionAssert.AreEqual(targetConfiguration, File.ReadAllBytes(configurationPath));
        }
    }

    [TestMethod]
    [TestCategory("Deterministic")]
    public void DirectPostCommitFailureRetainsProvisionalExactOwnership()
    {
        using var target = new TemporaryHarnessTarget();
        var configurationPath = Path.Combine(
            target.GameDirectory,
            "community_patch_settings.toml");
        var original = "baseline"u8.ToArray();
        File.WriteAllBytes(configurationPath, original);
        var failOwnershipConfirmation = true;
        using var campaign = new RestorableGameInstallCampaign(
            target.GameDirectory,
            new MutableGameProcessInspector(target.GameDirectory),
            afterPromotionBeforeOwnership: _ =>
            {
                if (failOwnershipConfirmation)
                {
                    failOwnershipConfirmation = false;
                    throw new IOException("Injected direct ownership confirmation failure.");
                }
            });

        Assert.ThrowsException<IOException>(() =>
            campaign.WriteGameFileAtomically(
                "community_patch_settings.toml",
                "bridge"u8.ToArray()));

        campaign.EmergencyRestore();
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

        public void BeforeTemporaryFlush(string temporaryPath) =>
            inner.BeforeTemporaryFlush(temporaryPath);

        public void TemporaryRemoved(string temporaryPath) =>
            inner.TemporaryRemoved(temporaryPath);

        public void BeforeCommitValidation(
            string temporaryPath,
            string destinationPath) =>
            inner.BeforeCommitValidation(temporaryPath, destinationPath);

        public void DestinationObserved(string destinationPath, ExactFileRevision revision) =>
            inner.DestinationObserved(destinationPath, revision);

        public void DestinationPrepared(string destinationPath, ExactFileRevision revision) =>
            inner.DestinationPrepared(destinationPath, revision);

        public void AfterPromotionBeforeOwnership(string destinationPath) =>
            inner.AfterPromotionBeforeOwnership(destinationPath);

        public void DestinationCommitted(string destinationPath, ExactFileRevision revision) =>
            inner.DestinationCommitted(destinationPath, revision);

        public void DeleteCreatedDestination(string destinationPath, string expectedSha256) =>
            inner.DeleteCreatedDestination(destinationPath, expectedSha256);

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

        public void BeforeTemporaryFlush(string temporaryPath) =>
            inner.BeforeTemporaryFlush(temporaryPath);

        public void TemporaryRemoved(string temporaryPath) =>
            inner.TemporaryRemoved(temporaryPath);

        public void BeforeCommitValidation(
            string temporaryPath,
            string destinationPath)
        {
            inner.BeforeCommitValidation(temporaryPath, destinationPath);
            if (replaced)
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

        public void DestinationObserved(string destinationPath, ExactFileRevision revision) =>
            inner.DestinationObserved(destinationPath, revision);

        public void DestinationPrepared(string destinationPath, ExactFileRevision revision) =>
            inner.DestinationPrepared(destinationPath, revision);

        public void AfterPromotionBeforeOwnership(string destinationPath) =>
            inner.AfterPromotionBeforeOwnership(destinationPath);

        public void DestinationCommitted(string destinationPath, ExactFileRevision revision) =>
            inner.DestinationCommitted(destinationPath, revision);

        public void DeleteCreatedDestination(string destinationPath, string expectedSha256) =>
            inner.DeleteCreatedDestination(destinationPath, expectedSha256);

        public void VerifyCommitAllowed(
            AtomicTomlMutationBoundary boundary,
            string temporaryPath,
            string destinationPath)
        {
            inner.VerifyCommitAllowed(boundary, temporaryPath, destinationPath);
        }
    }

    private sealed class HookedMutationAdmission(
        IAtomicTomlMutationAdmission inner,
        Action<string>? beforeTemporaryFlush = null,
        Action<string, string>? beforeCommitValidation = null,
        Action<string>? afterPromotionBeforeOwnership = null) : IAtomicTomlMutationAdmission
    {
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

        public void BeforeTemporaryFlush(string temporaryPath)
        {
            inner.BeforeTemporaryFlush(temporaryPath);
            beforeTemporaryFlush?.Invoke(temporaryPath);
        }

        public void TemporaryRemoved(string temporaryPath) =>
            inner.TemporaryRemoved(temporaryPath);

        public void BeforeCommitValidation(string temporaryPath, string destinationPath)
        {
            inner.BeforeCommitValidation(temporaryPath, destinationPath);
            beforeCommitValidation?.Invoke(temporaryPath, destinationPath);
        }

        public void DestinationObserved(string destinationPath, ExactFileRevision revision) =>
            inner.DestinationObserved(destinationPath, revision);

        public void DestinationPrepared(string destinationPath, ExactFileRevision revision) =>
            inner.DestinationPrepared(destinationPath, revision);

        public void AfterPromotionBeforeOwnership(string destinationPath)
        {
            afterPromotionBeforeOwnership?.Invoke(destinationPath);
            inner.AfterPromotionBeforeOwnership(destinationPath);
        }

        public void DestinationCommitted(string destinationPath, ExactFileRevision revision) =>
            inner.DestinationCommitted(destinationPath, revision);

        public void DeleteCreatedDestination(string destinationPath, string expectedSha256) =>
            inner.DeleteCreatedDestination(destinationPath, expectedSha256);

        public void VerifyCommitAllowed(
            AtomicTomlMutationBoundary boundary,
            string temporaryPath,
            string destinationPath) =>
            inner.VerifyCommitAllowed(boundary, temporaryPath, destinationPath);
    }

    private sealed class FailingSelectionStore : ILauncherProviderSelectionStore
    {
        private LauncherProviderSelection? selection;

        public bool FailNextSave { get; set; }

        public Action? BeforeFailure { get; set; }

        public LauncherProviderSelection? Load() => selection;

        public void Save(LauncherProviderSelection value)
        {
            selection = value;
            if (!FailNextSave)
            {
                return;
            }
            FailNextSave = false;
            BeforeFailure?.Invoke();
            throw new IOException("Injected provider-selection write failure.");
        }

        public void Clear() => selection = null;
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
