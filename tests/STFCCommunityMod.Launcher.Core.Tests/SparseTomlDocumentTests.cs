using System.Text;
using STFCCommunityMod.Launcher.Core;

namespace STFCCommunityMod.Launcher.Core.Tests;

[TestClass]
public sealed class SparseTomlDocumentTests
{
    [TestMethod]
    public void SetOverridePreservesUnknownKeysCommentsAndFormatting()
    {
        const string source = """
            # Keep this entire neighborhood.
            mystery = "untouched"

            [notifications]
            incoming_attack = false   # explain why
            unknown_future_key = { nested = true }
            """;

        var document = Load(source);
        var result = document.SetOverride("notifications.incoming_attack", "true");

        Assert.IsTrue(result.IsValid, result.Error?.Message);
        Assert.IsTrue(result.Changed);
        Assert.AreEqual(
            source.Replace(
                "incoming_attack = false   # explain why",
                "incoming_attack = true   # explain why",
                StringComparison.Ordinal),
            Decode(result.Contents!));
    }

    [TestMethod]
    public void SetOverridePreservesCrLfAndUtf8Bom()
    {
        const string source = "[notifications]\r\nfleet_arrived = false\r\n# fin\r\n";
        var contents = new byte[] { 0xef, 0xbb, 0xbf }
            .Concat(Encoding.UTF8.GetBytes(source))
            .ToArray();
        var load = SparseTomlDocument.Load(contents, out var document);
        Assert.IsTrue(load.IsValid);

        var result = document!.SetOverride("notifications.fleet_arrived", "true");

        Assert.IsTrue(result.IsValid, result.Error?.Message);
        CollectionAssert.AreEqual(
            new byte[] { 0xef, 0xbb, 0xbf }
                .Concat(Encoding.UTF8.GetBytes(source.Replace("false", "true", StringComparison.Ordinal)))
                .ToArray(),
            result.Contents!);
    }

    [TestMethod]
    public void SetOverrideAddsRootExistingTableAndNewTableMinimally()
    {
        var document = Load("[existing]\nvalue = 1\n");

        var rootResult = document.SetOverride("root_toggle", "true");
        Assert.AreEqual("root_toggle = true\n[existing]\nvalue = 1\n", Decode(rootResult.Contents!));

        var tableDocument = Load(rootResult.Contents!);
        var tableResult = tableDocument.SetOverride("existing.second", "\"two\"");
        Assert.IsTrue(tableResult.IsValid, tableResult.Error?.Message);
        Assert.AreEqual(Decode(rootResult.Contents!), Decode(tableResult.Contents!).Replace("second = \"two\"\n", string.Empty, StringComparison.Ordinal));

        var newTableDocument = Load(tableResult.Contents!);
        var newTableResult = newTableDocument.SetOverride("new_section.enabled", "true");
        Assert.IsTrue(newTableResult.IsValid, newTableResult.Error?.Message);
        StringAssert.Contains(Decode(newTableResult.Contents!), Decode(tableResult.Contents!));
        var inserted = Load(newTableResult.Contents!).ReadOverrides();
        Assert.AreEqual("true", inserted.Overrides!["new_section.enabled"].RenderedValue);
    }

    [TestMethod]
    public void SetOverrideUsesDottedKeyWhenParentTableAlreadyExistsImplicitly()
    {
        const string source =
            "# preserve compatibility input\n"
            + "[notifications.events.fleet]\n"
            + "arrived_in_system = true\n"
            + "[unrelated]\nkeep = true\n";
        var document = Load(source);

        var result = document.SetOverride("notifications.fleet_arrived_in_system", "true");

        Assert.IsTrue(result.IsValid, result.Error?.Message);
        Assert.AreEqual(source, Decode(result.Contents!).Replace("notifications.fleet_arrived_in_system = true\n", string.Empty, StringComparison.Ordinal));
        var inserted = Load(result.Contents!).ReadOverrides();
        Assert.AreEqual("true", inserted.Overrides!["notifications.fleet_arrived_in_system"].RenderedValue);
        Assert.AreEqual("true", inserted.Overrides["notifications.events.fleet.arrived_in_system"].RenderedValue);
    }

    [TestMethod]
    public void RemoveOverrideRemovesOnlyTargetAssignment()
    {
        const string source = """
            [notifications]
            keep_before = true
            remove_me = false # target comment
            keep_after = "yes"
            """;
        var document = Load(source);

        var result = document.RemoveOverride("notifications.remove_me");

        Assert.IsTrue(result.IsValid, result.Error?.Message);
        Assert.AreEqual(
            """
            [notifications]
            keep_before = true
            keep_after = "yes"
            """,
            Decode(result.Contents!));
    }

