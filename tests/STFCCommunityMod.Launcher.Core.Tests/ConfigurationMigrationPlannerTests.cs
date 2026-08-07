using System.Text;
using System.Text.Json;
using STFCCommunityMod.Launcher.Core;

namespace STFCCommunityMod.Launcher.Core.Tests;

[TestClass]
public sealed class ConfigurationMigrationPlannerTests
{
    private const string AliasPath = "shortcuts.set_hotkeys_disable";
    private const string CanonicalPath = "input.bindings.hotkeys_disable";

    [TestMethod]
    public void AliasOnlyMoveIsBoundExactRedactedAndRescanned()
    {
        const string secret = "private-token-must-stay-out-of-preview";
        const string source =
            "# preserve this heading\r\n"
            + "shortcuts.set_hotkeys_disable = \"CTRL-ALT-MINUS\"\r\n"
            + $"future.private_token = \"{secret}\"\r\n";
        const string expected =
            "# preserve this heading\r\n"
            + $"future.private_token = \"{secret}\"\r\n"
            + "[input.bindings]\r\n"
            + "hotkeys_disable = \"CTRL-ALT-MINUS\"\r\n";
        var snapshot = Snapshot(source);
        var evidence = Evidence();
        var diagnosis = Analyzer().Analyze(snapshot, evidence);
        var remediationId = diagnosis.Findings.Single(
            finding => finding.Code == "CONFIG_ALIAS_PRESENT").RemediationId;

        Assert.AreEqual(
            $"configuration.alias.move:{AliasPath}->{CanonicalPath}",
            remediationId);
        var plan = Planner().Plan(snapshot, evidence, diagnosis, [remediationId!]);

        Assert.AreEqual(ConfigurationMigrationPlanState.Ready, plan.State);
        Assert.IsNull(plan.RejectionCode);
        Assert.IsNotNull(plan.Binding);
        Assert.AreEqual(snapshot.Revision, plan.Binding.Revision);
        Assert.AreEqual("guffawaffle", plan.Binding.ProviderId);
        Assert.AreEqual("stable", plan.Binding.ChannelId);
        Assert.AreEqual("guffawaffle.configuration", plan.Binding.CatalogId);
        Assert.AreEqual("1.0.0", plan.Binding.CatalogVersion);
        CollectionAssert.AreEqual(new[] { remediationId! }, plan.Binding.RemediationIds);
        Assert.AreEqual(1, plan.Operations.Count);
        Assert.AreEqual(ConfigurationMigrationOperationKind.MoveAlias, plan.Operations[0].Kind);
        Assert.AreEqual(AliasPath, plan.Operations[0].SourcePath);
        Assert.AreEqual(CanonicalPath, plan.Operations[0].CanonicalPath);
        CollectionAssert.AreEqual(
            Encoding.UTF8.GetBytes(expected),
            plan.DesiredContents!,
            Encoding.UTF8.GetString(plan.DesiredContents!));
        Assert.AreEqual(2, plan.PreviewLines.Count);
        Assert.AreEqual(ConfigurationMigrationPreviewLineKind.Removed, plan.PreviewLines[0].Kind);
        Assert.AreEqual(2, plan.PreviewLines[0].OriginalLineNumber);
        Assert.AreEqual(ConfigurationMigrationPreviewLineKind.Added, plan.PreviewLines[1].Kind);
        Assert.AreEqual(4, plan.PreviewLines[1].DesiredLineNumber);
        Assert.IsNotNull(plan.ResultingDiagnosis);
        Assert.IsFalse(plan.ResultingDiagnosis.Findings.Any(finding => finding.RemediationId == remediationId));

        var presentationJson = JsonSerializer.Serialize(
            new { plan.Operations, plan.PreviewLines, plan.Message });
        Assert.IsFalse(presentationJson.Contains(secret, StringComparison.Ordinal));
        Assert.IsFalse(presentationJson.Contains("CTRL-ALT-MINUS", StringComparison.Ordinal));

        var firstCopy = plan.DesiredContents!;
        firstCopy[0] = (byte)'!';
        CollectionAssert.AreEqual(Encoding.UTF8.GetBytes(expected), plan.DesiredContents!);
    }

