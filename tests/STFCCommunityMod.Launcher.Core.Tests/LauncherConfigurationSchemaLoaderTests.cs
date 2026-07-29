using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using STFCCommunityMod.Launcher.Core;

namespace STFCCommunityMod.Launcher.Core.Tests;

[TestClass]
public sealed class LauncherConfigurationSchemaLoaderTests
{
    private static readonly string[] ExpectedPlayerCategories = ["graphics", "notifications"];

    [TestMethod]
    public void LoadsTypedMetadataAndRetainsDetachedObjectDefault()
    {
        const string json =
            """
            {
              "schemaVersion": "1.0.0",
              "schemaId": "stfc-community-mod.config-schema",
              "source": {
                "id": "guffawaffle",
                "repository": "Guffawaffle/stfc-mod"
              },
              "settings": [
                {
                  "path": "notifications.fleet_arrived_in_system",
                  "title": "Fleet Arrived In System",
                  "description": "Choose system and audio delivery.",
                  "category": "notifications",
                  "control": "notification-policy",
                  "valueType": {
                    "kind": "union",
                    "variants": [
                      { "kind": "boolean" },
                      { "kind": "object" }
                    ]
                  },
                  "default": {
                    "system": true,
                    "audio": true,
                    "sound": "arrival"
                  },
                  "platforms": [ "windows", "macos" ],
                  "apply": "next-session",
                  "sensitivity": "public",
                  "stability": "experimental",
                  "sourceSupport": [ "guffawaffle" ],
                  "presentation": {
                    "label": "Fleet arrived in system",
                    "help": "Choose system and audio delivery.",
                    "group": "Fleet movement and mining",
                    "searchTerms": [
                      "notifications.fleet_arrived_in_system",
                      "fleet arrival"
                    ],
                    "editorWidth": "standard",
                    "applyTiming": "Next launch",
                    "accessibleName": "Fleet arrived in system",
                    "accessibleHelp": "Choose system and audio delivery. Applies: Next launch."
                  }
                }
              ]
            }
            """;

        LauncherConfigurationCatalog catalog;
        using (var stream = JsonStream(json))
        {
            catalog = LauncherConfigurationSchemaLoader.Load(stream);
        }

        var setting = catalog.Settings.Single();
        Assert.AreEqual(new Version(1, 0, 0), catalog.SchemaVersion);
        Assert.AreEqual(LauncherConfigurationSourceId.Guffawaffle, catalog.Source.Id);
        Assert.AreEqual("Guffawaffle/stfc-mod", catalog.Source.Repository);
        Assert.AreEqual(LauncherConfigurationControl.NotificationPolicy, setting.Control);
        Assert.AreEqual(LauncherConfigurationValueKind.Union, setting.ValueKind);
        Assert.AreEqual(LauncherConfigurationStability.Experimental, setting.Stability);
        Assert.AreEqual(
            LauncherConfigurationApplyBehavior.NextSession,
            setting.ApplyBehavior);
        Assert.AreEqual("next-session", setting.Apply);
        Assert.AreEqual("Fleet arrived in system", setting.Presentation.Label);
        Assert.AreEqual("Fleet movement and mining", setting.Presentation.Group);
        Assert.AreEqual(
            LauncherConfigurationEditorWidth.Standard,
            setting.Presentation.EditorWidth);
        CollectionAssert.Contains(
            setting.Presentation.SearchTerms.ToArray(),
            "notifications.fleet_arrived_in_system");
        CollectionAssert.AreEqual(
            new[]
            {
                LauncherConfigurationPlatform.Windows,
                LauncherConfigurationPlatform.Macos,
            },
            setting.Platforms.ToArray());
        Assert.IsTrue(setting.DefaultValue.GetProperty("system").GetBoolean());
        Assert.AreEqual("arrival", setting.DefaultValue.GetProperty("sound").GetString());
        Assert.AreEqual(JsonValueKind.Array, setting.ValueTypeDefinition.GetProperty("variants").ValueKind);
    }

