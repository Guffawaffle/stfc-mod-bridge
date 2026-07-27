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
