using STFCCommunityMod.Launcher.Core;

namespace STFCCommunityMod.Launcher.Core.Tests;

[TestClass]
public sealed class LauncherNotificationPolicyTests
{
    [DataTestMethod]
    [DataRow("false", false, false, "arrival", "false")]
    [DataRow("true", true, false, "arrival", "true")]
    [DataRow(
        """{ system = true, audio = true, sound = "alarm" }""",
        true,
        true,
        "alarm",
        """{ system = true, audio = true, sound = "alarm" }""")]
    [DataRow(
        """{ audio = true, sound = "soft" }""",
        false,
        true,
        "soft",
        """{ system = false, audio = true, sound = "soft" }""")]
    public void ParsesAndRendersCanonicalPolicies(
        string rendered,
        bool system,
        bool audio,
        string sound,
        string normalized)
    {
        var setting = LoadSetting();

        var result = LauncherNotificationPolicyParser.Parse(setting, rendered);

        Assert.IsTrue(result.IsValid, result.Error);
        Assert.AreEqual(system, result.Policy.System);
        Assert.AreEqual(audio, result.Policy.Audio);
        Assert.AreEqual(sound, result.Policy.Sound);
        Assert.AreEqual(normalized, result.Policy.Render());
    }

    [TestMethod]
    public void InvalidCanonicalPolicyFallsBackToTheEventDefault()
    {
        var setting = LoadSetting();

        var result = LauncherNotificationPolicyParser.Parse(
            setting,
            """{ system = true, audio = true, sound = "klaxon" }""");

        Assert.IsFalse(result.IsValid);
        Assert.IsFalse(result.Policy.System);
        Assert.IsFalse(result.Policy.Audio);
        Assert.AreEqual("arrival", result.Policy.Sound);
        StringAssert.Contains(result.Error, "not supported");
    }

    [TestMethod]
    public void RejectsUnknownAndDuplicateFields()
    {
        var setting = LoadSetting();

        var unknown = LauncherNotificationPolicyParser.Parse(
            setting,
            """{ toast = true }""");
        var duplicate = LauncherNotificationPolicyParser.Parse(
            setting,
            """{ system = true, system = false }""");

        Assert.IsFalse(unknown.IsValid);
        Assert.IsFalse(duplicate.IsValid);
    }

    private static LauncherConfigurationSetting LoadSetting()
    {
        var schemaPath = FindRepositoryFile(
            "docs",
            "windows-launcher",
            "config-schema.guffawaffle.v1.json");
        var catalog = LauncherConfigurationSchemaLoader.LoadFile(schemaPath);
        return catalog.Settings.Single(
            setting => setting.Path == "notifications.fleet_arrived_in_system");
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