    [TestMethod]
    public void VisibilityAndSearchExposePlayerSettingsWithoutDroppingInternalMetadata()
    {
        const string settings =
            """
            {
              "path": "graphics.ui_scale",
              "title": "UI Scale",
              "description": "Scale the game interface.",
              "category": "graphics",
              "control": "scalar",
              "valueType": { "kind": "number" },
              "default": 1.0,
              "platforms": [ "windows" ],
              "apply": "next-session",
              "sensitivity": "public",
              "stability": "stable",
              "sourceSupport": [ "guffawaffle" ]
            },
            {
              "path": "notifications.fleet_arrived_in_system",
              "title": "Fleet Arrival",
              "description": "Configure arrival delivery.",
              "category": "notifications",
              "control": "notification-policy",
              "valueType": { "kind": "union" },
              "default": false,
              "platforms": [ "windows" ],
              "apply": "next-session",
              "sensitivity": "public",
              "stability": "experimental",
              "sourceSupport": [ "guffawaffle" ]
            },
            {
              "path": "advanced.diagnostics.runtime_trace",
              "title": "Runtime Trace",
              "description": "Internal tracing.",
              "category": "advanced",
              "control": "scalar",
              "valueType": { "kind": "boolean" },
              "default": false,
              "platforms": [ "windows" ],
              "apply": "next-session",
              "sensitivity": "public",
              "stability": "internal",
              "sourceSupport": [ "guffawaffle" ]
            },
            {
              "path": "sync.token",
              "title": "Sync Token",
              "description": "Private credential.",
              "category": "sync",
              "control": "scalar",
              "valueType": { "kind": "string" },
              "default": "",
              "platforms": [ "windows" ],
              "apply": "next-session",
              "sensitivity": "secret",
              "stability": "stable",
              "sourceSupport": [ "guffawaffle" ]
            },
            {
              "path": "sync.targets.*.endpoint",
              "title": "Target Endpoint",
              "description": "Endpoint for a concrete sync target.",
              "category": "sync",
              "control": "scalar",
              "valueType": { "kind": "string" },
              "default": "",
              "platforms": [ "windows" ],
              "apply": "next-session",
              "sensitivity": "public",
              "stability": "stable",
              "sourceSupport": [ "guffawaffle" ]
            }
            """;

        using var stream = JsonStream(SchemaWithSettings(settings));
        var catalog = LauncherConfigurationSchemaLoader.Load(stream);

        Assert.AreEqual(5, catalog.Settings.Count);
        Assert.AreEqual(2, catalog.VisibleSettings.Count);
        CollectionAssert.AreEqual(
            ExpectedPlayerCategories,
            catalog.Categories.ToArray());
        Assert.AreEqual(
            "graphics.ui_scale",
            catalog.Search("INTERFACE", "GRAPHICS").Single().Path);
        Assert.AreEqual(
            "notifications.fleet_arrived_in_system",
            catalog.Search("arrival").Single().Path);
        Assert.AreEqual(
            "graphics.ui_scale",
            catalog.Search("friendly graphics search").Single().Path);
        Assert.AreEqual(0, catalog.Search("runtime trace").Count);
        Assert.AreEqual(0, catalog.Search("target endpoint").Count);
        Assert.AreEqual(2, catalog.Search(string.Empty).Count);

        var template = catalog.Settings.Single(setting => setting.Path == "sync.targets.*.endpoint");
        Assert.IsTrue(template.IsTemplate);
        Assert.IsTrue(template.IsPlayerFacing);
        Assert.IsFalse(template.IsDirectlyEditable);
        Assert.IsFalse(catalog.VisibleSettings.Contains(template));

        var concrete = catalog.Settings.Single(setting => setting.Path == "graphics.ui_scale");
        Assert.IsFalse(concrete.IsTemplate);
        Assert.IsTrue(concrete.IsPlayerFacing);
        Assert.IsTrue(concrete.IsDirectlyEditable);
    }

