using System.Text;
using STFCCommunityMod.Launcher.Core;

namespace STFCCommunityMod.Launcher.Core.Tests;

[TestClass]
public sealed class LauncherConfigurationEditSessionTests
{
    private const string GuffawaffleRealWorldFixture =
        """
        # Curated from Guffawaffle/stfc-mod example_community_patch_settings.toml.
        [control]
        hotkeys_enabled = true
        queue_enabled = true

        [graphics]
        # Preserve this comment and the player's exact spacing.
        free_resize = true
        loader_image = ""

        [notifications]
        incoming_attack_player = { system = true, audio = true, sound = "alarm" }

        [future_mod]
        unknown_key = "keep me"
        """;

    private const string NetnivRealWorldFixture =
        """
        # Curated from netniV/stfc-mod example_community_patch_settings.toml.
        [control]
        hotkeys_enabled = true
        queue_enabled = true

        [graphics]
        free_resize = true
        loader_image = ""

        [notifications]
        notifications_enabled = true
        notifications_fleet_arrived_in_system = true

        [notifications.system.fleet]
        arrived_in_system = false
        """;

    [TestMethod]
    public void SessionStagesMultipleChangesWithoutTouchingTheBaseline()
    {
        var catalog = LoadCatalog();
        var original = Encoding.UTF8.GetBytes(GuffawaffleRealWorldFixture);
        var load = LauncherConfigurationEditSession.Load(original, catalog, out var session);
        Assert.IsTrue(load.IsValid, load.Error?.Message);
        Assert.IsNotNull(session);

        var freeResize = catalog.Settings.Single(setting => setting.Path == "graphics.free_resize");
        var queueEnabled = catalog.Settings.Single(setting => setting.Path == "control.queue_enabled");
        Assert.IsTrue(session.StageSet(freeResize, "false").IsValid);
        Assert.IsTrue(session.StageRemove(queueEnabled).IsValid);

        Assert.AreEqual(2, session.PendingChangeCount);
        CollectionAssert.AreEqual(original, load.Contents!);
        var draft = session.BuildDraft();
        Assert.IsTrue(draft.IsValid, draft.Error?.Message);
        var text = Encoding.UTF8.GetString(draft.Contents!);
        StringAssert.Contains(text, "# Preserve this comment and the player's exact spacing.");
        StringAssert.Contains(text, "free_resize = false");
        Assert.IsFalse(text.Contains("queue_enabled", StringComparison.Ordinal));
        StringAssert.Contains(text, "unknown_key = \"keep me\"");
        StringAssert.Contains(
            text,
            """incoming_attack_player = { system = true, audio = true, sound = "alarm" }""");
    }

    [TestMethod]
    public void DiscardRestoresTheLoadedState()
    {
        var catalog = LoadCatalog();
        var original = Encoding.UTF8.GetBytes(GuffawaffleRealWorldFixture);
        LauncherConfigurationEditSession.Load(original, catalog, out var session);
        var setting = catalog.Settings.Single(item => item.Path == "graphics.free_resize");

        session!.StageSet(setting, "false");
        session.Discard();

        Assert.AreEqual(0, session.PendingChangeCount);
        Assert.IsFalse(session.HasPendingChanges);
        var state = session.GetState(setting);
        Assert.AreEqual("true", state.SavedRenderedOverride);
        Assert.AreEqual("true", state.DraftRenderedOverride);
        Assert.IsTrue(state.SavedHasOverride);
        Assert.IsTrue(state.DraftHasOverride);
        Assert.IsFalse(state.IsDirty);
        CollectionAssert.AreEqual(original, session.BuildDraft().Contents!);
    }

