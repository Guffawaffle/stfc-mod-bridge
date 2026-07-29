using STFCCommunityMod.Launcher.Core;

namespace STFCCommunityMod.Launcher.Core.Tests;

[TestClass]
public sealed class LauncherKeybindingValueTests
{
    [DataTestMethod]
    [DataRow("ctrl-shift-q | mouse1", "CTRL-SHIFT-Q|MOUSE1", "Ctrl + Shift + Q  /  Mouse 1")]
    [DataRow("NONE", "NONE", "Unbound")]
    [DataRow("-", "MINUS", "Minus")]
    [DataRow("apple-f2", "CMD-F2", "Command + F2")]
    [DataRow("lctrl-rctrl-q", "LCTRL-RCTRL-Q", "Left Ctrl + Right Ctrl + Q")]
    public void NormalizesSupportedBindings(string value, string normalized, string display)
    {
        var parsed = LauncherKeybindingValue.Parse(value);

        Assert.IsTrue(parsed.IsValid, parsed.Error);
        Assert.AreEqual(normalized, parsed.Normalized);
        Assert.AreEqual(display, parsed.Display);
    }

    [DataTestMethod]
    [DataRow("CTRL")]
    [DataRow("CTRL-CTRL-Q")]
    [DataRow("Q|Q")]
    [DataRow("CTRL-Q|")]
    [DataRow("CTRL-HYPER-Q")]
    [DataRow("F13")]
    public void RejectsInvalidBindings(string value)
    {
        Assert.IsFalse(LauncherKeybindingValue.Parse(value).IsValid);
    }

    [TestMethod]
    public void RegistryDefaultsParseAndConflictRulesPreserveIntentionalSharing()
    {
        var catalog = LoadCatalog();
        var assignments = catalog.Settings
            .Where(setting => setting.ValueKind == LauncherConfigurationValueKind.Keybinding)
            .Select(
                setting =>
                    new LauncherKeybindingAssignment(
                        setting,
                        LauncherKeybindingValue.Parse(setting.DefaultValue.GetString()!)))
            .ToArray();

        Assert.AreEqual(90, assignments.Length);
        Assert.IsTrue(assignments.All(assignment => assignment.Binding.IsValid));
        Assert.AreEqual(0, LauncherKeybindingConflictDetector.FindConflicts(assignments).Count);
    }

    [TestMethod]
    public void DetectsSameTriggerAndConflictGroupButIgnoresNone()
    {
        var catalog = LoadCatalog();
        var fleetPrimary = catalog.Settings.Single(
            setting => setting.Path == "input.bindings.fleet_primary");
        var fleetView = catalog.Settings.Single(
            setting => setting.Path == "input.bindings.fleet_view_info");
        var queueAdd = catalog.Settings.Single(
            setting => setting.Path == "input.bindings.fleet_queue_add");

        var conflicts = LauncherKeybindingConflictDetector.FindConflicts(
        [
            new(fleetPrimary, LauncherKeybindingValue.Parse("CTRL-Q")),
            new(fleetView, LauncherKeybindingValue.Parse("CTRL-Q")),
            new(queueAdd, LauncherKeybindingValue.Parse("CTRL-Q")),
        ]);

        Assert.AreEqual(1, conflicts.Count);
        Assert.AreEqual(fleetPrimary.Path, conflicts[0].First.Path);
        Assert.AreEqual(fleetView.Path, conflicts[0].Second.Path);
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