    [TestMethod]
    public void RejectsUnsupportedVersionAndUnknownMetadata()
    {
        var unsupportedVersion = SchemaWithSettings(
            ValidBooleanSetting(),
            schemaVersion: "2.0.0");
        var unknownControl = SchemaWithSettings(
            ValidBooleanSetting().Replace(
                @"""control"": ""scalar""",
                @"""control"": ""mystery""",
                StringComparison.Ordinal));

        Assert.ThrowsException<LauncherConfigurationSchemaException>(
            () => LoadJson(unsupportedVersion));
        Assert.ThrowsException<LauncherConfigurationSchemaException>(
            () => LoadJson(unknownControl));
    }

    [TestMethod]
    public void RejectsInvalidPresentationAndApplyMetadata()
    {
        var missingPresentation = SchemaWithSettings(
            ValidBooleanSetting(),
            includePresentation: false);
        var unknownApply = SchemaWithSettings(
            ValidBooleanSetting().Replace(
                @"""apply"": ""next-session""",
                @"""apply"": ""surprise""",
                StringComparison.Ordinal));
        var mismatchedApplyTiming = SchemaWithSettings(
            ValidBooleanSetting(),
            applyTiming: "Immediate");
        var unknownPresentationProperty = SchemaWithSettings(
            ValidBooleanSetting(),
            extraPresentationProperty: @"""surprise"": true");
        var duplicateSearchTerm = SchemaWithSettings(
            ValidBooleanSetting(),
            duplicateSearchTerm: true);
        var unitOnBoolean = SchemaWithSettings(
            ValidBooleanSetting(),
            unit: "%");
        var unknownEditorWidth = SchemaWithSettings(
            ValidBooleanSetting(),
            editorWidth: "enormous");

        Assert.ThrowsException<LauncherConfigurationSchemaException>(
            () => LoadJson(missingPresentation));
        Assert.ThrowsException<LauncherConfigurationSchemaException>(
            () => LoadJson(unknownApply));
        Assert.ThrowsException<LauncherConfigurationSchemaException>(
            () => LoadJson(mismatchedApplyTiming));
        Assert.ThrowsException<LauncherConfigurationSchemaException>(
            () => LoadJson(unknownPresentationProperty));
        Assert.ThrowsException<LauncherConfigurationSchemaException>(
            () => LoadJson(duplicateSearchTerm));
        Assert.ThrowsException<LauncherConfigurationSchemaException>(
            () => LoadJson(unitOnBoolean));
        Assert.ThrowsException<LauncherConfigurationSchemaException>(
            () => LoadJson(unknownEditorWidth));
    }

    [TestMethod]
    public void RejectsDuplicatePathsAndDefaultsThatDoNotMatchTheirKinds()
    {
        var duplicate = SchemaWithSettings(
            $"{ValidBooleanSetting()},{ValidBooleanSetting()}");
        var invalidDefault = SchemaWithSettings(
            ValidBooleanSetting().Replace(
                @"""default"": false",
                @"""default"": ""false""",
                StringComparison.Ordinal));

