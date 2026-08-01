using System.IO.Compression;

namespace STFCCommunityMod.Launcher.Core;

public static class LauncherArchiveExtractor
{
    private const int MaximumEntries = 128;
    private const long MaximumExpandedBytes = 768L * 1024L * 1024L;

    public static void Extract(byte[] contents, string destination)
    {
        ArgumentNullException.ThrowIfNull(contents);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);

        var destinationRoot = Path.GetFullPath(destination);
        Directory.CreateDirectory(destinationRoot);
        using var archive = new ZipArchive(new MemoryStream(contents, writable: false), ZipArchiveMode.Read);
        if (archive.Entries.Count is 0 or > MaximumEntries)
        {
            throw new InvalidDataException("Launcher archive entry count is invalid.");
        }

        long expandedBytes = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name))
            {
                continue;
            }

            expandedBytes = checked(expandedBytes + entry.Length);
            var relative = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
            var components = relative.Split(Path.DirectorySeparatorChar);
            var target = Path.GetFullPath(Path.Combine(destinationRoot, relative));
            if (expandedBytes > MaximumExpandedBytes
                || Path.IsPathFullyQualified(relative)
                || relative.Contains(':')
                || components.Any(component => component is "" or "." or "..")
                || !target.StartsWith(destinationRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || !seen.Add(target)
                || (entry.ExternalAttributes & 0xF0000000) == 0xA0000000)
            {
                throw new InvalidDataException("Launcher archive contains an unsafe entry.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: false);
        }
    }
}
