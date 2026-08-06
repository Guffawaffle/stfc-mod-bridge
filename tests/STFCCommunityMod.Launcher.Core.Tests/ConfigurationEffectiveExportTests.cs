using System.Text;
using STFCCommunityMod.Launcher.Core;

namespace STFCCommunityMod.Launcher.Core.Tests;

[TestClass]
public sealed class ConfigurationEffectiveExportTests
{
    [TestMethod]
    public void ExportIsExplicitlyUnredactedAndIncludesDefaultsAliasesAndUnknowns()
    {
        var catalog = LoadCatalog();
        var source = Encoding.UTF8.GetBytes(
            "[shortcuts]\nset_hotkeys_disable = \"M\"\n"
            + "[sync]\ntoken = \"secret-value\"\n"
            + "[future]\nprivate_endpoint = \"https://private.example.test\"\n");
        var result = ConfigurationEffectiveExportService.Build(
            new ConfigurationDocumentSnapshot("community_patch_settings.toml", source),
            LauncherConfigurationDiagnosisEvidence.Supported("guffawaffle", "stable", catalog));

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.IsTrue(result.Document!.Warning.Contains("intentionally unredacted", StringComparison.Ordinal));
        Assert.IsTrue(result.Document.Entries.Any(item =>
            item.Path == "input.bindings.hotkeys_disable"
            && item.Origin == "compatibility-alias:shortcuts.set_hotkeys_disable"
            && item.RenderedTomlValue == "\"M\""));
        Assert.IsTrue(result.Document.Entries.Any(item =>
            item.Path == "future.private_endpoint"
            && !item.CatalogKnown
            && item.RenderedTomlValue.Contains("private.example.test", StringComparison.Ordinal)));
        Assert.IsTrue(result.Document.Entries.Any(item => item.Origin == "provider-default"));
    }

    [TestMethod]
    public void CanonicalOverrideWinsEffectiveExportAccordingToCatalogPrecedence()
    {
        var catalog = LoadCatalog();
        var source = Encoding.UTF8.GetBytes(
            "[shortcuts]\nset_hotkeys_disable = \"M\"\n"
            + "[input.bindings]\nhotkeys_disable = \"N\"\n");
        var result = ConfigurationEffectiveExportService.Build(
            new ConfigurationDocumentSnapshot("community_patch_settings.toml", source),
            LauncherConfigurationDiagnosisEvidence.Supported("guffawaffle", "stable", catalog));

        Assert.IsTrue(result.IsSuccess, result.Error);
        var entry = result.Document!.Entries.Single(item => item.Path == "input.bindings.hotkeys_disable");
        Assert.AreEqual("canonical-override", entry.Origin);
        Assert.AreEqual("\"N\"", entry.RenderedTomlValue);
    }

    [TestMethod]
    public void InvalidKnownOverrideFailsClosedInsteadOfClaimingAnEffectiveValue()
    {
        var catalog = LoadCatalog();
        var result = ConfigurationEffectiveExportService.Build(
            new ConfigurationDocumentSnapshot(
                "community_patch_settings.toml",
                Encoding.UTF8.GetBytes("[graphics]\ndefault_system_zoom = \"not-a-number\"\n")),
            LauncherConfigurationDiagnosisEvidence.Supported("guffawaffle", "stable", catalog));

        Assert.AreEqual(ConfigurationEffectiveExportState.Invalid, result.State);
        StringAssert.Contains(result.Error, "cannot be established");
        Assert.IsNull(result.Document);
    }

    [TestMethod]
    public async Task LocalExportWritesTheExactUnredactedDocumentWithoutUsingSupportRedaction()
    {
        var catalog = LoadCatalog();
        var result = ConfigurationEffectiveExportService.Build(
            new ConfigurationDocumentSnapshot(
                "community_patch_settings.toml",
                Encoding.UTF8.GetBytes("[sync]\ntoken = \"local-secret\"\n")),
            LauncherConfigurationDiagnosisEvidence.Supported("guffawaffle", "stable", catalog));
        Assert.IsTrue(result.IsSuccess, result.Error);
        var directory = Path.Combine(Path.GetTempPath(), $"stfc-effective-export-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "effective.json");
        try
        {
            await ConfigurationEffectiveExportService.ExportAsync(result.Document!, path);
            var written = await File.ReadAllTextAsync(path);

            StringAssert.Contains(written, "stfc-mod-bridge-effective-configuration-v1");
            StringAssert.Contains(written, "local-secret");
            StringAssert.Contains(written, "Do not attach it to support requests");
            StringAssert.Contains(written, "\"Sensitivity\": \"secret\"");
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static LauncherConfigurationCatalog LoadCatalog()
    {
        var path = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "docs", "windows-launcher", "config-schema.guffawaffle.v1.json"));
        using var stream = File.OpenRead(path);
        return LauncherConfigurationSchemaLoader.Load(stream);
    }
}