        Assert.ThrowsException<LauncherConfigurationSchemaException>(
            () => LoadJson(duplicate));
        Assert.ThrowsException<LauncherConfigurationSchemaException>(
            () => LoadJson(invalidDefault));
    }

    [TestMethod]
    public void RetainsNumericConstraintsAndRejectsInvalidRanges()
    {
        const string numericSetting =
            """
            {
              "path": "advanced.interval",
              "title": "Interval",
              "description": "Diagnostic interval.",
              "category": "advanced",
              "control": "scalar",
              "valueType": { "kind": "integer" },
              "constraints": { "minimum": 1000, "maximum": 60000 },
              "default": 5000,
              "platforms": [ "windows" ],
              "apply": "next-session",
              "sensitivity": "public",
              "stability": "advanced",
              "sourceSupport": [ "guffawaffle" ]
            }
            """;

        var catalog = LoadJson(
            SchemaWithSettings(
                numericSetting,
                unit: "ms",
                editorWidth: "compact"));
        var constraints = catalog.Settings.Single().NumericConstraints;

        Assert.IsNotNull(constraints);
        Assert.AreEqual(1000d, constraints.Minimum);
        Assert.AreEqual(60000d, constraints.Maximum);
        Assert.IsTrue(constraints.Contains(5000));
        Assert.IsFalse(constraints.Contains(999));
        Assert.AreEqual("ms", catalog.Settings.Single().Presentation.Unit);
        Assert.AreEqual(
            LauncherConfigurationEditorWidth.Compact,
            catalog.Settings.Single().Presentation.EditorWidth);

        var reversedRange = numericSetting
            .Replace(@"""minimum"": 1000", @"""minimum"": 70000", StringComparison.Ordinal);
        var invalidDefault = numericSetting
            .Replace(@"""default"": 5000", @"""default"": 999", StringComparison.Ordinal);
        Assert.ThrowsException<LauncherConfigurationSchemaException>(
            () => LoadJson(SchemaWithSettings(reversedRange)));
        Assert.ThrowsException<LauncherConfigurationSchemaException>(
            () => LoadJson(SchemaWithSettings(invalidDefault)));
    }

    [TestMethod]
    public void RetainsPurposeSpecificStringFormatsAndRejectsUnknownFormats()
    {
        const string stringSetting =
            """
            {
              "path": "config.settings_url",
              "title": "Settings URL",
              "description": "Remote settings URL.",
              "category": "config",
              "control": "scalar",
              "valueType": { "kind": "string", "format": "uri" },
              "default": "",
              "platforms": [ "windows" ],
              "apply": "next-session",
              "sensitivity": "public",
              "stability": "stable",
              "sourceSupport": [ "guffawaffle" ]
            }
            """;

        var catalog = LoadJson(
            SchemaWithSettings(
                stringSetting,
                includeHelp: false));
        var setting = catalog.Settings.Single();
        Assert.IsNull(setting.Presentation.Help);
        Assert.AreEqual(
            "uri",
            LauncherConfigurationStringValue.ReadFormat(setting));

        var unknownFormat = stringSetting.Replace(
            @"""format"": ""uri""",
            @"""format"": ""surprise""",
            StringComparison.Ordinal);
        Assert.ThrowsException<LauncherConfigurationSchemaException>(
            () => LoadJson(SchemaWithSettings(unknownFormat)));
    }

    [TestMethod]
    public void LoadsGeneratedRepositorySchema()
    {
        var schemaPath = FindRepositoryFile(
            "docs",
            "windows-launcher",
            "config-schema.guffawaffle.v1.json");

        var catalog = LauncherConfigurationSchemaLoader.LoadFile(schemaPath);

        Assert.IsTrue(catalog.Settings.Count >= 300);
        Assert.IsTrue(catalog.VisibleSettings.Count > 0);
        Assert.IsTrue(
            catalog.Settings.Any(
                setting =>
                    setting.Control == LauncherConfigurationControl.NotificationPolicy));
        Assert.IsTrue(
            catalog.Settings.Any(
                setting =>
                    setting.Control == LauncherConfigurationControl.Keybinding));
        Assert.IsTrue(
            catalog.Settings.Any(
                setting =>
                    setting.Stability == LauncherConfigurationStability.Internal));

        var templates = catalog.Settings.Where(setting => setting.IsTemplate).ToArray();
        Assert.AreEqual(21, templates.Length);
        Assert.AreEqual(18, templates.Count(setting => setting.IsPlayerFacing));
        Assert.IsTrue(templates.All(setting => !setting.IsDirectlyEditable));
        Assert.IsFalse(catalog.VisibleSettings.Any(setting => setting.IsTemplate));
        Assert.AreEqual(0, catalog.Search("*").Count);

        var savedZoomFamily = catalog.Settings
            .Where(setting =>
                setting.Presentation.Family?.Id == "camera.saved-zoom-positions")
            .OrderBy(setting => setting.Presentation.Family!.MemberOrder)
            .ToArray();
        Assert.AreEqual(6, savedZoomFamily.Length);
        Assert.AreEqual("Camera", savedZoomFamily[0].Presentation.Family!.ParentGroup);
        Assert.AreEqual("Default", savedZoomFamily[0].Presentation.Family!.MemberLabel);
        Assert.AreEqual("Preset 5", savedZoomFamily[5].Presentation.Family!.MemberLabel);
        Assert.IsTrue(savedZoomFamily.All(setting =>
            setting.Presentation.Family!.PresentationHint == "compact-binding-list"));
    }

    private static LauncherConfigurationCatalog LoadJson(string json)
    {
        using var stream = JsonStream(json);
        return LauncherConfigurationSchemaLoader.Load(stream);
    }

    private static MemoryStream JsonStream(string json) =>
        new(Encoding.UTF8.GetBytes(json));

    private static string SchemaWithSettings(
        string settings,
        string schemaVersion = "1.0.0",
        bool includePresentation = true,
        bool includeHelp = true,
        string? applyTiming = null,
        string? extraPresentationProperty = null,
        bool duplicateSearchTerm = false,
        string? unit = null,
        string editorWidth = "standard")
    {
        var settingArray = JsonNode.Parse($"[{settings}]")!.AsArray();
        if (includePresentation)
        {
            foreach (var settingNode in settingArray)
            {
                var setting = settingNode!.AsObject();
                var path = setting["path"]!.GetValue<string>();
                var title = setting["title"]!.GetValue<string>();
                var description = setting["description"]!.GetValue<string>();
                var apply = setting["apply"]!.GetValue<string>();
                var terms = new JsonArray(path);
                if (path == "graphics.ui_scale")
                {
                    terms.Add("friendly graphics search");
                }
                if (duplicateSearchTerm)
                {
                    terms.Add(path.ToUpperInvariant());
                }

                var presentation = new JsonObject
                {
                    ["label"] = title,
                    ["group"] = "Test group",
                    ["searchTerms"] = terms,
                    ["editorWidth"] = editorWidth,
                    ["applyTiming"] = applyTiming ?? ApplyTimingForTest(apply),
                    ["accessibleName"] = title,
                    ["accessibleHelp"] = $"{description} Applies: {applyTiming ?? ApplyTimingForTest(apply)}.",
                };
                if (includeHelp)
                {
                    presentation["help"] = description;
                }
                if (unit is not null)
                {
                    presentation["unit"] = unit;
                }
                if (extraPresentationProperty is not null)
                {
                    var property = JsonNode.Parse($"{{{extraPresentationProperty}}}")!.AsObject().Single();
                    presentation[property.Key] = property.Value?.DeepClone();
                }
                setting["presentation"] = presentation;
            }
        }

        var renderedSettings = string.Join(
            $",{Environment.NewLine}",
            settingArray.Select(setting => setting!.ToJsonString()));
        return
        $$"""
          {
            "schemaVersion": "{{schemaVersion}}",
            "schemaId": "stfc-community-mod.config-schema",
            "source": {
              "id": "guffawaffle",
              "repository": "Guffawaffle/stfc-mod"
            },
            "settings": [
              {{renderedSettings}}
            ]
          }
          """;
    }

    private static string ApplyTimingForTest(string apply) =>
        apply switch
        {
            "live" => "Immediate",
            "next-session" => "Next launch",
            "restart-required" => "Restart required",
            _ => "Unsupported",
        };

    private static string ValidBooleanSetting() =>
        """
        {
          "path": "graphics.example",
          "title": "Example",
          "description": "Example setting.",
          "category": "graphics",
          "control": "scalar",
          "valueType": { "kind": "boolean" },
          "default": false,
          "platforms": [ "windows" ],
          "apply": "next-session",
          "sensitivity": "public",
          "stability": "stable",
          "sourceSupport": [ "guffawaffle" ]
        }
        """;

    private static string FindRepositoryFile(params string[] relativeParts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidateParts = new[] { directory.FullName }.Concat(relativeParts).ToArray();
            var candidate = Path.Combine(candidateParts);
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
