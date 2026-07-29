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

    [TestMethod]
    [DataRow("1_000", 1000L)]
    [DataRow("0xFF", 255L)]
    [DataRow("0o10", 8L)]
    [DataRow("0b1010", 10L)]
    [DataRow("-42", -42L)]
    public void TomlIntegerSyntaxIsParsedSemantically(string renderedValue, long expected)
    {
        Assert.IsTrue(LauncherTomlValue.TryReadInteger(renderedValue, out var value));
        Assert.AreEqual(expected, value);
    }

    [TestMethod]
    [DataRow("_1")]
    [DataRow("1_")]
    [DataRow("1__0")]
    [DataRow("0x_FF")]
    [DataRow("0b102")]
    public void InvalidTomlIntegerSyntaxIsRejected(string renderedValue)
    {
        Assert.IsFalse(LauncherTomlValue.TryReadInteger(renderedValue, out _));
    }

    [TestMethod]
    [DataRow("1_000.25", 1000.25)]
    [DataRow("6e-1", 0.6)]
    [DataRow("-0.05", -0.05)]
    public void TomlNumberSyntaxIsParsedSemantically(string renderedValue, double expected)
    {
        Assert.IsTrue(LauncherTomlValue.TryReadNumber(renderedValue, out var value));
        Assert.AreEqual(expected, value, 0.0000001);
    }

    [TestMethod]
    public void NumberRenderingRetainsTomlFloatShape()
    {
        Assert.AreEqual("1.0", LauncherTomlValue.RenderNumber(1));
        Assert.AreEqual("0.6", LauncherTomlValue.RenderNumber(0.6));
        Assert.ThrowsException<ArgumentOutOfRangeException>(
            () => LauncherTomlValue.RenderNumber(double.PositiveInfinity));
    }
}
