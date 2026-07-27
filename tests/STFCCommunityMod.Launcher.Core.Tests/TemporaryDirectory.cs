namespace STFCCommunityMod.Launcher.Core.Tests;

internal sealed class TemporaryDirectory : IDisposable
{
    private readonly string ownedRoot;

    public TemporaryDirectory(string? childName = null)
    {
        ownedRoot = System.IO.Path.GetFullPath(System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "stfc-launcher-tests",
            Guid.NewGuid().ToString("N")));
        Path = string.IsNullOrEmpty(childName)
            ? ownedRoot
            : System.IO.Path.Combine(ownedRoot, childName);
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string CreateDirectory(params string[] parts)
    {
        var path = parts.Aggregate(Path, System.IO.Path.Combine);
        Directory.CreateDirectory(path);
        return path;
    }

    public static string CreateFile(string directory, string fileName)
    {
        var path = System.IO.Path.Combine(directory, fileName);
        File.WriteAllBytes(path, []);
        return path;
    }

    public void Dispose()
    {
        var expectedParent = System.IO.Path.GetFullPath(System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "stfc-launcher-tests"));
        var actualParent = Directory.GetParent(ownedRoot)?.FullName;
        if (!string.Equals(actualParent, expectedParent, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Refusing to clean a test directory outside {expectedParent}: {ownedRoot}");
        }

        if (Directory.Exists(ownedRoot))
        {
            Directory.Delete(ownedRoot, true);
        }
    }
}
