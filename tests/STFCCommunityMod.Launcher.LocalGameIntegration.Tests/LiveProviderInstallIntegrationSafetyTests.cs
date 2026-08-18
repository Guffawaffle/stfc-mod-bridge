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
            campaign.RecordOwnedGameFileRevision(
                "community_patch_settings.toml",
                changed);
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
                campaign));

        Assert.AreEqual(0, mutations, "Unsafe preflight performed a harness-owned game mutation.");
        campaign.AssertBaseline("Unsafe preflight changed the protected game target.");
    }

    [DataTestMethod]
    [TestCategory("Deterministic")]
    [DataRow("[graphics]\nfree_resize = true\nfree_resize = false\n")]
    [DataRow("[graphics]\nfree_resize = \"not-a-boolean\"\n")]
    public async Task UnsafeCleanBaselinePreflightCreatesNoToml(string configuration)
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

        await Assert.ThrowsExceptionAsync<AssertFailedException>(() =>
            RunConfigurationRestoreRecoveryAsync(
                target.GameDirectory,
                configurationPath,
                campaign.StateDirectory,
                baselineConfiguration: null,
                campaign,
                createdConfigurationContents: System.Text.Encoding.UTF8.GetBytes(configuration)));

        Assert.AreEqual(0, mutations, "Unsafe clean-baseline preflight reached a game mutation.");
        Assert.IsFalse(File.Exists(configurationPath));
        campaign.AssertBaseline("Unsafe clean-baseline preflight changed the game target.");
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
        var mutationBoundaries = 0;

        using var campaign = new RestorableGameInstallCampaign(
            target.GameDirectory,
            inspector,
            (kind, _) =>
            {
                mutationBoundaries++;
                if (kind == DirectGameMutationKind.WriteTomlTemporary)
                {
                    inspector.SelectedState = GameProcessInspectionState.RunningTarget;
                }
            });

        var failure = await CaptureFailureAsync(() => RunConfigurationRestoreRecoveryAsync(
                target.GameDirectory,
                configurationPath,
                campaign.StateDirectory,
                original,
                campaign));

        Assert.IsInstanceOfType<InvalidOperationException>(failure, failure.ToString());

        Assert.AreEqual(1, mutationBoundaries);
        Assert.AreEqual(
            0,
            Directory.GetFiles(target.GameDirectory, ".community_patch_settings.toml.*.tmp").Length);
        campaign.AssertBaseline("The target changed after its game process started.");
    }

    [TestMethod]
    [TestCategory("Deterministic")]
    public async Task ExactInstallStartingBeforeRepositoryPromotionPreservesTargetAndRecoversStage()
    {
        using var target = new TemporaryHarnessTarget();
        var configurationPath = Path.Combine(
            target.GameDirectory,
            "community_patch_settings.toml");
        var original = "[graphics]\nfree_resize = true\n"u8.ToArray();
        File.WriteAllBytes(configurationPath, original);
        var inspector = new MutableGameProcessInspector(target.GameDirectory);
        var promotionObserved = false;
        string stateDirectory;
        using (var campaign = new RestorableGameInstallCampaign(
                   target.GameDirectory,
                   inspector,
                   (kind, _) =>
                   {
                       if (kind == DirectGameMutationKind.PromoteTomlTemporary)
                       {
                           promotionObserved = true;
                           inspector.SelectedState = GameProcessInspectionState.RunningTarget;
                       }
                   }))
        {
            stateDirectory = campaign.StateDirectory;
            campaign.PreserveStateForRecovery();
            var failure = await CaptureFailureAsync(() => RunConfigurationRestoreRecoveryAsync(
                target.GameDirectory,
                configurationPath,
                campaign.StateDirectory,
                original,
                campaign));

            Assert.IsInstanceOfType<InvalidOperationException>(failure, failure.ToString());
            Assert.IsTrue(promotionObserved);
            CollectionAssert.AreEqual(original, File.ReadAllBytes(configurationPath));
            Assert.AreEqual(
                1,
                Directory.GetFiles(target.GameDirectory, ".community_patch_settings.toml.*.tmp").Length);
        }

        try
        {
            inspector.SelectedState = GameProcessInspectionState.NotRunning;
            RestorableGameInstallCampaign.RecoverRetainedStages(
                target.GameDirectory,
                stateDirectory,
                inspector);
            CollectionAssert.AreEqual(original, File.ReadAllBytes(configurationPath));
            Assert.AreEqual(
                0,
                Directory.GetFiles(target.GameDirectory, ".community_patch_settings.toml.*.tmp").Length);
        }
        finally
        {
            if (Directory.Exists(stateDirectory))
            {
                Directory.Delete(stateDirectory, recursive: true);
            }
        }
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
        var mutationBoundaries = 0;
        var owned = "[graphics]\nfree_resize = true\n"u8.ToArray();

        using var campaign = new RestorableGameInstallCampaign(
            target.GameDirectory,
            inspector,
            (_, _) => mutationBoundaries++);
        inspector.SelectedState = GameProcessInspectionState.RunningTarget;

        Assert.ThrowsException<InvalidOperationException>(() =>
            campaign.WriteGameFileAtomically(
                "community_patch_settings.toml",
                owned));
        Assert.IsFalse(File.Exists(configurationPath));

        File.WriteAllBytes(configurationPath, owned);
        campaign.RecordOwnedGameFileRevision("community_patch_settings.toml", owned);
        Assert.ThrowsException<InvalidOperationException>(campaign.RestoreConfigurationBaseline);

        Assert.AreEqual(2, mutationBoundaries);
        Assert.IsTrue(File.Exists(configurationPath));
        inspector.SelectedState = GameProcessInspectionState.NotRunning;
        campaign.RestoreConfigurationBaseline();
        campaign.AssertBaseline("Closed-target cleanup did not remove the harness-created TOML.");
    }

    [DataTestMethod]
    [TestCategory("Deterministic")]
    [DataRow("version.dll")]
    [DataRow("community_patch_settings.toml")]
    public void EmergencyRestorePreservesExternallyChangedProtectedBytes(string fileName)
    {
        using var target = new TemporaryHarnessTarget();
        var path = Path.Combine(target.GameDirectory, fileName);
        var original = "protected baseline"u8.ToArray();
        var campaignRevision = "campaign revision"u8.ToArray();
        var externalRevision = "external revision"u8.ToArray();
        File.WriteAllBytes(path, original);
        using var campaign = new RestorableGameInstallCampaign(
            target.GameDirectory,
            new MutableGameProcessInspector(target.GameDirectory));
        File.WriteAllBytes(path, campaignRevision);
        campaign.RecordOwnedGameFileRevision(fileName, campaignRevision);
        File.WriteAllBytes(path, externalRevision);

        Assert.ThrowsException<InvalidOperationException>(campaign.EmergencyRestore);

        CollectionAssert.AreEqual(externalRevision, File.ReadAllBytes(path));
    }

    [TestMethod]
    [TestCategory("Deterministic")]
    public void SameBytesCreatedExternallyDuringConflictAreNeverClaimedOrDeleted()
    {
        using var target = new TemporaryHarnessTarget();
        var configurationPath = Path.Combine(
            target.GameDirectory,
            "community_patch_settings.toml");
        var intended = "[graphics]\nfree_resize = true\n"u8.ToArray();
        using var campaign = new RestorableGameInstallCampaign(
            target.GameDirectory,
            new MutableGameProcessInspector(target.GameDirectory),
            (kind, path) =>
            {
                if (kind == DirectGameMutationKind.PromoteRestoreStage)
                {
                    File.WriteAllBytes(path, intended);
                }
            });

        Assert.ThrowsException<InvalidOperationException>(() =>
            campaign.WriteGameFileAtomically(
                "community_patch_settings.toml",
                intended));
        Assert.ThrowsException<InvalidOperationException>(campaign.EmergencyRestore);

        CollectionAssert.AreEqual(intended, File.ReadAllBytes(configurationPath));
    }

    [TestMethod]
    [TestCategory("Deterministic")]
    public void RestorePromotionDetectsAndRestoresExternalBytesWrittenAtCommitBoundary()
    {
        using var target = new TemporaryHarnessTarget();
        var configurationPath = Path.Combine(
            target.GameDirectory,
            "community_patch_settings.toml");
        var original = "[graphics]\nfree_resize = true\n"u8.ToArray();
        var campaignRevision = "[graphics]\nfree_resize = false\n"u8.ToArray();
        var externalRevision = "[graphics]\nfree_resize = true\n# external\n"u8.ToArray();
        File.WriteAllBytes(configurationPath, original);
        var injectExternal = false;
        using var campaign = new RestorableGameInstallCampaign(
            target.GameDirectory,
            new MutableGameProcessInspector(target.GameDirectory),
            (kind, path) =>
            {
                if (injectExternal && kind == DirectGameMutationKind.CommitRestoreStage)
                {
                    File.WriteAllBytes(path, externalRevision);
                }
            });
        File.WriteAllBytes(configurationPath, campaignRevision);
        campaign.RecordOwnedGameFileRevision(
            "community_patch_settings.toml",
            campaignRevision);
        injectExternal = true;

        Assert.ThrowsException<InvalidOperationException>(campaign.RestoreConfigurationBaseline);

        CollectionAssert.AreEqual(externalRevision, File.ReadAllBytes(configurationPath));
    }

    [TestMethod]
    [TestCategory("Deterministic")]
    public void PreexistingBytesAtChosenRestoreStageArePreserved()
    {
        using var target = new TemporaryHarnessTarget();
        var sentinel = "external stage sentinel"u8.ToArray();
        string? stagePath = null;
        using var campaign = new RestorableGameInstallCampaign(
            target.GameDirectory,
            new MutableGameProcessInspector(target.GameDirectory),
            (kind, path) =>
            {
                if (kind == DirectGameMutationKind.WriteRestoreStage)
                {
                    stagePath = path;
                    File.WriteAllBytes(path, sentinel);
                }
            });

        Assert.ThrowsException<IOException>(() =>
            campaign.WriteGameFileAtomically(
                "community_patch_settings.toml",
                "[graphics]\nfree_resize = true\n"u8.ToArray()));

        Assert.IsNotNull(stagePath);
        CollectionAssert.AreEqual(sentinel, File.ReadAllBytes(stagePath));
    }

    [TestMethod]
    [TestCategory("Deterministic")]
    public void AlteredCompletedRestoreStageIsNeverPromotedOrDeleted()
    {
        using var target = new TemporaryHarnessTarget();
        var configurationPath = Path.Combine(
            target.GameDirectory,
            "community_patch_settings.toml");
        var original = "[graphics]\nfree_resize = true\n"u8.ToArray();
        var intended = "[graphics]\nfree_resize = false\n"u8.ToArray();
        var alteredStage = "external stage bytes"u8.ToArray();
        File.WriteAllBytes(configurationPath, original);
        string? stagePath = null;
        using var campaign = new RestorableGameInstallCampaign(
            target.GameDirectory,
            new MutableGameProcessInspector(target.GameDirectory),
            (kind, path) =>
            {
                if (kind == DirectGameMutationKind.WriteRestoreStage)
                {
                    stagePath = path;
                }
                else if (kind == DirectGameMutationKind.CommitRestoreStage)
                {
                    File.WriteAllBytes(stagePath!, alteredStage);
                }
            });
        File.WriteAllBytes(configurationPath, intended);
        campaign.RecordOwnedGameFileRevision(
            "community_patch_settings.toml",
            intended);

        Assert.ThrowsException<InvalidOperationException>(campaign.RestoreConfigurationBaseline);

        CollectionAssert.AreEqual(intended, File.ReadAllBytes(configurationPath));
        Assert.IsNotNull(stagePath);
        CollectionAssert.AreEqual(alteredStage, File.ReadAllBytes(stagePath));
    }

    [TestMethod]
    [TestCategory("Deterministic")]
    public void FinalProcessAdmissionBlocksRestoreAfterValidation()
    {
        using var target = new TemporaryHarnessTarget();
        var configurationPath = Path.Combine(
            target.GameDirectory,
            "community_patch_settings.toml");
        var original = "[graphics]\nfree_resize = true\n"u8.ToArray();
        var changed = "[graphics]\nfree_resize = false\n"u8.ToArray();
        File.WriteAllBytes(configurationPath, original);
        var inspector = new SequencedGameProcessInspector(
            target.GameDirectory,
            GameProcessInspectionState.NotRunning,
            GameProcessInspectionState.NotRunning,
            GameProcessInspectionState.NotRunning,
            GameProcessInspectionState.RunningTarget);
        using var campaign = new RestorableGameInstallCampaign(
            target.GameDirectory,
            inspector);
        File.WriteAllBytes(configurationPath, changed);
        campaign.RecordOwnedGameFileRevision(
            "community_patch_settings.toml",
            changed);

        Assert.ThrowsException<InvalidOperationException>(campaign.RestoreConfigurationBaseline);

        CollectionAssert.AreEqual(changed, File.ReadAllBytes(configurationPath));
    }

    [TestMethod]
    [TestCategory("Deterministic")]
    public void FinalProcessAdmissionBlocksOwnedDeleteAfterHashValidation()
    {
        using var target = new TemporaryHarnessTarget();
        var configurationPath = Path.Combine(
            target.GameDirectory,
            "community_patch_settings.toml");
        var owned = "[graphics]\nfree_resize = true\n"u8.ToArray();
        var inspector = new SequencedGameProcessInspector(
            target.GameDirectory,
            GameProcessInspectionState.NotRunning,
            GameProcessInspectionState.RunningTarget);
        using var campaign = new RestorableGameInstallCampaign(
            target.GameDirectory,
            inspector);
        File.WriteAllBytes(configurationPath, owned);
        campaign.RecordOwnedGameFileRevision(
            "community_patch_settings.toml",
            owned);

        Assert.ThrowsException<InvalidOperationException>(campaign.RestoreConfigurationBaseline);

        CollectionAssert.AreEqual(owned, File.ReadAllBytes(configurationPath));
    }

    [TestMethod]
    [TestCategory("Deterministic")]
    public void FinalProcessAdmissionBlocksMetadataMutationAfterHashValidation()
    {
        using var target = new TemporaryHarnessTarget();
        var configurationPath = Path.Combine(
            target.GameDirectory,
            "community_patch_settings.toml");
        File.WriteAllText(configurationPath, "[graphics]\nfree_resize = true\n");
        var originalTime = new DateTime(2026, 8, 17, 12, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(configurationPath, originalTime);
        var inspector = new SequencedGameProcessInspector(
            target.GameDirectory,
            GameProcessInspectionState.NotRunning,
            GameProcessInspectionState.RunningTarget);
        using var campaign = new RestorableGameInstallCampaign(
            target.GameDirectory,
            inspector);
        var changedTime = originalTime.AddMinutes(5);
        File.SetLastWriteTimeUtc(configurationPath, changedTime);

        Assert.ThrowsException<InvalidOperationException>(campaign.RestoreConfigurationBaseline);

        Assert.AreEqual(changedTime, File.GetLastWriteTimeUtc(configurationPath));
    }

    [TestMethod]
    [TestCategory("Deterministic")]
    public void ReceiptPreparationFailureCreatesNoDirectGameStage()
    {
        using var target = new TemporaryHarnessTarget();
        using var campaign = new RestorableGameInstallCampaign(
            target.GameDirectory,
            new MutableGameProcessInspector(target.GameDirectory),
            beforeReceiptPersist: _ =>
                throw new IOException("Injected receipt-persistence failure."));

        Assert.ThrowsException<IOException>(() =>
            campaign.WriteGameFileAtomically(
                "community_patch_settings.toml",
                "[graphics]\nfree_resize = true\n"u8.ToArray()));

        Assert.AreEqual(
            0,
            Directory.GetFiles(target.GameDirectory, "*.restore-stage").Length);
        campaign.AssertBaseline("Receipt failure changed the game target.");
    }

    [TestMethod]
    [TestCategory("Deterministic")]
    public void MalformedRetainedReceiptProducesOnlySanitizedFailure()
    {
        using var target = new TemporaryHarnessTarget();
        using var campaign = new RestorableGameInstallCampaign(
            target.GameDirectory,
            new MutableGameProcessInspector(target.GameDirectory));
        var malformedReceipt = Path.Combine(
            campaign.StateDirectory,
            "recovery-stage-malformed.json");
        File.WriteAllText(
            malformedReceipt,
            "{\"schemaVersion\":1,\"stagePath\":null,\"destinationPath\":null,\"expectedSha256\":null}");

        var recoveryFailure = TryEmergencyRestore(campaign);
        Assert.IsNotNull(recoveryFailure);
        var sanitized = SanitizedFailure(
            "The recovery lab failed.",
            recoveryFailure,
            target.GameDirectory,
            campaign.StateDirectory);

        Assert.IsNull(sanitized.InnerException);
        Assert.IsFalse(
            sanitized.ToString().Contains(
                target.GameDirectory,
                StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(
            sanitized.ToString().Contains(
                campaign.StateDirectory,
                StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    [TestCategory("Deterministic")]
    public void FreshRecoveryUsesPreparedRollbackReceiptAfterReplacementInterruption()
    {
        using var target = new TemporaryHarnessTarget();
        var configurationPath = Path.Combine(
            target.GameDirectory,
            "community_patch_settings.toml");
        var original = "[graphics]\nfree_resize = true\n"u8.ToArray();
        var changed = "[graphics]\nfree_resize = false\n"u8.ToArray();
        File.WriteAllBytes(configurationPath, original);
        var inspector = new MutableGameProcessInspector(target.GameDirectory);
        string stateDirectory;
        using (var campaign = new RestorableGameInstallCampaign(
                   target.GameDirectory,
                   inspector,
                   (kind, _) =>
                   {
                       if (kind == DirectGameMutationKind.AfterRestoreReplacement)
                       {
                           throw new IOException("Injected post-replacement interruption.");
                       }
                   }))
        {
            stateDirectory = campaign.StateDirectory;
            campaign.PreserveStateForRecovery();
            File.WriteAllBytes(configurationPath, changed);
            campaign.RecordOwnedGameFileRevision(
                "community_patch_settings.toml",
                changed);

            Assert.ThrowsException<IOException>(campaign.RestoreConfigurationBaseline);
            CollectionAssert.AreEqual(original, File.ReadAllBytes(configurationPath));
        }

        try
        {
            RestorableGameInstallCampaign.RecoverRetainedStages(
                target.GameDirectory,
                stateDirectory,
                inspector);
            CollectionAssert.AreEqual(original, File.ReadAllBytes(configurationPath));
            Assert.AreEqual(
                0,
                Directory.GetFiles(target.GameDirectory, "*.restore-rollback").Length);
        }
        finally
        {
            if (Directory.Exists(stateDirectory))
            {
                Directory.Delete(stateDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    [TestCategory("Deterministic")]
    public void CleanBaselineCleanupPreservesUnownedToml()
    {
        using var target = new TemporaryHarnessTarget();
        var configurationPath = Path.Combine(
            target.GameDirectory,
            "community_patch_settings.toml");
        var external = "[graphics]\nfree_resize = true\n# external\n"u8.ToArray();
        using var campaign = new RestorableGameInstallCampaign(
            target.GameDirectory,
            new MutableGameProcessInspector(target.GameDirectory));
        File.WriteAllBytes(configurationPath, external);

        Assert.ThrowsException<InvalidOperationException>(campaign.RestoreConfigurationBaseline);

        CollectionAssert.AreEqual(external, File.ReadAllBytes(configurationPath));
    }

    [TestMethod]
    [TestCategory("Deterministic")]
    public void EmergencyRestorePreservesPreexistingStageAndRollbackFiles()
    {
        using var target = new TemporaryHarnessTarget();
        var sentinels = new[]
        {
            ".version.dll.preexisting.stage",
            ".version.dll.preexisting.rollback",
            ".stfc-bridge-integration-version.dll.preexisting.restore-stage",
        };
        foreach (var sentinel in sentinels)
        {
            File.WriteAllText(Path.Combine(target.GameDirectory, sentinel), sentinel);
        }
        using var campaign = new RestorableGameInstallCampaign(
            target.GameDirectory,
            new MutableGameProcessInspector(target.GameDirectory));

        campaign.EmergencyRestore();

        foreach (var sentinel in sentinels)
        {
            Assert.AreEqual(sentinel, File.ReadAllText(Path.Combine(target.GameDirectory, sentinel)));
        }
        campaign.AssertBaseline("Emergency cleanup changed a pre-existing residue.");
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

    private sealed class SequencedGameProcessInspector(
        string selectedGameDirectory,
        params GameProcessInspectionState[] states) : IGameProcessInspector
    {
        private int inspection;

        public GameProcessInspectionState Inspect(string gameDirectory)
        {
            Assert.IsTrue(string.Equals(
                Path.GetFullPath(selectedGameDirectory),
                Path.GetFullPath(gameDirectory),
                StringComparison.OrdinalIgnoreCase));
            var index = Math.Min(inspection++, states.Length - 1);
            return states[index];
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
