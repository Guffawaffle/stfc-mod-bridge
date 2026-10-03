using System.Buffers.Binary;
using System.Text;

namespace STFCCommunityMod.Launcher.Core.Tests;

internal static class ProfileRuntimeFixture
{
    // A minimal x64 PE export directory exercises the same file parser without loading native code.
    internal static void WriteProfileDll(string path)
    {
        var bytes = new byte[0x400];
        bytes[0] = (byte)'M';
        bytes[1] = (byte)'Z';
        Put32(bytes, 0x3c, 0x80);
        bytes[0x80] = (byte)'P';
        bytes[0x81] = (byte)'E';
        Put16(bytes, 0x84, 0x8664); // AMD64
        Put16(bytes, 0x86, 1); // sections
        Put16(bytes, 0x94, 0xf0); // optional header size
        Put16(bytes, 0x98, 0x20b); // PE32+
        Put32(bytes, 0x98 + 32, 0x1000); // section alignment
        Put32(bytes, 0x98 + 36, 0x200); // file alignment
        Put32(bytes, 0x98 + 56, 0x2000); // image size
        Put32(bytes, 0x98 + 60, 0x200); // headers size
        Put32(bytes, 0x98 + 108, 16); // data directories
        Put32(bytes, 0x98 + 112, 0x1000); // export directory RVA
        Put32(bytes, 0x98 + 116, 0x80); // export directory size
        Encoding.ASCII.GetBytes(".rdata").CopyTo(bytes, 0x188);
        Put32(bytes, 0x188 + 8, 0x200); // virtual size
        Put32(bytes, 0x188 + 12, 0x1000); // virtual address
        Put32(bytes, 0x188 + 16, 0x200); // raw size
        Put32(bytes, 0x188 + 20, 0x200); // raw offset
        Put32(bytes, 0x200 + 20, 1); // function count
        Put32(bytes, 0x200 + 24, 1); // name count
        Put32(bytes, 0x200 + 28, 0x1040); // function table
        Put32(bytes, 0x200 + 32, 0x1044); // name table
        Put32(bytes, 0x200 + 36, 0x1048); // ordinal table
        Put32(bytes, 0x240, 0x1100); // function RVA
        Put32(bytes, 0x244, 0x1050); // export name RVA
        Encoding.ASCII.GetBytes("STFCProfilesExplicitLaunchContractV1\0").CopyTo(bytes, 0x250);
        File.WriteAllBytes(path, bytes);
    }

    private static void Put16(byte[] data, int offset, ushort value) =>
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(offset, 2), value);

    private static void Put32(byte[] data, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset, 4), value);
}
