using System.Collections.ObjectModel;
using System.Text;

namespace STFCCommunityMod.Launcher.Core;

/// <summary>Source-preserving edits prepared and semantically verified by the shared TOML engine.</summary>
public sealed class SparseTomlDocument
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly byte[] Utf8Bom = [0xef, 0xbb, 0xbf];
    private readonly string text;
    private readonly bool hasBom;
    private readonly byte[] originalContents;

    private SparseTomlDocument(string text, bool hasBom, byte[] contents)
    {
        this.text = text;
        this.hasBom = hasBom;
        originalContents = [.. contents];
    }

    public static SparseTomlEditResult Load(byte[] contents, out SparseTomlDocument? document)
    {
        ArgumentNullException.ThrowIfNull(contents);
        document = null;
        var bom = contents.AsSpan().StartsWith(Utf8Bom);
        try
        {
            var source = StrictUtf8.GetString(bom ? contents.AsSpan(Utf8Bom.Length) : contents.AsSpan());
            document = new(source, bom, contents);
            return SparseTomlEditResult.Unchanged([.. contents]);
        }
        catch (DecoderFallbackException)
        {
            return SparseTomlEditResult.Invalid(new(SparseTomlErrorCode.InvalidUtf8,
                "The configuration is not valid UTF-8."));
        }
    }

    public SparseTomlEditResult ValidateForMutation()
    {
        var result = TomlNativeRuntime.Request(new("validate", text));
        return result.Ok ? SparseTomlEditResult.Unchanged([.. originalContents])
            : SparseTomlEditResult.Invalid(MapError(result.Error));
    }

    public SparseTomlReadResult ReadOverrides()
    {
        var result = TomlNativeRuntime.Request(new("read", text));
        if (!result.Ok) return SparseTomlReadResult.Invalid(MapError(result.Error));
        if (result.Overrides is null || result.Tables is null
            || result.Overrides.Any(item => item.Path.Length == 0 || item.CanonicalPath.Length == 0 || item.Line <= 0)
            || result.Tables.Any(item => item.Path.Length == 0 || item.CanonicalPath.Length == 0 || item.Line <= 0))
            return SparseTomlReadResult.Invalid(MapError(new() { Code = "InternalError" }));
        var overrides = new Dictionary<string, SparseTomlOverride>(StringComparer.Ordinal);
        foreach (var item in result.Overrides)
        {
            var canonical = item.CanonicalPath;
            if (!overrides.TryAdd(canonical, new(canonical, item.Value, item.Line)
                { PathSegments = Array.AsReadOnly(item.Path), SemanticValue = item.SemanticValue }))
                return SparseTomlReadResult.Invalid(MapError(new() { Code = "DuplicateTarget", Line = item.Line }));
        }
        var tables = result.Tables.Select(item => new SparseTomlTable(item.CanonicalPath, item.Line)
            { PathSegments = Array.AsReadOnly(item.Path) }).ToArray();
        return SparseTomlReadResult.Success(new ReadOnlyDictionary<string, SparseTomlOverride>(overrides), Array.AsReadOnly(tables));
    }

    public SparseTomlEditResult SetOverride(string canonicalPath, string renderedTomlValue) =>
        Prepare("set", canonicalPath, renderedTomlValue);
    public SparseTomlEditResult RemoveOverride(string canonicalPath) => Prepare("remove", canonicalPath);
    public SparseTomlEditResult RemoveTable(string canonicalTablePath) => Prepare("remove_table", canonicalTablePath);
    public SparseTomlEditResult RenameTable(string canonicalTablePath, string newCanonicalTablePath) =>
        Prepare("rename_table", canonicalTablePath, destination: newCanonicalTablePath);

    private SparseTomlEditResult Prepare(string operation, string path, string? value = null, string? destination = null)
    {
        if (!LauncherTomlPath.TryParse(path, out var segments, out var error))
            return SparseTomlEditResult.Invalid(error!);
        string[]? destinationSegments = null;
        if (destination is not null && !LauncherTomlPath.TryParse(destination, out destinationSegments, out error))
            return SparseTomlEditResult.Invalid(error!);
        if (value is not null)
        {
            try { StrictUtf8.GetByteCount(value); }
            catch (EncoderFallbackException) { return SparseTomlEditResult.Invalid(MapError(new() { Code = "InvalidValue" })); }
        }
        var result = TomlNativeRuntime.Request(new(operation, text, segments, value, destinationSegments));
        if (!result.Ok) return SparseTomlEditResult.Invalid(MapError(result.Error));
        if (result.Text is null) return SparseTomlEditResult.Invalid(MapError(new() { Code = "InternalError" }));
        byte[] contents;
        try { contents = StrictUtf8.GetBytes(result.Text); }
        catch (EncoderFallbackException) { return SparseTomlEditResult.Invalid(MapError(new() { Code = "InternalError" })); }
        if (hasBom) contents = [.. Utf8Bom, .. contents];
        return contents.AsSpan().SequenceEqual(originalContents)
            ? SparseTomlEditResult.Unchanged(contents) : SparseTomlEditResult.Updated(contents);
    }

    internal static SparseTomlError MapError(TomlNativeError? error)
    {
        var code = error?.Code switch
        {
            "InvalidUtf8" => SparseTomlErrorCode.InvalidUtf8,
            "InvalidPath" => SparseTomlErrorCode.InvalidPath,
            "InvalidValue" => SparseTomlErrorCode.InvalidValue,
            "DuplicateTarget" => SparseTomlErrorCode.DuplicateTarget,
            "UnsupportedTarget" => SparseTomlErrorCode.UnsupportedTarget,
            "NativeComponentUnavailable" => SparseTomlErrorCode.EditorUnavailable,
            "InternalError" => SparseTomlErrorCode.EditorUnavailable,
            _ => SparseTomlErrorCode.InvalidDocument,
        };
        var message = code switch
        {
            SparseTomlErrorCode.InvalidUtf8 => "The configuration is not valid UTF-8.",
            SparseTomlErrorCode.InvalidPath => "The requested setting path is not a valid TOML key path.",
            SparseTomlErrorCode.InvalidValue => "The supplied value must be exactly one valid TOML value.",
            SparseTomlErrorCode.DuplicateTarget => "The configuration contains duplicate TOML definitions.",
            SparseTomlErrorCode.UnsupportedTarget => "The requested TOML edit cannot be verified safely. No change was prepared.",
            SparseTomlErrorCode.EditorUnavailable => "The verified TOML editor is unavailable or incompatible. Rebuild or repair Mod Bridge.",
            _ => "The configuration contains malformed TOML. No change was prepared.",
        };
        return new(code, message, error?.Line is > 0 ? error.Line : null);
    }
}