    [TestMethod]
    public void DuplicateTargetFailsClosed()
    {
        var document = Load(
            """
            [notifications]
            arrived = true
            arrived = false
            """);

        var result = document.SetOverride("notifications.arrived", "true");

        Assert.IsFalse(result.IsValid);
        Assert.AreEqual(SparseTomlErrorCode.DuplicateTarget, result.Error?.Code);
        Assert.IsNull(result.Contents);
    }

    [TestMethod]
    public void MultilineValuesAndUnrelatedArrayTablesCanBeEdited()
    {
        var multiline = Load(
            """
            [settings]
            value = [
              1,
              2,
            ]
            """);
        var multilineResult = multiline.SetOverride("settings.value", "[3]");
        Assert.IsTrue(multilineResult.IsValid, multilineResult.Error?.Message);
        Assert.AreEqual("[settings]\nvalue = [ 3 ]", Decode(multilineResult.Contents!));

        var arrayTable = Load(
            """
            [[profiles]]
            name = "first"
            """);
        var arrayResult = arrayTable.SetOverride("settings.value", "true");
        Assert.IsTrue(arrayResult.IsValid, arrayResult.Error?.Message);
        StringAssert.Contains(Decode(arrayResult.Contents!), "[[profiles]]\nname = \"first\"");
        var indexed = arrayTable.SetOverride("profiles.name", "\"second\"");
        Assert.AreEqual(SparseTomlErrorCode.UnsupportedTarget, indexed.Error?.Code);
        Assert.IsNull(indexed.Contents);
    }

    [TestMethod]
    public void InvalidStatementAndScalarTailFailClosed()
    {
        var invalidStatement = Load(
            """
            [settings]
            this is not TOML
            enabled = false
            """);
        var statementResult = invalidStatement.SetOverride("settings.enabled", "true");
        Assert.AreEqual(SparseTomlErrorCode.InvalidDocument, statementResult.Error?.Code);
        Assert.IsNull(statementResult.Contents);

        var invalidScalar = Load(
            """
            [settings]
            unknown = true false
            enabled = false
            """);
        var scalarResult = invalidScalar.SetOverride("settings.enabled", "true");
        Assert.AreEqual(SparseTomlErrorCode.InvalidDocument, scalarResult.Error?.Code);
        Assert.IsNull(scalarResult.Contents);
    }

    [TestMethod]
    public void ScalarParentCannotGainAChildAndNamespaceCannotBecomeAScalar()
    {
        var scalarParent = Load("a.b = 1\n");
        var childResult = scalarParent.SetOverride("a.b.c", "true");
        Assert.AreEqual(SparseTomlErrorCode.UnsupportedTarget, childResult.Error?.Code);
        Assert.IsNull(childResult.Contents);

        var populatedNamespace = Load("a.b.c = 1\n");
        var parentResult = populatedNamespace.SetOverride("a.b", "true");
        Assert.AreEqual(SparseTomlErrorCode.UnsupportedTarget, parentResult.Error?.Code);
        Assert.IsNull(parentResult.Contents);
    }

    [TestMethod]
    public void ExistingTablesCannotBecomeScalarSettingsIncludingNestedTables()
    {
        var directTable = Load("[a]\nx = 1\n");
        var directResult = directTable.SetOverride("a", "true");
        Assert.AreEqual(SparseTomlErrorCode.UnsupportedTarget, directResult.Error?.Code);
        Assert.IsNull(directResult.Contents);

        var nestedTable = Load("[a.b]\nx = 1\n");
        var parentResult = nestedTable.SetOverride("a", "true");
        Assert.AreEqual(SparseTomlErrorCode.UnsupportedTarget, parentResult.Error?.Code);
        Assert.IsNull(parentResult.Contents);

        var nestedResult = nestedTable.SetOverride("a.b", "true");
        Assert.AreEqual(SparseTomlErrorCode.UnsupportedTarget, nestedResult.Error?.Code);
        Assert.IsNull(nestedResult.Contents);
    }

    [TestMethod]
    public void DuplicateTableHeadersFailClosed()
    {
        var document = Load(
            """
            [settings]
            first = true

            [settings]
            second = true
            """);

        var result = document.SetOverride("settings.first", "false");

        Assert.IsFalse(result.IsValid);
        Assert.AreEqual(SparseTomlErrorCode.DuplicateTarget, result.Error?.Code);
        Assert.IsNull(result.Contents);
    }