    [TestMethod]
    public void RedundantCanonicalAliasRemovalPreservesCanonicalAndUnrelatedBytes()
    {
        const string source =
            "input.bindings.hotkeys_disable = \"CTRL-ALT-MINUS\" # canonical stays\n"
            + "shortcuts.set_hotkeys_disable = \"CTRL-ALT-MINUS\" # remove only this line\n"
            + "future.value = \"unchanged\"\n";
        const string expected =
            "input.bindings.hotkeys_disable = \"CTRL-ALT-MINUS\" # canonical stays\n"
            + "future.value = \"unchanged\"\n";
        var snapshot = Snapshot(source);
        var evidence = Evidence();
        var diagnosis = Analyzer().Analyze(snapshot, evidence);
        var finding = diagnosis.Findings.Single(
            item => item.Code == "CONFIG_CANONICAL_ALIAS_REDUNDANT");

        Assert.AreEqual(
            $"configuration.alias.remove:{AliasPath}->{CanonicalPath}",
            finding.RemediationId);
        var plan = Planner().Plan(snapshot, evidence, diagnosis, [finding.RemediationId!]);

        Assert.AreEqual(ConfigurationMigrationPlanState.Ready, plan.State);
        Assert.AreEqual(
            ConfigurationMigrationOperationKind.RemoveRedundantAlias,
            plan.Operations.Single().Kind);
        CollectionAssert.AreEqual(Encoding.UTF8.GetBytes(expected), plan.DesiredContents!);
        Assert.AreEqual(1, plan.PreviewLines.Count);
        Assert.AreEqual(ConfigurationMigrationPreviewLineKind.Removed, plan.PreviewLines[0].Kind);
    }

    [TestMethod]
    public void ConflictingOrAmbiguousAliasesCannotAuthorizeMigration()
    {
        var evidence = Evidence();
        var conflict = Snapshot(
            "input.bindings.hotkeys_disable = \"CTRL-ALT-MINUS\"\n"
            + "shortcuts.set_hotkeys_disable = \"CTRL-ALT-PLUS\"\n");
        var conflictDiagnosis = Analyzer().Analyze(conflict, evidence);
        Assert.IsNull(conflictDiagnosis.Findings.Single(
            finding => finding.Code == "CONFIG_CANONICAL_ALIAS_CONFLICT").RemediationId);

        var conflictPlan = Planner().Plan(
            conflict,
            evidence,
            conflictDiagnosis,
            ["configuration.alias.remove:forged"]);
        Assert.AreEqual(ConfigurationMigrationPlanState.Rejected, conflictPlan.State);
        Assert.AreEqual(
            ConfigurationMigrationPlanRejectionCode.BlockingDiagnosis,
            conflictPlan.RejectionCode);

        var ambiguous = Snapshot(
            "shortcuts.set_hotkeys_disable = \"CTRL-ALT-MINUS\"\n"
            + "shortcuts.set_hotkeys_disble = \"CTRL-ALT-MINUS\"\n");
        var ambiguousDiagnosis = Analyzer().Analyze(ambiguous, evidence);
        Assert.IsTrue(ambiguousDiagnosis.Findings.All(
            finding => finding.Code != "CONFIG_ALIAS_PRESENT" || finding.RemediationId is null));
        var ambiguousPlan = Planner().Plan(
            ambiguous,
            evidence,
            ambiguousDiagnosis,
            ["configuration.alias.move:forged"]);
        Assert.AreEqual(ConfigurationMigrationPlanState.Rejected, ambiguousPlan.State);
        Assert.AreEqual(
            ConfigurationMigrationPlanRejectionCode.IneligibleSelection,
            ambiguousPlan.RejectionCode);
    }

