using System.Text;

namespace STFCCommunityMod.Launcher.Core;

/// <summary>Unambiguous TOML paths; a literal dot in a segment never denotes a child.</summary>
public static class LauncherTomlPath
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    public static string Render(IEnumerable<string> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        var decoded = segments.ToArray();
        foreach (var segment in decoded) StrictUtf8.GetByteCount(segment);
        var result = TomlNativeRuntime.Request(new("parse_path", string.Empty, Path: decoded));
        if (!result.Ok || result.Value is null)
            throw new InvalidOperationException("The verified TOML editor could not render a setting path.");
        return result.Value;
    }

    public static bool TryParse(string path, out string[] segments) => TryParse(path, out segments, out _);

    internal static bool TryParse(string path, out string[] segments, out SparseTomlError? error)
    {
        segments = [];
        try { StrictUtf8.GetByteCount(path); }
        catch (Exception exception) when (exception is EncoderFallbackException or ArgumentNullException)
        {
            error = SparseTomlDocument.MapError(new() { Code = "InvalidPath" });
            return false;
        }
        var result = TomlNativeRuntime.Request(new("parse_path", string.Empty, Value: path));
        error = result.Ok ? null : SparseTomlDocument.MapError(result.Error);
        if (!result.Ok || result.Path is not { Length: > 0 })
        {
            error ??= SparseTomlDocument.MapError(new() { Code = "InvalidPath" });
            return false;
        }
        segments = result.Path;
        return true;
    }

    public static bool HasPrefix(IReadOnlyList<string> path, IReadOnlyList<string> prefix) =>
        path.Count >= prefix.Count && prefix.Select((segment, index) => segment == path[index]).All(equal => equal);
}