    [TestMethod]
    public void ReadOverridesReturnsCanonicalPathsRawValuesAndLineNumbers()
    {
        var document = Load(
            """
            root = "keep"

            [notifications]
            incoming_attack_player = { system = true, audio = true, sound = "alarm" }
            """);

        var result = document.ReadOverrides();

        Assert.IsTrue(result.IsValid, result.Error?.Message);
        Assert.IsNotNull(result.Overrides);
        Assert.AreEqual("\"keep\"", result.Overrides["root"].RenderedValue);
        Assert.AreEqual(1, result.Overrides["root"].LineNumber);
        Assert.AreEqual(
            """{ system = true, audio = true, sound = "alarm" }""",
            result.Overrides["notifications.incoming_attack_player"].RenderedValue);
        Assert.AreEqual(4, result.Overrides["notifications.incoming_attack_player"].LineNumber);
        Assert.IsNotNull(result.Tables);
        Assert.AreEqual(3, result.Tables.Single(item => item.CanonicalPath == "notifications").LineNumber);
    }

    [TestMethod]
    public void ReadOverridesPreservesTomlCaseSensitiveKeys()
    {
        var document = Load(
            """
            FutureKey = 1
            futurekey = 2
            """);

        var result = document.ReadOverrides();

        Assert.IsTrue(result.IsValid, result.Error?.Message);
        Assert.AreEqual(2, result.Overrides?.Count);
        Assert.AreEqual("1", result.Overrides!["FutureKey"].RenderedValue);
        Assert.AreEqual("2", result.Overrides["futurekey"].RenderedValue);
    }

    [TestMethod]
    public void RenameTablePreservesBodyUnknownFieldsCommentsAndChildTables()
    {
        const string source = """
            # target heading stays outside the table body
            [sync.targets.old] # provider note
            url = "https://example.invalid/sync"
            future = "keep"

            [sync.targets.old.metadata]
            label = "keep child"

            [unrelated]
            value = true
            """;
        var document = Load(source);

        var result = document.RenameTable("sync.targets.old", "sync.targets.renamed");

        Assert.IsTrue(result.IsValid, result.Error?.Message);
        Assert.IsTrue(result.Changed);
        Assert.AreEqual(
            source
                .Replace("[sync.targets.old]", "[sync.targets.renamed]", StringComparison.Ordinal)
                .Replace("[sync.targets.old.metadata]", "[sync.targets.renamed.metadata]", StringComparison.Ordinal),
            Decode(result.Contents!));
    }

    [TestMethod]
    public void RenameTableRejectsDestinationAndDottedAssignmentCollisions()
    {
        var destination = Load(
            """
            [sync.targets.old]
            value = true
            [sync.targets.new]
            value = false
            """);
        var destinationResult = destination.RenameTable("sync.targets.old", "sync.targets.new");
        Assert.IsFalse(destinationResult.IsValid);
        Assert.AreEqual(SparseTomlErrorCode.DuplicateTarget, destinationResult.Error?.Code);
        Assert.IsNull(destinationResult.Contents);

        var dotted = Load(
            """
            sync.targets.new.value = false
            [sync.targets.old]
            value = true
            """);
        var dottedResult = dotted.RenameTable("sync.targets.old", "sync.targets.new");
        Assert.IsFalse(dottedResult.IsValid);
        Assert.AreEqual(SparseTomlErrorCode.DuplicateTarget, dottedResult.Error?.Code);
        Assert.IsNull(dottedResult.Contents);
    }

    [TestMethod]
    public void RemoveTableRemovesItsDescendantsAndPreservesUnrelatedTables()
    {
        const string source = """
            [sync.targets.remove]
            url = "https://example.invalid/sync"
            unknown = "remove with owner"

            [unrelated]
            value = true

            [sync.targets.remove.metadata]
            label = "also remove"

            [sync.targets.keep]
            url = "https://keep.example.invalid/sync"
            """;
        var document = Load(source);

        var result = document.RemoveTable("sync.targets.remove");

        Assert.IsTrue(result.IsValid, result.Error?.Message);
        var updated = Decode(result.Contents!);
        Assert.IsFalse(updated.Contains("sync.targets.remove", StringComparison.Ordinal));
        Assert.IsFalse(updated.Contains("unknown", StringComparison.Ordinal));
        StringAssert.Contains(updated, "[unrelated]");
        StringAssert.Contains(updated, "[sync.targets.keep]");
    }