    [TestMethod]
    public void StaleOrMismatchedDiagnosisFailsBeforeMutation()
    {
        var evidence = Evidence();
        var original = Snapshot("shortcuts.set_hotkeys_disable = \"CTRL-ALT-MINUS\"\n");
        var diagnosis = Analyzer().Analyze(original, evidence);
        var remediationId = diagnosis.Findings.Single(
            finding => finding.RemediationId is not null).RemediationId!;
        var changed = Snapshot(
            "shortcuts.set_hotkeys_disable = \"CTRL-ALT-MINUS\"\nfuture.value = true\n");

        var stale = Planner().Plan(changed, evidence, diagnosis, [remediationId]);

        Assert.AreEqual(ConfigurationMigrationPlanState.Rejected, stale.State);
        Assert.AreEqual(ConfigurationMigrationPlanRejectionCode.StaleDiagnosis, stale.RejectionCode);
        Assert.IsNull(stale.DesiredContents);

        var otherChannel = LauncherConfigurationDiagnosisEvidence.Supported(
            "guffawaffle",
            "dev",
            LoadCatalog());
        var mismatch = Planner().Plan(original, otherChannel, diagnosis, [remediationId]);
        Assert.AreEqual(ConfigurationMigrationPlanState.Rejected, mismatch.State);
        Assert.AreEqual(ConfigurationMigrationPlanRejectionCode.BindingMismatch, mismatch.RejectionCode);
    }

    [TestMethod]
    public void CleanResultIsIdempotentAndOldSelectionBecomesIneligible()
    {
        var evidence = Evidence();
        var snapshot = Snapshot("shortcuts.set_hotkeys_disable = \"CTRL-ALT-MINUS\"\n");
        var diagnosis = Analyzer().Analyze(snapshot, evidence);
        var remediationId = diagnosis.Findings.Single(
            finding => finding.RemediationId is not null).RemediationId!;
        var first = Planner().Plan(snapshot, evidence, diagnosis, [remediationId]);
        var migrated = new ConfigurationDocumentSnapshot(snapshot.Path, first.DesiredContents!);
        var migratedDiagnosis = Analyzer().Analyze(migrated, evidence);

        var repeated = Planner().Plan(migrated, evidence, migratedDiagnosis, [remediationId]);
        Assert.AreEqual(ConfigurationMigrationPlanState.Rejected, repeated.State);
        Assert.AreEqual(
            ConfigurationMigrationPlanRejectionCode.IneligibleSelection,
            repeated.RejectionCode);

        var noChange = Planner().Plan(migrated, evidence, migratedDiagnosis, []);
        Assert.AreEqual(ConfigurationMigrationPlanState.NoChange, noChange.State);
        CollectionAssert.AreEqual(migrated.Contents, noChange.DesiredContents!);
        Assert.AreEqual(0, noChange.Operations.Count);
        Assert.AreEqual(0, noChange.PreviewLines.Count);
    }

    [TestMethod]
    public void InvalidSelectionsAreRejectedWithoutContents()
    {
        var snapshot = Snapshot("shortcuts.set_hotkeys_disable = \"CTRL-ALT-MINUS\"\n");
        var evidence = Evidence();
        var diagnosis = Analyzer().Analyze(snapshot, evidence);
        var remediationId = diagnosis.Findings.Single(
            finding => finding.RemediationId is not null).RemediationId!;

        var duplicate = Planner().Plan(
            snapshot,
            evidence,
            diagnosis,
            [remediationId, remediationId]);

        Assert.AreEqual(ConfigurationMigrationPlanState.Rejected, duplicate.State);
        Assert.AreEqual(
            ConfigurationMigrationPlanRejectionCode.InvalidSelection,
            duplicate.RejectionCode);
        Assert.IsNull(duplicate.DesiredContents);
    }

    private static ConfigurationMigrationPlanner Planner() => new();

    private static ConfigurationHealthAnalyzer Analyzer() => new();

    private static LauncherConfigurationDiagnosisEvidence Evidence() =>
        LauncherConfigurationDiagnosisEvidence.Supported("guffawaffle", "stable", LoadCatalog());

    private static LauncherConfigurationCatalog LoadCatalog() =>
        LauncherConfigurationSchemaLoader.LoadFile(
            Path.Combine(
                AppContext.BaseDirectory,
                "Fixtures",
                "Configuration",
                "config-schema.guffawaffle.v1.json"));

    private static ConfigurationDocumentSnapshot Snapshot(string source) =>
        new(
            Path.Combine(Path.GetTempPath(), "configuration-migration-planner.toml"),
            Encoding.UTF8.GetBytes(source));
}
