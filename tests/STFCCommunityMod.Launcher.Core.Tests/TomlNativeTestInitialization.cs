using STFCCommunityMod.Launcher.Core;

namespace STFCCommunityMod.Launcher.Core.Tests;

[TestClass]
public sealed class TomlNativeTestInitialization
{
    [AssemblyInitialize]
    public static void Initialize(TestContext context)
    {
        var path = Environment.GetEnvironmentVariable("STFC_TOML_NATIVE_TEST_DLL");
        var hash = Environment.GetEnvironmentVariable("STFC_TOML_NATIVE_TEST_SHA256");
        if (path is null && hash is null) return;
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(hash))
            throw new InvalidOperationException("TOML tests require both an explicit native library path and its pinned SHA-256.");
        TomlNativeRuntime.ConfigureForTesting(path, hash);
    }
}