    [TestMethod]
    public void TableOperationsPreserveBomAndCrLf()
    {
        const string source = "[sync.targets.old]\r\nurl = \"https://example.invalid\"\r\n";
        var contents = new byte[] { 0xef, 0xbb, 0xbf }
            .Concat(Encoding.UTF8.GetBytes(source))
            .ToArray();
        var document = Load(contents);

        var result = document.RenameTable("sync.targets.old", "sync.targets.new");

        CollectionAssert.AreEqual(
            new byte[] { 0xef, 0xbb, 0xbf }
                .Concat(Encoding.UTF8.GetBytes(source.Replace("old", "new", StringComparison.Ordinal)))
                .ToArray(),
            result.Contents!);
    }

    [TestMethod]
    [DataRow("\"enabled\"")]
    [DataRow("'enabled'")]
    [DataRow("nested . \"enabled\"")]
    [DataRow("\"nested\".'enabled'")]
    public void SimpleQuotedAssignmentPreservesBytesAndUsesBareCanonicalIdentity(string key)
    {
        var source = "[settings]\r\n  " + key + " = false # retain\r\nunknown = 12\r\n";
        var contents = new byte[] { 0xef, 0xbb, 0xbf }.Concat(Encoding.UTF8.GetBytes(source)).ToArray();
        var path = key.Contains('.') ? "settings.nested.enabled" : "settings.enabled";
        var document = Load(contents);
        Assert.IsTrue(document.ReadOverrides().Overrides!.ContainsKey(path));
        var changed = document.SetOverride(path, "true");
        Assert.IsTrue(changed.IsValid, changed.Error?.Message);
        CollectionAssert.AreEqual(new byte[] { 0xef, 0xbb, 0xbf }
            .Concat(Encoding.UTF8.GetBytes(source.Replace("false", "true", StringComparison.Ordinal))).ToArray(), changed.Contents!);
        var removed = document.RemoveOverride(path);
        Assert.IsTrue(removed.IsValid, removed.Error?.Message);
        CollectionAssert.AreEqual(new byte[] { 0xef, 0xbb, 0xbf }
            .Concat(Encoding.UTF8.GetBytes("[settings]\r\nunknown = 12\r\n")).ToArray(), removed.Contents!);
    }

    [TestMethod]
    [DataRow("enabled = true\n\"enabled\" = false\n")]
    [DataRow("\"enabled\" = true\n'enabled' = false\n")]
    [DataRow("\"settings\".enabled = true\n[settings]\n'enabled' = false\n")]
    public void QuotedAndBareDuplicateSpellingsFailClosed(string source)
    {
        var result = Load(source).ValidateForMutation();
        Assert.IsFalse(result.IsValid);
        Assert.AreEqual(SparseTomlErrorCode.DuplicateTarget, result.Error?.Code);
        Assert.IsNull(result.Contents);
    }

    [TestMethod]
    [DataRow("\"settings\" = true\n")]
    [DataRow("\"settings\".enabled.child = true\n")]
    public void QuotedKeysRetainScalarAndNamespaceExclusion(string source)
    {
        var result = Load(source).SetOverride("settings.enabled", "true");
        Assert.IsFalse(result.IsValid);
        Assert.IsNull(result.Contents);
    }

    [TestMethod]
    [DataRow("\"key.with.dot\"")]
    [DataRow("\"key with space\"")]
    [DataRow("\"\\u006bey\"")]
    [DataRow("'clé'")]
    [DataRow("\"\"")]
    [DataRow("'🚀'")]
    public void ValidQuotedKeysRemainDistinctAndSourcePreserved(string key)
    {
        var source = key + " = true\nother = false\n";
        var document = Load(source);
        Assert.IsTrue(document.ValidateForMutation().IsValid);
        var result = document.SetOverride("other", "true");
        Assert.IsTrue(result.IsValid, result.Error?.Message);
        Assert.AreEqual(source.Replace("other = false", "other = true", StringComparison.Ordinal), Decode(result.Contents!));
        var original = document.ReadOverrides();
        var literal = original.Overrides!.Values.Single(item => item.CanonicalPath != "other");
        Assert.AreEqual(1, literal.PathSegments.Count);
        var changed = document.SetOverride(literal.CanonicalPath, "false");
        Assert.IsTrue(changed.IsValid, changed.Error?.Message);
        Assert.AreEqual(source.Replace(" = true", " = false", StringComparison.Ordinal), Decode(changed.Contents!));
    }

