namespace STFCCommunityMod.Launcher.Core;

public enum SparseTomlErrorCode
{
    InvalidUtf8,
    InvalidPath,
    InvalidValue,
    DuplicateTarget,
    UnsupportedDocument,
    UnsupportedTarget,
}

public sealed record SparseTomlError(
    SparseTomlErrorCode Code,
    string Message,
    int? LineNumber = null);

public sealed record SparseTomlEditResult(
    bool IsValid,
    bool Changed,
    byte[]? Contents,
    SparseTomlError? Error)
{
    public static SparseTomlEditResult Unchanged(byte[] contents) =>
        new(true, false, contents, null);

    public static SparseTomlEditResult Updated(byte[] contents) =>
        new(true, true, contents, null);

    public static SparseTomlEditResult Invalid(SparseTomlError error) =>
        new(false, false, null, error);
}

public sealed record SparseTomlOverride(
    string CanonicalPath,
    string RenderedValue,
    int LineNumber);

public sealed record SparseTomlTable(
    string CanonicalPath,
    int LineNumber);

public sealed record SparseTomlReadResult(
    bool IsValid,
    IReadOnlyDictionary<string, SparseTomlOverride>? Overrides,
    IReadOnlyList<SparseTomlTable>? Tables,
    SparseTomlError? Error)
{
    public static SparseTomlReadResult Success(
        IReadOnlyDictionary<string, SparseTomlOverride> overrides,
        IReadOnlyList<SparseTomlTable> tables) =>
        new(true, overrides, tables, null);

    public static SparseTomlReadResult Invalid(SparseTomlError error) =>
        new(false, null, null, error);
}
