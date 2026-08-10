using System.Collections;
using System.Buffers.Binary;
using System.Resources;
using System.Security.Cryptography;
using System.Xml.Linq;

namespace STFCCommunityMod.Launcher.Tests;

[TestClass]
public sealed class HomeArtworkTests
{
    private const string CanonicalArtworkHash =
        "489148D1A63DC549261D649C83C2CC1BD5C9C883C13A987120AEA2D5DE57E1DB";
    private static readonly XNamespace Presentation =
        "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml =
        "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly XNamespace Automation =
        "clr-namespace:System.Windows.Automation;assembly=PresentationCore";

    [TestMethod]
    public void HomeUsesCanonicalResponsiveArtworkWithoutTemporaryCopy()
    {
        var document = LoadXml("src/STFCCommunityMod.Launcher/MainWindow.xaml");
        var artwork = document.Descendants(Presentation + "Image")
            .Single(element => (string?)element.Attribute(Xaml + "Name") == "HomeProductArtwork");

        Assert.AreEqual("Assets/stfc-mod-bridge-banner.png", (string?)artwork.Attribute("Source"));
        Assert.AreEqual("640", (string?)artwork.Attribute("MaxWidth"));
        Assert.IsNull(artwork.Attribute("Width"));
        Assert.IsNull(artwork.Attribute("Height"));
        Assert.AreEqual("Stretch", (string?)artwork.Attribute("HorizontalAlignment"));
        Assert.AreEqual("Uniform", (string?)artwork.Attribute("Stretch"));
        Assert.AreEqual("HighQuality", (string?)artwork.Attribute("RenderOptions.BitmapScalingMode"));
        Assert.AreEqual("False", (string?)artwork.Attribute("Focusable"));
        Assert.AreEqual("False", (string?)artwork.Attribute("IsHitTestVisible"));
        Assert.IsFalse(document.ToString().Contains("Make STFC yours.", StringComparison.Ordinal));
        Assert.IsFalse(document.ToString().Contains(
            "Install, update, and configure the community mod in one place.",
            StringComparison.Ordinal));
    }

    [TestMethod]
    public void ArtworkUsesOneQuietAutomationIdentityAndPreservesAccessibleProductText()
    {
        var document = LoadXml("src/STFCCommunityMod.Launcher/MainWindow.xaml");
        var artwork = document.Descendants(Presentation + "Image")
            .Single(element => (string?)element.Attribute(Xaml + "Name") == "HomeProductArtwork");
        var productTitle = document.Descendants(Presentation + "TextBlock")
            .Single(element => (string?)element.Attribute(Xaml + "Name") == "ProductTitleText");

        Assert.AreEqual("STFC Mod Bridge", (string?)artwork.Attribute(Automation + "AutomationProperties.Name"));
        Assert.AreEqual("STFC Mod Bridge", (string?)productTitle.Attribute("Text"));
        Assert.AreEqual(
            1,
            artwork.Attributes().Count(attribute => attribute.Name.Namespace == Automation));
        Assert.IsFalse(artwork.Descendants().Any(element =>
            element.Attributes().Any(attribute => attribute.Name.Namespace == Automation)));
    }

    [TestMethod]
    public void ProjectLinksCanonicalArtworkWithoutCreatingASecondCopy()
    {
        var project = LoadXml("src/STFCCommunityMod.Launcher/STFCCommunityMod.Launcher.csproj");
        var artworkResource = project.Descendants("Resource")
            .Single(element =>
                (string?)element.Attribute("Include") == "..\\..\\assets\\portfolio\\stfc-mod-bridge-banner.png");
        var canonicalPath = RepositoryPath("assets/portfolio/stfc-mod-bridge-banner.png");

        Assert.AreEqual("Assets\\stfc-mod-bridge-banner.png", (string?)artworkResource.Attribute("Link"));
        Assert.IsFalse(File.Exists(RepositoryPath(
            "src/STFCCommunityMod.Launcher/Assets/stfc-mod-bridge-banner.png")));
        Assert.AreEqual(
            CanonicalArtworkHash,
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(canonicalPath))));
    }

    [TestMethod]
    public void CompiledPackResourceContainsCanonicalPngDimensions()
    {
        var assembly = typeof(App).Assembly;
        var compiledResourceName = assembly.GetManifestResourceNames().Single(name =>
            name.EndsWith(".g.resources", StringComparison.Ordinal));
        using var compiledResourceStream = assembly.GetManifestResourceStream(compiledResourceName);
        Assert.IsNotNull(
            compiledResourceStream,
            "The application resource bundle was not compiled into the launcher assembly.");
        using var resources = new ResourceReader(compiledResourceStream);
        var resource = resources.Cast<DictionaryEntry>().Single(entry =>
            string.Equals(
                entry.Key as string,
                "assets/stfc-mod-bridge-banner.png",
                StringComparison.OrdinalIgnoreCase));
        Assert.IsInstanceOfType<Stream>(resource.Value);
        using var stream = (Stream)resource.Value;
        Span<byte> header = stackalloc byte[24];
        stream.ReadExactly(header);

        CollectionAssert.AreEqual(
            new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 },
            header[..8].ToArray());
        Assert.AreEqual(13u, BinaryPrimitives.ReadUInt32BigEndian(header[8..12]));
        CollectionAssert.AreEqual("IHDR"u8.ToArray(), header[12..16].ToArray());
        Assert.AreEqual(2172u, BinaryPrimitives.ReadUInt32BigEndian(header[16..20]));
        Assert.AreEqual(724u, BinaryPrimitives.ReadUInt32BigEndian(header[20..24]));
    }

    [TestMethod]
    public void PackagedSmokeRequiresDecodedArtworkBoundsAndAspectRatio()
    {
        var smoke = File.ReadAllText(RepositoryPath("scripts/smoke-settings.ps1"));

        StringAssert.Contains(smoke, "[System.Windows.Automation.ControlType]::Image");
        StringAssert.Contains(smoke, "$artworkBounds.Width -lt 240");
        StringAssert.Contains(smoke, "$artworkBounds.Height -lt 80");
        StringAssert.Contains(smoke, "$artworkAspectRatio -lt 2.95");
        StringAssert.Contains(smoke, "$artworkAspectRatio -gt 3.05");
    }

    private static XDocument LoadXml(string relativePath) =>
        XDocument.Load(RepositoryPath(relativePath));

    private static string RepositoryPath(string relativePath) =>
        Path.Combine(RepositoryRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar));

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
               && !File.Exists(Path.Combine(directory.FullName, "STFCCommunityMod.Launcher.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("Could not find the Mod Bridge repository root.");
    }

}
