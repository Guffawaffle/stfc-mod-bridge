using STFCCommunityMod.Launcher.Core;

namespace STFCCommunityMod.Launcher.Core.Tests;

[TestClass]
public sealed class LauncherTomlValueTests
{
    [TestMethod]
    public void RenderedStringRoundTripsEscapedContent()
    {
        const string value = "arrival \"alarm\" \u2605";

        var rendered = LauncherTomlValue.RenderString(value);
        var parsed = LauncherTomlValue.TryReadString(rendered, out var roundTrip);

        Assert.IsTrue(parsed);
        Assert.AreEqual(value, roundTrip);
    }

    [TestMethod]
    public void SingleQuotedTomlLiteralIsAccepted()
    {
        Assert.IsTrue(LauncherTomlValue.TryReadString("'fallthrough_all'", out var value));
        Assert.AreEqual("fallthrough_all", value);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("unquoted")]
    [DataRow("'broken'value'")]
    [DataRow("\"unterminated")]
    public void InvalidStringSyntaxIsRejected(string renderedValue)
    {
        Assert.IsFalse(LauncherTomlValue.TryReadString(renderedValue, out _));
    }
}