    [TestMethod]
    public void RevertRestoresSavedValueAndSavedOverridePresenceForOneRow()
    {
        const string source =
            """
            [graphics]
            free_resize = true
            """;
        var catalog = LoadCatalog();
        LauncherConfigurationEditSession.Load(
            Encoding.UTF8.GetBytes(source),
            catalog,
            out var session);
        var savedCustom = catalog.Settings.Single(
            item => item.Path == "graphics.free_resize");
        var savedDefault = catalog.Settings.Single(
            item => item.Path == "graphics.ui_scale");

        Assert.IsTrue(session!.StageRemove(savedCustom).IsValid);
        Assert.IsTrue(session.StageSet(savedDefault, "0.60").IsValid);
        Assert.AreEqual(2, session.PendingChangeCount);
        Assert.IsFalse(session.GetState(savedCustom).DraftHasOverride);
        Assert.IsTrue(session.GetState(savedDefault).DraftHasOverride);

        Assert.IsTrue(session.Revert(savedCustom).IsValid);
        Assert.IsTrue(session.Revert(savedDefault).IsValid);

        Assert.AreEqual(0, session.PendingChangeCount);
        var customState = session.GetState(savedCustom);
        Assert.IsTrue(customState.SavedHasOverride);
        Assert.IsTrue(customState.DraftHasOverride);
        Assert.AreEqual("true", customState.SavedRenderedOverride);
        Assert.AreEqual("true", customState.DraftRenderedOverride);
        Assert.IsFalse(customState.IsDirty);
        var defaultState = session.GetState(savedDefault);
        Assert.IsFalse(defaultState.SavedHasOverride);
        Assert.IsFalse(defaultState.DraftHasOverride);
        Assert.IsFalse(defaultState.IsDirty);
        CollectionAssert.AreEqual(
            Encoding.UTF8.GetBytes(source),
            session.BuildDraft().Contents!);
    }

    [TestMethod]
    public async Task SaveCommitsTheBatchAndRefreshesSessionBaseline()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var path = Path.Combine(temporaryDirectory.Path, "community_patch_settings.toml");
        var original = Encoding.UTF8.GetBytes(GuffawaffleRealWorldFixture);
        await File.WriteAllBytesAsync(path, original);
        var catalog = LoadCatalog();
        LauncherConfigurationEditSession.Load(original, catalog, out var session);
        var setting = catalog.Settings.Single(item => item.Path == "graphics.free_resize");
        session!.StageSet(setting, "false");

        var result = await session.SaveAsync(path, new AtomicTomlStore());

