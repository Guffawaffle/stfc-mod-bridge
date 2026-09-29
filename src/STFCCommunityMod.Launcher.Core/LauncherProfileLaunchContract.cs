using System.Reflection.PortableExecutable;

namespace STFCCommunityMod.Launcher.Core;

public sealed record LauncherProfileContractResult(bool IsValid, string? ProfileId, string Message);

public static class LauncherProfileLaunchContract
{
    private const string MarkerFileName = "stfc_community_mod.profile";
    private const string ContractExport = "STFCModProfileIsolationContractV1";

    public static LauncherProfileContractResult Inspect(string gameDirectory, string? expectedId = null)
    {
        try
        {
            var game = GameInstallValidator.Validate(gameDirectory);
            if (!game.IsValid || game.GameDirectory is null)
            {
                return Invalid(game.Message);
            }

            var markerPath = Path.Combine(game.GameDirectory, MarkerFileName);
            if (!File.Exists(markerPath))
            {
                return Invalid("This game folder has no profile marker.");
            }
            using var marker = File.OpenRead(markerPath);
            Span<byte> bytes = stackalloc byte[65];
            var count = marker.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false);
            if (count > 64 || !TryParseMarker(bytes[..count], out var id))
            {
                return Invalid("The profile marker is invalid. Expected one v1:<lowercase-id> line.");
            }
            if (expectedId is not null && !string.Equals(id, expectedId, StringComparison.Ordinal))
            {
                return Invalid("The profile marker no longer matches this saved profile key.");
            }

            var capability = InspectCapableDll(game.GameDirectory);
            if (!capability.IsValid)
            {
                return capability;
            }
            return new(true, id, "Profile marker and mod contract verified.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException or BadImageFormatException
            or InvalidOperationException or OverflowException)
        {
            return Invalid($"The profile launch files could not be verified: {exception.Message}");
        }
    }

    public static LauncherProfileContractResult InspectCapableDll(string gameDirectory)
    {
        try
        {
            var game = GameInstallValidator.Validate(gameDirectory);
            if (!game.IsValid || game.GameDirectory is null)
            {
                return Invalid(game.Message);
            }
            var dllPath = Path.Combine(game.GameDirectory, "version.dll");
            return File.Exists(dllPath) && HasContractExport(dllPath)
                ? new(true, null, "Profile-capable community mod DLL verified.")
                : Invalid("This game folder needs a profile-capable community mod version.dll.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException or BadImageFormatException
            or InvalidOperationException or OverflowException)
        {
            return Invalid($"The profile-capable DLL could not be verified: {exception.Message}");
        }
    }

    private static LauncherProfileContractResult Invalid(string message) => new(false, null, message);

    private static bool TryParseMarker(ReadOnlySpan<byte> bytes, out string id)
    {
        id = string.Empty;
        if (bytes.EndsWith("\r\n"u8))
        {
            bytes = bytes[..^2];
        }
        else if (bytes.EndsWith("\n"u8))
        {
            bytes = bytes[..^1];
        }
        if (!bytes.StartsWith("v1:"u8))
        {
            return false;
        }
        bytes = bytes[3..];
        foreach (var value in bytes)
        {
            if (value is not (>= (byte)'a' and <= (byte)'z'
                or >= (byte)'0' and <= (byte)'9' or (byte)'-' or (byte)'_'))
            {
                return false;
            }
        }
        id = System.Text.Encoding.ASCII.GetString(bytes);
        return LauncherProfiles.ValidId(id);
    }

    private static bool HasContractExport(string path)
    {
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        var headers = pe.PEHeaders;
        if (headers.CoffHeader.Machine != Machine.Amd64 || headers.PEHeader is null)
        {
            return false;
        }
        var export = headers.PEHeader.ExportTableDirectory;
        if (export.RelativeVirtualAddress <= 0 || export.Size < 40)
        {
            return false;
        }
        var directory = pe.GetSectionData(export.RelativeVirtualAddress).GetReader();
        if (directory.Length < 40)
        {
            return false;
        }
        directory.Offset = 20;
        var functionCount = directory.ReadUInt32();
        var nameCount = directory.ReadUInt32();
        var functionsRva = directory.ReadUInt32();
        var namesRva = directory.ReadUInt32();
        var ordinalsRva = directory.ReadUInt32();
        if (nameCount is 0 or > 4096 || functionCount is 0 or > 65536)
        {
            return false;
        }
        var names = pe.GetSectionData(checked((int)namesRva)).GetReader();
        var ordinals = pe.GetSectionData(checked((int)ordinalsRva)).GetReader();
        var functions = pe.GetSectionData(checked((int)functionsRva)).GetReader();
        if (names.Length < nameCount * 4 || ordinals.Length < nameCount * 2
            || functions.Length < functionCount * 4)
        {
            return false;
        }
        for (var index = 0; index < nameCount; index++)
        {
            var nameRva = names.ReadUInt32();
            var name = pe.GetSectionData(checked((int)nameRva)).GetReader();
            var matches = true;
            foreach (var character in ContractExport)
            {
                if (name.RemainingBytes == 0 || name.ReadByte() != (byte)character)
                {
                    matches = false;
                    break;
                }
            }
            if (!matches || name.RemainingBytes == 0 || name.ReadByte() != 0)
            {
                ordinals.ReadUInt16();
                continue;
            }
            var ordinal = ordinals.ReadUInt16();
            if (ordinal >= functionCount)
            {
                return false;
            }
            functions.Offset = ordinal * 4;
            var functionRva = functions.ReadUInt32();
            return functionRva != 0
                && (functionRva < export.RelativeVirtualAddress
                    || (ulong)functionRva >= (ulong)export.RelativeVirtualAddress + (uint)export.Size);
        }
        return false;
    }
}
