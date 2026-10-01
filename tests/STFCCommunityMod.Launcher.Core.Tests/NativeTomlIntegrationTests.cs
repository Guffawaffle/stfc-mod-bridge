using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using STFCCommunityMod.Launcher.Core;

namespace STFCCommunityMod.Launcher.Core.Tests;

[TestClass]
public sealed class NativeTomlIntegrationTests
{
    private static readonly string[] UnicodePath = ["future", "🚀.key"];
    [TestMethod]
    public void PinnedLibraryExecutesTheVersionedOfflineProtocol()
    {
        var (path, hash) = Component();
        using var transport = new TomlNativeTransport(path, hash);
        var read = transport.Request(new("read", "\"key.with.dot\" = '''held'''\n[[future]]\nname = 'keep'\n"));
        Assert.IsTrue(read.Ok, read.Error?.Code);
        var held = read.Overrides!.Single(item => item.Path.Length == 1 && item.Path[0] == "key.with.dot");
        Assert.AreEqual("'''held'''", held.Value);
        Assert.AreEqual("held", DecodeString(held.SemanticValue));
        Assert.AreEqual("\"key.with.dot\"", held.CanonicalPath);
        Assert.AreEqual(1, held.Line);
    }

    [TestMethod]
    public void MismatchedLibraryBytesAreRejectedBeforeNativeExecution()
    {
        var (path, _) = Component();
        using var transport = new TomlNativeTransport(path, new string('1', 64));
        Assert.ThrowsException<InvalidDataException>(() => transport.Request(new("validate", string.Empty)));
    }

    [TestMethod]
    public void LoadedLibraryRemainsLockedAgainstByteReplacement()
    {
        var (path, hash) = Component();
        using var transport = new TomlNativeTransport(path, hash);
        Assert.IsTrue(transport.Request(new("validate", string.Empty)).Ok);
        Assert.ThrowsException<IOException>(() =>
        {
            using var attempted = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read);
        });
    }

    [TestMethod]
    public void UnqualifiedLoaderAndNativeFailureHaveEditorAvailabilityMeaning()
    {
        var (path, _) = Component();
        using var transport = new TomlNativeTransport(path, string.Empty);
        Assert.ThrowsException<InvalidDataException>(() => transport.Request(new("validate", string.Empty)));
        Assert.AreEqual(SparseTomlErrorCode.EditorUnavailable,
            SparseTomlDocument.MapError(new() { Code = "NativeComponentUnavailable" }).Code);
        Assert.AreEqual(SparseTomlErrorCode.EditorUnavailable,
            SparseTomlDocument.MapError(new() { Code = "InternalError" }).Code);
    }

    [TestMethod]
    public void SupplementaryUnicodePathsAndValuesRoundTripWithoutSurrogateEscapes()
    {
        const string value = "🚀 café\nline";
        var rendered = LauncherTomlValue.RenderString(value);
        var path = LauncherTomlPath.Render(UnicodePath);
        Assert.IsTrue(LauncherTomlPath.TryParse(path, out var parsed));
        CollectionAssert.AreEqual(UnicodePath, parsed);
        Assert.IsTrue(LauncherTomlValue.TryReadString(rendered, out var decoded));
        Assert.AreEqual(value, decoded);
        SparseTomlDocument.Load(Encoding.UTF8.GetBytes("# preserve\r\n"), out var document);
        var result = document!.SetOverride(path, rendered);
        Assert.IsTrue(result.IsValid, result.Error?.Message);
        SparseTomlDocument.Load(result.Contents!, out var updated);
        Assert.AreEqual(value, DecodeString(updated!.ReadOverrides().Overrides![path].RenderedValue));
    }

    [TestMethod]
    public void PathsAndValuesCannotInjectAdditionalStatements()
    {
        SparseTomlDocument.Load("enabled = false\n"u8.ToArray(), out var document);
        foreach (var path in new[] { "enabled = true\ninjected", "enabled\n[injected]", "enabled # injected" })
        {
            var result = document!.SetOverride(path, "true");
            Assert.IsFalse(result.IsValid);
            Assert.AreEqual(SparseTomlErrorCode.InvalidPath, result.Error?.Code);
            Assert.IsNull(result.Contents);
        }
        var injectedValue = document!.SetOverride("enabled", "true\ninjected = false");
        Assert.AreEqual(SparseTomlErrorCode.InvalidValue, injectedValue.Error?.Code);
        Assert.IsNull(injectedValue.Contents);
        CollectionAssert.AreEqual("enabled = false\n"u8.ToArray(), document.ValidateForMutation().Contents!);
    }

    private static string DecodeString(string rendered)
    {
        Assert.IsTrue(LauncherTomlValue.TryReadString(rendered, out var value));
        return value;
    }

    private static (string Path, string Hash) Component()
    {
        var path = Environment.GetEnvironmentVariable("STFC_TOML_NATIVE_TEST_DLL")
            ?? Path.Combine(AppContext.BaseDirectory, TomlNativeTransport.LibraryName);
        var hash = Environment.GetEnvironmentVariable("STFC_TOML_NATIVE_TEST_SHA256")
            ?? typeof(SparseTomlDocument).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                .SingleOrDefault(attribute => attribute.Key == "TomlNativeSha256")?.Value;
        Assert.IsFalse(string.IsNullOrWhiteSpace(hash), "Build or explicitly pin the native test component.");
        Assert.IsTrue(File.Exists(path), "Build or explicitly provide the native test component.");
        Assert.AreEqual(hash, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant());
        return (path, hash!);
    }
}