        Assert.AreEqual(AtomicTomlWriteState.Succeeded, result.State, result.Error);
        Assert.AreEqual(0, session.PendingChangeCount);
        var state = session.GetState(setting);
        Assert.AreEqual("false", state.SavedRenderedOverride);
        Assert.AreEqual("false", state.DraftRenderedOverride);
        Assert.IsTrue(state.SavedHasOverride);
        Assert.IsTrue(state.DraftHasOverride);
        Assert.IsFalse(state.IsDirty);
        CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(path + ".bak"));
    }

    [TestMethod]
    public void NetnivFixtureRoundTripPreservesLegacyNotificationFamilies()
    {
        var catalog = LoadCatalog();
        var original = Encoding.UTF8.GetBytes(NetnivRealWorldFixture);
        LauncherConfigurationEditSession.Load(original, catalog, out var session);
        var setting = catalog.Settings.Single(item => item.Path == "graphics.free_resize");

        var stage = session!.StageSet(setting, "false");

        Assert.IsTrue(stage.IsValid, stage.Error?.Message);
        var text = Encoding.UTF8.GetString(session.BuildDraft().Contents!);
        StringAssert.Contains(text, "notifications_enabled = true");
        StringAssert.Contains(text, "notifications_fleet_arrived_in_system = true");
        StringAssert.Contains(text, "[notifications.system.fleet]");
        StringAssert.Contains(text, "arrived_in_system = false");
    }

    [TestMethod]
    public void StageSetRejectsValuesThatDoNotMatchTheSchemaKind()
    {
        var catalog = LoadCatalog();
        LauncherConfigurationEditSession.Load(
            Encoding.UTF8.GetBytes(GuffawaffleRealWorldFixture),
            catalog,
            out var session);
        var setting = catalog.Settings.Single(item => item.Path == "graphics.free_resize");

        var result = session!.StageSet(setting, "\"not a boolean\"");

        Assert.IsFalse(result.IsValid);
        Assert.AreEqual(SparseTomlErrorCode.InvalidValue, result.Error?.Code);
        Assert.AreEqual(0, session.PendingChangeCount);
    }

    [TestMethod]
    public void EnumStagingAcceptsDeclaredValuesAndCancelsEquivalentQuoteChanges()
    {
        const string source =
            """
            [input]
            original_frame_policy = 'mod'
            """;
        var catalog = LoadCatalog();
        LauncherConfigurationEditSession.Load(
            Encoding.UTF8.GetBytes(source),
            catalog,
            out var session);
        var setting = catalog.Settings.Single(
            item => item.Path == "input.original_frame_policy");

        var changed = session!.StageSet(setting, "\"fallthrough_unhandled\"");
        Assert.IsTrue(changed.IsValid, changed.Error?.Message);
        Assert.AreEqual(1, session.PendingChangeCount);
        StringAssert.Contains(
            Encoding.UTF8.GetString(session.BuildDraft().Contents!),
            "original_frame_policy = \"fallthrough_unhandled\"");

        var restored = session.StageSet(setting, "\"mod\"");
        Assert.IsTrue(restored.IsValid, restored.Error?.Message);
        Assert.AreEqual(0, session.PendingChangeCount);
        CollectionAssert.AreEqual(
            Encoding.UTF8.GetBytes(source),
            session.BuildDraft().Contents!);

        var invalid = session.StageSet(setting, "\"unsupported\"");
        Assert.IsFalse(invalid.IsValid);
        Assert.AreEqual(SparseTomlErrorCode.InvalidValue, invalid.Error?.Code);
        Assert.AreEqual(0, session.PendingChangeCount);
    }

    [TestMethod]
    public void ExplicitRuntimeDefaultRemainsAnOverrideWhenNoOverrideWasSaved()
    {
        const string source =
            """
            # No explicit values: both settings use their runtime defaults.
            """;
        var catalog = LoadCatalog();
        LauncherConfigurationEditSession.Load(
            Encoding.UTF8.GetBytes(source),
            catalog,
            out var session);
        var booleanSetting = catalog.Settings.Single(
            item => item.Path == "graphics.free_resize");
        var enumSetting = catalog.Settings.Single(
            item => item.Path == "input.original_frame_policy");
        var numericSetting = catalog.Settings.Single(
            item => item.Path == "graphics.ui_scale");

        Assert.IsTrue(session!.StageSet(booleanSetting, "true").IsValid);
        Assert.IsTrue(session.StageSet(enumSetting, "\"mod\"").IsValid);
        Assert.IsTrue(session.StageSet(numericSetting, "0.60").IsValid);
        Assert.AreEqual(3, session.PendingChangeCount);

        var booleanState = session.GetState(booleanSetting);
        Assert.IsFalse(booleanState.SavedHasOverride);
        Assert.IsTrue(booleanState.DraftHasOverride);
        Assert.IsTrue(booleanState.IsDirty);
        var draft = Encoding.UTF8.GetString(session.BuildDraft().Contents!);
        StringAssert.Contains(draft, "free_resize = true");
        StringAssert.Contains(draft, "original_frame_policy = \"mod\"");
        StringAssert.Contains(draft, "ui_scale = 0.60");
    }

    [TestMethod]
    public void NumericStagingEnforcesBoundsAndCancelsSemanticNoOps()
    {
        const string source =
            """
            [sidecar.logging]
            jsonl_recent_logs = 3_00

            [graphics]
            ui_scale = 6e-1
            """;
        var catalog = LoadCatalog();
        LauncherConfigurationEditSession.Load(
            Encoding.UTF8.GetBytes(source),
            catalog,
            out var session);
        var retainedLogs = catalog.Settings.Single(
            item => item.Path == "sidecar.logging.jsonl_recent_logs");
        var uiScale = catalog.Settings.Single(
            item => item.Path == "graphics.ui_scale");

        var retainedLogsChange = session!.StageSet(retainedLogs, "600");
        Assert.IsTrue(retainedLogsChange.IsValid, retainedLogsChange.Error?.Message);
        Assert.AreEqual(1, session.PendingChangeCount);

        var retainedLogsRestore = session.StageSet(retainedLogs, "300");
        Assert.IsTrue(retainedLogsRestore.IsValid, retainedLogsRestore.Error?.Message);
        Assert.AreEqual(0, session.PendingChangeCount);

        var invalidRetainedLogs = session.StageSet(retainedLogs, "-1");
        Assert.IsFalse(invalidRetainedLogs.IsValid);
        Assert.AreEqual(SparseTomlErrorCode.InvalidValue, invalidRetainedLogs.Error?.Code);
        Assert.AreEqual(0, session.PendingChangeCount);

        var scaleChange = session.StageSet(uiScale, "0.7");
        Assert.IsTrue(scaleChange.IsValid, scaleChange.Error?.Message);
        Assert.AreEqual(1, session.PendingChangeCount);

        var scaleRestore = session.StageSet(uiScale, "0.60");
        Assert.IsTrue(scaleRestore.IsValid, scaleRestore.Error?.Message);
        Assert.AreEqual(0, session.PendingChangeCount);
        CollectionAssert.AreEqual(
            Encoding.UTF8.GetBytes(source),
            session.BuildDraft().Contents!);
    }

    [TestMethod]
    public void StringStagingUsesSemanticEqualityAndPurposeSpecificValidation()
    {
        const string source =
            """
            [config]
            settings_url = 'https://example.invalid/settings'

            [ui]
            disabled_banner_types = "Victory"
            """;
        var catalog = LoadCatalog();
        LauncherConfigurationEditSession.Load(
            Encoding.UTF8.GetBytes(source),
            catalog,
            out var session);
        var settingsUrl = catalog.Settings.Single(
            item => item.Path == "config.settings_url");
        var disabledBanners = catalog.Settings.Single(
            item => item.Path == "ui.disabled_banner_types");

        var semanticNoOp = session!.StageSet(
            settingsUrl,
            "\"https://example.invalid/settings\"");
        Assert.IsTrue(semanticNoOp.IsValid, semanticNoOp.Error?.Message);
        Assert.AreEqual(0, session.PendingChangeCount);

        var invalidUrl = session.StageSet(settingsUrl, "\"ftp://example.invalid/settings\"");
        Assert.IsFalse(invalidUrl.IsValid);
        Assert.AreEqual(SparseTomlErrorCode.InvalidValue, invalidUrl.Error?.Code);
        Assert.AreEqual(0, session.PendingChangeCount);

        var banners = session.StageSet(disabledBanners, "\"Victory, Defeat\"");
        Assert.IsTrue(banners.IsValid, banners.Error?.Message);
        Assert.AreEqual(1, session.PendingChangeCount);
    }

    [TestMethod]
    public void EmptyStringDefaultCanBeSavedAsAnExplicitOverride()
    {
        const string source = "# All public strings use their runtime defaults.\n";
        var catalog = LoadCatalog();
        LauncherConfigurationEditSession.Load(
            Encoding.UTF8.GetBytes(source),
            catalog,
            out var session);
        var setting = catalog.Settings.Single(
            item => item.Path == "config.assets_url_override");

        var result = session!.StageSet(setting, "\"\"");

        Assert.IsTrue(result.IsValid, result.Error?.Message);
        Assert.AreEqual(1, session.PendingChangeCount);
        StringAssert.Contains(
            Encoding.UTF8.GetString(session.BuildDraft().Contents!),
            "assets_url_override = \"\"");
    }

    [TestMethod]
    public void NotificationPolicyStagingUsesWholePolicySemanticEquality()
    {
        const string source =
            """
            [notifications]
            fleet_arrived_in_system = { audio = true, sound = 'arrival', system = true }
            """;
        var catalog = LoadCatalog();
        LauncherConfigurationEditSession.Load(
            Encoding.UTF8.GetBytes(source),
            catalog,
            out var session);
        var setting = catalog.Settings.Single(
            item => item.Path == "notifications.fleet_arrived_in_system");

        var semanticNoOp = session!.StageSet(
            setting,
            """{ system = true, audio = true, sound = "arrival" }""");

        Assert.IsTrue(semanticNoOp.IsValid, semanticNoOp.Error?.Message);
        Assert.AreEqual(0, session.PendingChangeCount);
        CollectionAssert.AreEqual(
            Encoding.UTF8.GetBytes(source),
            session.BuildDraft().Contents!);
    }

    [TestMethod]
    public void NotificationEventDefaultCanBeSavedAsAnExplicitOverride()
    {
        const string source = "# Notifications use event defaults.\n";
        var catalog = LoadCatalog();
        LauncherConfigurationEditSession.Load(
            Encoding.UTF8.GetBytes(source),
            catalog,
            out var session);
        var setting = catalog.Settings.Single(
            item => item.Path == "notifications.armada_created");

        var result = session!.StageSet(setting, "false");

        Assert.IsTrue(result.IsValid, result.Error?.Message);
        Assert.AreEqual(1, session.PendingChangeCount);
        StringAssert.Contains(
            Encoding.UTF8.GetString(session.BuildDraft().Contents!),
            "armada_created = false");
    }

    [TestMethod]
    public void SessionRefusesToStageAnInvalidNotificationPolicy()
    {
        var catalog = LoadCatalog();
        LauncherConfigurationEditSession.Load(
            Encoding.UTF8.GetBytes(GuffawaffleRealWorldFixture),
            catalog,
            out var session);
        var setting = catalog.Settings.Single(
            item => item.Path == "notifications.incoming_attack_player");

        var result = session!.StageSet(
            setting,
            """{ system = true, audio = true, sound = "klaxon" }""");

        Assert.IsFalse(result.IsValid);
        Assert.AreEqual(SparseTomlErrorCode.InvalidValue, result.Error?.Code);
        Assert.AreEqual(0, session.PendingChangeCount);
    }

    [TestMethod]
    public async Task FullGuffawaffleExampleSurvivesDisposableSaveRoundTrip()
    {
        var sourcePath = FindRepositoryFile(
            "providers",
            "guffawaffle",
            "examples",
            "example_community_patch_settings.toml");
        var original = await File.ReadAllBytesAsync(sourcePath);
        var catalog = LoadCatalog();
        var load = LauncherConfigurationEditSession.Load(original, catalog, out var session);
        Assert.IsTrue(load.IsValid, load.Error?.Message);
        Assert.IsNotNull(session);
        var setting = catalog.Settings.Single(item => item.Path == "graphics.free_resize");
        var current = session.GetState(setting).DraftRenderedOverride;
        var replacement = string.Equals(current, "true", StringComparison.Ordinal) ? "false" : "true";
        Assert.IsTrue(session.StageSet(setting, replacement).IsValid);

        using var temporaryDirectory = new TemporaryDirectory();
        var disposablePath = Path.Combine(
            temporaryDirectory.Path,
            "community_patch_settings.toml");
        await File.WriteAllBytesAsync(disposablePath, original);
        var save = await session.SaveAsync(disposablePath, new AtomicTomlStore());

        Assert.AreEqual(AtomicTomlWriteState.Succeeded, save.State, save.Error);
        CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(disposablePath + ".bak"));
        var saved = await File.ReadAllBytesAsync(disposablePath);
        var savedLoad = SparseTomlDocument.Load(saved, out var savedDocument);
        Assert.IsTrue(savedLoad.IsValid, savedLoad.Error?.Message);
        var read = savedDocument!.ReadOverrides();
        Assert.IsTrue(read.IsValid, read.Error?.Message);
        Assert.AreEqual(replacement, read.Overrides!["graphics.free_resize"].RenderedValue);
        Assert.AreEqual(
            "#  +--------------------------------------------------------------+",
            Encoding.UTF8.GetString(saved)
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .First());
    }

    private static LauncherConfigurationCatalog LoadCatalog()
    {
        var schemaPath = FindRepositoryFile(
            "docs",
            "windows-launcher",
            "config-schema.guffawaffle.v1.json");
        return LauncherConfigurationSchemaLoader.LoadFile(schemaPath);
    }

    private static string FindRepositoryFile(params string[] relativeParts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine([directory.FullName, .. relativeParts]);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        Assert.Fail($"Could not find repository file '{Path.Combine(relativeParts)}'.");
        return string.Empty;
    }
}
