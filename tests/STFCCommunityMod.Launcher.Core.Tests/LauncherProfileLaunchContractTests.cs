using System.Buffers.Binary;
using System.Text;
using STFCCommunityMod.Launcher.Core;

namespace STFCCommunityMod.Launcher.Core.Tests;

[TestClass]
public sealed class LauncherProfileLaunchContractTests
{
    [TestMethod]
    public void MarkedInstallWithProfileExportCanBeAdoptedAndRechecked()
    {
        using var root = new TemporaryDirectory();
        var game = MakeGame(root);
        File.WriteAllText(Path.Combine(game, "stfc_community_mod.profile"), "v1:dev\n", new UTF8Encoding(false));
        WriteProfileDll(Path.Combine(game, "version.dll"));

        var adopted = LauncherProfileLaunchContract.Inspect(game);
        Assert.IsTrue(adopted.IsValid, adopted.Message);
        Assert.AreEqual("dev", adopted.ProfileId);

        var snapshot = LauncherProfiles.Add(LauncherProfilesSnapshot.Empty, "Secondary", game, null, adopted.ProfileId);
        var renamed = LauncherProfiles.Edit(snapshot, "dev", "Other display name", game, null);
        Assert.AreEqual("dev", renamed.Profiles.Single().Id);
        Assert.IsTrue(LauncherProfileLaunchContract.Inspect(renamed.Profiles.Single().GameDirectory, "dev").IsValid);

        File.WriteAllText(Path.Combine(game, "stfc_community_mod.profile"), "v1:other\n", new UTF8Encoding(false));
        Assert.IsFalse(LauncherProfileLaunchContract.Inspect(game, "dev").IsValid);
    }

    [TestMethod]
    public void AdoptionRejectsMalformedMarkerAndMissingProfileExport()
    {
        using var root = new TemporaryDirectory();
        var game = MakeGame(root);
        var marker = Path.Combine(game, "stfc_community_mod.profile");
        var dll = Path.Combine(game, "version.dll");
        WriteProfileDll(dll);

        foreach (var content in new[] { "v1:DEV", "v1:con", "v1:dev\nsecond", "v1:dev ", "\uFEFFv1:dev" })
        {
            File.WriteAllText(marker, content, new UTF8Encoding(false));
            Assert.IsFalse(LauncherProfileLaunchContract.Inspect(game).IsValid, content);
        }

        File.WriteAllText(marker, "v1:dev", new UTF8Encoding(false));
        var withoutExport = File.ReadAllBytes(dll);
        withoutExport[0x250] = (byte)'X';
        File.WriteAllBytes(dll, withoutExport);
        Assert.IsFalse(LauncherProfileLaunchContract.Inspect(game).IsValid);
        File.WriteAllBytes(dll, [1, 2, 3]);
        Assert.IsFalse(LauncherProfileLaunchContract.Inspect(game).IsValid);
        File.Delete(dll);
        Assert.IsFalse(LauncherProfileLaunchContract.Inspect(game).IsValid);
    }

    [TestMethod]
    public void ProfileIdsFollowRuntimeLowercaseAndDeviceNameRules()
    {
        using var root = new TemporaryDirectory();
        var game = MakeGame(root);
        foreach (var id in new[] { "DEV", "con", "prn", "aux", "nul", "com1", "lpt9" })
        {
            Assert.IsFalse(LauncherProfiles.ValidId(id), id);
            Assert.ThrowsException<ArgumentException>(() =>
                LauncherProfiles.Add(LauncherProfilesSnapshot.Empty, "Secondary", game, null, id));
        }
        Assert.IsTrue(LauncherProfiles.ValidId("dev_2"));
    }

    private static string MakeGame(TemporaryDirectory root)
    {
        var game = root.CreateDirectory("game");
        TemporaryDirectory.CreateFile(game, "prime.exe");
        return game;
    }

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
        Encoding.ASCII.GetBytes("STFCModProfileIsolationContractV1\0").CopyTo(bytes, 0x250);
        File.WriteAllBytes(path, bytes);
    }

    private static void Put16(byte[] data, int offset, ushort value) =>
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(offset, 2), value);

    private static void Put32(byte[] data, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset, 4), value);
}
