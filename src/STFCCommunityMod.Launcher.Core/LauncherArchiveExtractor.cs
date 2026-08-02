using System.IO.Compression;

namespace STFCCommunityMod.Launcher.Core;

public static class LauncherArchiveExtractor
{
    private const int MaximumEntries = 128;
    private const long MaximumExpandedBytes = 768L * 1024L * 1024L;
    private static readonly HashSet<string> PortableExecutableAllowlist = new(StringComparer.Ordinal)
    {
        ModControlProductIdentity.ExecutableName,
        ModControlProductIdentity.UpdaterExecutableName,
    };

    public static void Extract(byte[] contents, string destination)
    {
        ArgumentNullException.ThrowIfNull(contents);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);

        var destinationRoot = Path.GetFullPath(destination);
        Directory.CreateDirectory(destinationRoot);
        using var archive = new ZipArchive(new MemoryStream(contents, writable: false), ZipArchiveMode.Read);
        if (archive.Entries.Count is 0 or > MaximumEntries)
        {
            throw new InvalidDataException("Mod Control archive entry count is invalid.");
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
                throw new InvalidDataException("Mod Control archive contains an unsafe entry.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: false);
        }

        RejectUnexpectedPortableExecutables(destinationRoot);
    }

    private static void RejectUnexpectedPortableExecutables(string destinationRoot)
    {
        foreach (var path in Directory.EnumerateFiles(destinationRoot, "*", SearchOption.AllDirectories))
        {
            if (!IsPortableExecutable(path))
            {
                continue;
            }

            var relativePath = Path.GetRelativePath(destinationRoot, path).Replace('\\', '/');
            if (!PortableExecutableAllowlist.Contains(relativePath))
            {
                throw new InvalidDataException(
                    $"Mod Control archive contains an unexpected portable executable: {relativePath}");
            }
        }
    }

    private static bool IsPortableExecutable(string path)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length < 64 || stream.ReadByte() != 'M' || stream.ReadByte() != 'Z')
        {
            return false;
        }

        stream.Position = 0x3c;
        Span<byte> offsetBytes = stackalloc byte[4];
        if (stream.Read(offsetBytes) != offsetBytes.Length)
        {
            return false;
        }
        var peOffset = BitConverter.ToInt32(offsetBytes);
        if (peOffset < 0 || peOffset > stream.Length - 4)
        {
            return false;
        }

        stream.Position = peOffset;
        Span<byte> signature = stackalloc byte[4];
        return stream.Read(signature) == signature.Length
            && signature[0] == 'P'
            && signature[1] == 'E'
            && signature[2] == 0
            && signature[3] == 0;
    }
}
