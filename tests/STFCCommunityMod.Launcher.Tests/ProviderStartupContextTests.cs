using STFCCommunityMod.Launcher.Core;

namespace STFCCommunityMod.Launcher.Tests;

[TestClass]
public sealed class ProviderStartupContextTests
{
    [TestMethod]
    public void CorruptSelectionProducesRestrictedRecoveryContextInsteadOfThrowing()
    {
        using var directory = new TemporaryStateDirectory();
        File.WriteAllText(
            Path.Combine(directory.Path, "provider-selection.json"),
            "{ corrupt-selection");

        var context = BundledLauncherProviderCatalog.LoadStartupContext(directory.Path);

        Assert.AreEqual(
            LauncherProviderSelectionResolutionState.InvalidSelection,
            context.Selection.State);
        Assert.IsFalse(context.Selection.IsResolved);
        Assert.IsNull(context.Selection.Provider);
        StringAssert.Contains(context.Selection.Message, "unreadable");
        Assert.AreEqual("guffawaffle", context.Catalog.DefaultProviderId);
    }

    [TestMethod]
    public void WithdrawnSelectionProducesRestrictedRecoveryContextInsteadOfFallback()
    {
        using var directory = new TemporaryStateDirectory();
        var store = new JsonLauncherProviderSelectionStore(directory.Path);
        store.Save(new("withdrawn-provider", "stable"));

        var context = BundledLauncherProviderCatalog.LoadStartupContext(directory.Path);

        Assert.AreEqual(
            LauncherProviderSelectionResolutionState.UnknownProvider,
            context.Selection.State);
        Assert.IsFalse(context.Selection.IsResolved);
        Assert.AreEqual("withdrawn-provider", context.Selection.Selection.ProviderId);
        StringAssert.Contains(context.Selection.Message, "not present");
    }

    private sealed class TemporaryStateDirectory : IDisposable
    {
        public TemporaryStateDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "stfc-launcher-provider-startup-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, true);
            }
        }
    }

}
