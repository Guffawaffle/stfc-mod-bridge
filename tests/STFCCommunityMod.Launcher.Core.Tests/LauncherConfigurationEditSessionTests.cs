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
        Assert.AreEqual("true", state.RenderedOverride);
        Assert.IsTrue(state.HasOverride);
        Assert.IsFalse(state.IsStaged);
        CollectionAssert.AreEqual(original, session.BuildDraft().Contents!);
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
        Assert.AreEqual("false", session.GetState(setting).RenderedOverride);
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
        var sourcePath = FindRepositoryFile("example_community_patch_settings.toml");
        var original = await File.ReadAllBytesAsync(sourcePath);
        var catalog = LoadCatalog();
        var load = LauncherConfigurationEditSession.Load(original, catalog, out var session);
        Assert.IsTrue(load.IsValid, load.Error?.Message);
        Assert.IsNotNull(session);
        var setting = catalog.Settings.Single(item => item.Path == "graphics.free_resize");
        var current = session.GetState(setting).RenderedOverride;
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
