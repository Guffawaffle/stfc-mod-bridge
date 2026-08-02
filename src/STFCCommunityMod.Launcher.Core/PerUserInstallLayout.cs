namespace STFCCommunityMod.Launcher.Core;

public sealed record PerUserInstallLayout(string ProgramDirectory, string StateDirectory)
{
    public static PerUserInstallLayout FromLocalApplicationData(string localApplicationData)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localApplicationData);

        var normalizedRoot = Path.GetFullPath(localApplicationData);
        return new(
            Path.Combine(normalizedRoot, "Programs", ModControlProductIdentity.ProgramDirectoryName),
            Path.Combine(normalizedRoot, ModControlProductIdentity.StateDirectoryName));
    }

    public static PerUserInstallLayout FromCurrentUser()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new InvalidOperationException("Windows did not provide a per-user LocalApplicationData directory.");
        }

        return FromLocalApplicationData(root);
    }
}
