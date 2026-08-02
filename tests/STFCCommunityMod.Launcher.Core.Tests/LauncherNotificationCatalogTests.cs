using System.Text;
using STFCCommunityMod.Launcher.Core;

namespace STFCCommunityMod.Launcher.Core.Tests;

[TestClass]
public sealed class LauncherNotificationCatalogTests
{
    private static readonly string[] FleetArrivalCompatibilityPath =
        ["notifications.events.fleet.arrived_in_system"];

    [TestMethod]
    public void BundledProviderCatalogProjectsEveryNotificationWithCompleteMetadata()
    {
        var catalog = LoadBundledCatalog();
        var declared = catalog.Settings
            .Where(setting => setting.Control == LauncherConfigurationControl.NotificationPolicy)
            .ToArray();

        Assert.AreEqual(declared.Length, catalog.NotificationCatalog.Events.Count);
        Assert.IsTrue(catalog.NotificationCatalog.Events.Count > 0);
        Assert.IsTrue(catalog.NotificationCatalog.Events.All(item => item.HasCompleteProviderMetadata));
        Assert.IsTrue(catalog.NotificationCatalog.Events.All(item => item.Sounds.Count > 0));
        Assert.IsTrue(catalog.NotificationCatalog.Events.All(item => item.Setting.Sensitivity == LauncherConfigurationSensitivity.Public));
        Assert.IsTrue(catalog.NotificationCatalog.Events.All(item => !string.IsNullOrWhiteSpace(item.Setting.Presentation.Group)));
        Assert.IsTrue(catalog.NotificationCatalog.Events.All(item => !string.IsNullOrWhiteSpace(item.Setting.Presentation.AccessibleName)));
        Assert.AreEqual(
            declared.Length + declared.Sum(setting => setting.Aliases.Count),
            catalog.NotificationCatalog.EntriesByPath.Count);
    }

    [TestMethod]
    public void SparseTomlDoesNotControlCatalogCompletenessOrMaterializeDefaults()
    {
        const string source = "# intentionally sparse\n[custom]\nkeep = \"verbatim\"\n";
        var catalog = LoadBundledCatalog();
        var load = LauncherConfigurationEditSession.Load(
            Encoding.UTF8.GetBytes(source),
            catalog,
            out var session);

        Assert.IsTrue(load.IsValid);
        Assert.IsNotNull(session);
        foreach (var definition in catalog.NotificationCatalog.Events)
        {
            var state = session.GetState(definition.Setting);
            Assert.IsFalse(state.SavedHasOverride);
            Assert.AreEqual(LauncherConfigurationValueOrigin.ProviderDefault, state.SavedOrigin);
            Assert.AreEqual(definition.DefaultPolicy, LauncherNotificationPolicyParser.Parse(definition.Setting, null).Policy);
        }

        CollectionAssert.AreEqual(Encoding.UTF8.GetBytes(source), session.BuildDraft().Contents!);
    }

    [TestMethod]
    public void CanonicalPolicyReplacesDeclaredAliasWhileSparseDraftPreservesAlias()
    {
        const string source =
            "# retain compatibility input\n"
            + "[notifications.events.fleet]\n"
            + "arrived_in_system = { system = false, audio = true, sound = \"arrival\" }\n"
            + "[unrelated]\nkeep = true\n";
        var catalog = LoadBundledCatalog();
        var definition = catalog.NotificationCatalog.Events.Single(
            item => item.Setting.Path == "notifications.fleet_arrived_in_system");
        var load = LauncherConfigurationEditSession.Load(
            Encoding.UTF8.GetBytes(source),
            catalog,
            out var session);

        Assert.IsTrue(load.IsValid);
        var compatibility = session!.GetState(definition.Setting);
        Assert.AreEqual(LauncherConfigurationValueOrigin.CompatibilityAlias, compatibility.DraftOrigin);
        Assert.IsFalse(compatibility.DraftHasOverride);
        CollectionAssert.AreEqual(
            FleetArrivalCompatibilityPath,
            compatibility.CompatibilitySourcePaths.ToArray());

        Assert.IsTrue(session.StageSet(definition.Setting, "true").IsValid);
        var canonical = session.GetState(definition.Setting);
        Assert.AreEqual(LauncherConfigurationValueOrigin.CanonicalOverride, canonical.DraftOrigin);
        Assert.IsTrue(canonical.DraftHasOverride);
        var draft = Encoding.UTF8.GetString(session.BuildDraft().Contents!);
        StringAssert.Contains(draft, "arrived_in_system = { system = false, audio = true");
        StringAssert.Contains(draft, "fleet_arrived_in_system = true");
        StringAssert.Contains(draft, "[unrelated]\nkeep = true");

        Assert.IsTrue(session.StageRemove(definition.Setting).IsValid);
        Assert.AreEqual(
            LauncherConfigurationValueOrigin.CompatibilityAlias,
            session.GetState(definition.Setting).DraftOrigin);
        Assert.AreEqual(source, Encoding.UTF8.GetString(session.BuildDraft().Contents!));
    }

    [TestMethod]
    public void CanonicalBooleanAndInlinePoliciesRetainProviderDefaultAndSoundSemantics()
    {
        var catalog = LoadBundledCatalog();
        var definition = catalog.NotificationCatalog.Events.Single(
            item => item.Setting.Path == "notifications.fleet_arrived_in_system");

        Assert.AreEqual("notification-event-catalog", definition.Provenance.DefaultSource);
        Assert.AreEqual(definition.Setting.Path, definition.Provenance.RuntimePath);
        Assert.AreEqual(new LauncherNotificationPolicy(true, false, "arrival"), LauncherNotificationPolicyParser.Parse(definition.Setting, "true").Policy);
        Assert.AreEqual(
            new LauncherNotificationPolicy(false, true, "repair"),
            LauncherNotificationPolicyParser.Parse(
                definition.Setting,
                "{ system = false, audio = true, sound = \"repair\" }").Policy);
    }

    [TestMethod]
    public void AliasSearchUsesTypedCatalogMetadataRatherThanPresentationDuplication()
    {
        var catalog = LoadBundledCatalog();
        var definition = catalog.NotificationCatalog.Events.First(item => item.Aliases.Count > 0);
        var alias = definition.Aliases[0].Path;

        Assert.AreSame(definition, catalog.NotificationCatalog.EntriesByPath[alias]);
        Assert.AreEqual(definition.Setting.Path, catalog.Search(alias).Single().Path);
    }

    private static LauncherConfigurationCatalog LoadBundledCatalog() =>
        LauncherConfigurationSchemaLoader.LoadFile(
            FindRepositoryFile(
                "docs",
                "windows-launcher",
                "config-schema.guffawaffle.v1.json"));

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