    [TestMethod]
    public void LiteralDotsAndDecodedUnicodeCannotAliasDottedPaths()
    {
        const string source = "\"key.with.dot\" = true\nkey.with.dot = false\n\"\\u006bey\".other = true\n";
        var document = Load(source);
        var read = document.ReadOverrides();
        Assert.IsTrue(read.IsValid, read.Error?.Message);
        Assert.AreEqual(3, read.Overrides!.Count);
        Assert.AreEqual("true", read.Overrides["\"key.with.dot\""].RenderedValue);
        Assert.AreEqual("false", read.Overrides["key.with.dot"].RenderedValue);
        Assert.IsTrue(read.Overrides.ContainsKey("key.other"));
        var result = document.SetOverride("\"key.with.dot\"", "false");
        Assert.IsTrue(result.IsValid, result.Error?.Message);
        Assert.AreEqual(source.Replace("\"key.with.dot\" = true", "\"key.with.dot\" = false", StringComparison.Ordinal), Decode(result.Contents!));
        Assert.AreEqual(SparseTomlErrorCode.DuplicateTarget, Load("key = true\n\"\\u006bey\" = false\n").ValidateForMutation().Error?.Code);
    }

    [TestMethod]
    public void QuotedHeadersAndQuotedApiPathsUseDecodedIdentity()
    {
        const string source = "[\"settings\"] # keep\nenabled = false\n";
        var result = Load(source).SetOverride("\"settings\".enabled", "true");
        Assert.IsTrue(result.IsValid, result.Error?.Message);
        Assert.AreEqual(source.Replace("false", "true", StringComparison.Ordinal), Decode(result.Contents!));
        Assert.AreEqual(SparseTomlErrorCode.InvalidPath, Load(source).SetOverride("settings\nenabled", "true").Error?.Code);
        Assert.IsFalse(Load("'key\" = true\n").ValidateForMutation().IsValid);
    }

    [TestMethod]
    [DataRow("\"settings\"")]
    [DataRow("settings")]
    public void MultilineScalarIdentityBlocksChildInsertion(string key)
    {
        var document = Load(key + " = \"\"\"\nheld\n\"\"\"\n");
        Assert.IsTrue(document.ValidateForMutation().IsValid);
        var result = document.SetOverride("settings.enabled", "true");
        Assert.IsFalse(result.IsValid);
        Assert.IsNull(result.Contents);
    }

    [TestMethod]
    [DataRow("enabled = \"\"\"\nheld\n\"\"\"\n\"enabled\" = false\n")]
    [DataRow("\"enabled\" = \"\"\"\nheld\n\"\"\"\nenabled = false\n")]
    [DataRow("enabled = \"\"\"\nheld\n\"\"\"\nenabled = '''\nalso held\n'''\n")]
    public void MultilineAliasesParticipateInDuplicateGuards(string source)
    {
        var document = Load(source);
        Assert.AreEqual(SparseTomlErrorCode.DuplicateTarget, document.ReadOverrides().Error?.Code);
        var result = document.SetOverride("other", "true");
        Assert.AreEqual(SparseTomlErrorCode.DuplicateTarget, result.Error?.Code);
        Assert.IsNull(result.Contents);
    }

    [TestMethod]
    public void MultilineReadRetainsRawValueAndRemovalUsesWholeAssignment()
    {
        const string source = "held = \"\"\"\r\nkeep everything\r\n\"\"\" # retain\r\n\"enabled\" = false\r\n";
        var document = Load(source);
        var held = document.ReadOverrides().Overrides!["held"];
        Assert.AreEqual("\"\"\"\r\nkeep everything\r\n\"\"\"", held.RenderedValue);
        Assert.IsTrue(LauncherTomlValue.TryReadString(held.RenderedValue, out var decoded));
        Assert.AreEqual("keep everything\n", decoded);
        var result = document.SetOverride("enabled", "true");
        Assert.IsTrue(result.IsValid, result.Error?.Message);
        Assert.AreEqual(source.Replace("false", "true", StringComparison.Ordinal), Decode(result.Contents!));
        var removed = document.RemoveOverride("held");
        Assert.IsTrue(removed.IsValid, removed.Error?.Message);
        Assert.AreEqual("\"enabled\" = false\r\n", Decode(removed.Contents!));
    }

    private static SparseTomlDocument Load(string source) =>
        Load(Encoding.UTF8.GetBytes(source));

    private static SparseTomlDocument Load(byte[] source)
    {
        var result = SparseTomlDocument.Load(source, out var document);
        Assert.IsTrue(result.IsValid, result.Error?.Message);
        Assert.IsNotNull(document);
        return document;
    }

    private static string Decode(byte[] contents)
    {
        var offset = contents.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }) ? 3 : 0;
        return Encoding.UTF8.GetString(contents, offset, contents.Length - offset);
    }
}
